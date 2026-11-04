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
}
