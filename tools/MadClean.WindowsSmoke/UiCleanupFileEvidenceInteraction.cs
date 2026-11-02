using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Threading;
using MadClean.App;
using MadClean.Core;
using MadClean.Windows;
using Microsoft.Win32.SafeHandles;

namespace MadClean.WindowsSmoke;

internal static partial class UiCapture
{
    public static bool CleanupFileEvidenceInteractionCleanupConfirmed { get; private set; } = true;
    // Uncertain native/UI ownership retains these guards until the harness process exits.
    private static readonly List<SafeFileHandle> CleanupInteractionRetainedGuards = [];

    private static async Task CheckCleanupFileEvidenceInteraction(MainWindow window, MainViewModel model, SmokeReport report)
    {
        const string check = "ui-cleanup-file-evidence-interaction";
        const string raceCheck = "ui-cleanup-file-evidence-selection-race";
        const string cancelCheck = "ui-cleanup-file-evidence-cancellation";
        var budget = Stopwatch.StartNew();
        List<SafeFileHandle> guards = [];
        string? root = null, selectedPath = null, markerPath = null;
        bool created = false, auditReady = false, ownershipUncertain = false;
        int ancestorGuardCount = 0;
        byte[] marker = [], payload = Encoding.ASCII.GetBytes("MadClean owned inert UI metadata validation file; never a cleanup target.");
        CleanupInteractionIdentity rootId = default, fileId = default, markerId = default;
        CleanupFileEvidenceInteractionCleanupConfirmed = false;
        async Task<bool> Wait(int seconds)
        {
            var clock = Stopwatch.StartNew();
            while (model.IsBusy && clock.Elapsed < TimeSpan.FromSeconds(seconds) && budget.Elapsed < TimeSpan.FromSeconds(95))
            { await Task.Delay(25); await Idle(window); }
            if (!model.IsBusy && !WindowsCleanupFileEvidence.HasPendingMetadataRead) return true;
            if (model.CancelCommand.CanExecute(null)) model.CancelCommand.Execute(null);
            var settle = Stopwatch.StartNew();
            while (model.IsBusy && settle.Elapsed < TimeSpan.FromSeconds(5)) { await Task.Delay(25); await Idle(window); }
            return false; // No late completion upgrades a timed-out observation.
        }
        try
        {
            if (model.IsBusy || WindowsCleanupFileEvidence.HasPendingMetadataRead || !model.ScanCommand.CanExecute(null))
            {
                ownershipUncertain = true;
                report.Unknown(check, "An earlier UI/native operation remains uncertain; no validation folder or additional query was started.");
                return;
            }
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
            string temp = Path.Combine(local, "Temp");
            if (!Path.IsPathFullyQualified(local) || !Path.IsPathFullyQualified(temp) || local.StartsWith(@"\\", StringComparison.Ordinal))
                throw new IOException("Owned local temporary scope unavailable.");
            guards.AddRange(SystemToolTrustNative.LockDirectoryChain(temp));
            ancestorGuardCount = guards.Count;
            string suffix = Guid.NewGuid().ToString("N");
            root = Path.Combine(temp, "MadClean.WindowsSmoke.UI-Cleanup-" + suffix);
            if (root.Length > 240 || Directory.Exists(root)) throw new IOException("Owned scope unavailable.");
            selectedPath = Path.Combine(root, "owned-validation.tmp"); markerPath = Path.Combine(root, ".madclean-ui-fixture-owner");
            if (selectedPath.Length > 258 || markerPath.Length > 258) throw new IOException("Owned path exceeds current metadata bounds.");
            if (!CleanupInteractionCreateDirectory(root, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
            created = true;
            var ownGuards = SystemToolTrustNative.LockDirectoryChain(root); guards.AddRange(ownGuards);
            rootId = CleanupInteractionStamp(ownGuards[^1]);
            marker = Encoding.ASCII.GetBytes("MadClean.WindowsSmoke.UI-Cleanup:" + suffix);
            using (var stream = new FileStream(markerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(marker); stream.Flush(true); }
            using (var stream = new FileStream(selectedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(payload); stream.Flush(true); }
            File.SetLastWriteTimeUtc(selectedPath, DateTime.UtcNow.AddDays(-10));
            // READ_ATTRIBUTES is excluded from data-read sharing checks, so this no-delete
            // identity guard does not fabricate a sharing conflict for the scanner's real
            // exclusive content-read eligibility probe.
            var fileGuard = CleanupInteractionOpenMetadata(selectedPath); guards.Add(fileGuard);
            var markerGuard = SystemToolTrustNative.OpenFile(markerPath); guards.Add(markerGuard);
            fileId = CleanupInteractionStamp(fileGuard); markerId = CleanupInteractionStamp(markerGuard); auditReady = true;

            model.ScanCommand.Execute(null);
            if (!await Wait(45)) { ownershipUncertain = true; report.Unknown(check, "Actual production inspection exceeded the bounded wait; no selected query or subsequent fixture is authorized."); return; }
            model.SelectedPage = 1; await Idle(window);
            var grid = Descendants<DataGrid>(window).Single(item => ReferenceEquals(item.ItemsSource, model.CleanupRows));
            var button = Descendants<Button>(window).Single(item => AutomationProperties.GetName(item) == "Recheck selected cleanup file metadata");
            var details = Descendants<TextBox>(window).Single(item => AutomationProperties.GetName(item) == "Current cleanup file metadata and uncertainty");
            var status = Descendants<TextBlock>(window).Single(item => AutomationProperties.GetName(item) == "Current cleanup file metadata status");
            var rows = model.CleanupRows.ToArray();
            var matches = rows.Where(row => string.Equals(row.Name, selectedPath, StringComparison.OrdinalIgnoreCase) && row.State == "User temporary files").ToArray();
            if (matches.Length != 1)
            {
                report.Unknown(check, "The actual production inventory did not expose the unique owned temporary candidate; no fabricated row, source override or selected query was substituted.");
                return;
            }
            var selected = matches[0];
            string cleanupMessage = model.CleanupMessage, summary = model.CleanupSummary;
            bool HistoryUnchanged() => rows.Length == model.CleanupRows.Count && rows.Zip(model.CleanupRows).All(pair => ReferenceEquals(pair.First, pair.Second)) &&
                model.CleanupMessage == cleanupMessage && model.CleanupSummary == summary;
            void Select()
            {
                grid.SelectedItem = selected;
                if (!ReferenceEquals(model.SelectedCleanupRow, selected) || !ReferenceEquals(grid.SelectedItem, selected))
                    throw new SmokeAssertionException("ui-cleanup-evidence-selection-binding");
            }
            void Invoke()
            {
                if (!ReferenceEquals(button.Command, model.RecheckCleanupFileCommand) || !button.Command.CanExecute(button.CommandParameter))
                    throw new SmokeAssertionException("ui-cleanup-evidence-command-binding");
                var peer = UIElementAutomationPeer.CreatePeerForElement(button) ?? new ButtonAutomationPeer(button);
                if (peer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
                    throw new SmokeAssertionException("ui-cleanup-evidence-invoke-unavailable");
                invoke.Invoke();
            }
            Select(); await Idle(window);
            var before = DateTimeOffset.UtcNow; Invoke();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            if (!await Wait(15)) { ownershipUncertain = true; report.Unknown(check, "Selected metadata did not settle in the bounded wait; ownership and cleanup remain unconfirmed."); return; }
            var after = DateTimeOffset.UtcNow;
            if (!HistoryUnchanged() || !details.IsReadOnly || details.Text != model.CleanupFileEvidenceDetails || status.Text != model.CleanupFileEvidenceMessage ||
                details.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path?.Path != nameof(MainViewModel.CleanupFileEvidenceDetails))
                throw new SmokeAssertionException("ui-cleanup-evidence-display-binding");
            if (!details.Text.StartsWith("Current path: " + selectedPath + "\n", StringComparison.Ordinal) ||
                !(status.Text.StartsWith("Available", StringComparison.Ordinal) || status.Text.StartsWith("Partial", StringComparison.Ordinal)))
            { report.Unknown(check, "Actual selected-file lookup returned no accepted current metadata display; no fixture data or success was substituted."); return; }
            if (!details.Text.Contains("Current logical bytes: " + payload.Length.ToString("N0", CultureInfo.CurrentCulture), StringComparison.Ordinal) ||
                !details.Text.Contains("reported links: 1", StringComparison.Ordinal) || !details.Text.Contains("Nothing is deleted", StringComparison.Ordinal) ||
                !details.Text.Contains("Active use and application ownership remain unknown", StringComparison.Ordinal) ||
                !CleanupInteractionInterval(details.Text, before, after) || !HistoryUnchanged())
                throw new SmokeAssertionException("ui-cleanup-evidence-current-facts");
            grid.SelectedItem = null; await Idle(window);
            if (model.SelectedCleanupRow != null || model.RecheckCleanupFileCommand.CanExecute(null) || button.IsEnabled ||
                !model.CleanupFileEvidenceMessage.StartsWith("Not checked", StringComparison.Ordinal) || details.Text.Contains("Current path:", StringComparison.Ordinal))
                throw new SmokeAssertionException("ui-cleanup-evidence-clear-invalidation");
            CleanupInteractionActivity(model);
            report.Pass(check, "Actual production inspection discovered the owned temporary candidate; actual grid selection and bound button supplied accepted current WindowsTemporary display lineage, byte/link/time facts and uncertainty. Visible historical rows stayed unchanged; clearing selection invalidated details and disabled recheck. No deletion, private-snapshot claim, source injection, path export or post-query screenshot occurred.");

            Select(); await Idle(window); Invoke();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            bool liveRace = model.IsBusy;
            if (liveRace) grid.SelectedItem = null;
            if (!await Wait(15)) { ownershipUncertain = true; report.Unknown(raceCheck, "The actual selection-race query did not settle; cleanup ownership remains unconfirmed."); return; }
            if (liveRace)
            {
                await Idle(window);
                if (model.SelectedCleanupRow != null || details.Text.Contains("Current path:", StringComparison.Ordinal) || !HistoryUnchanged())
                    throw new SmokeAssertionException("ui-cleanup-evidence-stale-selection-result");
                report.Pass(raceCheck, "Selection was cleared while the real production query was Busy; no stale details were published after it settled. Historical display rows stayed unchanged.");
            }
            else report.Unknown(raceCheck, "The real metadata query finished before Busy could be observed; no live-selection-race proof is claimed.");
            grid.SelectedItem = null; Select(); await Idle(window); Invoke();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            bool liveCancel = model.IsBusy && model.CancelCommand.CanExecute(null);
            if (liveCancel) model.CancelCommand.Execute(null);
            if (!await Wait(15)) { ownershipUncertain = true; report.Unknown(cancelCheck, "Read-only cancellation did not establish settled UI/native ownership; cleanup remains unconfirmed."); return; }
            if (liveCancel && !HistoryUnchanged()) throw new SmokeAssertionException("ui-cleanup-evidence-cancel-history");
            if (liveCancel && model.CleanupFileEvidenceMessage.StartsWith("Cancelled", StringComparison.Ordinal))
                report.Pass(cancelCheck, "Production CancelCommand was requested while the real metadata query was Busy; the UI settled as Cancelled, with historical display rows preserved. No process kill or file action occurred.");
            else report.Unknown(cancelCheck, "A live cancelled terminal display was not observed; completion racing cancellation is not counted as cancellation proof.");
            grid.SelectedItem = null; await Idle(window); CleanupInteractionActivity(model);
        }
        catch (SmokeAssertionException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or System.Security.SecurityException or SystemToolTrustFailure)
        { report.Unknown(check, "Owned temporary preparation, source access or exact audit was unavailable; no arbitrary provider text, paths or fixture injection is exported."); }
        finally
        {
            bool safe = !ownershipUncertain && !model.IsBusy && !WindowsCleanupFileEvidence.HasPendingMetadataRead;
            try
            {
                if (created)
                {
                    if (!safe || !auditReady || root == null || selectedPath == null || markerPath == null) safe = false;
                    else
                    {
                        var names = Directory.EnumerateFileSystemEntries(root).Take(3).ToArray();
                        if (names.Length != 2 || !names.Contains(selectedPath, StringComparer.OrdinalIgnoreCase) || !names.Contains(markerPath, StringComparer.OrdinalIgnoreCase)) safe = false;
                        if (safe)
                        {
                            var auditGuards = SystemToolTrustNative.LockDirectoryChain(root);
                            try
                            {
                                using var fileAudit = SystemToolTrustNative.OpenFile(selectedPath);
                                using var markerAudit = SystemToolTrustNative.OpenFile(markerPath);
                                safe = CleanupInteractionStamp(auditGuards[^1]) == rootId && CleanupInteractionStamp(fileAudit) == fileId &&
                                    CleanupInteractionStamp(markerAudit) == markerId && CleanupInteractionBytes(selectedPath, payload) &&
                                    CleanupInteractionBytes(markerPath, marker);
                            }
                            finally { foreach (var guard in auditGuards.AsEnumerable().Reverse()) guard.Dispose(); }
                        }
                        if (safe)
                        {
                            // Release only the two exact file guards for own-file removal; keep
                            // the owned root and existing Temp ancestry held through those deletes.
                            for (int i = 0; i < 2; i++) { guards[^1].Dispose(); guards.RemoveAt(guards.Count - 1); }
                            File.Delete(selectedPath); File.Delete(markerPath);
                            while (guards.Count > ancestorGuardCount) { guards[^1].Dispose(); guards.RemoveAt(guards.Count - 1); }
                            Directory.Delete(root, recursive: false);
                        }
                    }
                }
            }
            catch (Exception) { safe = false; }
            if (!safe) CleanupInteractionRetainedGuards.AddRange(guards);
            else foreach (var guard in guards.AsEnumerable().Reverse()) guard.Dispose();
            CleanupFileEvidenceInteractionCleanupConfirmed = safe;
            if (safe) report.Pass("ui-cleanup-file-evidence-teardown", created
                ? "Owned exact temporary files and directory were audited and removed nonrecursively; UI/native ownership is settled. This is harness teardown only, never application cleanup."
                : "No owned temporary directory was created; preparation guards were released with settled UI/native ownership. No file cleanup was attempted.");
            else report.Unknown("ui-cleanup-file-evidence-teardown", "Owned temporary scope or UI/native ownership remains unconfirmed; available guards and owned files are retained and later fixtures must not run.");
            Array.Clear(marker); Array.Clear(payload);
        }
    }

    private static bool CleanupInteractionBytes(string path, byte[] expected)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != expected.Length) return false;
        byte[] bytes = new byte[expected.Length];
        try { stream.ReadExactly(bytes); return bytes.AsSpan().SequenceEqual(expected) && stream.ReadByte() == -1; }
        finally { Array.Clear(bytes); }
    }

    private static bool CleanupInteractionInterval(string details, DateTimeOffset before, DateTimeOffset after)
    {
        const string prefix = "Check interval UTC: ";
        string? line = details.Split('\n').SingleOrDefault(value => value.StartsWith(prefix, StringComparison.Ordinal));
        if (line == null) return false;
        var parts = line[prefix.Length..].Split(" — ", StringSplitOptions.None);
        return parts.Length == 2 && DateTimeOffset.TryParseExact(parts[0], "u", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var start) &&
            DateTimeOffset.TryParseExact(parts[1], "u", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var end) &&
            start >= before.AddSeconds(-1) && start <= end && end <= after;
    }
    private static void CleanupInteractionActivity(MainViewModel model)
    {
        bool observedArea = false;
        foreach (string item in model.Activity)
        {
            string[] pieces = item.Split("   ", StringSplitOptions.None);
            string[] labels = pieces.Length == 2 ? pieces[1].Split(" · ", StringSplitOptions.None) : [];
            if (labels.Length != 2 || !Enum.TryParse<ActivityArea>(labels[0], out var area) || !Enum.IsDefined(area) ||
                !Enum.TryParse<ActivityOutcome>(labels[1], out var outcome) || !Enum.IsDefined(outcome))
                throw new SmokeAssertionException("ui-cleanup-evidence-activity-schema");
            if (area == ActivityArea.CleanupFileEvidence) observedArea = true;
        }
        if (!observedArea) throw new SmokeAssertionException("ui-cleanup-evidence-activity-missing");
    }
    private static CleanupInteractionIdentity CleanupInteractionStamp(SafeFileHandle handle)
    {
        SystemToolTrustNative.ValidateAttributes(handle, directory: CleanupInteractionDirectory(handle));
        if (!CleanupInteractionGetId(handle, 18, out var identity, (uint)Marshal.SizeOf<CleanupInteractionIdentity>()) ||
            identity.Volume == 0 || identity.Low == 0 && identity.High == 0)
            throw new IOException("Owned identity unavailable.");
        return identity;
    }
    private static bool CleanupInteractionDirectory(SafeFileHandle handle)
    {
        if (!CleanupInteractionGetTag(handle, 9, out var tag, 8)) throw new IOException("Owned attributes unavailable.");
        return (tag.Attributes & 0x10) != 0;
    }
    private static SafeFileHandle CleanupInteractionOpenMetadata(string path)
    {
        var handle = CleanupInteractionCreateFile(path, 0x80, 1, 0, 3, 0x00200000 | 0x00100000, 0);
        if (!handle.IsInvalid)
        {
            try
            {
                SystemToolTrustNative.ValidateAttributes(handle, directory: false);
                SystemToolTrustNative.ValidateFinalPath(handle, path);
                return handle;
            }
            catch { handle.Dispose(); throw; }
        }
        int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error);
    }
    [StructLayout(LayoutKind.Sequential)] private record struct CleanupInteractionIdentity(ulong Volume, ulong Low, ulong High);
    [StructLayout(LayoutKind.Sequential)] private struct CleanupInteractionTag { internal uint Attributes, Tag; }
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CleanupInteractionGetId(SafeFileHandle handle, int kind, out CleanupInteractionIdentity identity, uint bytes);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CleanupInteractionGetTag(SafeFileHandle handle, int kind, out CleanupInteractionTag tag, uint bytes);
    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CleanupInteractionCreateDirectory(string path, nint security);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CleanupInteractionCreateFile(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
}
