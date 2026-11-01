using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using MadClean.Core;
using Microsoft.Win32.SafeHandles;

namespace MadClean.Windows;

internal enum SystemToolCatalogStage { Unknown, ToolLease, CatalogHash, CatalogLookup, CatalogLease, MemberEnumeration, CatalogSignature, Completed }
internal enum SystemToolCatalogReason
{
    Observed, NoFirstCatalog, UnqualifiedScope, NativeFailure, HashUnsupported, MemberNotFound,
    MemberLimit, SignatureNotEstablished, SignerNotQualified, UnsupportedPlatform, MalformedMember,
    PendingWorker, CleanupUnavailable
}
internal sealed record SystemToolCatalogObservationResult(CheckState State, SystemToolCatalogStage Stage,
    SystemToolCatalogReason Reason, int CatalogCount, int MemberCount, int MatchingMemberCount,
    int? NativeErrorCode, int? NativeTrustResult, bool Limited);

/// <summary>
/// Isolated, existential observation of the first SHA256 catalog candidate for a fixed OS tool.
/// Never used by production identity acceptance. The calling metadata worker owns its entire native wait.
/// </summary>
internal static class SystemToolCatalogObservation
{
    private const int MaximumMembers = 4096;
    private const long MaximumCatalogBytes = 16_777_216;
    private const string Sha256Oid = "2.16.840.1.101.3.4.2.1";
    private static readonly SemaphoreSlim Worker = new(1, 1);
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    internal static SystemToolCatalogObservationResult Read(SystemToolOperation operation, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var observation = new Observation();
        if (!OperatingSystem.IsWindows()) return observation.Result(CheckState.Unsupported, SystemToolCatalogReason.UnsupportedPlatform);
        if (!Environment.Is64BitProcess) return observation.Result(CheckState.Unsupported, SystemToolCatalogReason.UnsupportedPlatform);
        if (!LayoutSupported()) return observation.Result(CheckState.Unknown, SystemToolCatalogReason.UnqualifiedScope);
        if (!Worker.Wait(0)) return observation.Result(CheckState.Unavailable, SystemToolCatalogReason.PendingWorker);
        var resources = new NativeResources();
        SystemToolCatalogObservationResult result;
        try { result = ReadCore(operation, observation, resources, token); }
        catch (SystemToolTrustFailure)
        {
            result = observation.Result(CheckState.Unknown, observation.TrustResult == 0
                ? SystemToolCatalogReason.SignerNotQualified : observation.TrustResult.HasValue
                    ? SystemToolCatalogReason.SignatureNotEstablished : SystemToolCatalogReason.UnqualifiedScope);
        }
        catch (Win32Exception exception)
        { result = observation.Result(CheckState.Unavailable, SystemToolCatalogReason.NativeFailure, exception.NativeErrorCode); }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException)
        { result = observation.Result(CheckState.Unsupported, SystemToolCatalogReason.UnsupportedPlatform); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or System.Security.SecurityException or CryptographicException or System.Formats.Asn1.AsnContentException)
        { result = observation.Result(CheckState.Unknown, SystemToolCatalogReason.UnqualifiedScope); }
        finally
        {
            try { resources.Dispose(); }
            finally { Worker.Release(); }
        }
        token.ThrowIfCancellationRequested();
        return resources.CleanupFailed ? result with { State = CheckState.Unknown, Reason = SystemToolCatalogReason.CleanupUnavailable } : result;
    }

    private static SystemToolCatalogObservationResult ReadCore(SystemToolOperation operation, Observation observation,
        NativeResources resources, CancellationToken token)
    {
        var definition = SystemToolActionsCatalog.Find(operation);
        if (definition == null) return observation.Result(CheckState.Unsupported, SystemToolCatalogReason.UnqualifiedScope);
        observation.Stage = SystemToolCatalogStage.ToolLease;
        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (!Path.IsPathFullyQualified(system) || system.Length is < 4 or > 1024 || !char.IsAsciiLetter(system[0]) || system[1] != ':' || system[2] != '\\')
            return observation.Result(CheckState.Unknown, SystemToolCatalogReason.UnqualifiedScope);
        system = Path.TrimEndingDirectorySeparator(Path.GetFullPath(system));
        string toolPath = Path.Combine(system, definition.FileName);
        var toolDirectories = SystemToolTrustNative.LockDirectoryChain(system);
        try
        {
            using var toolHandle = SystemToolTrustNative.OpenFile(toolPath);
            SystemToolTrustNative.ValidateAttributes(toolHandle, false); SystemToolTrustNative.ValidateFinalPath(toolHandle, toolPath);
            using var tool = new FileStream(toolHandle, FileAccess.Read, 65_536, false);
            try
            {
                if (tool.Length is <= 0 or > 33_554_432) return observation.Result(CheckState.Unknown, SystemToolCatalogReason.UnqualifiedScope);
                token.ThrowIfCancellationRequested();
                observation.Stage = SystemToolCatalogStage.CatalogHash;
                // Null subsystem is documented by the SDK as a global FIND, not an Add.
                if (!CryptCATAdminAcquireContext2(out nint admin, 0, "SHA256", 0, 0)) return NativeFailure(observation);
                if (admin == 0) return observation.Result(CheckState.Unknown, SystemToolCatalogReason.NativeFailure);
                resources.Admin = admin; // Only a successfully acquired context becomes owned.
                uint length = 0;
                Reset(tool, token);
                if (!CryptCATAdminCalcHashFromFileHandle2(resources.Admin, toolHandle, ref length, null, 0)) return NativeFailure(observation);
                if (length != 32) return observation.Result(CheckState.Unknown, SystemToolCatalogReason.HashUnsupported);
                byte[] hash = new byte[32];
                try
                {
                    Reset(tool, token);
                    if (!CryptCATAdminCalcHashFromFileHandle2(resources.Admin, toolHandle, ref length, hash, 0)) return NativeFailure(observation);
                    if (length != hash.Length) return observation.Result(CheckState.Unknown, SystemToolCatalogReason.HashUnsupported);
                    token.ThrowIfCancellationRequested();
                    Reset(tool, token);
                    if (!CryptSIPRetrieveSubjectGuid(toolPath, toolHandle, out var subject)) return NativeFailure(observation);
                    if (subject == Guid.Empty) return observation.Result(CheckState.Unknown, SystemToolCatalogReason.MalformedMember);
                    observation.Stage = SystemToolCatalogStage.CatalogLookup;
                    resources.CatalogInfo = CryptCATAdminEnumCatalogFromHash(resources.Admin, hash, (uint)hash.Length, 0, 0);
                    if (resources.CatalogInfo == 0) return observation.Result(CheckState.Unknown, SystemToolCatalogReason.NoFirstCatalog);
                    observation.CatalogCount = 1;
                    var info = new CatalogInfo { Size = (uint)Marshal.SizeOf<CatalogInfo>(), File = new char[260] };
                    if (!CryptCATCatalogInfoFromContext(resources.CatalogInfo, ref info, 0)) return NativeFailure(observation);
                    int terminator = Array.IndexOf(info.File, '\0');
                    if (terminator <= 0 || terminator >= 260) return observation.Result(CheckState.Unknown, SystemToolCatalogReason.UnqualifiedScope);
                    string catalogPath = new(info.File, 0, terminator);
                    string catRoot = Path.Combine(system, "CatRoot");
                    if (!ValidCatalogPath(catalogPath, catRoot)) return observation.Result(CheckState.Unknown, SystemToolCatalogReason.UnqualifiedScope);
                    observation.Stage = SystemToolCatalogStage.CatalogLease;
                    var catalogDirectories = SystemToolTrustNative.LockDirectoryChain(Path.GetDirectoryName(catalogPath)!);
                    try
                    {
                        using var catalogHandle = SystemToolTrustNative.OpenFile(catalogPath);
                        SystemToolTrustNative.ValidateAttributes(catalogHandle, false); SystemToolTrustNative.ValidateFinalPath(catalogHandle, catalogPath);
                        using var catalogFile = new FileStream(catalogHandle, FileAccess.Read, 4096, false);
                        try
                        {
                            if (catalogFile.Length is <= 0 or > MaximumCatalogBytes) return observation.Result(CheckState.Unknown, SystemToolCatalogReason.UnqualifiedScope);
                            token.ThrowIfCancellationRequested();
                            resources.Catalog = CryptCATOpen(catalogPath, 0, 0, 0x200, 0);
                            if (resources.Catalog == 0 || resources.Catalog == new nint(-1)) return NativeFailure(observation);
                            observation.Stage = SystemToolCatalogStage.MemberEnumeration;
                            nint previous = 0;
                            var seen = new HashSet<nint>();
                            for (int index = 0; index <= MaximumMembers; index++)
                            {
                                token.ThrowIfCancellationRequested();
                                nint memberPointer = CryptCATEnumerateMember(resources.Catalog, previous);
                                // NULL does not establish database completeness or a successful negative.
                                if (memberPointer == 0) return observation.Result(CheckState.Unknown, SystemToolCatalogReason.MemberNotFound);
                                if (index == MaximumMembers) return observation.Result(CheckState.Unknown, SystemToolCatalogReason.MemberLimit);
                                if (!seen.Add(memberPointer)) return observation.Result(CheckState.Unknown, SystemToolCatalogReason.MalformedMember);
                                previous = memberPointer; observation.MemberCount++;
                                var member = Marshal.PtrToStructure<CatalogMemberPrefix>(memberPointer);
                                if (member.Size < Marshal.SizeOf<CatalogMemberPrefix>()) return observation.Result(CheckState.Unknown, SystemToolCatalogReason.MalformedMember);
                                if (member.Subject != subject || member.IndirectData == 0) continue;
                                var indirect = Marshal.PtrToStructure<IndirectData>(member.IndirectData);
                                string? oid = BoundedAnsi(indirect.Algorithm.Oid, 64);
                                if (oid == null) return observation.Result(CheckState.Unknown, SystemToolCatalogReason.MalformedMember);
                                if (oid != Sha256Oid) continue;
                                // SHA256 AlgorithmIdentifier has absent or DER NULL parameters only.
                                if (!ValidAlgorithmParameters(indirect.Algorithm.Parameters)) return observation.Result(CheckState.Unknown, SystemToolCatalogReason.MalformedMember);
                                if (indirect.Digest.Size != 32 || indirect.Digest.Data == 0) continue;
                                byte[] memberHash = new byte[32]; Marshal.Copy(indirect.Digest.Data, memberHash, 0, 32);
                                bool equal;
                                try { equal = CryptographicOperations.FixedTimeEquals(hash, memberHash); }
                                finally { CryptographicOperations.ZeroMemory(memberHash); }
                                if (!equal) continue;
                                string? tag = BoundedUnicode(member.ReferenceTag, 1024);
                                if (tag == null || !SafeUnicode(tag)) return observation.Result(CheckState.Unknown, SystemToolCatalogReason.MalformedMember);
                                observation.MatchingMemberCount = 1;
                                token.ThrowIfCancellationRequested();
                                Reset(tool, token);
                                observation.Stage = SystemToolCatalogStage.CatalogSignature;
                                VerifyCatalog(toolPath, toolHandle, catalogPath, tag, hash, resources, observation);
                                token.ThrowIfCancellationRequested();
                                return observation.Result(CheckState.Available, SystemToolCatalogReason.Observed) with { Stage = SystemToolCatalogStage.Completed };
                            }
                            return observation.Result(CheckState.Unknown, SystemToolCatalogReason.MemberLimit);
                        }
                        finally { resources.Dispose(); }
                    }
                    finally { foreach (var directory in catalogDirectories.AsEnumerable().Reverse()) directory.Dispose(); }
                }
                finally { CryptographicOperations.ZeroMemory(hash); }
            }
            finally { resources.Dispose(); }
        }
        finally { foreach (var directory in toolDirectories.AsEnumerable().Reverse()) directory.Dispose(); }
    }

    private static void VerifyCatalog(string toolPath, SafeFileHandle tool, string catalogPath, string tag, byte[] hash,
        NativeResources resources, Observation observation)
    {
        nint catalogMemory = 0, tagMemory = 0, toolMemory = 0, hashMemory = 0, infoMemory = 0;
        bool held = false; var data = new WinTrustData();
        try
        {
            tool.DangerousAddRef(ref held);
            catalogMemory = Marshal.StringToCoTaskMemUni(catalogPath); tagMemory = Marshal.StringToCoTaskMemUni(tag);
            toolMemory = Marshal.StringToCoTaskMemUni(toolPath); hashMemory = Marshal.AllocHGlobal(32); Marshal.Copy(hash, 0, hashMemory, 32);
            var info = new WinTrustCatalogInfo
            {
                Size = (uint)Marshal.SizeOf<WinTrustCatalogInfo>(), CatalogPath = catalogMemory, MemberTag = tagMemory,
                MemberPath = toolMemory, MemberFile = tool.DangerousGetHandle(), CalculatedHash = hashMemory,
                HashLength = 32, Admin = resources.Admin
            };
            infoMemory = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustCatalogInfo>()); Marshal.StructureToPtr(info, infoMemory, false);
            data = new WinTrustData
            {
                Size = (uint)Marshal.SizeOf<WinTrustData>(), UiChoice = 2, RevocationChecks = 1,
                UnionChoice = 2, Info = infoMemory, StateAction = 1, ProviderFlags = 0x40 | 0x2000
            };
            Guid action = GenericVerifyV2;
            int status = WinVerifyTrust(new nint(-1), ref action, ref data); observation.TrustResult = status;
            if (status != 0) throw new CatalogTrustFailure();
            SystemToolTrustNative.InspectMicrosoftSigner(data.StateData);
        }
        catch (CatalogTrustFailure) { throw new SystemToolTrustFailure(CheckState.Unknown, SystemToolTrustState.Unknown, "Catalog verification was not established."); }
        finally
        {
            try
            {
                if (data.StateData != 0)
                {
                    data.StateAction = 2; Guid action = GenericVerifyV2;
                    if (WinVerifyTrust(new nint(-1), ref action, ref data) != 0) resources.CleanupFailed = true;
                }
            }
            finally
            {
                if (infoMemory != 0) Marshal.FreeHGlobal(infoMemory);
                if (hashMemory != 0) { Marshal.Copy(new byte[32], 0, hashMemory, 32); Marshal.FreeHGlobal(hashMemory); }
                if (toolMemory != 0) Marshal.FreeCoTaskMem(toolMemory);
                if (tagMemory != 0) Marshal.FreeCoTaskMem(tagMemory);
                if (catalogMemory != 0) Marshal.FreeCoTaskMem(catalogMemory);
                if (held) tool.DangerousRelease();
            }
        }
    }

    private sealed class CatalogTrustFailure : Exception { }
    private sealed class Observation
    {
        internal SystemToolCatalogStage Stage;
        internal int CatalogCount, MemberCount, MatchingMemberCount;
        internal int? TrustResult;
        internal SystemToolCatalogObservationResult Result(CheckState state, SystemToolCatalogReason reason, int? error = null) =>
            new(state, Stage, reason, CatalogCount, MemberCount, MatchingMemberCount, error, TrustResult, true);
    }
    private sealed class NativeResources : IDisposable
    {
        internal nint Admin, CatalogInfo, Catalog;
        internal bool CleanupFailed;
        public void Dispose()
        {
            // Detach ownership before any release. Inner guarded scopes clean up first;
            // the outer safety finally can run again without closing a handle twice.
            nint catalog = Catalog, catalogInfo = CatalogInfo, admin = Admin;
            Catalog = 0; CatalogInfo = 0; Admin = 0;
            try { if (catalog != 0 && catalog != new nint(-1) && !CryptCATClose(catalog)) CleanupFailed = true; }
            finally
            {
                try { if (catalogInfo != 0 && !CryptCATAdminReleaseCatalogContext(admin, catalogInfo, 0)) CleanupFailed = true; }
                finally { if (admin != 0 && !CryptCATAdminReleaseContext(admin, 0)) CleanupFailed = true; }
            }
        }
    }
    private static bool LayoutSupported()
    {
        static bool Offset<T>(string field, int expected) => Marshal.OffsetOf<T>(field).ToInt64() == expected;
        try
        {
            return Marshal.SizeOf<CatalogInfo>() == 524 && Offset<CatalogInfo>(nameof(CatalogInfo.File), 4) &&
            Marshal.SizeOf<CatalogMemberPrefix>() == 56 && Offset<CatalogMemberPrefix>(nameof(CatalogMemberPrefix.ReferenceTag), 8) &&
            Offset<CatalogMemberPrefix>(nameof(CatalogMemberPrefix.FileName), 16) && Offset<CatalogMemberPrefix>(nameof(CatalogMemberPrefix.Subject), 24) &&
            Offset<CatalogMemberPrefix>(nameof(CatalogMemberPrefix.Flags), 40) && Offset<CatalogMemberPrefix>(nameof(CatalogMemberPrefix.IndirectData), 48) &&
            Marshal.SizeOf<Blob>() == 16 && Offset<Blob>(nameof(Blob.Data), 8) &&
            Marshal.SizeOf<AttributeValue>() == 24 && Offset<AttributeValue>(nameof(AttributeValue.Value), 8) &&
            Marshal.SizeOf<AlgorithmIdentifier>() == 24 && Offset<AlgorithmIdentifier>(nameof(AlgorithmIdentifier.Parameters), 8) &&
            Marshal.SizeOf<IndirectData>() == 64 && Offset<IndirectData>(nameof(IndirectData.Algorithm), 24) && Offset<IndirectData>(nameof(IndirectData.Digest), 48) &&
            Marshal.SizeOf<WinTrustCatalogInfo>() == 72 && Offset<WinTrustCatalogInfo>(nameof(WinTrustCatalogInfo.CatalogPath), 8) &&
            Offset<WinTrustCatalogInfo>(nameof(WinTrustCatalogInfo.MemberTag), 16) && Offset<WinTrustCatalogInfo>(nameof(WinTrustCatalogInfo.MemberPath), 24) &&
            Offset<WinTrustCatalogInfo>(nameof(WinTrustCatalogInfo.MemberFile), 32) && Offset<WinTrustCatalogInfo>(nameof(WinTrustCatalogInfo.CalculatedHash), 40) &&
            Offset<WinTrustCatalogInfo>(nameof(WinTrustCatalogInfo.HashLength), 48) && Offset<WinTrustCatalogInfo>(nameof(WinTrustCatalogInfo.CatalogContext), 56) &&
            Offset<WinTrustCatalogInfo>(nameof(WinTrustCatalogInfo.Admin), 64) &&
            Marshal.SizeOf<WinTrustData>() == 88 && Offset<WinTrustData>(nameof(WinTrustData.PolicyCallback), 8) &&
            Offset<WinTrustData>(nameof(WinTrustData.SipClient), 16) && Offset<WinTrustData>(nameof(WinTrustData.UiChoice), 24) &&
            Offset<WinTrustData>(nameof(WinTrustData.RevocationChecks), 28) && Offset<WinTrustData>(nameof(WinTrustData.UnionChoice), 32) &&
            Offset<WinTrustData>(nameof(WinTrustData.Info), 40) && Offset<WinTrustData>(nameof(WinTrustData.StateAction), 48) &&
            Offset<WinTrustData>(nameof(WinTrustData.StateData), 56) && Offset<WinTrustData>(nameof(WinTrustData.UrlReference), 64) &&
            Offset<WinTrustData>(nameof(WinTrustData.ProviderFlags), 72) && Offset<WinTrustData>(nameof(WinTrustData.UiContext), 76) &&
            Offset<WinTrustData>(nameof(WinTrustData.SignatureSettings), 80);
        }
        catch (Exception exception) when (exception is ArgumentException or TypeLoadException or NotSupportedException)
        { return false; }
    }
    private static SystemToolCatalogObservationResult NativeFailure(Observation observation) =>
        observation.Result(CheckState.Unavailable, observation.Stage == SystemToolCatalogStage.CatalogHash
            ? SystemToolCatalogReason.HashUnsupported : SystemToolCatalogReason.NativeFailure, Marshal.GetLastWin32Error());
    private static void Reset(FileStream stream, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (stream.Seek(0, SeekOrigin.Begin) != 0 || stream.Position != 0) throw new IOException("Held stream positioning unavailable.");
    }
    private static bool ValidCatalogPath(string path, string root)
    {
        if (path.Length > 259 || !SafeUnicode(path) || !path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase) ||
            !path.EndsWith(".cat", StringComparison.OrdinalIgnoreCase) || path.Contains('/') || path.Contains('%') || path[2..].Contains(':')) return false;
        return path[3..].Split('\\').All(part => part.Length > 0 && part is not ("." or "..") &&
            !part.EndsWith('.') && !part.EndsWith(' ') && part.IndexOfAny(['*', '?', '"', '<', '>', '|']) < 0);
    }
    private static bool ValidAlgorithmParameters(Blob value) => value.Size == 0 ||
        value.Size == 2 && value.Data != 0 && Marshal.ReadByte(value.Data) == 5 && Marshal.ReadByte(value.Data, 1) == 0;
    private static string? BoundedAnsi(nint pointer, int maximum)
    {
        if (pointer == 0) return null;
        var text = new StringBuilder();
        for (int i = 0; i <= maximum; i++)
        { byte value = Marshal.ReadByte(pointer, i); if (value == 0) return text.Length > 0 ? text.ToString() : null; if (value > 127) return null; text.Append((char)value); }
        return null;
    }
    private static string? BoundedUnicode(nint pointer, int maximum)
    {
        if (pointer == 0) return null;
        var text = new StringBuilder();
        for (int i = 0; i <= maximum; i++)
        { char value = unchecked((char)Marshal.ReadInt16(pointer, i * 2)); if (value == '\0') return text.Length > 0 ? text.ToString() : null; text.Append(value); }
        return null;
    }
    private static bool SafeUnicode(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char value = text[i];
            if (char.IsControl(value) || char.GetUnicodeCategory(text, i) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) return false;
            if (char.IsHighSurrogate(value)) { if (++i >= text.Length || !char.IsLowSurrogate(text[i])) return false; }
            else if (char.IsLowSurrogate(value)) return false;
        }
        return true;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct CatalogInfo
    { internal uint Size; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 260, ArraySubType = UnmanagedType.U2)] internal char[] File; }
    [StructLayout(LayoutKind.Sequential)] private struct CatalogMemberPrefix
    { internal uint Size; internal nint ReferenceTag, FileName; internal Guid Subject; internal uint Flags; internal nint IndirectData; }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { internal uint Size; internal nint Data; }
    [StructLayout(LayoutKind.Sequential)] private struct AttributeValue { internal nint Oid; internal Blob Value; }
    [StructLayout(LayoutKind.Sequential)] private struct AlgorithmIdentifier { internal nint Oid; internal Blob Parameters; }
    [StructLayout(LayoutKind.Sequential)] private struct IndirectData { internal AttributeValue Data; internal AlgorithmIdentifier Algorithm; internal Blob Digest; }
    [StructLayout(LayoutKind.Sequential)] private struct WinTrustCatalogInfo
    { internal uint Size, Version; internal nint CatalogPath, MemberTag, MemberPath, MemberFile, CalculatedHash; internal uint HashLength; internal nint CatalogContext, Admin; }
    [StructLayout(LayoutKind.Sequential)] private struct WinTrustData
    { internal uint Size; internal nint PolicyCallback, SipClient; internal uint UiChoice, RevocationChecks, UnionChoice; internal nint Info; internal uint StateAction; internal nint StateData, UrlReference; internal uint ProviderFlags, UiContext; internal nint SignatureSettings; }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminAcquireContext2(out nint admin, nint subsystem, string algorithm, nint policy, uint flags);
    [DllImport("wintrust.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminCalcHashFromFileHandle2(nint admin, SafeFileHandle file, ref uint size, [Out] byte[]? hash, uint flags);
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptSIPRetrieveSubjectGuid(string fileName, SafeFileHandle file, out Guid subject);
    [DllImport("wintrust.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern nint CryptCATAdminEnumCatalogFromHash(nint admin, byte[] hash, uint size, uint flags, nint previous);
    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATCatalogInfoFromContext(nint catalog, ref CatalogInfo info, uint flags);
    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern nint CryptCATOpen(string path, uint flags, nint provider, uint version, uint encoding);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern nint CryptCATEnumerateMember(nint catalog, nint previous);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptCATClose(nint catalog);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptCATAdminReleaseCatalogContext(nint admin, nint catalog, uint flags);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptCATAdminReleaseContext(nint admin, uint flags);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int WinVerifyTrust(nint window, ref Guid action, ref WinTrustData data);
}
