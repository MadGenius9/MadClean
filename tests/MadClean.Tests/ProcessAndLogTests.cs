using MadClean.Core;
using System.Text.Json;
using Xunit;
namespace MadClean.Tests;

public sealed class ProcessAndLogTests
{
    private static (string File, string[] Args) SleepCommand() => OperatingSystem.IsWindows()
        ? (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"), ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"])
        : ("/bin/sh", ["-c", "sleep 30"]);
    [Fact]
    public async Task ProviderTimeoutFailsInsteadOfReturningEmptySuccess()
    {
        var command = SleepCommand();
        await Assert.ThrowsAsync<TimeoutException>(() => new ReadOnlyProcess().RunAsync(command.File, command.Args, TimeSpan.FromMilliseconds(150), default));
    }
    [Fact]
    public async Task CancellationStopsProviderProcessPromptly()
    {
        var command = SleepCommand(); using var source = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ReadOnlyProcess().RunAsync(command.File, command.Args, TimeSpan.FromSeconds(20), source.Token));
    }
    [Fact]
    public async Task CancellationCompletesEvenWhenTheCallingDispatcherStopsPumping()
    {
        using var source = new CancellationTokenSource();
        var command = SleepCommand();
        var context = new ShutdownContext();
        var previous = SynchronizationContext.Current;
        Task<ProcessResult> query;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            query = new ReadOnlyProcess().RunAsync(command.File, command.Args, TimeSpan.FromSeconds(20), source.Token);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        await Task.Delay(100);
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await query.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, context.PostCount);
    }
    private sealed class ShutdownContext : SynchronizationContext
    {
        private int postCount;
        public int PostCount => Volatile.Read(ref postCount);
        public override void Post(SendOrPostCallback callback, object? state) => Interlocked.Increment(ref postCount);
    }
    [Theory]
    [InlineData("")]
    [InlineData("relative/activity.jsonl")]
    public void MissingAbsoluteLogDestinationDoesNotFallBackToWorkingDirectory(string path)
    {
        Assert.False(new ActivityLog(path).TryWrite(ActivityArea.Session, ActivityOutcome.Started));
    }
    [Fact]
    public async Task OversizedProviderResponseFailsInsteadOfHangingOrReturningPartialData()
    {
        var command = OperatingSystem.IsWindows()
            ? (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"), new[] { "-NoProfile", "-NonInteractive", "-Command", "[Console]::Write('x' * 4200000)" })
            : ("/bin/sh", new[] { "-c", "yes x" });
        await Assert.ThrowsAsync<InvalidDataException>(() => new ReadOnlyProcess().RunAsync(command.Item1, command.Item2, TimeSpan.FromSeconds(15), default));
    }
    [Fact]
    public void ActivityLogContainsOnlyBoundedOperationMetadata()
    {
        var path = Path.Combine(Path.GetTempPath(), "MadCleanLog-" + Guid.NewGuid(), "activity.jsonl");
        try
        {
            var log = new ActivityLog(path); Assert.True(log.TryWrite(ActivityArea.Defender, ActivityOutcome.Completed));
            using var value = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(new[] { "utc", "area", "outcome" }, value.RootElement.EnumerateObject().Select(p => p.Name));
            File.WriteAllText(path, new string('x', 1_048_600));
            Assert.True(log.TryWrite(ActivityArea.Cleanup, ActivityOutcome.Partial));
            Assert.True(File.Exists(path + ".previous")); Assert.True(new FileInfo(path).Length < 1000);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }
    [Fact]
    public void LogWriteFailureIsReportedWithoutCrashing()
    {
        var path = Path.GetTempFileName();
        try { Assert.False(new ActivityLog(Path.Combine(path, "activity.jsonl")).TryWrite(ActivityArea.Session, ActivityOutcome.Started)); }
        finally { File.Delete(path); }
    }
}
