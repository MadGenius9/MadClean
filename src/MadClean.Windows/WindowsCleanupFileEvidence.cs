using System.ComponentModel;
using MadClean.Core;

namespace MadClean.Windows;

internal enum CleanupEvidenceUnavailableReason
{
    CurrentExclusionsChangedOrUnreadable, ProtectedSelection, ProtectionCoverageUnavailable,
    TooRecent, MultipleLinks, UnsafeMetadata, ChangedDuringRead, CurrentRootUnavailable
}
internal sealed class CleanupEvidenceUnavailable(CleanupEvidenceUnavailableReason reason) : IOException
{
    internal CleanupEvidenceUnavailableReason Reason { get; } = reason;
}

/// <summary>Selected temporary-file metadata only. No content, action or surviving lease is provided.</summary>
public sealed class WindowsCleanupFileEvidence : ICleanupFileEvidence
{
    private static int active;
    public static bool HasPendingMetadataRead => Volatile.Read(ref active) != 0;

    public Task<CheckResult<CleanupFileEvidenceReport>> ReadAsync(CleanupFileEvidenceScope scope, CancellationToken token)
        => Start(scope, null, null, token);
    internal Task<CheckResult<CleanupFileEvidenceReport>> ReadOwnedFixtureAsync(CleanupFileEvidenceScope scope,
        string ownedFixtureRoot, string ownerMarker, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(ownedFixtureRoot); ArgumentNullException.ThrowIfNull(ownerMarker);
        return Start(scope, ownedFixtureRoot, ownerMarker, token);
    }

    private static async Task<CheckResult<CleanupFileEvidenceReport>> Start(CleanupFileEvidenceScope scope,
        string? fixture, string? marker, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(scope);
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            return Empty(CheckState.Unsupported, "Selected cleanup metadata requires supported Windows x64 identities. No selected-file query ran.");
        if (!Fresh(scope)) return Empty(CheckState.Unknown, "The cleanup selection expired. Rescan and select the file again; no selected-file query ran.");
        if (Interlocked.CompareExchange(ref active, 1, 0) != 0)
            return Empty(CheckState.Unavailable, "A cleanup metadata worker is still finishing. Wait for its handles to be released before retrying.");
        var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var completion = new TaskCompletionSource<CheckResult<CleanupFileEvidenceReport>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(() =>
        {
            CheckResult<CleanupFileEvidenceReport> result;
            try { result = ReadCore(scope, fixture, marker, stop.Token); stop.Token.ThrowIfCancellationRequested(); }
            catch (OperationCanceledException) { result = Empty(CheckState.Cancelled, "Cleanup metadata observation was cancelled; no file action ran."); }
            catch (CleanupEvidenceUnavailable failure)
            { result = Empty(CheckState.Unknown, UnavailableMessage(failure.Reason)); }
            catch (Win32Exception failure)
            { result = Empty(CheckState.Unknown, $"Native cleanup metadata was unavailable (Windows code 0x{failure.NativeErrorCode:X8}). Check permissions and select a supported ordinary file; no action ran."); }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException or
                NotSupportedException or InvalidOperationException or System.Security.SecurityException)
            { result = Empty(CheckState.Unknown, "Current cleanup roots, protection metadata, exclusions or ordinary stable single-link file metadata could not be established. Reload exclusions, rescan and select a supported old temporary file. No accepted current evidence or action is claimed."); }
            catch (Exception) { result = Empty(CheckState.Failed, "The cleanup metadata worker failed unexpectedly; retry the query. No action ran."); }
            finally { stop.Dispose(); Volatile.Write(ref active, 0); }
            completion.TrySetResult(result);
        }, CancellationToken.None);
        try
        {
            var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(45), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); return result;
        }
        catch (TimeoutException)
        {
            try { stop.Cancel(); } catch (ObjectDisposedException) { }
            return Empty(CheckState.Unavailable, "The caller wait expired. The cleanup worker retains its handles until native metadata returns; no action ran.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            try { stop.Cancel(); } catch (ObjectDisposedException) { }
            return Empty(CheckState.Cancelled, "Observation stop was requested. The cleanup worker retains its handles until native metadata returns; no action ran.");
        }
    }

    private static CheckResult<CleanupFileEvidenceReport> ReadCore(CleanupFileEvidenceScope scope, string? fixture,
        string? marker, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Fresh(scope)) return Empty(CheckState.Unknown, "The cleanup selection expired before this worker began. Rescan and select again.");
        string path = CleanupFileEvidenceScope.NormalizeFilePath(scope.Candidate.Path) ?? throw new InvalidDataException();
        string root, settings;
        CleanupEvidenceSourceKind source;
        CleanupFileEvidenceNative.DirectoryLease? fixtureLease = null;
        try
        {
            if (fixture != null)
            {
                ValidateFixture(fixture, marker);
                fixtureLease = CleanupFileEvidenceNative.OpenDirectories(fixture, token);
                CleanupFileEvidenceNative.VerifyOwnedMarker(Path.Combine(fixture, ".madclean-fixture-owner"), marker!, token);
                string local = Path.Combine(fixture, "cleanup-file-evidence");
                root = StrictFolder(Path.Combine(local, scope.RootKind == CleanupRootKind.UserTemporary ? "user-temp" : "windows-temp"));
                settings = Path.Combine(local, "cleanup-exclusions.json");
                source = CleanupEvidenceSourceKind.OwnedDevelopmentFixture;
            }
            else
            {
                string category = scope.RootKind == CleanupRootKind.UserTemporary ? "User temporary files" : "Windows temporary files";
                var locations = new WindowsInventory().CleanupLocations.Where(item => item.Category == category).ToArray();
                if (locations.Length != 1) throw new CleanupEvidenceUnavailable(CleanupEvidenceUnavailableReason.CurrentRootUnavailable);
                root = StrictFolder(locations[0].Path);
                settings = Path.Combine(StrictFolder(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
                    Environment.SpecialFolderOption.DoNotVerify)), "MadClean", "cleanup-exclusions.json");
                source = CleanupEvidenceSourceKind.WindowsTemporary;
            }
            if (!Descendant(path, root)) throw new CleanupEvidenceUnavailable(CleanupEvidenceUnavailableReason.CurrentRootUnavailable);
            if (scope.MatchesCapturedExclusions(path) || CleanupSafetyPolicy.IsProtectedRelativePath(path[(root.Length + 1)..]))
                throw new CleanupEvidenceUnavailable(CleanupEvidenceUnavailableReason.ProtectedSelection);
            RequireExclusions(settings, scope, token);
            try { RequireOutsideProtectedFolders(path, root, token); }
            catch (CleanupEvidenceUnavailable) { throw; }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException or
                NotSupportedException or System.Security.SecurityException)
            { throw new CleanupEvidenceUnavailable(CleanupEvidenceUnavailableReason.ProtectionCoverageUnavailable); }
            token.ThrowIfCancellationRequested();
            if (!Fresh(scope)) return Empty(CheckState.Unknown, "The cleanup selection expired during metadata preparation. Rescan and select again.");
            var started = DateTimeOffset.UtcNow;
            CleanupFileEvidenceNative.Snapshot before;
            string rootIdentity;
            CleanupStreamScopeObservation streamScope;
            using (var directories = CleanupFileEvidenceNative.OpenDirectories(Path.GetDirectoryName(path)!, token))
            using (var file = CleanupFileEvidenceNative.OpenFile(path))
            {
                var rootBefore = directories.At(root);
                rootIdentity = rootBefore.Identity;
                before = CleanupFileEvidenceNative.Observe(file, path, false);
                var written = Time(before.Written);
                if (written > started || started - written < CleanupFileEvidenceScope.MinimumAge)
                    throw new CleanupEvidenceUnavailable(CleanupEvidenceUnavailableReason.TooRecent);
                var firstStreams = CleanupFileEvidenceNative.ObserveStreams(file, before.Bytes, token);
                token.ThrowIfCancellationRequested();
                RequireExclusions(settings, scope, token);
                directories.Recheck(token); fixtureLease?.Recheck(token);
                var secondStreams = CleanupFileEvidenceNative.ObserveStreams(file, before.Bytes, token);
                streamScope = CleanupStreamScope.Compare(firstStreams.Snapshot, secondStreams.Snapshot,
                    firstStreams.NativeError, secondStreams.NativeError, token);
                if (!CleanupStreamScope.IsConsistent(streamScope, before.Bytes))
                    throw new CleanupEvidenceUnavailable(CleanupEvidenceUnavailableReason.UnsafeMetadata);
                if (before != CleanupFileEvidenceNative.Observe(file, path, false))
                    throw new CleanupEvidenceUnavailable(CleanupEvidenceUnavailableReason.ChangedDuringRead);
            }
            token.ThrowIfCancellationRequested();
            var completed = DateTimeOffset.UtcNow;
            var created = Time(before.Created); var modified = Time(before.Written); var changed = Time(before.Changed);
            if (created > completed || modified > completed || changed > completed || completed < started)
                throw new CleanupEvidenceUnavailable(CleanupEvidenceUnavailableReason.UnsafeMetadata);
            bool pathMatches = string.Equals(path, scope.Candidate.Path, StringComparison.OrdinalIgnoreCase);
            bool sizeMatches = before.Bytes == scope.Candidate.Bytes;
            bool modifiedMatches = modified.UtcDateTime == scope.Candidate.ModifiedUtc;
            var state = scope.SourceCoverageState == CheckState.Partial || !pathMatches || !sizeMatches || !modifiedMatches ||
                streamScope.State != CleanupStreamScopeState.UnnamedDataOnly
                ? CheckState.Partial : CheckState.Available;
            var report = new CleanupFileEvidenceReport(scope.Candidate with { }, scope.RootKind, source,
                new(path, before.Identity, before.Bytes, before.Links, created, modified, changed, before.Attributes),
                root, rootIdentity, started, completed, pathMatches, sizeMatches, modifiedMatches,
                CheckState.Available, scope.SourceCoverageState,
                ["One selected file's native metadata and at most two same-handle bounded stream metadata queries; no file contents, hashing, sibling enumeration or deletion.",
                 "Alternate or unknown stream scope blocks whole-file cleanup review. Counted stream names remain private and are never exported; zero gaps/tail are a conservative parser policy, not a Windows padding guarantee.",
                 "Actual temporary root, captured/current exclusion sets and configured protected-folder redirects were checked; unsupported evidence blocks the query.",
                 "The identity is a first fresh observation. Historical path/size/time comparisons do not establish uninterrupted object identity or unchanged contents.",
                 "Metadata bookend equality is not immutability or inactivity. READ_ATTRIBUTES is not an exclusive content-use reservation.",
                 "Installed-application ownership and arbitrary browser/data ownership are Unknown; no ARP ownership check ran. No deletion eligibility or recoverable-byte claim is made.",
                 "No native handle survives publication. Captured cleanup discovery coverage is independent of the current exclusion check."])
                { StreamScope = streamScope };
            string message = streamScope.State switch
            {
                CleanupStreamScopeState.AlternateStreamsObserved => "Fresh metadata observed, but alternate data streams block whole-file cleanup review. No stream contents were read; CanDelete is false.",
                CleanupStreamScopeState.Unknown => "Fresh metadata observed, but stream scope could not be established and blocks whole-file cleanup review. No stream contents were read; CanDelete is false.",
                _ => "Fresh selected cleanup metadata and unnamed-only stream scope observed. Active use, ownership, continuity and deletion safety remain unknown; CanDelete is false."
            };
            return new(state, message, [report]);
        }
        finally { fixtureLease?.Dispose(); }
    }

    private static DateTimeOffset Time(long fileTime)
    {
        if (fileTime <= 0) throw new CleanupEvidenceUnavailable(CleanupEvidenceUnavailableReason.UnsafeMetadata);
        try { return new(DateTime.FromFileTimeUtc(fileTime), TimeSpan.Zero); }
        catch (ArgumentOutOfRangeException) { throw new CleanupEvidenceUnavailable(CleanupEvidenceUnavailableReason.UnsafeMetadata); }
    }
    private static bool Fresh(CleanupFileEvidenceScope scope)
    {
        var now = DateTimeOffset.UtcNow;
        return scope.ValidatedUtc != default && scope.ValidatedUtc.Offset == TimeSpan.Zero && scope.ValidatedUtc <= now &&
            now - scope.ValidatedUtc <= CleanupFileEvidenceScope.ScopeLifetime && scope.CompletedUtc != default &&
            scope.CompletedUtc.Offset == TimeSpan.Zero && scope.CompletedUtc <= now && now - scope.CompletedUtc <= CleanupFileEvidenceScope.SnapshotLifetime;
    }
    private static string StrictFolder(string? path, CleanupEvidenceUnavailableReason reason = CleanupEvidenceUnavailableReason.CurrentRootUnavailable)
    {
        try
        {
            string normalized = CleanupFileEvidenceScope.NormalizeFilePath(path) ?? throw new CleanupEvidenceUnavailable(reason);
            if (new DriveInfo(normalized[..3]).DriveType != DriveType.Fixed) throw new CleanupEvidenceUnavailable(reason);
            return normalized;
        }
        catch (CleanupEvidenceUnavailable) { throw; }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        { throw new CleanupEvidenceUnavailable(reason); }
    }
    private static bool Descendant(string path, string root) => path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
    private static bool Within(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase) || Descendant(path, root);
    private static void RequireExclusions(string settings, CleanupFileEvidenceScope scope, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var current = new CleanupExclusionStore(settings).Load();
        token.ThrowIfCancellationRequested();
        if (current.Error != null || !CleanupFileEvidenceScope.TryNormalizeExclusions(current.Items, out var normalized, token) ||
            normalized.Length != scope.CapturedExclusions.Count || normalized.Any(item => !scope.CapturedExclusions.Any(prior =>
                prior.IncludeDescendants == item.IncludeDescendants && string.Equals(prior.Path, item.Path, StringComparison.OrdinalIgnoreCase))))
            throw new CleanupEvidenceUnavailable(CleanupEvidenceUnavailableReason.CurrentExclusionsChangedOrUnreadable);
    }
    private static void RequireOutsideProtectedFolders(string path, string root, CancellationToken token)
    {
        List<string> protectedFolders = [];
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.CommonProgramFiles, Environment.SpecialFolder.CommonProgramFilesX86,
            Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolder.Startup, Environment.SpecialFolder.CommonStartup, Environment.SpecialFolder.DesktopDirectory,
            Environment.SpecialFolder.CommonDesktopDirectory, Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.MyMusic,
            Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.CommonDocuments,
            Environment.SpecialFolder.CommonMusic, Environment.SpecialFolder.CommonPictures, Environment.SpecialFolder.CommonVideos })
        {
            token.ThrowIfCancellationRequested();
            protectedFolders.Add(StrictFolder(Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify), CleanupEvidenceUnavailableReason.ProtectionCoverageUnavailable));
        }
        foreach (var id in new[] { new Guid("374DE290-123F-4565-9164-39C4925E467B"),
            new Guid("4C5C32FF-BB9D-43B0-B5B4-2D72E54EAAA4"), new Guid("A520A1A4-1780-4FF6-BD18-167343C5AF16") })
        {
            token.ThrowIfCancellationRequested(); protectedFolders.Add(StrictFolder(WindowsShortcutPreviewNative.KnownFolder(id), CleanupEvidenceUnavailableReason.ProtectionCoverageUnavailable));
        }
        string profile = StrictFolder(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify), CleanupEvidenceUnavailableReason.ProtectionCoverageUnavailable);
        string local = StrictFolder(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), CleanupEvidenceUnavailableReason.ProtectionCoverageUnavailable);
        string roaming = StrictFolder(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify), CleanupEvidenceUnavailableReason.ProtectionCoverageUnavailable);
        foreach (string relative in new[] { @"Google\Chrome\User Data", @"Microsoft\Edge\User Data", @"Mozilla\Firefox\Profiles",
            @"BraveSoftware\Brave-Browser\User Data", @"Microsoft\Credentials", @"Microsoft\Vault", @"Microsoft\Protect",
            @"Microsoft\OneAuth", @"Microsoft\TokenBroker", @"Microsoft\IdentityCache", "Opera Software" })
        { protectedFolders.Add(Path.Combine(local, relative)); protectedFolders.Add(Path.Combine(roaming, relative)); }
        foreach (string relative in new[] { ".ssh", ".aws", ".azure", ".gnupg", "Saved Games", "Saves" })
            protectedFolders.Add(Path.Combine(profile, relative));
        if (root.Equals(profile, StringComparison.OrdinalIgnoreCase) || path.Equals(profile, StringComparison.OrdinalIgnoreCase) ||
            protectedFolders.Any(folder => Within(path, folder)))
            throw new CleanupEvidenceUnavailable(CleanupEvidenceUnavailableReason.ProtectedSelection);
    }
    private static void ValidateFixture(string root, string? marker)
    {
        if (root == null || root.Length > 200 || root != Path.GetFullPath(root) ||
            !Guid.TryParseExact(marker, "N", out var owner) || owner == Guid.Empty || marker != owner.ToString("N")) throw new InvalidDataException();
        string prefix = "windows-validation-fixtures-"; string name = Path.GetFileName(root);
        string artifacts = Path.Combine(Path.GetFullPath(Environment.CurrentDirectory), "artifacts");
        if (!string.Equals(Path.GetDirectoryName(root), artifacts, StringComparison.OrdinalIgnoreCase) ||
            !name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[prefix.Length..], "N", out var id) || id == Guid.Empty)
            throw new InvalidDataException();
        StrictFolder(root);
    }
    private static string UnavailableMessage(CleanupEvidenceUnavailableReason reason) => reason switch
    {
        CleanupEvidenceUnavailableReason.CurrentExclusionsChangedOrUnreadable => "Cleanup exclusions changed, are invalid, or could not be read. Reload exclusions, rescan, and select the file again; no accepted evidence or file action is claimed.",
        CleanupEvidenceUnavailableReason.ProtectedSelection => "The selected path is protected or excluded. Choose an eligible old temporary file outside protected data and saved exclusions; no action ran.",
        CleanupEvidenceUnavailableReason.ProtectionCoverageUnavailable => "A required protected-folder location is unavailable or has an unsupported redirect. Restore supported local folder settings and retry; protection was not assumed complete.",
        CleanupEvidenceUnavailableReason.TooRecent => "The current modification time is in the future or less than seven days old. Rescan after the supported age threshold; this observation cannot qualify the file.",
        CleanupEvidenceUnavailableReason.MultipleLinks => "The selected file has multiple hard links. Choose an ordinary single-link temporary file; aliases are not accepted as independent cleanup evidence.",
        CleanupEvidenceUnavailableReason.UnsafeMetadata => "The selected file or ancestry has unsupported canonical path, identity, timestamps, size or ordinary-file attributes. Choose a supported local file; no safety or active-use conclusion was made.",
        CleanupEvidenceUnavailableReason.ChangedDuringRead => "The selected file or checked ancestry changed during metadata observation. Rescan and select again after it stabilizes; no current evidence was accepted.",
        CleanupEvidenceUnavailableReason.CurrentRootUnavailable => "The current fixed-local temporary or settings root is unavailable, unsupported, or differs from this selection. Restore its availability and rescan; no fallback location was used.",
        _ => "Current cleanup metadata was not established. Rescan and select a supported temporary file; no action ran."
    };
    private static CheckResult<CleanupFileEvidenceReport> Empty(CheckState state, string reason) => CheckResult<CleanupFileEvidenceReport>.Empty(state, reason);
}
