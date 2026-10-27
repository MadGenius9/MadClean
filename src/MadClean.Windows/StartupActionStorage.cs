using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MadClean.Core;
using Microsoft.Win32.SafeHandles;

namespace MadClean.Windows;

internal sealed class StartupActionScope
{
    internal bool Fixture { get; }
    internal string SourceBase { get; }
    internal string JournalBase { get; }
    internal string BackupDirectory { get; }
    internal string? ProfileDirectory { get; }
    internal string ScopeHash { get; }
    private readonly string? markerPath;
    private readonly string? marker;

    private StartupActionScope(bool fixture, string sourceBase, string journalBase, string backup,
        string? profile, string? markerPath = null, string? marker = null)
    {
        Fixture = fixture; SourceBase = sourceBase; JournalBase = journalBase; BackupDirectory = backup;
        ProfileDirectory = profile; this.markerPath = markerPath; this.marker = marker;
        byte[] context = Encoding.UTF8.GetBytes("MadClean.Startup.v1|" + fixture + "|" + sourceBase.ToUpperInvariant() + "|" +
            backup.ToUpperInvariant() + "|" + marker);
        try { ScopeHash = Convert.ToHexString(SHA256.HashData(context)); }
        finally { CryptographicOperations.ZeroMemory(context); }
    }

    internal static StartupActionScope Production()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string backup = string.IsNullOrEmpty(profile) ? "" : Path.Combine(profile, "MadClean", "StartupActions");
        return new(false, @"Software\Microsoft\Windows\CurrentVersion", @"Software\MadClean\StartupActions\Journal", backup, profile);
    }

    internal static StartupActionScope ForFixture(string regBase, string backupDir)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        string artifacts = Path.Combine(Environment.CurrentDirectory, "artifacts");
        if (!StartupActionPolicy.TryFixtureRegistryBase(regBase, out var fixture) ||
            !StartupActionPolicy.IsFixtureBackupDirectory(backupDir, artifacts, fixture))
            throw new ArgumentException("Only the exact owned repository fixture topology is accepted.");
        string local = InstalledApplicationPaths.NormalizeLocalDirectory(backupDir)!;
        if (!WindowsShortcutNative.TryOpenAncestry(local, true, default, out var directory, out _))
            throw new IOException("The owned fixture backup directory could not be validated.");
        using (directory)
        {
            string markerPath = Path.Combine(Path.GetDirectoryName(local)!, ".madclean-fixture-owner");
            using var owner = StartupActionFiles.OpenMarker(markerPath);
            return new(true, regBase, regBase + @"\Journal", local, null, markerPath, owner.Marker);
        }
    }

    internal string SourcePath(StartupRegistrationSource source) => SourceBase + "\\" +
        (StartupActionPolicy.SourceSubkey(source) ?? throw new NotSupportedException("Unsupported registration source."));
    internal string JournalPath(string id) => StartupActionPolicy.IsOpaqueId(id) ? JournalBase + "\\" + id :
        throw new NotSupportedException("Unsupported recovery identity.");
    internal IDisposable LeaseOwnership()
    {
        if (!Fixture) return new NoopLease();
        var owned = StartupActionFiles.OpenMarker(markerPath!);
        if (!owned.Marker.Equals(marker, StringComparison.Ordinal)) { owned.Dispose(); throw new IOException("The fixture ownership marker changed."); }
        return owned;
    }
    private sealed class NoopLease : IDisposable { public void Dispose() { } }
}

internal sealed class StartupActionPayload : IDisposable
{
    public StartupActionPayload() { }
    public string Origin { get; set; } = "MadClean.StartupActions.v1";
    public int Version { get; set; } = 1;
    public string BackupId { get; set; } = "";
    public StartupRegistrationReference Reference { get; set; } = new(StartupRegistrationSource.CurrentUserRun, "");
    public DateTimeOffset CreatedUtc { get; set; }
    public string OwnerSid { get; set; } = "";
    public string ScopeHash { get; set; } = "";
    public string State { get; set; } = "";
    public uint Kind { get; set; }
    public byte[] Data { get; set; } = [];
    public void Dispose() => CryptographicOperations.ZeroMemory(Data);
}

internal static class StartupActionPayloadCodec
{
    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
        { "Origin", "Version", "BackupId", "Reference", "CreatedUtc", "OwnerSid", "ScopeHash", "State", "Kind", "Data" };
    private static readonly JsonSerializerOptions Options = new() { MaxDepth = 8, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    internal static string CurrentSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.ImpersonationLevel != TokenImpersonationLevel.None || identity.User == null)
            throw new NotSupportedException("Impersonated or unidentified-user mutation contexts are unsupported.");
        return identity.User.Value;
    }
    internal static StartupActionPayload Decode(byte[] cipher, StartupActionScope scope, string sid, params string[] allowedStates)
    {
        byte[] plain = StartupActionDataProtection.Unprotect(cipher);
        StartupActionPayload? value = null;
        try
        {
            using var document = JsonDocument.Parse(plain, new JsonDocumentOptions { MaxDepth = 8 });
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Private record schema is unsupported.");
            foreach (var property in document.RootElement.EnumerateObject())
                if (!Fields.Contains(property.Name) || !names.Add(property.Name)) throw new InvalidDataException("Private record schema is unsupported.");
            if (names.Count != Fields.Count) throw new InvalidDataException("Private record schema is incomplete.");
            var reference = document.RootElement.GetProperty("Reference");
            if (reference.ValueKind != JsonValueKind.Object || reference.EnumerateObject().Count() != 2 ||
                !reference.TryGetProperty("Source", out _) || !reference.TryGetProperty("ValueName", out _))
                throw new InvalidDataException("Private registration identity is unsupported.");
            value = JsonSerializer.Deserialize<StartupActionPayload>(plain, Options) ?? throw new InvalidDataException("Private record is empty.");
            if (value.Origin != "MadClean.StartupActions.v1" || value.Version != 1 || !StartupActionPolicy.IsOpaqueId(value.BackupId) ||
                !StartupActionPolicy.IsValidReference(value.Reference) || value.CreatedUtc == default || value.CreatedUtc.Offset != TimeSpan.Zero ||
                value.OwnerSid != sid || value.ScopeHash != scope.ScopeHash || !allowedStates.Contains(value.State, StringComparer.Ordinal) ||
                value.Data == null || !StartupActionPolicy.IsSupportedRawValue(value.Kind, value.Data))
                throw new InvalidDataException("Private recovery identity, scope or data could not be validated.");
            var result = value; value = null; return result;
        }
        finally { value?.Dispose(); CryptographicOperations.ZeroMemory(plain); }
    }
    internal static bool SameOriginal(StartupActionPayload first, StartupActionPayload second) =>
        first.BackupId == second.BackupId && first.Reference == second.Reference && first.CreatedUtc == second.CreatedUtc &&
        first.OwnerSid == second.OwnerSid && first.ScopeHash == second.ScopeHash && first.Kind == second.Kind &&
        CryptographicOperations.FixedTimeEquals(first.Data, second.Data);
}

internal sealed class StartupActionBackupLease : IDisposable
{
    private readonly IDisposable directory;
    private readonly FileStream stream;
    internal StartupActionPayload Payload { get; }
    internal StartupActionBackupLease(IDisposable directory, FileStream stream, StartupActionPayload payload)
    { this.directory = directory; this.stream = stream; Payload = payload; }
    public void Dispose() { Payload.Dispose(); stream.Dispose(); directory.Dispose(); }
}

internal sealed class StartupActionProtectedStore(StartupActionScope scope)
{
    internal string VaultPath => Path.Combine(scope.BackupDirectory, "vault");
    internal string BackupPath(string id) => StartupActionPolicy.IsOpaqueId(id) ? Path.Combine(VaultPath, "backup-" + id + ".startup.dpapi") :
        throw new NotSupportedException("Unsupported backup identity.");

    internal StartupActionBackupLease Open(string id, string sid)
    {
        IDisposable? directory = null; FileStream? stream = null; StartupActionPayload? payload = null; byte[] cipher = [];
        try
        {
            directory = OpenVault(sid);
            stream = StartupActionFiles.OpenPrivateFile(BackupPath(id), sid);
            if (stream.Length == 0 || stream.Length > StartupActionPolicy.MaximumPrivatePayloadBytes) throw new InvalidDataException("Encrypted backup size is unsupported.");
            cipher = new byte[checked((int)stream.Length)]; stream.ReadExactly(cipher);
            payload = StartupActionPayloadCodec.Decode(cipher, scope, sid, "Prepared");
            if (payload.BackupId != id) throw new InvalidDataException("Backup identity did not match its selected file.");
            var result = new StartupActionBackupLease(directory, stream, payload);
            directory = null; stream = null; payload = null; return result;
        }
        finally { payload?.Dispose(); stream?.Dispose(); directory?.Dispose(); CryptographicOperations.ZeroMemory(cipher); }
    }

    internal IDisposable OpenVault(string sid)
    {
        if (string.IsNullOrWhiteSpace(scope.BackupDirectory)) throw new IOException("A verified local backup directory is unavailable.");
        if (!WindowsShortcutNative.TryOpenAncestry(VaultPath, true, default, out var directory, out _))
            throw new IOException("The private vault could not be validated as existing fixed-local non-reparse storage.");
        try
        {
            StartupActionFiles.VerifyDirectory(VaultPath, sid);
            return directory!;
        }
        catch { directory?.Dispose(); throw; }
    }
}

internal static class StartupActionFiles
{
    private const uint Read = 0x80000000, ReadControl = 0x20000, ReadAttributes = 0x80;
    private const uint OpenExisting = 3, ShareRead = 1;
    private const uint Reparse = 0x00200000, NoRecall = 0x00100000, BackupSemantics = 0x02000000;

    internal sealed class MarkerLease(IDisposable directory, FileStream stream, string marker) : IDisposable
    {
        internal string Marker { get; } = marker;
        public void Dispose() { stream.Dispose(); directory.Dispose(); }
    }
    internal static MarkerLease OpenMarker(string path)
    {
        if (!WindowsShortcutNative.TryOpenAncestry(path, false, default, out var directory, out _) ||
            directory!.LeafInformation.Bytes != 32 || (directory.LeafInformation.FileAttributes & WindowsShortcutNative.DirectoryAttribute) != 0)
        { directory?.Dispose(); throw new IOException("The owned fixture marker could not be validated."); }
        FileStream? stream = null;
        try
        {
            stream = OpenOrdinaryRead(path);
            var bytes = new byte[32]; stream.ReadExactly(bytes);
            string marker = Encoding.ASCII.GetString(bytes);
            if (!Guid.TryParseExact(marker, "N", out _)) throw new IOException("The owned fixture marker format is unsupported.");
            var result = new MarkerLease(directory, stream, marker); stream = null; return result;
        }
        catch { stream?.Dispose(); directory.Dispose(); throw; }
    }

    internal static void VerifyDirectory(string path, string sid)
    {
        using var handle = CreateFileW(path, ReadAttributes | ReadControl, ShareRead, IntPtr.Zero, OpenExisting,
            Reparse | NoRecall | BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) throw new StartupActionNativeFailure("Private directory security open", Marshal.GetLastWin32Error());
        VerifyPrivateSecurity(handle, sid);
    }

    internal static FileStream OpenPrivateFile(string path, string sid)
    {
        var handle = CreateFileW(path, Read | ReadControl, ShareRead, IntPtr.Zero, OpenExisting, Reparse | NoRecall, IntPtr.Zero);
        if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new StartupActionNativeFailure("Private backup read open", error); }
        try
        {
            VerifyPrivateSecurity(handle, sid);
            if (!WindowsShortcutNative.TryReadSafeFileInformation(handle, out _)) throw new IOException("The encrypted backup is not a supported ordinary file.");
            return new FileStream(handle, FileAccess.Read, 4096, isAsync: false);
        }
        catch { handle.Dispose(); throw; }
    }
    private static FileStream OpenOrdinaryRead(string path)
    {
        var handle = CreateFileW(path, Read, ShareRead, IntPtr.Zero, OpenExisting, Reparse | NoRecall, IntPtr.Zero);
        if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new StartupActionNativeFailure("Fixture marker read", error); }
        return new FileStream(handle, FileAccess.Read, 4096, isAsync: false);
    }
    private static void VerifyPrivateSecurity(SafeFileHandle handle, string sid)
    {
        uint error = GetSecurityInfo(handle, 1, 5, out _, out _, out _, out _, out var descriptor);
        if (error != 0) throw new StartupActionNativeFailure("Private backup owner/DACL query", unchecked((int)error));
        try
        {
            uint length = GetSecurityDescriptorLength(descriptor);
            if (length == 0 || length > 65_536) throw new IOException("Private security descriptor length is unsupported.");
            var bytes = new byte[checked((int)length)]; Marshal.Copy(descriptor, bytes, 0, bytes.Length);
            var security = new RawSecurityDescriptor(bytes, 0);
            if (security.Owner?.Value != sid || security.DiscretionaryAcl == null ||
                (security.ControlFlags & ControlFlags.DiscretionaryAclProtected) == 0) throw new IOException("Backup storage is not protected for the current user.");
            bool user = false, system = false;
            foreach (var ace in security.DiscretionaryAcl)
            {
                if (ace is not CommonAce common || common.AceQualifier != AceQualifier.AccessAllowed ||
                    common.SecurityIdentifier.Value is not string allowed || (allowed != sid && allowed != "S-1-5-18") ||
                    (common.AccessMask & 0x1F01FF) != 0x1F01FF) throw new IOException("Backup storage grants an unsupported principal or access policy.");
                if (allowed == sid) user = true; else system = true;
            }
            if (!user || !system) throw new IOException("Backup storage lacks the required current-user/SYSTEM ACL.");
        }
        finally { LocalFree(descriptor); }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("advapi32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetSecurityInfo(SafeFileHandle handle, uint objectType, uint information, out IntPtr owner,
        out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
