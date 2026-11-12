using MadClean.Core;
using MadClean.Validation;
using Xunit;

namespace MadClean.Tests;

public sealed class LocalUpdateHistoryEvidenceTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 12, 1, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = Start.AddSeconds(2);
    private static LocalUpdateHistoryFacts Positive() => new(true, false, "Partial", true, true, 5, 5, 3, true, null);
    private static LocalUpdateHistoryFacts Unknown() => new(null, false, null, null, null, null, null, null, null, null);
    private static LocalUpdateHistoryReport Report(LocalUpdateHistoryFacts? facts = null) =>
        LocalUpdateHistoryEvidence.Create(Start, End, facts ?? Positive());
    private static string Outcome(LocalUpdateHistoryFacts facts) => Report(facts).State;

    private static CheckResult<WindowsUpdateHistoryReport> Response(DateTimeOffset started, int count = 1, bool knownFields = true)
    {
        var state = knownFields ? UpdateHistoryFieldState.Known : UpdateHistoryFieldState.Unknown;
        var rows = Enumerable.Range(0, count).Select(index => new WindowsUpdateHistoryEntry(index,
            new(state, knownFields ? "History metadata" : null), new(UpdateHistoryFieldState.Unknown, null),
            new(UpdateHistoryFieldState.Unknown, null), new(UpdateHistoryFieldState.Unknown, null),
            new(UpdateHistoryFieldState.Unknown, null), new(UpdateHistoryFieldState.Unknown, null), new(UpdateHistoryFieldState.Unknown, null))).ToArray();
        return new(CheckState.Partial, "bounded source metadata", [new(started, started.AddMilliseconds(1), count, count, rows)]);
    }

    [Fact]
    public void ConsistentPositiveFactsHaveNarrowCoverageOnly()
    {
        var report = Report(); Assert.Equal("passed", report.State); Assert.True(LocalUpdateHistoryEvidence.IsConsistent(report, End));
        Assert.Equal(1, report.FormatVersion); Assert.Equal("local-update-history", report.Context);
        Assert.Equal("unverified", report.WindowsServiceCompletion); Assert.Equal("unverified", report.CurrentInstallation);
        Assert.Equal("unverified", report.Maintenance); Assert.Equal("unverified", report.PhysicalInput); Assert.Equal("unverified", report.SourceAuthenticity);
    }

    [Theory]
    [InlineData("format")] [InlineData("context")] [InlineData("service")] [InlineData("installation")]
    [InlineData("maintenance")] [InlineData("input")] [InlineData("authenticity")] [InlineData("outcome")]
    public void EvidenceCannotBePromotedToAnotherModeOrCoverage(string changed)
    {
        var good = Report(); var report = changed switch
        {
            "format" => good with { FormatVersion = 2 }, "context" => good with { Context = "local-neutral-ui" },
            "service" => good with { WindowsServiceCompletion = "passed" }, "installation" => good with { CurrentInstallation = "verified" },
            "maintenance" => good with { Maintenance = "passed" }, "input" => good with { PhysicalInput = "passed" },
            "authenticity" => good with { SourceAuthenticity = "verified" }, _ => good with { State = "inconclusive" }
        };
        Assert.False(LocalUpdateHistoryEvidence.IsConsistent(report, End));
    }

    [Theory]
    [InlineData("Unknown")] [InlineData("Unavailable")] [InlineData("Unsupported")]
    [InlineData("Failed")] [InlineData("Cancelled")]
    public void GenuineEmptyNegativeProviderResponsesRemainInconclusive(string provider)
    {
        var facts = new LocalUpdateHistoryFacts(true, false, provider, true, false, null, null, null, null, null);
        var report = Report(facts); Assert.Equal("inconclusive", report.State); Assert.True(LocalUpdateHistoryEvidence.IsConsistent(report, End));
        Assert.Equal("failed", Outcome(facts with { ResponseAccepted = true }));
        Assert.Equal("failed", Outcome(facts with { ReturnedEntryCount = 0 }));
    }

    [Fact]
    public void UnknownObservationsStayInconclusiveUnlessTheyContradictKnownFacts()
    {
        foreach (var facts in new[] { Unknown(), Unknown() with { QueryTaskSettled = false }, Unknown() with { QueryTaskSettled = true },
            Unknown() with { QueryTaskSettled = true, ResponseShapeValid = true },
            Positive() with { TotalBefore = null }, Positive() with { RequestedCount = null },
            Positive() with { ReturnedEntryCount = null, FieldProjectionObserved = null }, Positive() with { FieldProjectionObserved = null } })
        {
            Assert.Equal("inconclusive", Outcome(facts)); Assert.True(LocalUpdateHistoryEvidence.IsConsistent(Report(facts), End));
        }
        Assert.Equal("failed", Outcome(Positive() with { QueryTaskSettled = null }));
        Assert.Equal("failed", Outcome(Positive() with { ReturnedEntryCount = null }));
        Assert.Equal("failed", Outcome(Positive() with { TotalBefore = 0, RequestedCount = null, ReturnedEntryCount = 1 }));
    }

    [Theory]
    [InlineData(0, 0, 0, false)] [InlineData(3, 3, 0, false)] [InlineData(3, 3, 2, false)]
    [InlineData(3, 3, 2, true)] [InlineData(500, 128, 128, true)]
    public void EmptyShortAndCappedHistoryCanQualifyOnlyTheBoundedRead(int total, int requested, int returned, bool projected)
    {
        var facts = Positive() with { TotalBefore = total, RequestedCount = requested, ReturnedEntryCount = returned, FieldProjectionObserved = projected };
        Assert.Equal("passed", Outcome(facts)); Assert.Equal("inconclusive", Outcome(facts with { ObservationBudgetExpired = true }));
    }

    [Theory]
    [InlineData("unsettled")] [InlineData("malformed")] [InlineData("refused")]
    [InlineData("available")] [InlineData("unrecognized")] [InlineData("count")] [InlineData("empty-projection")]
    public void ContradictionsRemainFailedEvenAfterBudgetExpiry(string changed)
    {
        var facts = changed switch
        {
            "unsettled" => Positive() with { QueryTaskSettled = false }, "malformed" => Positive() with { ResponseShapeValid = false },
            "refused" => Positive() with { ResponseAccepted = false }, "available" => Positive() with { ProviderState = "Available" },
            "unrecognized" => Positive() with { ProviderState = "Unrecognized" }, "count" => Positive() with { RequestedCount = 4 },
            _ => Positive() with { ReturnedEntryCount = 0, FieldProjectionObserved = true }
        };
        Assert.Equal("failed", Outcome(facts)); Assert.Equal("failed", Outcome(facts with { ObservationBudgetExpired = true }));
        Assert.True(LocalUpdateHistoryEvidence.IsConsistent(Report(facts), End));
    }

    [Fact]
    public void InvalidNumericRangesAndProviderStringsAreStructurallyRejected()
    {
        foreach (var facts in new[] { Positive() with { TotalBefore = -1 }, Positive() with { RequestedCount = 129 },
            Positive() with { ReturnedEntryCount = -1 }, Positive() with { ReturnedEntryCount = 129 }, Positive() with { ProviderState = "made-up" } })
        {
            Assert.Equal("failed", Outcome(facts)); Assert.False(LocalUpdateHistoryEvidence.IsConsistent(Report(facts), End));
        }
    }

    [Fact]
    public void ErrorsCannotBeHiddenByACompleteResponseOrAnExpiredBudget()
    {
        var facts = Positive() with { ExceptionHResult = unchecked((int)0x80004005), ObservationBudgetExpired = true };
        Assert.Equal("failed", Outcome(facts)); Assert.True(LocalUpdateHistoryEvidence.IsConsistent(Report(facts), End));
        Assert.False(LocalUpdateHistoryEvidence.IsConsistent(Report(facts) with { State = "inconclusive" }, End));
    }

    [Fact]
    public void BudgetSnapshotSeparatesPendingAndAlreadySettledWithoutRewritingFinalOwnership()
    {
        foreach (bool settledAtObservation in new[] { false, true })
        {
            var facts = Positive() with { ObservationBudgetExpired = true, QueryTaskSettledWhenBudgetObserved = settledAtObservation };
            Assert.Equal("inconclusive", Outcome(facts)); Assert.True(LocalUpdateHistoryEvidence.IsConsistent(Report(facts), End));
            Assert.True(facts.QueryTaskSettled);
        }
        Assert.Equal("inconclusive", Outcome(Positive() with { ObservationBudgetExpired = true }));
        Assert.Equal("failed", Outcome(Positive() with { QueryTaskSettledWhenBudgetObserved = false }));
        var impossible = Unknown() with
        {
            QueryTaskSettled = false, ObservationBudgetExpired = true, QueryTaskSettledWhenBudgetObserved = true
        };
        Assert.Equal("failed", Outcome(impossible)); Assert.True(LocalUpdateHistoryEvidence.IsConsistent(Report(impossible), End));
    }

    [Fact]
    public void UtcOrderingFutureReceiptAndCoverageAreRequiredWithoutADrainDurationCap()
    {
        var report = Report();
        Assert.False(LocalUpdateHistoryEvidence.IsConsistent(report with { StartedUtc = default }, End));
        Assert.False(LocalUpdateHistoryEvidence.IsConsistent(report with { CompletedUtc = Start.AddTicks(-1) }, End));
        Assert.False(LocalUpdateHistoryEvidence.IsConsistent(report with { StartedUtc = Start.ToOffset(TimeSpan.FromHours(1)) }, End));
        Assert.False(LocalUpdateHistoryEvidence.IsConsistent(report, End.AddTicks(-1)));
        Assert.False(LocalUpdateHistoryEvidence.IsConsistent(report, End.ToOffset(TimeSpan.FromHours(-1))));
        var late = LocalUpdateHistoryEvidence.Create(Start, Start.AddHours(2), Positive() with { ObservationBudgetExpired = true });
        Assert.Equal("inconclusive", late.State); Assert.True(LocalUpdateHistoryEvidence.IsConsistent(late, late.CompletedUtc));
    }

    [Fact]
    public async Task BudgetExpiryCannotReturnBeforeTheSameActualTaskSettlesOrRewriteALatePositive()
    {
        var started = DateTimeOffset.UtcNow.AddSeconds(-1);
        var actual = new TaskCompletionSource<CheckResult<WindowsUpdateHistoryReport>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deadline = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        deadline.SetResult(true);
        var observed = LocalUpdateHistoryEvidence.ObserveAsync(actual.Task, deadline.Task, started);
        Assert.False(observed.IsCompleted); Assert.False(actual.Task.IsCompleted);
        var response = Response(started.AddMilliseconds(1)); actual.SetResult(response);
        var facts = await observed;
        Assert.Same(response, await actual.Task); Assert.True(facts.QueryTaskSettled); Assert.True(facts.ObservationBudgetExpired);
        Assert.True(facts.ResponseShapeValid); Assert.True(facts.ResponseAccepted); Assert.True(facts.FieldProjectionObserved);
        Assert.False(facts.QueryTaskSettledWhenBudgetObserved);
        Assert.Equal("inconclusive", LocalUpdateHistoryEvidence.Create(started, DateTimeOffset.UtcNow, facts).State);
    }

    [Fact]
    public async Task ActualSettlementBeforeDeadlineCanQualifyOnlyTheMetadataProjection()
    {
        var started = DateTimeOffset.UtcNow.AddSeconds(-1);
        var deadline = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var facts = await LocalUpdateHistoryEvidence.ObserveAsync(Task.FromResult(Response(started.AddMilliseconds(1))), deadline.Task, started);
        Assert.True(facts.QueryTaskSettled); Assert.False(facts.ObservationBudgetExpired); Assert.Equal("Partial", facts.ProviderState);
        Assert.Null(facts.QueryTaskSettledWhenBudgetObserved);
        Assert.Equal(1, facts.ReturnedEntryCount); Assert.Equal("passed", LocalUpdateHistoryEvidence.Create(started, DateTimeOffset.UtcNow, facts).State);
        Assert.False(deadline.Task.IsCompleted);
    }

    private sealed class CountSignal<T>(T value, Action signal) : IReadOnlyList<T>
    {
        public int Count { get { signal(); return 1; } }
        public T this[int index] => index == 0 ? value : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)new[] { value }).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public async Task ExpiryDuringResponseValidationCannotBeLostAfterActualTaskCompletion()
    {
        var started = DateTimeOffset.UtcNow.AddSeconds(-1); var deadline = new TaskCompletionSource<bool>();
        var original = Response(started.AddMilliseconds(1));
        var response = original with
        {
            Items = new CountSignal<WindowsUpdateHistoryReport>(original.Items[0], () => deadline.TrySetResult(true))
        };
        var actual = Task.FromResult(response);
        var facts = await LocalUpdateHistoryEvidence.ObserveAsync(actual, deadline.Task, started);
        Assert.True(facts.QueryTaskSettled); Assert.True(facts.ResponseAccepted); Assert.True(facts.ObservationBudgetExpired);
        Assert.True(facts.QueryTaskSettledWhenBudgetObserved);
        Assert.Equal("inconclusive", LocalUpdateHistoryEvidence.Create(started, DateTimeOffset.UtcNow, facts).State);
    }

    [Theory]
    [InlineData(0, false)] [InlineData(1, false)] [InlineData(1, true)]
    public async Task ProjectionMeansAnyKnownWhitelistedFieldAndDoesNotInventEmptyOrCompleteMetadata(int rows, bool known)
    {
        var started = DateTimeOffset.UtcNow.AddSeconds(-1);
        var deadline = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var facts = await LocalUpdateHistoryEvidence.ObserveAsync(Task.FromResult(Response(started.AddMilliseconds(1), rows, known)), deadline.Task, started);
        Assert.Equal(rows > 0 && known, facts.FieldProjectionObserved); Assert.Equal(rows, facts.ReturnedEntryCount);
        Assert.Equal("passed", LocalUpdateHistoryEvidence.Create(started, DateTimeOffset.UtcNow, facts).State);
    }

    [Fact]
    public async Task OlderOrMalformedResponsesCannotQualifyThisRequestedRead()
    {
        var started = DateTimeOffset.UtcNow.AddSeconds(-1); var deadline = new TaskCompletionSource<bool>();
        var old = await LocalUpdateHistoryEvidence.ObserveAsync(Task.FromResult(Response(started.AddSeconds(-1))), deadline.Task, started);
        Assert.True(old.ResponseShapeValid); Assert.False(old.ResponseAccepted); Assert.Null(old.TotalBefore);
        Assert.Equal("failed", LocalUpdateHistoryEvidence.Create(started, DateTimeOffset.UtcNow, old).State);
        var negativeWithRows = Response(started.AddMilliseconds(1)) with { State = CheckState.Failed };
        var malformed = await LocalUpdateHistoryEvidence.ObserveAsync(Task.FromResult(negativeWithRows), deadline.Task, started);
        Assert.False(malformed.ResponseShapeValid); Assert.False(malformed.ResponseAccepted); Assert.Null(malformed.ReturnedEntryCount);
    }

    [Fact]
    public async Task EmptyNegativeResponseKeepsItsProviderStatusWithoutCountsOrProjection()
    {
        var started = DateTimeOffset.UtcNow.AddSeconds(-1); var deadline = new TaskCompletionSource<bool>();
        var facts = await LocalUpdateHistoryEvidence.ObserveAsync(Task.FromResult(CheckResult<WindowsUpdateHistoryReport>.Empty(CheckState.Unavailable, "offline")), deadline.Task, started);
        Assert.Equal("Unavailable", facts.ProviderState); Assert.True(facts.ResponseShapeValid); Assert.False(facts.ResponseAccepted);
        Assert.Null(facts.TotalBefore); Assert.Null(facts.RequestedCount); Assert.Null(facts.ReturnedEntryCount); Assert.Null(facts.FieldProjectionObserved);
        Assert.Equal("inconclusive", LocalUpdateHistoryEvidence.Create(started, DateTimeOffset.UtcNow, facts).State);
    }

    [Theory]
    [InlineData(CheckState.Available)] [InlineData((CheckState)99)]
    public async Task UnsupportedPositiveOrUndefinedEnumsCannotBeReportedAsAcceptedHistory(CheckState state)
    {
        var started = DateTimeOffset.UtcNow.AddSeconds(-1); var deadline = new TaskCompletionSource<bool>();
        var facts = await LocalUpdateHistoryEvidence.ObserveAsync(Task.FromResult(CheckResult<WindowsUpdateHistoryReport>.Empty(state, "source state")), deadline.Task, started);
        Assert.Equal(state == CheckState.Available ? "Available" : "Unrecognized", facts.ProviderState);
        Assert.False(facts.ResponseShapeValid); Assert.False(facts.ResponseAccepted); Assert.Null(facts.ReturnedEntryCount);
        Assert.Equal("failed", LocalUpdateHistoryEvidence.Create(started, DateTimeOffset.UtcNow, facts).State);
    }

    [Fact]
    public async Task CancelledClientTaskDoesNotInventACancelledWindowsServiceOrProviderResponse()
    {
        var started = DateTimeOffset.UtcNow.AddSeconds(-1); var deadline = new TaskCompletionSource<bool>();
        var actual = Task.FromCanceled<CheckResult<WindowsUpdateHistoryReport>>(new CancellationToken(true));
        var facts = await LocalUpdateHistoryEvidence.ObserveAsync(actual, deadline.Task, started);
        Assert.True(facts.QueryTaskSettled); Assert.NotNull(facts.ExceptionHResult);
        Assert.Null(facts.ProviderState); Assert.Null(facts.ResponseShapeValid); Assert.Null(facts.ResponseAccepted);
        var report = LocalUpdateHistoryEvidence.Create(started, DateTimeOffset.UtcNow, facts);
        Assert.Equal("failed", report.State); Assert.Equal("unverified", report.WindowsServiceCompletion);
    }

    [Fact]
    public async Task FaultAfterDeadlineIsObservedAsAnErrorWithoutInventingAProviderResponse()
    {
        var started = DateTimeOffset.UtcNow.AddSeconds(-1);
        var actual = new TaskCompletionSource<CheckResult<WindowsUpdateHistoryReport>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deadline = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        deadline.SetResult(true);
        var observed = LocalUpdateHistoryEvidence.ObserveAsync(actual.Task, deadline.Task, started);
        Assert.False(observed.IsCompleted);
        var error = new InvalidOperationException("private details must not be exported"); actual.SetException(error);
        var facts = await observed;
        Assert.True(facts.QueryTaskSettled); Assert.True(facts.ObservationBudgetExpired); Assert.Equal(error.HResult, facts.ExceptionHResult);
        Assert.False(facts.QueryTaskSettledWhenBudgetObserved);
        Assert.Null(facts.ProviderState); Assert.Null(facts.ResponseShapeValid); Assert.Null(facts.ResponseAccepted);
        Assert.Null(facts.TotalBefore); Assert.Null(facts.FieldProjectionObserved);
        Assert.Equal("failed", LocalUpdateHistoryEvidence.Create(started, DateTimeOffset.UtcNow, facts).State);
    }

    private sealed class MonotonicClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref timestamp);
        internal void Advance(TimeSpan elapsed) => Interlocked.Add(ref timestamp, elapsed.Ticks);
    }

    [Fact]
    public async Task MonotonicExpiryWithoutATimerCallbackCannotAbandonTheActualTaskOrPassItsLateResult()
    {
        var started = DateTimeOffset.UtcNow.AddSeconds(-1); var clock = new MonotonicClock();
        var actual = new TaskCompletionSource<CheckResult<WindowsUpdateHistoryReport>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deadline = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = LocalUpdateHistoryEvidence.ObserveAsync(actual.Task, deadline.Task, started, clock, clock.GetTimestamp());
        clock.Advance(TimeSpan.FromSeconds(120)); await Task.Yield();
        Assert.False(deadline.Task.IsCompleted); Assert.False(observed.IsCompleted); Assert.False(actual.Task.IsCompleted);
        actual.SetResult(Response(started.AddMilliseconds(1)));
        var facts = await observed;
        Assert.True(facts.QueryTaskSettled); Assert.True(facts.ResponseAccepted); Assert.True(facts.ObservationBudgetExpired);
        // The clock was first sampled after the actual task completed; its earlier
        // pending state at 120s is deliberately not reconstructed or asserted.
        Assert.True(facts.QueryTaskSettledWhenBudgetObserved);
        Assert.Null(facts.ExceptionHResult); Assert.False(deadline.Task.IsCompleted);
        Assert.Equal("inconclusive", LocalUpdateHistoryEvidence.Create(started, DateTimeOffset.UtcNow, facts).State);
    }

    [Theory]
    [InlineData(119999, false)]
    [InlineData(120000, true)]
    [InlineData(121000, true)]
    public async Task APreWorkerTimestampIncludesTheQueuedPrefixAtTheExactBudgetBoundary(int elapsedMilliseconds, bool expired)
    {
        var started = DateTimeOffset.UtcNow.AddSeconds(-1); var clock = new MonotonicClock();
        long beforeQueue = clock.GetTimestamp();
        clock.Advance(TimeSpan.FromMilliseconds(elapsedMilliseconds));
        var deadline = new TaskCompletionSource<bool>();
        var facts = await LocalUpdateHistoryEvidence.ObserveAsync(Task.FromResult(Response(started.AddMilliseconds(1))),
            deadline.Task, started, clock, beforeQueue);
        Assert.Equal(expired, facts.ObservationBudgetExpired); Assert.False(deadline.Task.IsCompleted);
        Assert.Equal(expired ? true : (bool?)null, facts.QueryTaskSettledWhenBudgetObserved);
        Assert.Equal(expired ? "inconclusive" : "passed", LocalUpdateHistoryEvidence.Create(started, DateTimeOffset.UtcNow, facts).State);
    }

    [Fact]
    public async Task MonotonicExpiryDuringValidationIsCheckedBeforeTheFinalPositivePacket()
    {
        var started = DateTimeOffset.UtcNow.AddSeconds(-1); var clock = new MonotonicClock();
        var original = Response(started.AddMilliseconds(1)); int counted = 0;
        var response = original with
        {
            Items = new CountSignal<WindowsUpdateHistoryReport>(original.Items[0], () =>
            {
                if (Interlocked.Exchange(ref counted, 1) == 0) clock.Advance(TimeSpan.FromSeconds(120));
            })
        };
        var deadline = new TaskCompletionSource<bool>();
        var facts = await LocalUpdateHistoryEvidence.ObserveAsync(Task.FromResult(response), deadline.Task, started, clock);
        Assert.True(facts.QueryTaskSettled); Assert.True(facts.ResponseAccepted); Assert.True(facts.ObservationBudgetExpired);
        Assert.True(facts.QueryTaskSettledWhenBudgetObserved);
        Assert.False(deadline.Task.IsCompleted); Assert.Equal("inconclusive", LocalUpdateHistoryEvidence.Create(started, DateTimeOffset.UtcNow, facts).State);
    }

    private sealed class BrokenMonotonicClock : TimeProvider
    {
        public override long GetTimestamp() => throw new InvalidOperationException("private clock failure must not escape into evidence");
    }

    [Fact]
    public async Task AFailedClockStillAwaitsActualOwnershipBeforeReportingNumericFailure()
    {
        var started = DateTimeOffset.UtcNow.AddSeconds(-1);
        var actual = new TaskCompletionSource<CheckResult<WindowsUpdateHistoryReport>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deadline = new TaskCompletionSource<bool>();
        var observed = LocalUpdateHistoryEvidence.ObserveAsync(actual.Task, deadline.Task, started, new BrokenMonotonicClock());
        Assert.False(observed.IsCompleted); Assert.False(actual.Task.IsCompleted);
        actual.SetResult(Response(started.AddMilliseconds(1)));
        var facts = await observed;
        Assert.True(facts.QueryTaskSettled); Assert.False(facts.ObservationBudgetExpired); Assert.NotNull(facts.ExceptionHResult);
        Assert.Null(facts.QueryTaskSettledWhenBudgetObserved);
        Assert.Null(facts.ProviderState); Assert.Null(facts.ResponseAccepted); Assert.Null(facts.TotalBefore);
        Assert.Equal("failed", LocalUpdateHistoryEvidence.Create(started, DateTimeOffset.UtcNow, facts).State);
    }

    [Fact]
    public async Task ABackwardMonotonicIntervalCannotQualifyAPositiveResponse()
    {
        var started = DateTimeOffset.UtcNow.AddSeconds(-1); var clock = new MonotonicClock();
        var deadline = new TaskCompletionSource<bool>();
        var facts = await LocalUpdateHistoryEvidence.ObserveAsync(Task.FromResult(Response(started.AddMilliseconds(1))),
            deadline.Task, started, clock, observationStartedTimestamp: TimeSpan.TicksPerSecond);
        Assert.True(facts.QueryTaskSettled); Assert.False(facts.ObservationBudgetExpired); Assert.NotNull(facts.ExceptionHResult);
        Assert.Null(facts.QueryTaskSettledWhenBudgetObserved);
        Assert.Null(facts.ProviderState); Assert.Equal("failed", LocalUpdateHistoryEvidence.Create(started, DateTimeOffset.UtcNow, facts).State);
    }
}
