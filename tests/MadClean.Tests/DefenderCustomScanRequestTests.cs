using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class DefenderCustomScanRequestTests
{
    private static DefenderCustomScanDraft Draft(string folder = @"C:\Review\Fixture") =>
        Assert.IsType<DefenderCustomScanDraft>(DefenderCustomScanDraft.Prepare(folder));

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"\\fileserver\share\folder")]
    [InlineData(@"C:\Review\..\Fixture")]
    [InlineData(@"C:\Review\*")]
    [InlineData(@"C:\Review\\Fixture")]
    [InlineData(@"C:\Review\NUL")]
    [InlineData(@"C:\Review\Fixture:stream")]
    public void AmbiguousOrOverbroadInputsCannotCreateASelection(string folder) =>
        Assert.Null(DefenderCustomScanDraft.Prepare(folder));

    [Fact]
    public async Task ClosedClientAndStaleOrCancelledConfirmationNeverAdmitARequest()
    {
        var draft = Draft();
        var client = new TestClient();
        var request = new DefenderCustomScanRequest(client);

        Assert.Equal(DefenderScanSubmission.Unavailable,
            await new DefenderCustomScanRequest(null).SubmitAsync(draft, draft));
        Assert.Equal(DefenderScanSubmission.NotSubmitted, await request.SubmitAsync(draft, Draft()));
        Assert.Equal(DefenderScanSubmission.NotSubmitted,
            await request.SubmitAsync(draft, draft, new CancellationToken(true)));
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task AdmittedRequestOwnsTheClientThroughCallerCancellationAndRejectsReplay()
    {
        var draft = Draft();
        var other = Draft(@"C:\Review\Other");
        var client = new TestClient();
        var request = new DefenderCustomScanRequest(client);
        using var caller = new CancellationTokenSource();

        var first = request.SubmitAsync(draft, draft, caller.Token);
        Assert.Same(draft, client.Received);
        caller.Cancel();
        Assert.False(first.IsCompleted);
        Assert.Equal(DefenderScanSubmission.Busy, await request.SubmitAsync(other, other));

        client.Result.SetResult(DefenderScanSubmission.RequestReturnedWithoutCommandError);
        Assert.Equal(DefenderScanSubmission.RequestReturnedWithoutCommandError, await first);
        Assert.Equal(DefenderScanSubmission.NotSubmitted, await request.SubmitAsync(draft, draft));
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task ClientCancellationAfterAdmissionLeavesAnUnknownOutcome()
    {
        var draft = Draft();
        var client = new TestClient();
        var request = new DefenderCustomScanRequest(client);
        var submission = request.SubmitAsync(draft, draft);
        client.Result.SetCanceled();

        Assert.Equal(DefenderScanSubmission.Unknown, await submission);
        Assert.Equal(DefenderScanSubmission.NotSubmitted, await request.SubmitAsync(draft, draft));
        Assert.Equal(1, client.Calls);
    }

    private sealed class TestClient : IDefenderCustomScanClient
    {
        public TaskCompletionSource<DefenderScanSubmission> Result { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DefenderCustomScanDraft? Received { get; private set; }
        public int Calls { get; private set; }

        public Task<DefenderScanSubmission> SubmitAsync(DefenderCustomScanDraft draft)
        {
            Calls++;
            Received = draft;
            return Result.Task;
        }
    }
}
