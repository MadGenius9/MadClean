using System.Runtime.InteropServices;
using MadClean.Core;
using Microsoft.Win32.SafeHandles;

namespace MadClean.Windows;

/// <summary>Shared exact-GUID read-only ARP traversal. No command contents, paths or action capability.</summary>
internal sealed class WindowsMsiExactProductArpReader(string productCode, string originalIdentity,
    MsiArpProvenance originalSource, Action check, CancellationToken token)
{
    private const uint Query = 1, Enumerate = 8, OpenLink = 8;
    private static readonly string[] Components = ["Software", "Microsoft", "Windows", "CurrentVersion", "Uninstall"];
    internal IReadOnlyList<MsiSelectedArpLookup> Read()
    {
        if (!Guid.TryParseExact(productCode, "B", out var code) || code == Guid.Empty ||
            code.ToString("B").ToUpperInvariant() != productCode || !Enum.IsDefined(originalSource) ||
            !string.Equals(originalIdentity, MsiSelectedProductPreparationPolicy.RegistrationIdentity(originalSource, productCode), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("An exact canonical product and supported registry provenance are required.");
        Check();
        return Enum.GetValues<MsiArpProvenance>().Select(ReadSource).ToArray();
    }
    private void Check() { token.ThrowIfCancellationRequested(); check(); }
    private MsiSelectedArpLookup ReadSource(MsiArpProvenance source)
    {
        List<SafeRegistryHandle> handles = [];
        MsiSelectedArpLookup Unknown(int? code = null) =>
            new(source, MsiSelectedArpLookupState.Unknown, null, null, DateTimeOffset.UtcNow, code);
        MsiSelectedArpLookup Absent(int code) =>
            new(source, MsiSelectedArpLookupState.Absent, null, null, DateTimeOffset.UtcNow, code);
        try
        {
            Check();
            uint view = source == MsiArpProvenance.LocalMachine64 ? 0x100u : source == MsiArpProvenance.LocalMachine32 ? 0x200u : 0;
            int first = 0;
            SafeRegistryHandle anchor;
            int code;
            if (source == MsiArpProvenance.CurrentUserShared)
                code = RegOpenCurrentUser(Query | Enumerate, out anchor);
            else
            {
                code = RegOpenKeyExW(new nint(unchecked((int)0x80000002)), Components[0], OpenLink, Query | Enumerate | view, out anchor);
                first = 1;
            }
            handles.Add(anchor); Check();
            if (source == MsiArpProvenance.CurrentUserShared && code != 0) return Unknown(code);
            if (code is 2 or 3) return Absent(code);
            if (code != 0) return Unknown(code);
            if (anchor.IsInvalid) return Unknown();
            if (!NoLink(anchor, out int? linkError)) return Unknown(linkError);
            for (int index = first; index <= Components.Length; index++)
            {
                Check();
                string component = index == Components.Length ? productCode : Components[index];
                code = RegOpenRelativeKeyExW(handles[^1], component, OpenLink, Query | Enumerate | view, out var next);
                handles.Add(next); Check();
                if (code is 2 or 3) return Absent(code);
                if (code != 0) return Unknown(code);
                if (next.IsInvalid) return Unknown();
                if (!NoLink(next, out linkError)) return Unknown(linkError);
            }
            var key = handles[^1];
            bool? Flag(string field)
            {
                var read = BoundedRegistrationMetadata.ReadFlag(key, field, Check, token);
                return read.State == RegistrationMetadataState.Missing ? false : read.State == RegistrationMetadataState.Valid ? read.Value : null;
            }
            string Text(string field)
            {
                var read = BoundedRegistrationMetadata.ReadText(key, field, Check, token);
                return read.State == RegistrationMetadataState.Valid && read.Value != null ? read.Value : "Unknown";
            }
            bool? componentFlag = Flag("SystemComponent"), installer = Flag("WindowsInstaller"), restricted = Flag("NoRemove");
            string name = Text("DisplayName"), version = Text("DisplayVersion"), publisher = Text("Publisher");
            var normal = BoundedRegistrationMetadata.ProbeUninstallerName(key, "UninstallString", Check);
            var quiet = BoundedRegistrationMetadata.ProbeUninstallerName(key, "QuietUninstallString", Check);
            bool? present = normal.Exists == true || quiet.Exists == true ? true :
                normal.Exists.HasValue && quiet.Exists.HasValue ? false : null;
            Check();
            var observed = DateTimeOffset.UtcNow;
            string identity = source == originalSource ? originalIdentity :
                MsiSelectedProductPreparationPolicy.RegistrationIdentity(source, productCode);
            string label = MsiSelectedProductPreparationPolicy.SourceLabel(source);
            var evidence = new InstalledAppRegistrationEvidence(identity, label, restricted, installer, present, productCode, observed);
            var row = new InstalledApplication(identity, name, version, publisher, label, null,
                "Exact-product registration metadata only; no action permission", evidence);
            return new(source, MsiSelectedArpLookupState.Present, row, componentFlag, observed, null);
        }
        finally { foreach (var handle in handles.AsEnumerable().Reverse()) handle.Dispose(); }
    }
    private bool NoLink(SafeRegistryHandle key, out int? error)
    {
        Check(); uint size = 0;
        int code = RegQueryTypeW(key, "SymbolicLinkValue", 0, out uint type, 0, ref size);
        Check();
        error = code is 0 or 2 or 234 ? null : code;
        return error == null && !((code is 0 or 234) && type == 6);
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegOpenKeyExW(nint root, string path, uint options, uint rights, out SafeRegistryHandle result);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "RegOpenKeyExW"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegOpenRelativeKeyExW(SafeRegistryHandle root, string path, uint options, uint rights, out SafeRegistryHandle result);
    [DllImport("advapi32.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegOpenCurrentUser(uint rights, out SafeRegistryHandle result);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "RegQueryValueExW"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegQueryTypeW(SafeRegistryHandle key, string name, nint reserved, out uint type, nint data, ref uint bytes);
}

/// <summary>Shared scheduled-before-start ownership for both exact-product read-only adapters.</summary>
internal sealed class ScheduledMsiReadOwnership
{
    private static int scheduledReads;
    private int state;
    internal static bool HasPending => Volatile.Read(ref scheduledReads) != 0;
    internal void Register()
    {
        if (Interlocked.CompareExchange(ref state, 1, 0) != 0) throw new InvalidOperationException();
        Interlocked.Increment(ref scheduledReads);
    }
    internal void Release()
    {
        if (Interlocked.CompareExchange(ref state, 2, 1) == 1) Interlocked.Decrement(ref scheduledReads);
    }
}
