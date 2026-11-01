using System.Diagnostics;

namespace MadClean.Core;

public enum DuplicatePairRecheckState { Equal, Different, Changed, Unknown }
public sealed record DuplicatePairRecheckReport(DuplicatePairRecheckState State, string Reason,
    DuplicateEntry Candidate, DuplicateEntry RetainedEntry, DuplicateFileStamp? CandidateStamp,
    DuplicateFileStamp? RetainedStamp, DateTimeOffset StartedUtc, DateTimeOffset CompletedUtc,
    long ComparedBytes, CheckState SourceCoverageState, string Coverage)
{
    public bool CanRemove => false;
}
public interface IDuplicatePairRecheck
{
    Task<CheckResult<DuplicatePairRecheckReport>> ReadAsync(DuplicatePairRecheckScope scope, CancellationToken token);
}
public sealed class DuplicatePairRecheckScope
{
    // Current selected-pair scope uses ShortcutPathPolicy's 258-character local-path bound;
    // this increment does not claim full Windows long-path support.
    public string RootPath { get; }
    public DuplicateEntry Candidate { get; }
    public DuplicateEntry RetainedEntry { get; }
    public CheckState SourceCoverageState { get; }
    public DateTimeOffset ValidatedUtc { get; }
    private DuplicatePairRecheckScope(string root, DuplicatePairReview review, DateTimeOffset validated)
    {
        RootPath = root; Candidate = review.Candidate! with { }; RetainedEntry = review.RetainedEntry! with { };
        SourceCoverageState = review.SourceCoverageState; ValidatedUtc = validated;
    }
    public static CheckResult<DuplicatePairRecheckScope> Create(string root, CheckResult<DuplicateReport>? snapshot,
        DuplicateEntry? candidate, DuplicateEntry? retained, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;
        var review = DuplicatePairReviewBuilder.Build(snapshot, candidate, retained, now, token);
        string? normalized = ShortcutPathPolicy.NormalizeLocalPath(root, false);
        if (review.State != DuplicatePairReviewState.Preview || normalized == null || review.Candidate == null || review.RetainedEntry == null ||
            !Descendant(review.Candidate.Path, normalized) || !Descendant(review.RetainedEntry.Path, normalized) ||
            review.Candidate.ObservedLinkCount != 1 || review.RetainedEntry.ObservedLinkCount != 1 ||
            review.Candidate.Bytes > DuplicatePairRecheckEngine.MaximumFileBytes || review.RetainedEntry.Bytes > DuplicatePairRecheckEngine.MaximumFileBytes)
            return CheckResult<DuplicatePairRecheckScope>.Empty(CheckState.Unavailable,
                "Select an exact supported independent single-link pair from the current bounded source report, strictly inside one explicit local folder. No new file query was authorized.");
        token.ThrowIfCancellationRequested();
        return new(review.SourceCoverageState, "Historical pair selected for one fresh read-only comparison; no action or retained-copy protection is authorized.",
            [new(normalized, review, now)]);
    }
    private static bool Descendant(string path, string root) =>
        string.Equals(path, ShortcutPathPolicy.NormalizeLocalPath(path, false), StringComparison.OrdinalIgnoreCase) &&
        path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Exactly two simultaneous content handles; no enumeration, hashing, actions or surviving leases.</summary>
public sealed class DuplicatePairRecheckEngine
{
    public const long MaximumFileBytes = 67_108_864;
    public const long MaximumReadBytes = 134_217_728;
    public static readonly TimeSpan CooperativeBudget = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan ScopeLifetime = TimeSpan.FromMinutes(2);

    public CheckResult<DuplicatePairRecheckReport> Read(DuplicatePairRecheckScope scope, IDuplicateFileAccess access,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(scope); ArgumentNullException.ThrowIfNull(access);
        token.ThrowIfCancellationRequested();
        var started = DateTimeOffset.UtcNow;
        var clock = Stopwatch.StartNew();
        long consumed = 0;
        IDuplicateReadHandle? left = null, right = null;
        byte[]? leftBuffer = null, rightBuffer = null;
        DuplicateFileStamp? leftStamp = null, rightStamp = null;
        DuplicatePairRecheckState state = DuplicatePairRecheckState.Unknown;
        string reason = "A complete bounded current comparison was not established.";
        bool cleanupFailed = false;
        void Check()
        {
            token.ThrowIfCancellationRequested();
            if (clock.Elapsed >= CooperativeBudget) throw new ComparisonLimitException();
        }
        try
        {
            if (scope.ValidatedUtc == default || scope.ValidatedUtc.Offset != TimeSpan.Zero || scope.ValidatedUtc > started ||
                started - scope.ValidatedUtc > ScopeLifetime)
                reason = "Historical selection expired; select the pair again before another read.";
            else
            {
                Check(); left = access.Open(scope.Candidate.Path, true, token); Check();
                right = access.Open(scope.RetainedEntry.Path, true, token); Check();
                leftStamp = left.Stamp; rightStamp = right.Stamp;
                if (!Valid(leftStamp) || !Valid(rightStamp) || leftStamp.LinkCount != 1 || rightStamp.LinkCount != 1 ||
                    leftStamp.Identity.Equals(rightStamp.Identity, StringComparison.OrdinalIgnoreCase))
                    reason = "Current identities are unsupported, aliased or multi-link; no independent-pair conclusion was established.";
                else if (!Original(scope.Candidate, leftStamp) || !Original(scope.RetainedEntry, rightStamp) || !left.IsStable() || !right.IsStable())
                { state = DuplicatePairRecheckState.Changed; reason = "Current identity, size or stability differs from the selected observation; prior duplicate status was not reused."; }
                else if (!left.Content.CanRead || !right.Content.CanRead || !left.Content.CanSeek || !right.Content.CanSeek)
                    reason = "The held content streams do not support a complete bounded comparison.";
                else
                {
                    if (left.Content.Seek(0, SeekOrigin.Begin) != 0 || right.Content.Seek(0, SeekOrigin.Begin) != 0)
                        throw new InvalidOperationException();
                    byte[] a = leftBuffer = new byte[65_536];
                    byte[] b = rightBuffer = new byte[65_536];
                    bool equal = true, complete = true;
                    long remaining = leftStamp.Bytes;
                    int Fill(Stream stream, byte[] buffer, int requested)
                    {
                        int total = 0;
                        while (total < requested)
                        {
                            Check();
                            int count = stream.Read(buffer, total, requested - total);
                            if (count < 0 || count > requested - total) throw new InvalidOperationException();
                            consumed = checked(consumed + count);
                            if (consumed > MaximumReadBytes) throw new ComparisonLimitException();
                            Check();
                            if (count == 0) break;
                            total += count;
                        }
                        return total;
                    }
                    while (remaining > 0)
                    {
                        int requested = (int)Math.Min(remaining, a.Length);
                        int countA = Fill(left.Content, a, requested), countB = Fill(right.Content, b, requested);
                        if (countA != requested || countB != requested) { complete = false; break; }
                        if (!a.AsSpan(0, requested).SequenceEqual(b.AsSpan(0, requested))) equal = false;
                        remaining -= requested;
                    }
                    if (complete)
                    {
                        // At the exact aggregate byte ceiling, use the held streams' observed
                        // lengths/positions rather than risk reading one byte beyond the cap.
                        if (consumed == MaximumReadBytes)
                            complete = left.Content.Length == leftStamp.Bytes && right.Content.Length == rightStamp.Bytes &&
                                left.Content.Position == leftStamp.Bytes && right.Content.Position == rightStamp.Bytes;
                        else if (Fill(left.Content, a, 1) != 0 || Fill(right.Content, b, 1) != 0) complete = false;
                    }
                    Check();
                    if (!complete || !left.IsStable() || !right.IsStable() || left.Stamp != leftStamp || right.Stamp != rightStamp)
                    { state = DuplicatePairRecheckState.Changed; reason = "Content length or metadata changed during the read; no prior/current equality conclusion was accepted."; }
                    else
                    {
                        state = equal ? DuplicatePairRecheckState.Equal : DuplicatePairRecheckState.Different;
                        reason = equal ? "The two simultaneously held current streams were fully byte-compared and equal during this inspection."
                            : "The two simultaneously held current streams were fully byte-compared and differed during this inspection.";
                    }
                }
            }
        }
        catch (ComparisonLimitException) { state = DuplicatePairRecheckState.Unknown; reason = "The content/time budget was reached; no complete pair conclusion was established."; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or
            ArgumentException or NotSupportedException or InvalidOperationException or OverflowException)
        { state = DuplicatePairRecheckState.Unknown; reason = "A selected file was inaccessible, unsupported, locked or changed; the comparison did not establish a complete result."; }
        finally
        {
            if (leftBuffer != null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(leftBuffer);
            if (rightBuffer != null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(rightBuffer);
            try { right?.Dispose(); } catch (Exception) { cleanupFailed = true; }
            try { left?.Dispose(); } catch (Exception) { cleanupFailed = true; }
        }
        token.ThrowIfCancellationRequested();
        if (cleanupFailed) { state = DuplicatePairRecheckState.Unknown; reason = "Read-handle cleanup was not confirmed; no complete pair result was accepted."; }
        if (clock.Elapsed >= CooperativeBudget) { state = DuplicatePairRecheckState.Unknown; reason = "The cooperative comparison deadline elapsed; no complete result was accepted."; }
        if (!Valid(leftStamp)) leftStamp = null;
        if (!Valid(rightStamp)) rightStamp = null;
        var completed = DateTimeOffset.UtcNow;
        var resultState = state is DuplicatePairRecheckState.Equal or DuplicatePairRecheckState.Different
            ? scope.SourceCoverageState == CheckState.Partial ? CheckState.Partial : CheckState.Available : CheckState.Unknown;
        return new(resultState, reason, [new(state, reason, scope.Candidate with { }, scope.RetainedEntry with { },
            leftStamp, rightStamp, started, completed, consumed, scope.SourceCoverageState,
            "Exactly two selected content streams; no folder enumeration or hash-only equality. Selected local paths are currently limited to 258 characters; full Windows long-path support is not claimed. ComparedBytes is total actual content bytes read across both streams, not physical or reclaimable storage. " +
            "Matching current/original identity values do not prove uninterrupted object history or unchanged original bytes: the past entries contain no full stamps or content digest. " +
            "Observed consistency is not immutability. No keeper lease or retained-copy protection survives publication; ownership, inactivity and removal safety are unknown. No action permission is granted.")]);
    }
    private static bool Original(DuplicateEntry original, DuplicateFileStamp current) =>
        string.Equals(original.Identity, current.Identity, StringComparison.OrdinalIgnoreCase) && original.Bytes == current.Bytes;
    private static bool Valid([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] DuplicateFileStamp? stamp) => stamp != null && stamp.Bytes is >= 0 and <= MaximumFileBytes &&
        stamp.LinkCount > 0 && !string.IsNullOrWhiteSpace(stamp.Identity) && stamp.Identity.Length <= 256 &&
        ShortcutPathPolicy.HasSafeUnicode(stamp.Identity);
    private sealed class ComparisonLimitException : Exception { }
}
