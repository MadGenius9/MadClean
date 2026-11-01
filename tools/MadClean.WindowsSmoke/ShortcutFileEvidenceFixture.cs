using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using MadClean.Core;
using MadClean.Windows;
using Microsoft.Win32.SafeHandles;

namespace MadClean.WindowsSmoke;

internal static partial class NativeFixtures
{
    private static void ShortcutFileEvidenceFixture(string fixture, SmokeReport report, CancellationToken token)
    {
        const string check = "shortcut-file-evidence-fixture";
        string root = Path.Combine(fixture, "shortcut-file-evidence");
        string links = Path.Combine(root, "links"), targets = Path.Combine(root, "targets");
        string link = Path.Combine(links, "selected-missing.lnk"), missing = Path.Combine(targets, "initially-absent.bin");
        string sentinel = Path.Combine(targets, "untouched-sentinel.bin");
        var sourceBytes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        byte[] sentinelBytes = Encoding.ASCII.GetBytes("Owned shortcut evidence sentinel; never launched or changed by inspection.");
        byte[] restoredBytes = Encoding.ASCII.GetBytes("Owned target created only to witness missing-to-existing evidence.");
        try
        {
            token.ThrowIfCancellationRequested();
            if (WindowsShortcutInventory.HasPendingMetadataRead)
            { report.Unknown(check, "A prior shortcut native worker is pending; no evidence fixture was created and no further shortcut operation started."); return; }
            Directory.CreateDirectory(links); Directory.CreateDirectory(targets);
            File.WriteAllBytes(sentinel, sentinelBytes);
            if (File.Exists(missing)) throw new SmokeAssertionException("shortcut-evidence-owned-target-already-existed");
            CreateShortcut(link, missing);
            sourceBytes.Add(link, File.ReadAllBytes(link));
            var provider = new WindowsShortcutInventory();
            var snapshot = Query(ct => provider.ScanAsync(links, ct), token);
            if (ShortcutEvidencePending(report)) return;
            var selected = ShortcutEvidenceSelection(links, link, snapshot, token);
            if (selected == null)
            { report.Unknown(check, "The owned source did not establish a genuine selected missing-target observation; no documentary preview proof is claimed."); return; }
            var original = selected.PriorObservation;
            var native = ShortcutEvidenceReadStamp(link);
            if (native.LinkCount != 1 || native.FileId == Guid.Empty || native.Bytes != sourceBytes[link].Length)
                throw new SmokeAssertionException("shortcut-evidence-owned-native-baseline");
            IShortcutActionPreview preview = provider;
            var first = Query(ct => preview.InspectAsync(selected, ct), token);
            if (ShortcutEvidencePending(report)) return;
            if (first.Items.Count != 1 || first.Items[0].Protection != ShortcutProtectionState.OutsideKnownProtectedFolders)
            { report.Unknown(check, "Known-folder protection or a completed unique preview was unavailable; no protected-folder or identity result was manufactured."); return; }
            var data = first.Items[0];
            if (data.ReviewState != ShortcutActionReviewState.Preview || data.Evidence == null)
            { report.Unknown(check, $"The actual owned missing-target preview remained {data.ReviewState}; no documentary readiness or removal eligibility is inferred."); return; }
            var evidence = data.Evidence;
            string expectedHash = Convert.ToHexString(SHA256.HashData(sourceBytes[link]));
            if (first.State is not (CheckState.Available or CheckState.Partial) || data.CanMove || data.Current == null ||
                data.Current.State != ShortcutTargetState.LocalTargetMissing || data.Prior != original ||
                data.SourceCoverageState != selected.SourceCoverageState || !PathEquals(evidence.ShortcutPath, link) ||
                evidence.VolumeSerialNumber != native.Volume || evidence.FileId != native.FileId || evidence.Bytes != native.Bytes ||
                evidence.LinkCount != 1 || !string.Equals(evidence.Sha256, expectedHash, StringComparison.OrdinalIgnoreCase) ||
                evidence.Consistency != ShortcutReadConsistency.ObservedConsistent || evidence.ReadStartedUtc.Offset != TimeSpan.Zero ||
                evidence.ReadCompletedUtc.Offset != TimeSpan.Zero || evidence.ReadStartedUtc < selected.ValidatedUtc ||
                evidence.ReadStartedUtc > data.Current.CheckedAt || data.Current.CheckedAt > evidence.ReadCompletedUtc ||
                evidence.ReadCompletedUtc > DateTimeOffset.UtcNow || data.DaclAssessment != "NotChecked; deferred")
                throw new SmokeAssertionException("shortcut-evidence-native-projection-contract");
            ShortcutEvidenceAssertUnchanged(sourceBytes, sentinel, sentinelBytes);
            if (File.Exists(missing)) throw new SmokeAssertionException("shortcut-evidence-inspection-created-target");

            // Deliberate developer-owned external restoration; the provider itself must never create a target.
            using (var restoreTarget = new FileStream(missing, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                restoreTarget.Write(restoredBytes);
            var restored = Query(ct => preview.InspectAsync(selected, ct), token);
            if (ShortcutEvidencePending(report)) return;
            if (restored.Items.Count != 1 || restored.Items[0].Current == null)
            { report.Unknown(check, "The fresh restored-target result was incomplete; no stale-preview invalidation proof is claimed."); return; }
            var restoredData = restored.Items[0];
            if (restoredData.CanMove || restoredData.ReviewState != ShortcutActionReviewState.Blocked ||
                restoredData.Current!.State != ShortcutTargetState.TargetExists || restoredData.Prior != original ||
                selected.PriorObservation.State != ShortcutTargetState.LocalTargetMissing)
                throw new SmokeAssertionException("shortcut-evidence-restored-target-not-blocked");
            ShortcutEvidenceAssertUnchanged(sourceBytes, sentinel, sentinelBytes);
            if (!File.ReadAllBytes(missing).AsSpan().SequenceEqual(restoredBytes))
                throw new SmokeAssertionException("shortcut-evidence-restored-target-changed");

            // A separate source/absent target keeps the hard-link case independent of target restoration.
            string multi = Path.Combine(links, "multi-link.lnk"), alias = Path.Combine(links, "multi-link-alias.lnk");
            string multiTarget = Path.Combine(targets, "multi-link-absent.bin");
            if (File.Exists(multiTarget)) throw new SmokeAssertionException("shortcut-evidence-multi-target-already-existed");
            CreateShortcut(multi, multiTarget);
            sourceBytes.Add(multi, File.ReadAllBytes(multi));
            if (!CreateHardLinkW(alias, multi, 0))
            { report.Unknown(check, "The owned hard-link fixture could not be created; the full evidence/hard-link boundary check is inconclusive."); return; }
            sourceBytes.Add(alias, (byte[])sourceBytes[multi].Clone());
            if (ShortcutEvidenceReadStamp(multi).LinkCount != 2)
                throw new SmokeAssertionException("shortcut-evidence-hard-link-count");
            var withAlias = Query(ct => provider.ScanAsync(links, ct), token);
            if (ShortcutEvidencePending(report)) return;
            var multiScope = ShortcutEvidenceSelection(links, multi, withAlias, token);
            if (multiScope == null)
            { report.Unknown(check, "The actual multi-link source did not produce a usable historical missing-target selection; its preview boundary remains inconclusive."); return; }
            var hardLinked = Query(ct => preview.InspectAsync(multiScope, ct), token);
            if (ShortcutEvidencePending(report)) return;
            if (hardLinked.Items.Count != 1)
            { report.Unknown(check, "The hard-link preview did not return one documentary result; no hard-link action boundary is claimed."); return; }
            if (hardLinked.Items[0].CanMove || hardLinked.Items[0].ReviewState == ShortcutActionReviewState.Preview)
                throw new SmokeAssertionException("shortcut-evidence-hard-link-preview");

            bool cancelled = false;
            using (var stop = new CancellationTokenSource())
            {
                stop.Cancel();
                try
                {
                    var interrupted = preview.InspectAsync(selected, stop.Token).GetAwaiter().GetResult();
                    cancelled = interrupted.State == CheckState.Cancelled && interrupted.Items.Count == 0;
                }
                catch (OperationCanceledException) { cancelled = true; }
            }
            if (ShortcutEvidencePending(report)) return;
            if (!cancelled) throw new SmokeAssertionException("shortcut-evidence-precancel");
            ShortcutEvidenceAssertUnchanged(sourceBytes, sentinel, sentinelBytes);
            if (!File.ReadAllBytes(missing).AsSpan().SequenceEqual(restoredBytes) || File.Exists(multiTarget))
                throw new SmokeAssertionException("shortcut-evidence-final-target-boundary");
            report.Pass(check,
                $"sourceState={snapshot.State}; firstPreviewState={first.State}; restoredState={restored.State}; full128IdentityMatched=true; nativeSizeMatched=true; sha256Matched=true; missingPreview=true; restoredTargetBlocked=true; hardLinkNonPreview=true; preCancel=true; linkAndSentinelBytesUnchanged=true; CanMove=false; DaclAssessment=NotChecked. Only owned fixture targets were deliberately created by the harness; no provider move, target launch, privacy or recovery claim is made.");
        }
        catch (SmokeAssertionException ex)
        { report.Fail(check, "Owned shortcut evidence invariant " + ex.Code + " failed; no client action or recovery is validated."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or OperationCanceledException or
            System.ComponentModel.Win32Exception or InvalidOperationException or COMException or System.Security.SecurityException)
        { report.Unknown(check, $"The bounded owned shortcut evidence check could not complete (0x{ex.HResult:X8}); no readiness, movement or recovery proof is claimed. A pending native worker retains its resources and the parent fixture must be retained."); }
        finally
        {
            // These are independent audit copies, never passed to or retained by the native provider.
            foreach (byte[] bytes in sourceBytes.Values) Array.Clear(bytes);
            Array.Clear(sentinelBytes); Array.Clear(restoredBytes);
        }
        // No local teardown. The parent retains the entire marked tree while a metadata worker is pending.
    }

    private static bool ShortcutEvidencePending(SmokeReport report)
    {
        if (!WindowsShortcutInventory.HasPendingMetadataRead) return false;
        report.Unknown("shortcut-file-evidence-fixture", "A shortcut metadata call remains pending; no further fixture creation, mutation or byte verification was attempted. Worker ownership and the complete parent fixture tree must remain intact.");
        return true;
    }
    private static ShortcutRecheckScope? ShortcutEvidenceSelection(string root, string link, CheckResult<ShortcutReport> snapshot, CancellationToken token)
    {
        if (snapshot.State is not (CheckState.Available or CheckState.Partial) || snapshot.Items.Count != 1) return null;
        var entry = snapshot.Items[0].Entries.SingleOrDefault(e => PathEquals(e.ShortcutPath, link));
        if (entry == null || entry.State != ShortcutTargetState.LocalTargetMissing) return null;
        var scope = ShortcutRecheckScope.Create(root, snapshot, entry, token);
        return (scope.State is CheckState.Available or CheckState.Partial) && scope.Items.Count == 1 ? scope.Items[0] : null;
    }
    private static void ShortcutEvidenceAssertUnchanged(IReadOnlyDictionary<string, byte[]> expected, string sentinel, byte[] sentinelBytes)
    {
        foreach (var file in expected)
            if (!File.ReadAllBytes(file.Key).AsSpan().SequenceEqual(file.Value))
                throw new SmokeAssertionException("shortcut-evidence-link-bytes-changed");
        if (!File.ReadAllBytes(sentinel).AsSpan().SequenceEqual(sentinelBytes))
            throw new SmokeAssertionException("shortcut-evidence-sentinel-bytes-changed");
    }
    private sealed record ShortcutEvidenceStamp(ulong Volume, Guid FileId, long Bytes, uint LinkCount);
    private static ShortcutEvidenceStamp ShortcutEvidenceReadStamp(string path)
    {
        using var handle = ShortcutEvidenceCreateFileW(path, 0x80, 7, IntPtr.Zero, 3, 0x00200000 | 0x00100000, IntPtr.Zero);
        if (handle.IsInvalid || !ShortcutEvidenceGetIdentity(handle, 18, out var id, (uint)Marshal.SizeOf<ShortcutEvidenceId>()) ||
            !ShortcutEvidenceGetStandard(handle, 1, out var standard, (uint)Marshal.SizeOf<ShortcutEvidenceStandard>()) ||
            standard.Directory || standard.DeletePending || standard.EndOfFile <= 0 || standard.NumberOfLinks == 0)
            throw new SmokeAssertionException("shortcut-evidence-independent-native-stamp");
        return new(id.Volume, id.Id, standard.EndOfFile, standard.NumberOfLinks);
    }
    [StructLayout(LayoutKind.Sequential)] private struct ShortcutEvidenceId { public ulong Volume; public Guid Id; }
    [StructLayout(LayoutKind.Sequential)] private struct ShortcutEvidenceStandard
    {
        public long AllocationSize, EndOfFile; public uint NumberOfLinks;
        [MarshalAs(UnmanagedType.U1)] public bool DeletePending;
        [MarshalAs(UnmanagedType.U1)] public bool Directory;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle ShortcutEvidenceCreateFileW(string path, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShortcutEvidenceGetIdentity(SafeFileHandle handle, int info, out ShortcutEvidenceId identity, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShortcutEvidenceGetStandard(SafeFileHandle handle, int info, out ShortcutEvidenceStandard standard, uint size);
}
