using System.ComponentModel;
using System.Diagnostics;
using System.Formats.Asn1;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MadClean.Core;
using Microsoft.Win32.SafeHandles;

namespace MadClean.Windows;

internal sealed class SystemToolTrustFailure(CheckState state, SystemToolTrustState trust, string message,
    SystemToolIdentityDiagnostic? diagnostic = null) : Exception(message)
{
    internal CheckState State { get; } = state;
    internal SystemToolTrustState Trust { get; } = trust;
    internal SystemToolIdentityDiagnostic Diagnostic { get; } = diagnostic ??
        new(SystemToolIdentityStage.Unknown, SystemToolIdentityReason.Unspecified, null);
}

/// <summary>Holds genuine fixed-path executable and ancestor read leases throughout verification/launch preparation.</summary>
internal sealed class SystemToolTrustedLease : IDisposable
{
    private readonly List<SafeFileHandle> directories;
    private readonly FileStream file;
    internal string SystemDirectory { get; }
    internal string ToolPath { get; }
    internal string ToolVersion { get; }
    internal string Sha256 { get; }
    internal string SignatureDetail { get; }
    private SystemToolTrustedLease(List<SafeFileHandle> directories, FileStream file, string systemDirectory,
        string toolPath, string version, string hash, string signatureDetail)
    {
        this.directories = directories; this.file = file; SystemDirectory = systemDirectory;
        ToolPath = toolPath; ToolVersion = version; Sha256 = hash; SignatureDetail = signatureDetail;
    }
    internal static SystemToolTrustedLease Open(SystemToolActionDefinition definition, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) throw new SystemToolTrustFailure(CheckState.Unsupported, SystemToolTrustState.Unavailable, "Windows system tools cannot be verified on this platform.",
            new(SystemToolIdentityStage.Unknown, SystemToolIdentityReason.UnsupportedPlatform, null));
        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (!Path.IsPathFullyQualified(system) || system.Length < 4 || !char.IsAsciiLetter(system[0]) || system[1] != ':' || system[2] != '\\')
            throw new SystemToolTrustFailure(CheckState.Unavailable, SystemToolTrustState.Unavailable, "The genuine local Windows system directory could not be established.",
                new(SystemToolIdentityStage.SystemDirectory, SystemToolIdentityReason.IdentityBoundsOrPath, null));
        system = Path.TrimEndingDirectorySeparator(Path.GetFullPath(system));
        string path = Path.Combine(system, definition.FileName);
        var leases = SystemToolTrustNative.LockDirectoryChain(system);
        FileStream? stream = null;
        SafeFileHandle? handle = null;
        try
        {
            token.ThrowIfCancellationRequested();
            handle = SystemToolTrustNative.OpenFile(path);
            SystemToolTrustNative.ValidateAttributes(handle, directory: false);
            SystemToolTrustNative.ValidateFinalPath(handle, path);
            stream = new FileStream(handle, FileAccess.Read, 65_536, isAsync: false);
            handle = null; // FileStream now owns this SafeFileHandle.
            if (stream.Length is <= 0 or > 33_554_432)
                throw new SystemToolTrustFailure(CheckState.Unknown, SystemToolTrustState.Unknown, "The tool has an unsupported length; bounded identity verification was not completed.",
                    new(SystemToolIdentityStage.FileLease, SystemToolIdentityReason.IdentityBoundsOrPath, null));
            string signature = SystemToolTrustNative.VerifyMicrosoftSignature(path, stream.SafeFileHandle);
            token.ThrowIfCancellationRequested();
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[65_536]; long readTotal = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int count = stream.Read(buffer);
                if (count == 0) break;
                readTotal += count;
                if (readTotal > 33_554_432) throw new SystemToolTrustFailure(CheckState.Unknown, SystemToolTrustState.Unknown, "Tool identity read exceeded its bound.",
                    new(SystemToolIdentityStage.Fingerprint, SystemToolIdentityReason.IdentityBoundsOrPath, null));
                digest.AppendData(buffer, 0, count);
            }
            if (readTotal != stream.Length)
                throw new SystemToolTrustFailure(CheckState.Unknown, SystemToolTrustState.Unknown, "The tool changed or could not be read consistently; no identity was accepted.",
                    new(SystemToolIdentityStage.Fingerprint, SystemToolIdentityReason.IdentityBoundsOrPath, null));
            string hash = Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
            var versionInfo = FileVersionInfo.GetVersionInfo(path);
            if (!string.Equals(versionInfo.OriginalFilename, definition.FileName, StringComparison.OrdinalIgnoreCase))
                throw new SystemToolTrustFailure(CheckState.Unknown, SystemToolTrustState.Unknown, "The signed original executable filename does not match the selected fixed system tool; no identity was accepted.",
                    new(SystemToolIdentityStage.VersionMetadata, SystemToolIdentityReason.OriginalFilenameMismatch, 0));
            string? version = versionInfo.FileVersion;
            if (string.IsNullOrWhiteSpace(version) || version.Length > 128 || version.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format))
                version = "Unknown";
            token.ThrowIfCancellationRequested();
            return new(leases, stream, system, path, version, hash, signature);
        }
        catch
        {
            stream?.Dispose(); handle?.Dispose();
            foreach (var lease in leases.AsEnumerable().Reverse()) lease.Dispose();
            throw;
        }
    }
    public void Dispose()
    {
        file.Dispose();
        foreach (var directory in directories.AsEnumerable().Reverse()) directory.Dispose();
    }
}

internal static class SystemToolTrustNative
{
    private const uint ReadAttributes = 0x80, ListDirectory = 1, ShareRead = 1, OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000, BackupSemantics = 0x02000000, OpenNoRecall = 0x00100000;
    private const uint InvalidAttributes = 0x400 | 0x1000 | 0x4000 | 0x40000 | 0x400000;
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    internal static List<SafeFileHandle> LockDirectoryChain(string directory)
    {
        List<SafeFileHandle> handles = [];
        try
        {
            string root = Path.GetPathRoot(directory)!;
            if (GetDriveType(root) != 3) throw new SystemToolTrustFailure(CheckState.Unsupported, SystemToolTrustState.Unavailable, "System-tool verification requires an available fixed local volume.",
                new(SystemToolIdentityStage.FileLease, SystemToolIdentityReason.IdentityBoundsOrPath, null));
            var ancestors = new Stack<string>();
            for (string? current = directory; current != null; current = Path.GetDirectoryName(current)) ancestors.Push(current);
            foreach (string ancestor in ancestors)
            {
                var handle = CreateFile(ancestor, ReadAttributes | ListDirectory, ShareRead, 0, OpenExisting,
                    BackupSemantics | OpenReparsePoint | OpenNoRecall, 0);
                if (handle.IsInvalid) { int code = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(code); }
                handles.Add(handle); ValidateAttributes(handle, directory: true); ValidateFinalPath(handle, ancestor);
            }
            return handles;
        }
        catch { foreach (var handle in handles.AsEnumerable().Reverse()) handle.Dispose(); throw; }
    }
    internal static SafeFileHandle OpenFile(string path)
    {
        var handle = CreateFile(path, 0x80000000, ShareRead, 0, OpenExisting, OpenReparsePoint | OpenNoRecall | 0x08000000, 0);
        if (!handle.IsInvalid) return handle;
        int code = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(code);
    }
    internal static void ValidateAttributes(SafeFileHandle handle, bool directory)
    {
        if (!GetFileInformationByHandleEx(handle, 9, out AttributeTag attributes, (uint)Marshal.SizeOf<AttributeTag>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if ((attributes.Attributes & InvalidAttributes) != 0 || ((attributes.Attributes & 0x10) != 0) != directory)
            throw new SystemToolTrustFailure(CheckState.Unknown, SystemToolTrustState.Unknown, "A tool or ancestor has reparse, offline, encrypted, recall or unexpected object attributes; no identity was accepted.",
                new(SystemToolIdentityStage.FileLease, SystemToolIdentityReason.IdentityBoundsOrPath, null));
    }
    internal static void ValidateFinalPath(SafeFileHandle handle, string expected)
    {
        var buffer = new StringBuilder(2048);
        uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) throw new SystemToolTrustFailure(CheckState.Unknown, SystemToolTrustState.Unknown, "The bounded final system-tool path could not be verified.",
            new(SystemToolIdentityStage.FileLease, SystemToolIdentityReason.IdentityBoundsOrPath, null));
        string actual = buffer.ToString();
        if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual[4..];
        if (!string.Equals(Path.TrimEndingDirectorySeparator(actual), Path.TrimEndingDirectorySeparator(expected), StringComparison.OrdinalIgnoreCase))
            throw new SystemToolTrustFailure(CheckState.Unknown, SystemToolTrustState.Unknown, "The open system-tool object does not match the fixed requested path.",
                new(SystemToolIdentityStage.FileLease, SystemToolIdentityReason.IdentityBoundsOrPath, null));
    }
    internal static string VerifyMicrosoftSignature(string path, SafeFileHandle file)
    {
        nint pathMemory = 0, fileMemory = 0;
        bool fileReferenceHeld = false;
        bool nativeTrustEstablished = false;
        var data = new WinTrustData();
        try
        {
            file.DangerousAddRef(ref fileReferenceHeld);
            pathMemory = Marshal.StringToCoTaskMemUni(path);
            var info = new WinTrustFileInfo { Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(), Path = pathMemory, File = file.DangerousGetHandle() };
            fileMemory = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(info, fileMemory, false);
            data = new WinTrustData
            {
                Size = (uint)Marshal.SizeOf<WinTrustData>(), UiChoice = 2, RevocationChecks = 1,
                UnionChoice = 1, File = fileMemory, StateAction = 1,
                ProviderFlags = 0x40 | 0x2000 // whole-chain revocation; disable MD2/MD4; no bypass/cache-only flags.
            };
            Guid action = GenericVerifyV2;
            int status = WinVerifyTrust(new nint(-1), ref action, ref data);
            if (status != 0)
            {
                bool unavailable = unchecked((uint)status) is 0x800B0100 or 0x800B010E or 0x80092013;
                throw new SystemToolTrustFailure(unavailable ? CheckState.Unavailable : CheckState.Failed,
                    unavailable ? SystemToolTrustState.Unknown : SystemToolTrustState.Failed,
                    $"Windows Authenticode trust was not established (code 0x{status:X8}). Catalog-only signatures or unavailable revocation evidence are not bypassed; no launch is allowed.",
                    new(SystemToolIdentityStage.Signature, SystemToolIdentityReason.NativeTrustNotEstablished, status));
            }
            nativeTrustEstablished = true;
            nint provider = WTHelperProvDataFromStateData(data.StateData);
            nint signer = provider == 0 ? 0 : WTHelperGetProvSignerFromChain(provider, 0, false, 0);
            nint certificate = signer == 0 ? 0 : WTHelperGetProvCertFromChain(signer, 0);
            if (certificate == 0) throw SignerUnknown();
            var prefix = Marshal.PtrToStructure<ProviderCertificatePrefix>(certificate);
            if (prefix.Size < Marshal.SizeOf<ProviderCertificatePrefix>() || prefix.Certificate == 0) throw SignerUnknown();
            var context = Marshal.PtrToStructure<CertificateContextPrefix>(prefix.Certificate);
            if (context.Encoded == 0 || context.EncodedLength is 0 or > 1_048_576) throw SignerUnknown();
            byte[] bytes = new byte[context.EncodedLength]; Marshal.Copy(context.Encoded, bytes, 0, bytes.Length);
            using var cert = X509CertificateLoader.LoadCertificate(bytes);
            var names = ReadSubject(cert.SubjectName.RawData);
            if (names.Organization != "Microsoft Corporation" || names.CommonName is not ("Microsoft Windows" or "Microsoft Windows Publisher" or "Microsoft Corporation"))
                throw new SystemToolTrustFailure(CheckState.Unknown, SystemToolTrustState.Unknown, "A trusted signature was present but its exact Microsoft publisher identity was not established; no launch is allowed.",
                    new(SystemToolIdentityStage.Signature, SystemToolIdentityReason.SignerNotQualified, 0));
            return "Windows Authenticode policy accepted the signature with whole-chain revocation requested; signer organization and common name identify Microsoft. This does not verify servicing runtime behavior or enable execution.";
        }
        catch (Exception ex) when (nativeTrustEstablished && (ex is CryptographicException or AsnContentException or InvalidOperationException))
        {
            // Preserve the former generic Unavailable/Unknown result while identifying the
            // known signer-inspection stage. Zero is the actual preceding WinVerifyTrust result.
            throw new SystemToolTrustFailure(CheckState.Unavailable, SystemToolTrustState.Unknown,
                $"System-tool identity could not be established (code 0x{ex.HResult:X8}). No validation was bypassed and no tool was launched.",
                new(SystemToolIdentityStage.Signature, SystemToolIdentityReason.SignerInspectionUnavailable, 0));
        }
        finally
        {
            try
            {
                if (data.StateData != 0)
                {
                    data.StateAction = 2; Guid action = GenericVerifyV2;
                    _ = WinVerifyTrust(new nint(-1), ref action, ref data);
                }
            }
            finally
            {
                if (fileMemory != 0) Marshal.FreeHGlobal(fileMemory);
                if (pathMemory != 0) Marshal.FreeCoTaskMem(pathMemory);
                if (fileReferenceHeld) file.DangerousRelease();
            }
        }
    }
    private static SystemToolTrustFailure SignerUnknown() => new(CheckState.Unknown, SystemToolTrustState.Unknown, "The bounded Authenticode signer certificate could not be inspected; no publisher identity was guessed.",
        new(SystemToolIdentityStage.Signature, SystemToolIdentityReason.SignerInspectionUnavailable, 0));
    private static (string Organization, string CommonName) ReadSubject(byte[] name)
    {
        var reader = new AsnReader(name, AsnEncodingRules.DER); var sequence = reader.ReadSequence();
        List<string> organizations = [], commonNames = [];
        while (sequence.HasData)
        {
            var set = sequence.ReadSetOf();
            while (set.HasData)
            {
                var item = set.ReadSequence(); string oid = item.ReadObjectIdentifier(); var tag = item.PeekTag();
                if (tag.TagClass != TagClass.Universal || tag.TagValue is not (12 or 19 or 20 or 22 or 28 or 30)) throw SignerUnknown();
                string text = item.ReadCharacterString((UniversalTagNumber)tag.TagValue); item.ThrowIfNotEmpty();
                if (oid == "2.5.4.10") organizations.Add(text);
                if (oid == "2.5.4.3") commonNames.Add(text);
            }
        }
        reader.ThrowIfNotEmpty();
        if (organizations.Count != 1 || commonNames.Count != 1) throw SignerUnknown();
        return (organizations[0], commonNames[0]);
    }
    [StructLayout(LayoutKind.Sequential)] private struct AttributeTag { internal uint Attributes, ReparseTag; }
    [StructLayout(LayoutKind.Sequential)] private struct WinTrustFileInfo { internal uint Size; internal nint Path, File, KnownSubject; }
    [StructLayout(LayoutKind.Sequential)] private struct WinTrustData
    {
        internal uint Size; internal nint PolicyCallback, SipClient; internal uint UiChoice, RevocationChecks, UnionChoice;
        internal nint File; internal uint StateAction; internal nint StateData, UrlReference; internal uint ProviderFlags, UiContext; internal nint SignatureSettings;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProviderCertificatePrefix { internal uint Size; internal nint Certificate; }
    [StructLayout(LayoutKind.Sequential)] private struct CertificateContextPrefix { internal uint Encoding; internal nint Encoded; internal uint EncodedLength; }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", EntryPoint = "GetDriveTypeW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint GetDriveType(string root);
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out AttributeTag attributes, uint length);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int WinVerifyTrust(nint window, ref Guid action, ref WinTrustData data);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern nint WTHelperProvDataFromStateData(nint state);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern nint WTHelperGetProvSignerFromChain(nint provider, uint signerIndex, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIndex);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern nint WTHelperGetProvCertFromChain(nint signer, uint certificateIndex);
}
