using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using MadClean.Core;
using MadClean.Windows;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace MadClean.WindowsSmoke;

internal static partial class NativeFixtures
{
    private static readonly string[] StartupCheckNames =
    [
        "startup-fixture-inventory", "startup-fixture-preview", "startup-fixture-mutation-gate", "startup-fixture-scope"
    ];

    private static void StartupReadOnly(string fixture, SmokeReport report, CancellationToken token)
    {
        StartupFixtureRegistry? owned = null;
        var recorded = new HashSet<string>(StringComparer.Ordinal);
        void Passed(string name, string detail) { report.Pass(name, detail); recorded.Add(name); }
        try
        {
            owned = new StartupFixtureRegistry(fixture);
            var run = new StartupRegistrationReference(StartupRegistrationSource.CurrentUserRun, "FixtureRunOnly");
            var once = new StartupRegistrationReference(StartupRegistrationSource.CurrentUserRunOnce, "FixtureRunOnceOnly");
            byte[] runBytes = Encoding.Unicode.GetBytes("never-execute-this-fixture\0");
            byte[] onceBytes = Encoding.Unicode.GetBytes("%MADCLEAN_FIXTURE_ONLY%\\never-execute-this-fixture\0");
            owned.Write(run, 1, runBytes); owned.Write(once, 2, onceBytes);
            owned.WriteJournalCanary();
            var before = owned.Snapshot();
            using var actions = StartupRegistrationActions.ForFixture(owned.RegistryBase, owned.BackupDirectory);
            var inventory = Query(ct => actions.InventoryAsync(ct), token);
            if (inventory.State != CheckState.Available)
                throw new StartupFixtureInconclusive("Read-only registration inventory returned " + inventory.State + ". " + BoundedReason(inventory.Message, 420));
            if (inventory.Items.Count != 2 || !inventory.Items.Contains(run) || !inventory.Items.Contains(once))
                throw new SmokeAssertionException("startup-fixture-inventory");
            Passed("startup-fixture-inventory", "The isolated owned HKCU fixture Run/RunOnce names were inventoried. No actual Windows startup source or command was enumerated by this fixture check.");

            StartupActionPreview? firstPreview = null;
            foreach (var item in new[] { (Reference: run, Kind: "REG_SZ", Bytes: runBytes.Length), (Reference: once, Kind: "REG_EXPAND_SZ", Bytes: onceBytes.Length) })
            {
                var result = Query(ct => actions.PreviewDisableAsync(item.Reference, ct), token);
                if (result.State != CheckState.Available)
                    throw new StartupFixtureInconclusive("Read-only preview returned " + result.State + ". " + BoundedReason(result.Message, 420));
                if (result.Items.Count != 1) throw new SmokeAssertionException("startup-fixture-preview-count");
                var preview = result.Items[0];
                if (preview.Reference != item.Reference || preview.ValueKind != item.Kind || preview.ByteCount != item.Bytes ||
                    !StartupActionPolicy.IsOpaqueId(preview.Token) || string.IsNullOrWhiteSpace(preview.ExactEffect) ||
                    string.IsNullOrWhiteSpace(preview.Permissions) || string.IsNullOrWhiteSpace(preview.RecoveryLimits))
                    throw new SmokeAssertionException("startup-fixture-preview-metadata");
                string publicText = preview.ExactEffect + preview.Permissions + preview.RecoveryLimits;
                if (publicText.Contains("never-execute-this-fixture", StringComparison.Ordinal) || publicText.Contains("MADCLEAN_FIXTURE_ONLY", StringComparison.Ordinal))
                    throw new SmokeAssertionException("startup-fixture-command-exposure");
                firstPreview ??= preview;
            }
            owned.AssertUnchanged(before);
            Passed("startup-fixture-preview", "Read-only previews preserve exact REG_SZ/REG_EXPAND_SZ kind and raw NUL-terminated byte count without exposing commands, writing a backup or changing registry data. This is not a disable/restore proof.");

            var disabled = Query(ct => actions.DisableAsync(firstPreview!.Token, ct), token);
            var restored = Query(ct => actions.RestoreAsync(Guid.NewGuid().ToString("N"), ct), token);
            if (disabled.Outcome != StartupActionOutcome.Unavailable || restored.Outcome != StartupActionOutcome.Unavailable ||
                disabled.BackupId != null || restored.BackupId != null)
                throw new SmokeAssertionException("startup-fixture-unconditional-gate");
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                var stopped = actions.DisableAsync(firstPreview!.Token, cancelled.Token).GetAwaiter().GetResult();
                if (stopped.Outcome != StartupActionOutcome.CancelledBeforeCommit || stopped.BackupId != null)
                    throw new SmokeAssertionException("startup-fixture-cancelled-gate");
            }
            var backups = Query(ct => actions.ListBackupsAsync(ct), token);
            if (backups.State != CheckState.Available || backups.Items.Count != 0)
                throw new StartupFixtureInconclusive("Empty owned recovery inventory returned " + backups.State + "; no recoverability or restoration is claimed.");
            owned.AssertUnchanged(before);
            Passed("startup-fixture-mutation-gate", "Both mutation APIs remain Unavailable even for a valid fixture preview/explicit request; pre-cancellation reports CancelledBeforeCommit. Source kinds/bytes, journal and files are unchanged. Atomic disable/restore and DPAPI durability remain unverified and unavailable.");

            bool wrongPrefixBlocked = RejectFixtureScope(@"Software\MadClean.WindowsSmoke-Outside\" + owned.FixtureId, owned.BackupDirectory);
            bool mismatchedGuidBlocked = RejectFixtureScope(StartupActionPolicy.FixtureRegistryPrefix + Guid.NewGuid().ToString("N"), owned.BackupDirectory);
            bool wrongBackupBlocked = RejectFixtureScope(owned.RegistryBase, Path.Combine(fixture, "not-startup-backups"));
            if (!wrongPrefixBlocked || !mismatchedGuidBlocked || !wrongBackupBlocked || Directory.Exists(Path.Combine(fixture, "not-startup-backups")))
                throw new SmokeAssertionException("startup-fixture-scope");
            owned.AssertUnchanged(before);
            Passed("startup-fixture-scope", "The fixture factory rejects a wrong prefix, mismatched ownership GUID and out-of-topology backup directory without creating that directory or modifying owned data. Only isolated current-user fixture sources were used.");
        }
        catch (SmokeAssertionException ex)
        {
            string name = StartupCheckNames.FirstOrDefault(candidate => !recorded.Contains(candidate)) ?? "startup-fixture-mutation-gate";
            report.Fail(name, "Fixed startup fixture invariant " + ex.Code + " failed; no production startup-action validation is claimed.");
            recorded.Add(name);
        }
        catch (StartupFixtureInconclusive ex)
        {
            string name = StartupCheckNames.FirstOrDefault(candidate => !recorded.Contains(candidate)) ?? "startup-fixture-mutation-gate";
            report.Unknown(name, ex.SafeDetail); recorded.Add(name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or ArgumentException or System.Security.SecurityException or OperationCanceledException or TimeoutException)
        {
            string name = StartupCheckNames.FirstOrDefault(candidate => !recorded.Contains(candidate)) ?? "startup-fixture-mutation-gate";
            report.Unknown(name, $"The isolated read-only startup fixture could not finish: {SmokeAssertionException.SafeContext(ex)} (0x{ex.HResult:X8}). No mutation/recovery proof is claimed.");
            recorded.Add(name);
        }
        finally
        {
            foreach (string name in StartupCheckNames.Where(name => !recorded.Contains(name)))
                report.Unknown(name, "This read-only fixture check was not reached because a prerequisite was inconclusive or failed; it is not a pass.");
            try
            {
                owned?.Dispose();
                if (owned != null) report.Pass("startup-fixture-teardown", "Only the ownership-marked disposable HKCU fixture subtree was removed; actual startup sources were never modified.");
                else report.Unknown("startup-fixture-teardown", "No owned registry fixture was fully initialized; no broader registry cleanup was attempted.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or System.Security.SecurityException)
            { report.Unknown("startup-fixture-teardown", $"The owned fixture subtree could not be removed (0x{ex.HResult:X8}); no broader registry deletion was attempted."); }
        }
    }

    private static bool RejectFixtureScope(string registryBase, string backup)
    {
        try { using var rejected = StartupRegistrationActions.ForFixture(registryBase, backup); return false; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException) { return true; }
    }

    private sealed class StartupFixtureInconclusive(string safeDetail) : Exception("A read-only startup fixture prerequisite is inconclusive.")
    { internal string SafeDetail { get; } = safeDetail; }

    private sealed record StartupFixtureSnapshot(string[] SourceNames, uint[] SourceKinds, byte[][] SourceData, string JournalDigest, string[] Files);

    private sealed class StartupFixtureRegistry : IDisposable
    {
        private const string OwnerName = "MadCleanFixtureOwner";
        private readonly string owner;
        private bool created;
        internal string FixtureId { get; }
        internal string RegistryBase { get; }
        internal string BackupDirectory { get; }
        internal StartupFixtureRegistry(string fixture)
        {
            const string prefix = "windows-validation-fixtures-";
            string name = Path.GetFileName(fixture);
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[prefix.Length..], "N", out var id) || id == Guid.Empty)
                throw new SmokeAssertionException("startup-fixture-folder");
            FixtureId = id.ToString("N"); RegistryBase = StartupActionPolicy.FixtureRegistryPrefix + FixtureId;
            owner = File.ReadAllText(Path.Combine(fixture, ".madclean-fixture-owner"));
            if (!StartupActionPolicy.IsOpaqueId(owner)) throw new SmokeAssertionException("startup-fixture-marker");
            BackupDirectory = Path.Combine(fixture, "startup-backups"); Directory.CreateDirectory(BackupDirectory);
            using (var existing = Registry.CurrentUser.OpenSubKey(RegistryBase))
                if (existing != null) throw new SmokeAssertionException("startup-fixture-existing-key");
            using (var root = Registry.CurrentUser.CreateSubKey(RegistryBase, writable: true))
                root.SetValue(OwnerName, owner, RegistryValueKind.String);
            created = true;
            try
            {
                using var run = Registry.CurrentUser.CreateSubKey(RegistryBase + @"\Run", writable: true);
                using var once = Registry.CurrentUser.CreateSubKey(RegistryBase + @"\RunOnce", writable: true);
                using var journal = Registry.CurrentUser.CreateSubKey(RegistryBase + @"\Journal", writable: true);
            }
            catch { Dispose(); throw; }
        }

        internal void Write(StartupRegistrationReference reference, uint kind, byte[] bytes)
        {
            string? child = StartupActionPolicy.SourceSubkey(reference.Source);
            if (child == null || !StartupActionPolicy.IsValidReference(reference)) throw new SmokeAssertionException("startup-fixture-reference");
            using var key = Registry.CurrentUser.OpenSubKey(RegistryBase + "\\" + child, writable: true) ?? throw new SmokeAssertionException("startup-fixture-key");
            int code = RegSetValueExW(key.Handle, reference.ValueName, 0, kind, bytes, checked((uint)bytes.Length));
            if (code != 0) throw new Win32Exception(code);
        }

        internal void WriteJournalCanary()
        {
            using var journal = Registry.CurrentUser.OpenSubKey(RegistryBase + @"\Journal", writable: true) ?? throw new SmokeAssertionException("startup-fixture-journal");
            journal.SetValue("ReadOnlyFixtureCanary", new byte[] { 0x41, 0x52, 0x4F }, RegistryValueKind.Binary);
        }

        internal StartupFixtureSnapshot Snapshot()
        {
            var names = new List<string>(); var kinds = new List<uint>(); var data = new List<byte[]>();
            foreach (string child in new[] { "Run", "RunOnce" })
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryBase + "\\" + child) ?? throw new SmokeAssertionException("startup-fixture-read-source");
                foreach (string value in key.GetValueNames().Order(StringComparer.Ordinal))
                {
                    var raw = ReadRaw(key.Handle, value);
                    names.Add(child + "\\" + value); kinds.Add(raw.Kind); data.Add(raw.Bytes);
                }
            }
            using var journal = Registry.CurrentUser.OpenSubKey(RegistryBase + @"\Journal") ?? throw new SmokeAssertionException("startup-fixture-read-journal");
            if (journal.SubKeyCount != 0 || journal.GetValueNames().Length != 1) throw new SmokeAssertionException("startup-fixture-journal-changed");
            var canary = ReadRaw(journal.Handle, "ReadOnlyFixtureCanary");
            string journalDigest = canary.Kind + ":" + Convert.ToHexString(SHA256.HashData(canary.Bytes));
            string[] files = Directory.EnumerateFileSystemEntries(BackupDirectory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
            return new(names.ToArray(), kinds.ToArray(), data.ToArray(), journalDigest, files);
        }

        internal void AssertUnchanged(StartupFixtureSnapshot expected)
        {
            var current = Snapshot();
            if (!current.SourceNames.SequenceEqual(expected.SourceNames) || !current.SourceKinds.SequenceEqual(expected.SourceKinds) ||
                !current.SourceData.Zip(expected.SourceData).All(pair => pair.First.AsSpan().SequenceEqual(pair.Second)) ||
                current.SourceData.Length != expected.SourceData.Length || current.JournalDigest != expected.JournalDigest || !current.Files.SequenceEqual(expected.Files))
                throw new SmokeAssertionException("startup-fixture-readonly-change");
        }

        public void Dispose()
        {
            if (!created) return;
            using (var root = Registry.CurrentUser.OpenSubKey(RegistryBase))
                if (root?.GetValue(OwnerName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string != owner)
                    throw new IOException("Owned registry fixture marker changed.");
            Registry.CurrentUser.DeleteSubKeyTree(RegistryBase, throwOnMissingSubKey: false);
            using var remains = Registry.CurrentUser.OpenSubKey(RegistryBase);
            if (remains != null) throw new IOException("Owned registry fixture remains.");
            created = false;
        }

        private static (uint Kind, byte[] Bytes) ReadRaw(SafeRegistryHandle key, string name)
        {
            uint size = 0;
            int code = RegQueryValueExW(key, name, 0, out uint kind, null, ref size);
            if (code != 0 || size > 4096) throw new Win32Exception(code == 0 ? 234 : code);
            var bytes = new byte[checked((int)size)]; uint actual = size;
            code = RegQueryValueExW(key, name, 0, out kind, bytes, ref actual);
            if (code != 0 || actual != size) throw new Win32Exception(code == 0 ? 234 : code);
            return (kind, bytes);
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegSetValueExW(SafeRegistryHandle key, string name, uint reserved, uint kind, byte[] data, uint length);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegQueryValueExW(SafeRegistryHandle key, string name, nint reserved, out uint kind, byte[]? data, ref uint length);
}
