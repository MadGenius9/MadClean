using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.WindowsSmoke;

internal static partial class NativeFixtures
{
    public static void Run(string repository, SmokeReport report, CancellationToken token)
    {
        string fixture = Path.Combine(repository, "artifacts", "windows-validation-fixtures-" + Guid.NewGuid().ToString("N"));
        string marker = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(fixture);
        File.WriteAllText(Path.Combine(fixture, ".madclean-fixture-owner"), marker);
        bool transactionProbeTeardownCompleted = true;
        try
        {
            var duplicates = Duplicates(fixture, report, token);
            DuplicatePairReviewFixture(fixture, duplicates, report, token);
            DiskIdentity(fixture, report, token);
            LeftoverContent(fixture, report, token);
            Shortcuts(fixture, report, token);
            Diagnostics(report, token);
            DefenderStatus(report, token);
            var registrations = InstalledRegistrations(report, token);
            MsiRegistrations(registrations, report, token);
            PackagedApplications(report, token);
            BoundedRegistrations(fixture, report, token);
            WerMetadata(report, token);
            HardwareMetadata(report, token);
            StartupReadOnly(fixture, report, token);
            SystemToolEmbeddedVersions(report, token);
            SystemToolIdentities(report, token);
            SystemToolGate(report, token);
            // The isolated transaction worker alone owns its registry/transaction cleanup.
            // Never remove its filesystem ownership marker while its outcome is uncertain.
            transactionProbeTeardownCompleted = false;
            transactionProbeTeardownCompleted = StartupTransactions(fixture, report, token);
            if (transactionProbeTeardownCompleted && !StartupTransactionProbe.HasPendingNativeProbe)
            {
                transactionProbeTeardownCompleted = false;
                transactionProbeTeardownCompleted = StartupTransactionInterference(fixture, report, token);
            }
            else report.Unknown("startup-transaction-interference-probe", "The earlier isolated transaction probe did not establish safe teardown; the interference probe was not started and the entire owned fixture tree is retained.");
        }
        finally
        {
            if (StartupTransactionProbe.HasPendingNativeProbe || !transactionProbeTeardownCompleted)
            {
                report.Unknown("fixture-teardown", "The isolated transaction probe is pending or did not establish registry teardown. The entire owned fixture tree and ownership marker were retained; no filesystem deletion or broader cleanup was attempted.");
            }
            else try
            {
                // Teardown is restricted to exactly this harness-created tree. Production scanners never delete.
                if (Directory.GetParent(fixture)?.FullName != Path.Combine(repository, "artifacts") ||
                    !Path.GetFileName(fixture).StartsWith("windows-validation-fixtures-", StringComparison.Ordinal) ||
                    File.ReadAllText(Path.Combine(fixture, ".madclean-fixture-owner")) != marker || HasReparseEntry(fixture))
                    throw new IOException("Fixture ownership could not be revalidated.");
                Directory.Delete(fixture, recursive: true);
                report.Pass("fixture-teardown", "Only the owned development fixture tree was removed; no application or personal-file cleanup action was exercised.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { report.Unknown("fixture-teardown", $"Owned fixture cleanup could not complete (0x{ex.HResult:X8}); no broader deletion was attempted."); }
        }
    }

    private static CheckResult<DuplicateReport>? Duplicates(string fixture, SmokeReport report, CancellationToken token)
    {
        string scope = Path.Combine(fixture, "duplicates"); Directory.CreateDirectory(scope);
        string first = Path.Combine(scope, "different-name-one.txt"), second = Path.Combine(scope, "different-name-two.txt");
        string different = Path.Combine(scope, "same-size-different.txt"), alias = Path.Combine(scope, "same-file-alias.txt");
        byte[] content = Encoding.UTF8.GetBytes("MadClean disposable fixture A\r\n");
        File.WriteAllBytes(first, content); File.WriteAllBytes(second, content);
        byte[] other = (byte[])content.Clone(); other[0] = (byte)'X'; File.WriteAllBytes(different, other);
        bool linked = CreateHardLinkW(alias, first, 0);
        try
        {
            var result = Query(ct => new WindowsDuplicateInventory().ScanAsync(scope, ct), token);
            var data = SingleReport(result, "duplicate-provider", report);
            if (data == null)
            {
                report.Unknown("duplicate-fixture-context", "App result: " + BoundedReason(result.Message, 600));
                return null;
            }
            var a = data.Entries.FirstOrDefault(entry => PathEquals(entry.Path, first));
            var b = data.Entries.FirstOrDefault(entry => PathEquals(entry.Path, second));
            bool falseMatch = data.Entries.Any(entry => PathEquals(entry.Path, different) && (entry.GroupId == a?.GroupId || entry.GroupId == b?.GroupId));
            if (falseMatch)
            { report.Fail("duplicate-content-fixtures", "Same-size but different bytes were included with verified matching fixture contents."); return null; }
            if (a == null || b == null)
            {
                if (result.State == CheckState.Available) report.Fail("duplicate-content-fixtures", "A completed fixture inventory omitted the two independent byte-identical files.");
                else report.Unknown("duplicate-content-fixtures", $"Fixture content equality was not established; provider state {result.State}, {data.Issues.Count} bounded issue(s). No duplicate-proof claim. " + SafeFixtureContext(result.Message, data.Issues));
                return null;
            }
            if (a.GroupId != b.GroupId || string.IsNullOrWhiteSpace(a.Identity) || string.IsNullOrWhiteSpace(b.Identity) || a.Identity == b.Identity || a.Bytes != content.Length || b.Bytes != content.Length)
            { report.Fail("duplicate-content-fixtures", "Returned fixture group/identity/length evidence contradicts independent byte-identical files."); return null; }
            report.Pass("duplicate-content-fixtures", $"Native fixture observations group equal bytes across different names, retain independent identities and exclude a same-size different-content file. Provider state {result.State}; this is scoped fixture evidence only.");
            if (!linked)
            { report.Unknown("duplicate-hardlink-fixture", "The native hard-link fixture could not be created on this runner; identity/alias behavior is not verified here."); return result; }
            var link = data.Entries.FirstOrDefault(entry => PathEquals(entry.Path, alias));
            if (link == null)
            {
                if (result.State == CheckState.Available) report.Fail("duplicate-hardlink-fixture", "A completed fixture inventory omitted the known same-file alias observation.");
                else report.Unknown("duplicate-hardlink-fixture", "Alias creation succeeded, but partial provider evidence did not expose the same-file relationship. " + SafeFixtureContext(result.Message, data.Issues));
            }
            else if (link.Identity != a.Identity || link.GroupId != a.GroupId || !(link.Relationship.Contains("alias", StringComparison.OrdinalIgnoreCase) || a.Relationship.Contains("alias", StringComparison.OrdinalIgnoreCase)))
                report.Fail("duplicate-hardlink-fixture", "A known hard-link alias was reported with inconsistent file identity or without an alias relationship.");
            else report.Pass("duplicate-hardlink-fixture", "An actual native hard link shares the original file identity and group and is explicitly labeled an alias, not an independent copy.");
            return result;
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { report.Unknown("duplicate-provider", $"The bounded native fixture query could not finish (0x{ex.HResult:X8}); Windows content/identity verification is not claimed."); return null; }
    }

    private static void Shortcuts(string fixture, SmokeReport report, CancellationToken token)
    {
        string scope = Path.Combine(fixture, "shortcuts"), targets = Path.Combine(fixture, "targets");
        Directory.CreateDirectory(scope); Directory.CreateDirectory(targets);
        string existing = Path.Combine(targets, "existing.txt"), missing = Path.Combine(targets, "missing.txt");
        File.WriteAllText(existing, "MadClean disposable shortcut fixture; never opened or executed.");
        string existingLink = Path.Combine(scope, "existing.lnk"), missingLink = Path.Combine(scope, "missing.lnk");
        try
        {
            CreateShortcut(existingLink, existing); CreateShortcut(missingLink, missing);
            var result = Query(ct => new WindowsShortcutInventory().ScanAsync(scope, ct), token);
            var data = SingleReport(result, "shortcut-provider", report);
            if (data == null)
            {
                report.Unknown("shortcut-selected-recheck-fixture", "The prior actual shortcut inventory did not establish a readable fixture snapshot; no selected recheck or external-restore fixture was started.");
                return;
            }
            CheckShortcut(data, existingLink, ShortcutTargetState.TargetExists, "shortcut-existing-local-fixture", result.State, report);
            CheckShortcut(data, missingLink, ShortcutTargetState.LocalTargetMissing, "shortcut-missing-local-fixture", result.State, report);
            if (!File.Exists(existing) || File.Exists(missing) || !File.Exists(existingLink) || !File.Exists(missingLink))
            {
                report.Fail("shortcut-no-mutation", "Read-only discovery changed fixture presence unexpectedly.");
                report.Unknown("shortcut-selected-recheck-fixture", "The earlier unchanged-fixture boundary failed; no recheck or external-restore simulation started.");
                return;
            }
            report.Pass("shortcut-no-mutation", "Both shortcut entries and the existing target remain, and the absent target stays absent. No link or target was launched.");
            ShortcutSelectedRecheckFixture(scope, missingLink, missing, result, report, token);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException or COMException)
        {
            report.Unknown("shortcut-provider", $"The bounded native fixture query could not finish (0x{ex.HResult:X8}); local-target classification is not verified here.");
            report.Unknown("shortcut-selected-recheck-fixture", "The preceding owned shortcut setup/inventory did not complete; no selected recheck proof is claimed.");
        }
    }

    private static void CheckShortcut(ShortcutReport data, string path, ShortcutTargetState expected, string name, CheckState providerState, SmokeReport report)
    {
        var entry = data.Entries.FirstOrDefault(item => PathEquals(item.ShortcutPath, path));
        if (entry == null)
        {
            if (providerState == CheckState.Available) report.Fail(name, "A completed inventory omitted a supported local shortcut fixture.");
            else report.Unknown(name, $"No supported observation was returned; provider state {providerState}.");
        }
        else if (entry.State == expected && !string.IsNullOrWhiteSpace(entry.Source) && !string.IsNullOrWhiteSpace(entry.Reason))
            report.Pass(name, $"The native fixture returned {expected} with a reason/source; this is target availability, not proof a shortcut can launch or is obsolete.");
        else if (entry.State is ShortcutTargetState.Unknown or ShortcutTargetState.Unsupported or ShortcutTargetState.UnresolvedMetadata)
            report.Unknown(name, $"The fixture remains {entry.State}; no missing/broken or activation claim is inferred. App reason: " + BoundedReason(entry.Reason, 500));
        else report.Fail(name, $"The known local fixture was classified {entry.State} instead of {expected}.");
    }

    private static void SystemToolGate(SmokeReport report, CancellationToken token)
    {
        // Never Prepare a tool identity or start servicing merely to test the hard execution gate.
        ISystemToolActions actions = new SystemToolActionsProvider();
        if (actions.ProductionExecutionValidated)
        {
            report.Fail("system-tool-execution-gate", "The production execution gate unexpectedly reports enabled; no launch request was called.");
            return;
        }
        var request = new SystemToolLaunchRequest(Guid.NewGuid(), SystemToolOperation.DismRestoreHealth,
            new string('0', 64), UserSelectedOperation: true, EffectsAndElevationAcknowledged: true);
        var result = Query(ct => actions.LaunchAsync(request, ct), token);
        if (result.State != SystemToolRunState.Unavailable || result.ElevationRequested || result.ProcessId != null || result.ClientExitCode != null)
            report.Fail("system-tool-execution-gate", "The dummy request did not remain an unavailable, non-elevated, process-free result.");
        else report.Pass("system-tool-execution-gate", "The hard gate rejects an acknowledged dummy repair descriptor with Unavailable/no elevation/no PID. The harness did not Prepare a tool identity, start servicing or request UAC; actual execution remains unverified and disabled.");
    }

    private static void Diagnostics(SmokeReport report, CancellationToken token)
    {
        try
        {
            var result = Query(ct => new WindowsDiagnosticsInventory().ReadAsync(1, ct), token);
            var data = SingleReport(result, "diagnostics-provider", report);
            if (data == null) return;
            if (data.StartUtc >= data.EndUtc || data.EndUtc - data.StartUtc > TimeSpan.FromDays(2) || data.Coverage.Count == 0 ||
                data.Findings.Any(finding => finding.Channel is not ("Application" or "System") || finding.Count <= 0 || finding.FirstUtc < data.StartUtc || finding.LastUtc >= data.EndUtc || finding.FirstUtc > finding.LastUtc ||
                    string.IsNullOrWhiteSpace(finding.Uncertainty) || string.IsNullOrWhiteSpace(finding.Impact)))
            { report.Fail("diagnostics-metadata-contract", "Native evidence violates the selected time/channel/recurrence/uncertainty contract."); return; }
            bool readableSource = data.Findings.Count > 0 || data.Coverage.Any(note =>
                note.StartsWith("Application: queried;", StringComparison.Ordinal) || note.StartsWith("System: queried;", StringComparison.Ordinal));
            if (!readableSource)
            {
                report.Unknown("diagnostics-metadata-contract", $"Provider state {result.State} did not establish a successfully read source; coverage remains inconclusive.");
                return;
            }
            // Exercise the new summary with the already-read real provider result, without a
            // second query, UI data injection, event contents or incident correlation.
            var summary = DiagnosticRecurrenceSummaryBuilder.Build(result, DateTimeOffset.UtcNow, token);
            if (summary.State != result.State || summary.StartUtc != data.StartUtc || summary.EndUtc != data.EndUtc ||
                summary.ObservedRecords != data.Findings.Sum(finding => (long)finding.Count) ||
                summary.ObservedGroups != data.Findings.Count ||
                summary.RepeatedGroups != data.Findings.Count(finding => finding.Count >= 2) ||
                summary.RecordsInRepeatedGroups != data.Findings.Where(finding => finding.Count >= 2).Sum(finding => (long)finding.Count))
            { report.Fail("diagnostics-metadata-contract", "The actual projected event evidence could not produce a consistent bounded recurrence summary; no incident or health verdict was inferred."); return; }
            report.Pass("diagnostics-metadata-contract", $"A real local metadata query returned state {result.State}, {data.Findings.Count} grouped observation(s) and {data.Coverage.Count} coverage note(s). recurrenceSummaryVerified=true; observedRecordCount={summary.ObservedRecords}; repeatedGroupCount={summary.RepeatedGroups}. No raw event messages/payloads, providers or record references are stored here; records are not unique crashes and zero observations is not a healthy verdict.");
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { report.Unknown("diagnostics-provider", $"The bounded local event query could not finish (0x{ex.HResult:X8}); event-source access/coverage remains unverified."); }
    }

    private static TReport? SingleReport<TReport>(CheckResult<TReport> result, string name, SmokeReport report) where TReport : class
    {
        if (result.State is not (CheckState.Available or CheckState.Partial))
        { report.Unknown(name, $"Provider returned {result.State}; it is not treated as a successful Windows verification."); return null; }
        if (result.Items.Count != 1)
        { report.Fail(name, "A readable provider response did not return exactly one report."); return null; }
        return result.Items[0];
    }

    private static T Query<T>(Func<CancellationToken, Task<T>> start, CancellationToken token)
    {
        using var source = CancellationTokenSource.CreateLinkedTokenSource(token);
        source.CancelAfter(TimeSpan.FromSeconds(70));
        var operation = start(source.Token);
        try { return operation.WaitAsync(TimeSpan.FromSeconds(80), token).GetAwaiter().GetResult(); }
        catch { source.Cancel(); throw; }
    }

    // Only app-authored classifier messages from this harness's own fixtures belong here.
    // Never add Issue.Path, target paths, raw exceptions, arguments or native event payloads.
    private static string SafeFixtureContext(string message, IReadOnlyList<FileReviewIssue> issues) =>
        "App result: " + BoundedReason(message, 420) + " App reasons: " +
        string.Join(" | ", issues.Take(2).Select(issue => BoundedReason(issue.Reason, 160)));

    private static string BoundedReason(string value, int limit) =>
        new(value.Where(character => !char.IsControl(character) && char.GetUnicodeCategory(character) != System.Globalization.UnicodeCategory.Format).Take(limit).ToArray());

    private static bool PathEquals(string first, string second) => string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);

    private static bool HasReparseEntry(string root)
    {
        var pending = new Stack<string>(); pending.Push(root); int count = 0;
        while (pending.TryPop(out var path))
        {
            if (++count > 100) return true;
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) return true;
            if ((attributes & FileAttributes.Directory) != 0)
                foreach (var child in Directory.EnumerateFileSystemEntries(path)) pending.Push(child);
        }
        return false;
    }

    // Development fixture creation only. Production S1 code never creates/saves/activates shortcuts.
    private static void CreateShortcut(string path, string target)
    {
        object? shell = null, shortcut = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell", throwOnError: true) ?? throw new InvalidOperationException();
            shell = Activator.CreateInstance(type) ?? throw new InvalidOperationException();
            shortcut = ((dynamic)shell).CreateShortcut(path);
            ((dynamic)shortcut).TargetPath = target;
            ((dynamic)shortcut).Save();
        }
        finally
        {
            if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newFileName, string existingFileName, nint securityAttributes);
}
