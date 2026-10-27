using System.ComponentModel;
using System.IO;
using System.Text;
using MadClean.Core;
using MadClean.Windows;
using Microsoft.Win32;

namespace MadClean.WindowsSmoke;

internal static partial class NativeFixtures
{
    private static void BoundedRegistrations(string fixture, SmokeReport report, CancellationToken token)
    {
        const string check = "bounded-registration-fixture";
        BoundedFixtureRegistry? owned = null;
        bool verified = false, removed = false;
        try
        {
            owned = new BoundedFixtureRegistry(fixture);
            using var key = Registry.CurrentUser.OpenSubKey(owned.RegistryBase + "\\BoundedMetadata", writable: true)
                ?? throw new SmokeAssertionException("bounded-fixture-key");
            void Check() => token.ThrowIfCancellationRequested();
            void Write(string field, uint type, byte[] bytes) => owned.Write(key, field, type, bytes);
            void Text(uint type, byte[] bytes, RegistrationMetadataState expected, string? value)
            {
                Write("DisplayName", type, bytes);
                var result = BoundedRegistrationMetadata.ReadText(key.Handle, "DisplayName", Check, token);
                if (result.State != expected || result.Value != value || result.NativeError != 0)
                    throw new SmokeAssertionException("bounded-text-state");
            }
            Text(1, Encoding.Unicode.GetBytes("Owned literal metadata\0"), RegistrationMetadataState.Valid, "Owned literal metadata");
            Text(1, [0, 0], RegistrationMetadataState.Valid, "");
            string maximum = new('N', RegistrationMetadataDecoder.NameCharacters);
            Text(1, Encoding.Unicode.GetBytes(maximum + "\0"), RegistrationMetadataState.Valid, maximum);
            Text(1, Encoding.Unicode.GetBytes(maximum + "X\0"), RegistrationMetadataState.Invalid, null);
            Text(3, Encoding.Unicode.GetBytes("Wrong binary type\0"), RegistrationMetadataState.Invalid, null);
            Text(1, [65, 0, 0], RegistrationMetadataState.Invalid, null); // Odd byte count.
            Text(1, [65, 0], RegistrationMetadataState.Invalid, null); // Missing NUL.
            Text(1, [65, 0, 0, 0, 66, 0, 0, 0], RegistrationMetadataState.Invalid, null); // Embedded NUL.
            Text(1, [0, 216, 0, 0], RegistrationMetadataState.Invalid, null); // Unpaired high surrogate.
            const string literal = "%MADCLEAN_BOUNDED_FIXTURE_ONLY%\\literal";
            Text(2, Encoding.Unicode.GetBytes(literal + "\0"), RegistrationMetadataState.Valid, literal);
            string versionMax = new('V', RegistrationMetadataDecoder.VersionCharacters);
            Write("DisplayVersion", 1, Encoding.Unicode.GetBytes(versionMax + "\0"));
            var version = BoundedRegistrationMetadata.ReadText(key.Handle, "DisplayVersion", Check, token);
            if (version.State != RegistrationMetadataState.Valid || version.Value != versionMax)
                throw new SmokeAssertionException("bounded-field-cap");
            Write("DisplayVersion", 1, Encoding.Unicode.GetBytes(versionMax + "X\0"));
            if (BoundedRegistrationMetadata.ReadText(key.Handle, "DisplayVersion", Check, token).State != RegistrationMetadataState.Invalid)
                throw new SmokeAssertionException("bounded-field-over-cap");
            var missing = BoundedRegistrationMetadata.ReadText(key.Handle, "Publisher", Check, token);
            if (missing.State != RegistrationMetadataState.Missing || missing.Value != null || missing.NativeError != 2)
                throw new SmokeAssertionException("bounded-missing-text");

            foreach (var item in new (uint Type, byte[] Bytes, bool? Expected)[]
            {
                (4, [0, 0, 0, 0], false), (4, [1, 0, 0, 0], true), (4, [2, 0, 0, 0], null),
                (3, [1, 0, 0, 0], null), (4, [1, 0], null), (4, [1, 0, 0, 0, 0, 0, 0, 0], null)
            })
            {
                Write("NoRemove", item.Type, item.Bytes);
                var flag = BoundedRegistrationMetadata.ReadFlag(key.Handle, "NoRemove", Check, token);
                if (flag.State != (item.Expected.HasValue ? RegistrationMetadataState.Valid : RegistrationMetadataState.Invalid) ||
                    flag.Value != item.Expected || flag.NativeError != 0)
                    throw new SmokeAssertionException("bounded-flag-state");
            }
            var missingFlag = BoundedRegistrationMetadata.ReadFlag(key.Handle, "WindowsInstaller", Check, token);
            if (missingFlag.State != RegistrationMetadataState.Missing || missingFlag.Value != null || missingFlag.NativeError != 2)
                throw new SmokeAssertionException("bounded-missing-flag");

            // Inert developer bytes only, not an actual command. Wrong type and large size must
            // not matter to a name-presence query; production lpData=NULL is source-reviewed.
            Write("UninstallString", 3, new byte[32 * 1024]);
            var presence = BoundedRegistrationMetadata.ProbeUninstallerName(key.Handle, "UninstallString", Check);
            var absence = BoundedRegistrationMetadata.ProbeUninstallerName(key.Handle, "QuietUninstallString", Check);
            if (presence.Exists != true || presence.NativeError != 0 || absence.Exists != false || absence.NativeError != 2)
                throw new SmokeAssertionException("bounded-presence");
            bool textBlocked = false, flagBlocked = false, presenceBlocked = false;
            int rejectedCallbacks = 0;
            void RejectedCheck() => rejectedCallbacks++;
            try { BoundedRegistrationMetadata.ReadText(key.Handle, "UninstallString", RejectedCheck, token); }
            catch (ArgumentException) { textBlocked = true; }
            try { BoundedRegistrationMetadata.ReadFlag(key.Handle, "QuietUninstallString", RejectedCheck, token); }
            catch (ArgumentException) { flagBlocked = true; }
            try { BoundedRegistrationMetadata.ProbeUninstallerName(key.Handle, "DisplayName", RejectedCheck); }
            catch (ArgumentException) { presenceBlocked = true; }
            if (!textBlocked || !flagBlocked || !presenceBlocked || rejectedCallbacks != 0)
                throw new SmokeAssertionException("bounded-command-api-separation");

            var names = new HashSet<string>(StringComparer.Ordinal);
            for (uint index = 0; index < 3; index++)
            {
                var item = BoundedRegistrationMetadata.EnumerateKey(key.Handle, index, Check);
                if (index == 2)
                {
                    if (!item.End || item.Name != null || item.NativeError != 259)
                        throw new SmokeAssertionException("bounded-enumeration-end");
                }
                else if (item.End || item.Name == null || item.NativeError != 0 || !names.Add(item.Name))
                    throw new SmokeAssertionException("bounded-enumeration");
            }
            if (!names.SetEquals(["owned-child", new string('K', 255)]))
                throw new SmokeAssertionException("bounded-enumeration-name-cap");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            int observedCancellation = 0;
            Action[] probes =
            [
                () => { BoundedRegistrationMetadata.ReadText(key.Handle, "DisplayName", cancelled.Token.ThrowIfCancellationRequested, cancelled.Token); },
                () => { BoundedRegistrationMetadata.ReadFlag(key.Handle, "NoRemove", cancelled.Token.ThrowIfCancellationRequested, cancelled.Token); },
                () => { BoundedRegistrationMetadata.EnumerateKey(key.Handle, 0, cancelled.Token.ThrowIfCancellationRequested); },
                () => { BoundedRegistrationMetadata.ProbeUninstallerName(key.Handle, "UninstallString", cancelled.Token.ThrowIfCancellationRequested); }
            ];
            foreach (var probe in probes)
                try { probe(); } catch (OperationCanceledException) { observedCancellation++; }
            if (observedCancellation != probes.Length) throw new SmokeAssertionException("bounded-precall-cancellation");
            verified = true;
        }
        catch (SmokeAssertionException ex)
        { report.Fail(check, "Fixed owned registry metadata invariant " + ex.Code + " failed; no production metadata/action readiness is inferred."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or ArgumentException or System.Security.SecurityException or OperationCanceledException)
        { report.Unknown(check, $"Owned registry metadata fixture could not complete (code 0x{ex.HResult:X8}); native bounded metadata behavior remains unverified."); }
        finally
        {
            try { owned?.Dispose(); removed = owned != null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or System.Security.SecurityException)
            { report.Unknown("bounded-registration-teardown", $"Exact owned registry subtree teardown could not complete (code 0x{ex.HResult:X8}); no broader registry removal attempted."); }
        }
        if (verified && removed)
            report.Pass(check, "Owned registry fixture verified bounded literal/empty/max/over-cap text, malformed types/termination/UTF16, strict DWORD flags, missing values, 255-character key enumeration/end, pre-call cancellation and uninstall-presence/API separation. seededBytesBelow64KiB=true; ownChildCount=2; nativeEndCode=259; exactMarkerTeardownVerified=true. No names/paths/values/hashes exported; no actual ARP/startup/command metadata touched. Runtime presence checks do not instrument lpData: NULL-data/no-command-read and fixed-allocation claims additionally rely on reviewed helper source.");
        else if (verified)
            report.Unknown(check, "Owned metadata assertions completed, but exact marker-verified teardown did not complete; fixture is not a full pass.");
    }

    private sealed class BoundedFixtureRegistry : IDisposable
    {
        private const string MarkerName = "MadCleanFixtureOwner";
        private readonly byte[] ownerBytes;
        private bool created;
        private int writtenBytes;
        internal string RegistryBase { get; }

        internal BoundedFixtureRegistry(string fixture)
        {
            const string prefix = "windows-validation-fixtures-";
            string leaf = Path.GetFileName(fixture);
            if (!leaf.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(leaf[prefix.Length..], "N", out var id) || id == Guid.Empty)
                throw new SmokeAssertionException("bounded-fixture-folder");
            string owner = File.ReadAllText(Path.Combine(fixture, ".madclean-fixture-owner"));
            if (!Guid.TryParseExact(owner, "N", out var ownerId) || ownerId == Guid.Empty)
                throw new SmokeAssertionException("bounded-fixture-owner");
            ownerBytes = Encoding.Unicode.GetBytes(owner + "\0");
            RegistryBase = @"Software\MadClean.WindowsSmoke\" + id.ToString("N");
            using (var existing = Registry.CurrentUser.OpenSubKey(RegistryBase))
                if (existing != null) throw new SmokeAssertionException("bounded-fixture-existing-key");
            using (var root = Registry.CurrentUser.CreateSubKey(RegistryBase, writable: true))
            {
                int code = RegSetValueExW(root.Handle, MarkerName, 0, 1, ownerBytes, (uint)ownerBytes.Length);
                if (code != 0) throw new Win32Exception(code);
            }
            created = true;
            try
            {
                using var metadata = Registry.CurrentUser.CreateSubKey(RegistryBase + "\\BoundedMetadata", writable: true);
                using var first = metadata.CreateSubKey("owned-child");
                using var longest = metadata.CreateSubKey(new string('K', 255));
            }
            catch { Dispose(); throw; }
        }

        internal void Write(RegistryKey key, string field, uint type, byte[] bytes)
        {
            writtenBytes = checked(writtenBytes + bytes.Length);
            if (writtenBytes > 64 * 1024) throw new SmokeAssertionException("bounded-fixture-byte-budget");
            int code = RegSetValueExW(key.Handle, field, 0, type, bytes, (uint)bytes.Length);
            if (code != 0) throw new Win32Exception(code);
        }

        public void Dispose()
        {
            if (!created) return;
            using (var root = Registry.CurrentUser.OpenSubKey(RegistryBase))
            {
                if (root == null || root.SubKeyCount != 1 || root.ValueCount != 1)
                    throw new IOException();
                uint count = 0;
                int code = RegQueryValueExW(root.Handle, MarkerName, 0, out uint kind, null, ref count);
                if (code != 0 || kind != 1 || count != ownerBytes.Length) throw new IOException();
                var actual = new byte[ownerBytes.Length];
                code = RegQueryValueExW(root.Handle, MarkerName, 0, out kind, actual, ref count);
                if (code != 0 || kind != 1 || count != actual.Length || !actual.SequenceEqual(ownerBytes)) throw new IOException();
                var child = BoundedRegistrationMetadata.EnumerateKey(root.Handle, 0, () => { });
                if (child.End || child.Name != "BoundedMetadata" || child.NativeError != 0) throw new IOException();
            }
            Registry.CurrentUser.DeleteSubKeyTree(RegistryBase, throwOnMissingSubKey: false);
            using var remains = Registry.CurrentUser.OpenSubKey(RegistryBase);
            if (remains != null) throw new IOException();
            created = false;
        }
    }
}
