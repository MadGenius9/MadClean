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
        bool nativeUnterminatedEstablished = false, normalizedUnterminatedSeed = false;
        string? textFailureDetail = null;
        try
        {
            owned = new BoundedFixtureRegistry(fixture);
            using var key = Registry.CurrentUser.OpenSubKey(owned.RegistryBase + "\\BoundedMetadata", writable: true)
                ?? throw new SmokeAssertionException("bounded-fixture-key");
            void Check() => token.ThrowIfCancellationRequested();
            void Write(string field, uint type, byte[] bytes) => owned.Write(key, field, type, bytes);
            void Text(string caseCode, uint type, byte[] bytes, RegistrationMetadataState expected, string? value)
            {
                Write("DisplayName", type, bytes);
                var result = BoundedRegistrationMetadata.ReadText(key.Handle, "DisplayName", Check, token);
                if (result.State != expected || result.Value != value || result.NativeError != 0)
                {
                    uint storedCount = 0;
                    int storedCode = RegQueryValueExW(key.Handle, "DisplayName", 0, out uint storedType, null, ref storedCount);
                    // Only fixed developer case labels, enums, numeric ABI metadata and a match
                    // boolean. Never include the returned/stored text or registry pathname.
                    textFailureDetail = $"Owned text case={caseCode}; expectedState={expected}; observedState={result.State}; nativeCode={result.NativeError}; valueMatch={(result.Value == value ? "true" : "false")}; requestedType={type}; requestedBytes={bytes.Length}; storedProbeCode={storedCode}; storedType={storedType}; storedBytes={storedCount}. No text values or paths exported.";
                    throw new SmokeAssertionException("bounded-text-" + caseCode);
                }
            }
            Text("literal", 1, Encoding.Unicode.GetBytes("Owned literal metadata\0"), RegistrationMetadataState.Valid, "Owned literal metadata");
            Text("empty", 1, [0, 0], RegistrationMetadataState.Valid, "");
            string maximum = new('N', RegistrationMetadataDecoder.NameCharacters);
            Text("maximum", 1, Encoding.Unicode.GetBytes(maximum + "\0"), RegistrationMetadataState.Valid, maximum);
            Text("over-cap", 1, Encoding.Unicode.GetBytes(maximum + "X\0"), RegistrationMetadataState.Invalid, null);
            Text("binary-type", 3, Encoding.Unicode.GetBytes("Wrong binary type\0"), RegistrationMetadataState.Invalid, null);
            Text("odd-bytes", 1, [65, 0, 0], RegistrationMetadataState.Invalid, null);
            // RegSetValueExW requires terminated input; malformed fixture writes need not retain
            // the requested representation. Observe only this owned value's actual stored shape.
            // RegQueryValueExW itself does not guarantee termination: production stays strict.
            // https://learn.microsoft.com/windows/win32/api/winreg/nf-winreg-regsetvalueexw
            // https://learn.microsoft.com/windows/win32/api/winreg/nf-winreg-regqueryvalueexw
            byte[] unterminatedSeed = [65, 0], stored = new byte[8];
            try
            {
                Write("DisplayName", 1, unterminatedSeed);
                uint storedCount = 0;
                int shapeCode = RegQueryValueExW(key.Handle, "DisplayName", 0, out uint storedType, null, ref storedCount);
                if (shapeCode != 0 || storedType != 1 || storedCount is not (2 or 4))
                {
                    textFailureDetail = $"Owned unterminated seed shape probeCode={shapeCode}; storedType={storedType}; storedBytes={storedCount}; recognizedShape=false. No values or paths exported.";
                    throw new SmokeAssertionException("bounded-unterminated-seed-size");
                }
                uint received = (uint)stored.Length;
                int readCode = RegQueryValueExW(key.Handle, "DisplayName", 0, out uint receivedType, stored, ref received);
                bool shape = readCode == 0 && receivedType == storedType && received == storedCount &&
                    stored[0] == 65 && stored[1] == 0 && (storedCount == 2 || stored[2] == 0 && stored[3] == 0);
                if (!shape)
                {
                    textFailureDetail = $"Owned unterminated seed fixed-buffer readCode={readCode}; receivedType={receivedType}; receivedBytes={received}; queriedBytes={storedCount}; recognizedShape=false. No values or paths exported.";
                    throw new SmokeAssertionException("bounded-unterminated-seed-shape");
                }
                nativeUnterminatedEstablished = storedCount == 2;
                normalizedUnterminatedSeed = storedCount == 4;
                var observed = BoundedRegistrationMetadata.ReadText(key.Handle, "DisplayName", Check, token);
                var expected = nativeUnterminatedEstablished ? RegistrationMetadataState.Invalid : RegistrationMetadataState.Valid;
                bool match = observed.Value == (nativeUnterminatedEstablished ? null : "A");
                if (observed.State != expected || observed.NativeError != 0 || !match)
                {
                    textFailureDetail = $"Owned text case=unterminated-observed-shape; expectedState={expected}; observedState={observed.State}; nativeCode={observed.NativeError}; valueMatch={match}; nativeUnterminatedEstablished={nativeUnterminatedEstablished}; normalizedSeed={normalizedUnterminatedSeed}. No values or paths exported.";
                    throw new SmokeAssertionException("bounded-unterminated-reader");
                }
            }
            finally { Array.Clear(unterminatedSeed); Array.Clear(stored); }
            Text("embedded-nul", 1, [65, 0, 0, 0, 66, 0, 0, 0], RegistrationMetadataState.Invalid, null);
            Text("unpaired-surrogate", 1, [0, 216, 0, 0], RegistrationMetadataState.Invalid, null);
            const string literal = "%MADCLEAN_BOUNDED_FIXTURE_ONLY%\\literal";
            Text("literal-expand", 2, Encoding.Unicode.GetBytes(literal + "\0"), RegistrationMetadataState.Valid, literal);
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
        { report.Fail(check, textFailureDetail ?? "Fixed owned registry metadata invariant " + ex.Code + " failed; no production metadata/action readiness is inferred."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or ArgumentException or System.Security.SecurityException or OperationCanceledException)
        { report.Unknown(check, $"Owned registry metadata fixture could not complete (code 0x{ex.HResult:X8}); native bounded metadata behavior remains unverified."); }
        finally
        {
            try { owned?.Dispose(); removed = owned != null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or System.Security.SecurityException)
            { report.Unknown("bounded-registration-teardown", $"Exact owned registry subtree teardown could not complete (code 0x{ex.HResult:X8}); no broader registry removal attempted."); }
        }
        if (verified && removed)
            report.Pass(check, "Owned registry fixture verified bounded literal/empty/max/over-cap text, wrong types/odd bytes/embedded NUL/invalid UTF16, DWORD flags, missing values, 255-character key enumeration/end, pre-call cancellation and presence/API separation. " +
                $"nativeUnterminatedEstablished={(nativeUnterminatedEstablished ? "true" : "false")}; normalizedUnterminatedSeed={(normalizedUnterminatedSeed ? "true" : "false")}; " +
                (normalizedUnterminatedSeed ? "The owned unterminated request was stored as an exact terminated representation; native unterminated rejection remains unverified. " : "Exact stored unterminated bytes were rejected. ") +
                "seededBytesBelow64KiB=true; ownChildCount=2; nativeEndCode=259; exactMarkerTeardownVerified=true. No names/paths/values/hashes exported; no actual ARP/startup/command metadata touched. NULL-data/no-command-read and fixed-allocation claims also rely on helper source review, not native-call instrumentation.");
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
