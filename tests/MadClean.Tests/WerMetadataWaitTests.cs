using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class WerMetadataWaitTests
{
    [Fact]
    public async Task CallerTimeoutReturnsUnknownWhileItsWorkerRemainsIncomplete()
    {
        var worker = new TaskCompletionSource<CheckResult<WerMetadataReport>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = await WerMetadataWait.AwaitAsync(worker.Task, TimeSpan.FromMilliseconds(20), default);
        Assert.Equal(CheckState.Unknown, result.State); Assert.Empty(result.Items);
        Assert.False(worker.Task.IsCompleted);
        Assert.Contains("may still be running", result.Message);
        worker.SetResult(CheckResult<WerMetadataReport>.Empty(CheckState.Available, "Test-only late completion"));
        Assert.Equal(CheckState.Available, (await worker.Task).State);
        Assert.Equal(CheckState.Unknown, result.State); // A late result does not rewrite the earlier view outcome.
    }

    [Fact]
    public async Task CallerCancellationIsARequestAndDoesNotCancelOrReleaseItsWorker()
    {
        using var source = new CancellationTokenSource();
        var worker = new TaskCompletionSource<CheckResult<WerMetadataReport>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = WerMetadataWait.AwaitAsync(worker.Task, TimeSpan.FromSeconds(10), source.Token);
        source.Cancel();
        var result = await waiting;
        Assert.Equal(CheckState.Cancelled, result.State); Assert.Empty(result.Items);
        Assert.Contains("not proof a native call was stopped", result.Message);
        Assert.False(worker.Task.IsCompleted);
        worker.SetCanceled();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.Task);
    }

    [Fact]
    public async Task CompletedWorkerResultIsPreservedWithoutInventingPendingWork()
    {
        var actual = CheckResult<WerMetadataReport>.Empty(CheckState.Unavailable, "Test-only unavailable store");
        var result = await WerMetadataWait.AwaitAsync(Task.FromResult(actual), TimeSpan.FromSeconds(1), default);
        Assert.Same(actual, result);
    }

    [Fact]
    public async Task LateWorkerFailureDoesNotTurnTheTimedOutViewIntoSuccess()
    {
        var worker = new TaskCompletionSource<CheckResult<WerMetadataReport>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = await WerMetadataWait.AwaitAsync(worker.Task, TimeSpan.FromMilliseconds(20), default);
        worker.SetException(new InvalidOperationException("Test-only late native failure"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => worker.Task);
        Assert.Equal(CheckState.Unknown, result.State); Assert.Empty(result.Items);
    }

    [Fact]
    public async Task InvalidUnboundedCallerWaitIsRejected()
    {
        var worker = Task.FromResult(CheckResult<WerMetadataReport>.Empty(CheckState.Unknown, "Test-only"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => WerMetadataWait.AwaitAsync(worker, Timeout.InfiniteTimeSpan, default));
    }
}
