using System.ComponentModel;
using MadClean.Windows;
using Xunit;

namespace MadClean.Tests;

public sealed class MsiClientStartBoundaryTests
{
    [Theory]
    [InlineData("Win32")]
    [InlineData("IO")]
    [InlineData("Unauthorized")]
    [InlineData("InvalidOperation")]
    [InlineData("Argument")]
    [InlineData("NotSupported")]
    [InlineData("Security")]
    [InlineData("OperationCancelled")]
    [InlineData("TaskCancelled")]
    [InlineData("Format")]
    [InlineData("Other")]
    public void AnyThrownEnteredStartRetainsUnknownInsteadOfInventingFalse(string kind)
    {
        Exception expected = kind switch
        {
            "Win32" => new Win32Exception(5, "secret provider details"),
            "IO" => new IOException("secret provider details"),
            "Unauthorized" => new UnauthorizedAccessException("secret provider details"),
            "InvalidOperation" => new InvalidOperationException("secret provider details"),
            "Argument" => new ArgumentException("secret provider details"),
            "NotSupported" => new NotSupportedException("secret provider details"),
            "Security" => new System.Security.SecurityException("secret provider details"),
            "OperationCancelled" => new OperationCanceledException("secret provider details"),
            "TaskCancelled" => new TaskCanceledException("secret provider details"),
            "Format" => new FormatException("secret provider details"),
            _ => new Exception("secret provider details")
        };
        var boundary = new MsiClientStartBoundary();
        int entries = 0;
        var thrown = Assert.ThrowsAny<Exception>(() => boundary.Invoke(() =>
        {
            entries++;
            Assert.True(boundary.Attempted);
            Assert.Null(boundary.ReturnedStarted);
            Assert.True(boundary.RequiresRetention);
            throw expected;
        }));

        Assert.Same(expected, thrown);
        Assert.Equal(1, entries);
        Assert.True(boundary.Attempted);
        Assert.Null(boundary.ReturnedStarted);
        Assert.True(boundary.RequiresRetention);
        Assert.Throws<InvalidOperationException>(() => boundary.Invoke(() => { entries++; return false; }));
        Assert.Equal(1, entries);
        Assert.Null(boundary.ReturnedStarted);
        Assert.True(boundary.RequiresRetention);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NormalReturnRecordsOnlyActualBooleanAndCannotBeRepeated(bool observed)
    {
        var boundary = new MsiClientStartBoundary();
        Assert.False(boundary.Attempted);
        Assert.Null(boundary.ReturnedStarted);
        Assert.False(boundary.RequiresRetention);
        int entries = 0;
        Assert.Equal(observed, boundary.Invoke(() => { entries++; return observed; }));
        Assert.True(boundary.Attempted);
        Assert.Equal(observed, boundary.ReturnedStarted);
        Assert.Equal(observed, boundary.RequiresRetention);
        Assert.Throws<InvalidOperationException>(() => boundary.Invoke(() => { entries++; return !observed; }));
        Assert.Equal(1, entries);
        Assert.Equal(observed, boundary.ReturnedStarted);
        Assert.Equal(observed, boundary.RequiresRetention);
    }

    [Fact]
    public void MissingCallbackRefusesBeforeEntryWithoutCreatingAnObservation()
    {
        var boundary = new MsiClientStartBoundary();
        Assert.Throws<ArgumentNullException>(() => boundary.Invoke(null!));
        Assert.False(boundary.Attempted);
        Assert.Null(boundary.ReturnedStarted);
        Assert.False(boundary.RequiresRetention);
        // An argument refusal is not entry; exactly one genuine call is still possible.
        Assert.False(boundary.Invoke(() => false));
        Assert.True(boundary.Attempted);
        Assert.False(boundary.ReturnedStarted);
    }

    [Fact]
    public async Task ConcurrentRepeatCannotEnterOrSupplyFalseWhileActualStartIsStillRunning()
    {
        var boundary = new MsiClientStartBoundary();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        int entries = 0;
        var actual = Task.Run(() => boundary.Invoke(() =>
        {
            Interlocked.Increment(ref entries);
            entered.SetResult(true);
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Controlled test did not release its callback.");
            return true;
        }));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(boundary.Attempted);
            Assert.Null(boundary.ReturnedStarted);
            Assert.True(boundary.RequiresRetention);
            Assert.Throws<InvalidOperationException>(() => boundary.Invoke(() => { Interlocked.Increment(ref entries); return false; }));
            Assert.Equal(1, Volatile.Read(ref entries));
            Assert.Null(boundary.ReturnedStarted);
            Assert.True(boundary.RequiresRetention);
        }
        finally { release.Set(); await actual.WaitAsync(TimeSpan.FromSeconds(10)); }
        Assert.True(boundary.ReturnedStarted);
        Assert.True(boundary.RequiresRetention);
        Assert.Equal(1, entries);
    }
}
