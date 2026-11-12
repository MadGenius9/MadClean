using MadClean.WindowsSmoke;
using Xunit;

namespace MadClean.Tests;

public sealed class ValidationLifetimeTests
{
    [Fact]
    public void BudgetBoundaryAndLateFinalObservationRecordOneFailure()
    {
        int failures = 0;
        var budget = TimeSpan.FromMinutes(4);
        var deadline = new ValidationDeadline(budget, () => failures++);
        deadline.ObserveElapsed(budget - TimeSpan.FromTicks(1));
        Assert.False(deadline.HasExpired);
        Assert.Equal(0, failures);
        // This final observation can be the first one if a timer was delayed.
        deadline.ObserveElapsed(budget);
        deadline.ObserveElapsed(budget + TimeSpan.FromHours(1));
        deadline.ObserveElapsed(TimeSpan.Zero);
        Assert.True(deadline.HasExpired);
        Assert.Equal(1, failures);
    }

    [Fact]
    public void ConcurrentTimerAndFinalObservationsCannotDuplicateFailure()
    {
        int failures = 0;
        var deadline = new ValidationDeadline(TimeSpan.FromMinutes(4), () => Interlocked.Increment(ref failures));
        Parallel.For(0, 128, index => deadline.ObserveElapsed(TimeSpan.FromMinutes(index % 2 == 0 ? 4 : 5)));
        Assert.True(deadline.HasExpired);
        Assert.Equal(1, failures);
    }

    [Fact]
    public async Task CompetingFinalObservationWaitsForFailurePublication()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finalEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        int publications = 0;
        var deadline = new ValidationDeadline(TimeSpan.FromSeconds(1), () =>
        {
            entered.SetResult();
            release.Wait();
            Interlocked.Increment(ref publications);
        });
        var timer = Task.Run(() => deadline.ObserveElapsed(TimeSpan.FromSeconds(1)));
        Task? final = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            final = Task.Run(() =>
            {
                finalEntered.SetResult();
                deadline.ObserveElapsed(TimeSpan.FromSeconds(2));
            });
            await finalEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(final.IsCompleted);
            Assert.Equal(0, publications);
        }
        finally { release.Set(); }
        await timer;
        if (final != null) await final;
        Assert.Equal(1, publications);
        Assert.True(deadline.HasExpired);
    }

    [Fact]
    public void PublicationFailureDoesNotReenterOrResetDeadline()
    {
        int attempts = 0;
        var deadline = new ValidationDeadline(TimeSpan.FromSeconds(1), () =>
        {
            attempts++;
            throw new InvalidOperationException("Inert test publication failure");
        });
        Assert.Throws<InvalidOperationException>(() => deadline.ObserveElapsed(TimeSpan.FromSeconds(1)));
        deadline.ObserveElapsed(TimeSpan.FromSeconds(2));
        Assert.True(deadline.HasExpired);
        Assert.Equal(1, attempts);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonpositiveBudgetIsRejected(long ticks)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new ValidationDeadline(TimeSpan.FromTicks(ticks), () => { }));

    [Fact]
    public async Task CancelledCallerWaitCannotCompleteActualWorkerDrain()
    {
        var worker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = new ValidationWorkerLifetime(worker.Task);
        var drain = lifetime.DrainAsync();
        using var callerStop = new CancellationTokenSource();
        callerStop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.Task.WaitAsync(callerStop.Token));
        Assert.True(lifetime.HasPendingWork);
        Assert.False(drain.IsCompleted);
        Assert.False(worker.Task.IsCompleted);
        worker.SetResult();
        await drain;
        Assert.False(lifetime.HasPendingWork);
    }

    [Fact]
    public async Task ActualFaultIsObservedWithoutReclassifyingItOrAbandoningDrain()
    {
        var worker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = new ValidationWorkerLifetime(worker.Task);
        var drain = lifetime.DrainAsync();
        Assert.False(drain.IsCompleted);
        worker.SetException(new InvalidOperationException("Inert worker fault"));
        await drain;
        Assert.True(worker.Task.IsFaulted);
        Assert.False(lifetime.HasPendingWork);
    }

    [Fact]
    public async Task ActualWorkerCancellationRemainsCancelledAfterDrain()
    {
        var worker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = new ValidationWorkerLifetime(worker.Task);
        var drain = lifetime.DrainAsync();
        Assert.False(drain.IsCompleted);
        worker.SetCanceled();
        await drain;
        Assert.True(worker.Task.IsCanceled);
        Assert.False(lifetime.HasPendingWork);
    }

    [Fact]
    public async Task OwnershipFactsCannotBeReadBeforeTheExactActualWorkerSettles()
    {
        var worker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = new ValidationWorkerLifetime(worker.Task);
        int pendingReads = 0, retentionReads = 0, polls = 0;
        var drain = lifetime.DrainOwnershipAsync(() => { pendingReads++; return false; },
            () => { retentionReads++; return false; }, () => { polls++; return Task.CompletedTask; });
        Assert.True(lifetime.HasPendingWork); Assert.False(drain.IsCompleted);
        Assert.Equal(0, pendingReads); Assert.Equal(0, retentionReads); Assert.Equal(0, polls);
        worker.SetResult();
        await drain.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, pendingReads); Assert.Equal(1, retentionReads); Assert.Equal(0, polls);
        Assert.False(lifetime.HasPendingWork);
    }

    [Theory]
    [InlineData("returned")] [InlineData("faulted")] [InlineData("cancelled")]
    public async Task ActualTerminalTaskNeverSubstitutesForItsStillPendingNativeOwner(string terminal)
    {
        var worker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var poll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var polled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = new ValidationWorkerLifetime(worker.Task);
        bool pending = true; int pendingReads = 0, retentionReads = 0, polls = 0;
        var drain = lifetime.DrainOwnershipAsync(() => { pendingReads++; return pending; },
            () => { retentionReads++; return false; }, () => { polls++; polled.TrySetResult(); return poll.Task; });
        if (terminal == "returned") worker.SetResult();
        else if (terminal == "faulted") worker.SetException(new InvalidOperationException("Inert actual-worker failure"));
        else worker.SetCanceled();
        await polled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(worker.Task.IsCompleted); Assert.False(lifetime.HasPendingWork); Assert.False(drain.IsCompleted);
        Assert.Equal(1, pendingReads); Assert.Equal(1, retentionReads); Assert.Equal(1, polls);
        pending = false; poll.SetResult();
        await drain.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, pendingReads); Assert.Equal(2, retentionReads); Assert.Equal(1, polls);
        Assert.Equal(terminal == "faulted", worker.Task.IsFaulted);
        Assert.Equal(terminal == "cancelled", worker.Task.IsCanceled);
    }

    [Fact]
    public void RetentionIsAPermanentHoldAndASecondCallCannotReplaceItsOriginalObserver()
    {
        var lifetime = new ValidationWorkerLifetime(Task.CompletedTask);
        bool retained = true; int reads = 0, polls = 0, replacements = 0;
        var drain = lifetime.DrainOwnershipAsync(() => { reads++; return false; }, () => retained,
            () => { polls++; return Task.CompletedTask; });
        Assert.False(drain.IsCompleted); Assert.Equal(1, reads); Assert.Equal(0, polls);
        retained = false;
        var again = lifetime.DrainOwnershipAsync(() => { replacements++; return false; }, () => false);
        Assert.Same(drain, again); Assert.False(again.IsCompleted);
        Assert.Equal(1, reads); Assert.Equal(0, replacements); Assert.Equal(0, polls);
    }

    [Theory]
    [InlineData(null, false)] [InlineData(true, null)] [InlineData(false, null)] [InlineData(null, true)]
    public void NullableNativeFactsCannotBePolledOrResetIntoCleanup(bool? pending, bool? retained)
    {
        var lifetime = new ValidationWorkerLifetime(Task.CompletedTask);
        int pendingReads = 0, retentionReads = 0, polls = 0, replacements = 0;
        var drain = lifetime.DrainOwnershipAsync(() => { pendingReads++; return pending; },
            () => { retentionReads++; return retained; }, () => { polls++; return Task.CompletedTask; });
        Assert.False(drain.IsCompleted); Assert.Equal(1, pendingReads); Assert.Equal(1, retentionReads); Assert.Equal(0, polls);
        var again = lifetime.DrainOwnershipAsync(() => { replacements++; return false; }, () => false);
        Assert.Same(drain, again); Assert.False(again.IsCompleted); Assert.Equal(0, replacements);
        Assert.Equal(1, pendingReads); Assert.Equal(1, retentionReads); Assert.Equal(0, polls);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void PredicateFailureRetainsTheOriginalOwnershipPromiseWithoutRetry(bool throwFromPending)
    {
        var lifetime = new ValidationWorkerLifetime(Task.CompletedTask);
        int pendingReads = 0, retentionReads = 0, polls = 0, replacements = 0;
        var drain = lifetime.DrainOwnershipAsync(() =>
        {
            pendingReads++;
            if (throwFromPending) throw new InvalidOperationException("inert pending observation failure");
            return false;
        }, () =>
        {
            retentionReads++;
            throw new InvalidOperationException("inert retention observation failure");
        }, () => { polls++; return Task.CompletedTask; });
        Assert.False(drain.IsCompleted); Assert.Equal(1, pendingReads); Assert.Equal(throwFromPending ? 0 : 1, retentionReads);
        Assert.Equal(0, polls);
        Assert.Same(drain, lifetime.DrainOwnershipAsync(() => { replacements++; return false; }, () => false));
        Assert.Equal(0, replacements); Assert.False(drain.IsCompleted);
    }

    [Theory]
    [InlineData("throw")] [InlineData("fault")] [InlineData("cancel")] [InlineData("null")]
    public void PollingFailureRetainsWithoutReobservingOrInventingSettlement(string failure)
    {
        var lifetime = new ValidationWorkerLifetime(Task.CompletedTask);
        int pendingReads = 0, polls = 0, replacements = 0;
        var drain = lifetime.DrainOwnershipAsync(() => { pendingReads++; return true; }, () => false, () =>
        {
            polls++;
            return failure switch
            {
                "throw" => throw new InvalidOperationException("inert polling failure"),
                "fault" => Task.FromException(new InvalidOperationException("inert async polling failure")),
                "cancel" => Task.FromCanceled(new CancellationToken(true)),
                _ => null!
            };
        });
        Assert.False(drain.IsCompleted); Assert.Equal(1, pendingReads); Assert.Equal(1, polls);
        Assert.Same(drain, lifetime.DrainOwnershipAsync(() => { replacements++; return false; }, () => false));
        Assert.Equal(0, replacements); Assert.Equal(1, pendingReads); Assert.Equal(1, polls); Assert.False(drain.IsCompleted);
    }

    [Fact]
    public async Task RetentionObservedAfterControlledPollingStaysStickyAfterTheFlagsChange()
    {
        var lifetime = new ValidationWorkerLifetime(Task.CompletedTask);
        var poll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondObservation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool pending = true, retained = false; int reads = 0, polls = 0, replacements = 0;
        var drain = lifetime.DrainOwnershipAsync(() => pending, () =>
        {
            bool observed = retained;
            if (++reads == 2) secondObservation.TrySetResult();
            return observed;
        }, () => { polls++; return poll.Task; });
        Assert.False(drain.IsCompleted); Assert.Equal(1, polls);
        pending = false; retained = true; poll.SetResult();
        await secondObservation.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(drain.IsCompleted); Assert.Equal(2, reads); Assert.Equal(1, polls);
        retained = false;
        Assert.Same(drain, lifetime.DrainOwnershipAsync(() => { replacements++; return false; }, () => false));
        Assert.False(drain.IsCompleted); Assert.Equal(0, replacements); Assert.Equal(2, reads); Assert.Equal(1, polls);
    }

    [Fact]
    public async Task MissingObserversStillCannotAbandonAnUnsettledActualWorker()
    {
        var worker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = new ValidationWorkerLifetime(worker.Task);
        var drain = lifetime.DrainOwnershipAsync(null!, () => false);
        Assert.False(drain.IsCompleted); Assert.True(lifetime.HasPendingWork);
        worker.SetResult(); await lifetime.DrainAsync();
        Assert.False(drain.IsCompleted); Assert.False(lifetime.HasPendingWork);
        Assert.Same(drain, lifetime.DrainOwnershipAsync(() => false, () => false));
    }

    [Fact]
    public async Task ReentrantObservationUsesTheSamePromiseWithoutTakingThePredicateUnderAStatusLock()
    {
        var lifetime = new ValidationWorkerLifetime(Task.CompletedTask); Task? nested = null, retry = null;
        using var callbackCompleted = new ManualResetEventSlim();
        bool observedWithoutLock = false;
        int reads = 0, replacements = 0;
        var drain = lifetime.DrainOwnershipAsync(() =>
        {
            reads++;
            // A same-thread Monitor reentry would not expose a held status lock.
            // This controlled managed caller must return on another thread while
            // the first predicate is still active. The safety wait is test-only.
            retry = Task.Run(() =>
            {
                nested = lifetime.DrainOwnershipAsync(() => { replacements++; return false; }, () => false);
                callbackCompleted.Set();
            });
            observedWithoutLock = callbackCompleted.Wait(TimeSpan.FromSeconds(5));
            return observedWithoutLock ? false : null;
        }, () => false);
        Assert.NotNull(retry); await retry.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(observedWithoutLock);
        Assert.Same(drain, nested); await drain.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, reads); Assert.Equal(0, replacements);
    }
}
