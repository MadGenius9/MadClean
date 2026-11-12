using System.Security.Principal;
using System.Text;
using MadClean.Core;
using Microsoft.Win32.SafeHandles;

namespace MadClean.Windows;

/// <summary>Fixed per-user cooperating journal; never called by inventory, startup or dispatch.</summary>
internal sealed class SystemToolJournalPrivateStore : IDisposable
{
    private const bool ProductionCreationValidation = false;
    internal static bool ProductionCreationValidated => ProductionCreationValidation;
    private const string FixtureLeaf = "system-tool-private-journal-v1", LeaseLeaf = "lease.bin";
    private readonly string folder, sid;
    private readonly bool writable;
    private readonly List<IDisposable> guards;
    private readonly SafeFileHandle directory;
    private readonly FileStream lease;
    private SystemToolJournalStore? active;
    private Guid activeId;
    private bool disposed, uncertain;
    internal static bool HasUncertainCleanup => SystemToolJournalStore.HasUncertainCleanup;
    internal string ScopeDirectory => folder;
    internal static string FixedUserDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
        Environment.SpecialFolderOption.DoNotVerify), "MadClean", "operation-journal-v1");

    private SystemToolJournalPrivateStore(string folder, string sid, bool writable, List<IDisposable> guards,
        SafeFileHandle directory, FileStream lease)
    { this.folder = folder; this.sid = sid; this.writable = writable; this.guards = guards; this.directory = directory; this.lease = lease; }

    internal static SystemToolJournalPrivateStore OpenForSelectedAction(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        // Parent owner/DACL and relative-creation namespace/race qualification is not
        // established. No runtime flag or caller consent enables this write path.
        if (!ProductionCreationValidated)
            throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.ScopeUnsupported);
        return OpenProduction(true, token);
    }
    internal static SystemToolJournalPrivateStore OpenExistingForReview(CancellationToken token = default) => OpenProduction(false, token);
    private static SystemToolJournalPrivateStore OpenProduction(bool create, CancellationToken token)
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        if (ShortcutPathPolicy.NormalizeLocalPath(local, false) == null)
            throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.ScopeUnsupported);
        return OpenScope(Path.Combine(local, "MadClean"), "operation-journal-v1", create, null, token,
            createParentFrom: create ? local : null);
    }
    internal static SystemToolJournalPrivateStore OpenOwnedFixture(string root, string marker, bool create, CancellationToken token = default)
    {
        string normalized = SystemToolJournalStore.ValidateRoot(root, marker);
        return OpenScope(normalized, FixtureLeaf, create, marker, token);
    }
    private static SystemToolJournalPrivateStore OpenScope(string parent, string leaf, bool create, string? marker,
        CancellationToken token, string? createParentFrom = null)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess || HasUncertainCleanup)
            throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.ScopeUnsupported);
        string folder = Path.Combine(parent, leaf);
        if (ShortcutPathPolicy.NormalizeLocalPath(folder, false) == null || folder.Length + 39 > 258)
            throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.ScopeUnsupported);
        List<IDisposable> guards = []; FileStream? lease = null;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            string sid = identity.User?.Value ?? throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.SecurityUnsupported);
            string ancestryRoot = createParentFrom ?? parent;
            SystemToolJournalStore.VerifyFixedLocalPath(ancestryRoot);
            if (!WindowsShortcutNative.TryOpenAncestry(ancestryRoot, true, token, out var ancestry, out _))
                throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.UnsafeObject);
            guards.Add(ancestry!);
            // Verify fixed local volume and persistent ACL support before ANY creation,
            // including a missing fixed MadClean parent on the explicit write path.
            if (create)
            {
                // Keep the exact no-follow parent handle alive through all child work.
                // Inherited parent ACLs may pass namespace exclusion; private child
                // objects still require the unchanged strict protected descriptor.
                var creationParent = SystemToolJournalStore.Open(ancestryRoot, 0x20080, 3, true);
                guards.Add(creationParent);
                SystemToolJournalStore.ValidateObject(creationParent, ancestryRoot, true);
                SystemToolJournalStore.VerifyPersistentAclVolume(creationParent);
                SystemToolJournalStore.VerifyCreationParentSecurity(creationParent, sid);
            }
            else
            {
                // Existing-only review keeps its original metadata access contract.
                using var volume = SystemToolJournalStore.Open(ancestryRoot, 0x80, 3, true);
                SystemToolJournalStore.ValidateObject(volume, ancestryRoot, true);
                SystemToolJournalStore.VerifyPersistentAclVolume(volume);
            }
            if (createParentFrom != null)
            {
                token.ThrowIfCancellationRequested();
                SystemToolJournalStore.CreatePrivateDirectory(parent, sid);
                if (HasUncertainCleanup) throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.CleanupUncertain);
                var parentGuard = SystemToolJournalStore.Open(parent, 0x20080, 3, true);
                guards.Add(parentGuard); SystemToolJournalStore.ValidateObject(parentGuard, parent, true);
                SystemToolJournalStore.VerifyCreationParentSecurity(parentGuard, sid);
                // Ordinary ancestry is namespace-qualified, never asserted private.
            }
            if (marker != null)
            {
                using var markerHandle = SystemToolJournalStore.Open(Path.Combine(parent, ".madclean-fixture-owner"), 0x80000000, 1, false);
                SystemToolJournalStore.ValidateObject(markerHandle, Path.Combine(parent, ".madclean-fixture-owner"), false, 32);
                using var input = new FileStream(markerHandle, FileAccess.Read);
                byte[] bytes = new byte[32];
                try { input.ReadExactly(bytes); if (input.ReadByte() != -1 || Encoding.ASCII.GetString(bytes) != marker) throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.ScopeUnsupported); }
                finally { Array.Clear(bytes); }
            }
            token.ThrowIfCancellationRequested();
            if (create)
            {
                SystemToolJournalStore.CreatePrivateDirectory(folder, sid);
                if (HasUncertainCleanup) throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.CleanupUncertain);
            }
            var directory = SystemToolJournalStore.Open(folder, 0x20080, 3, true); guards.Add(directory);
            SystemToolJournalStore.ValidateObject(directory, folder, true); SystemToolJournalStore.VerifyPrivateSecurity(directory, sid, true);
            string leasePath = Path.Combine(folder, LeaseLeaf);
            // The review path NEVER tries CREATE_NEW, creates a directory, repairs an ACL or truncates.
            SafeFileHandle handle = create ? SystemToolJournalStore.CreateIntent(leasePath, sid, out _)
                : SystemToolJournalStore.Open(leasePath, 0x80020000, 0, false);
            try
            {
                SystemToolJournalStore.ValidateObject(handle, leasePath, false, 0);
                SystemToolJournalStore.VerifyPrivateSecurity(handle, sid, false);
                lease = new FileStream(handle, create ? FileAccess.ReadWrite : FileAccess.Read, 4096, false);
            }
            catch { SystemToolJournalStore.DisposeChecked(handle); throw; }
            token.ThrowIfCancellationRequested();
            if (HasUncertainCleanup) throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.CleanupUncertain);
            var store = new SystemToolJournalPrivateStore(folder, sid, create, guards, directory, lease);
            lease = null; guards = []; return store;
        }
        finally
        {
            SystemToolJournalStore.DisposeChecked(lease);
            for (int i = guards.Count - 1; i >= 0; i--) SystemToolJournalStore.DisposeChecked(guards[i]);
        }
    }

    internal SystemToolJournalHistory ReadHistory(DateTimeOffset nowUtc, CancellationToken token = default)
    {
        Check(token);
        if (uncertain) return Unknown(nowUtc);
        try
        {
            var paths = Directory.EnumerateFileSystemEntries(folder).Take(SystemToolJournalReconciliation.MaximumJournals + 2).ToArray();
            if (paths.Length > SystemToolJournalReconciliation.MaximumJournals + 1 ||
                paths.Count(path => Path.GetFileName(path) == LeaseLeaf) != 1) return Unknown(nowUtc);
            List<SystemToolJournalReview> reviews = [];
            foreach (var path in paths)
            {
                Check(token);
                string name = Path.GetFileName(path);
                if (name == LeaseLeaf) continue;
                if (name.Length != 38 || !name.EndsWith(".jsonl", StringComparison.Ordinal) ||
                    !Guid.TryParseExact(name[..32], "N", out var id) || id == Guid.Empty || id.ToString("N") + ".jsonl" != name)
                    return Unknown(nowUtc);
                SystemToolJournalReview review;
                if (active != null && id == activeId) review = active.ReadJournal(nowUtc, token);
                else
                {
                    using var input = SystemToolJournalStore.OpenLifecycleFile(path, directory, sid, false, true, token);
                    review = input.ReadJournal(nowUtc, token);
                }
                if (HasUncertainCleanup || review.State == SystemToolJournalState.Unknown || review.Entries[0].ExecutionId != id)
                    return Unknown(nowUtc);
                reviews.Add(review);
            }
            Check(token);
            return SystemToolJournalReconciliation.Review(reviews, true, nowUtc);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        { return Unknown(nowUtc); }
    }
    internal SystemToolJournalReview BeginIntent(SystemToolJournalEntry entry, DateTimeOffset nowUtc, CancellationToken token = default)
    {
        Check(token);
        if (!writable || uncertain || active != null) return UnknownJournal(nowUtc);
        var candidate = SystemToolJournal.Review([entry], nowUtc);
        if (candidate.State != SystemToolJournalState.Unresolved || candidate.Entries[0].Phase != SystemToolJournalPhase.DispatchIntent)
            return UnknownJournal(nowUtc);
        var history = ReadHistory(nowUtc, token);
        if (history.State is SystemToolJournalHistoryState.UnknownEvidence or SystemToolJournalHistoryState.UnresolvedPresent ||
            history.Journals.Count >= SystemToolJournalReconciliation.MaximumJournals) return UnknownJournal(nowUtc);
        Check(token);
        activeId = entry.ExecutionId;
        try
        {
            active = SystemToolJournalStore.OpenLifecycleFile(Path.Combine(folder, activeId.ToString("N") + ".jsonl"), directory, sid, true, false, token);
            var result = active.AppendJournalSnapshot(candidate.Entries, nowUtc, token);
            if (result.State == SystemToolJournalState.Unknown) uncertain = true;
            return result;
        }
        catch { uncertain = true; throw; }
    }
    internal SystemToolJournalReview AppendSnapshot(IReadOnlyList<SystemToolJournalEntry> entries, DateTimeOffset nowUtc, CancellationToken token = default)
    {
        Check(token);
        if (!writable || uncertain || active == null) return UnknownJournal(nowUtc);
        try
        {
            var result = active.AppendJournalSnapshot(entries, nowUtc, token);
            if (result.State == SystemToolJournalState.Unknown) uncertain = true;
            return result;
        }
        catch { uncertain = true; throw; }
    }
    private void Check(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this); token.ThrowIfCancellationRequested();
        if (HasUncertainCleanup) throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.CleanupUncertain);
    }
    private static SystemToolJournalHistory Unknown(DateTimeOffset now) => SystemToolJournalReconciliation.Review(null, false, now);
    private static SystemToolJournalReview UnknownJournal(DateTimeOffset now) => SystemToolJournal.Review(null, now);
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        SystemToolJournalStore.DisposeChecked(active); SystemToolJournalStore.DisposeChecked(lease);
        for (int i = guards.Count - 1; i >= 0; i--) SystemToolJournalStore.DisposeChecked(guards[i]);
    }
}
