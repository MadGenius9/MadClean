using System.Diagnostics;
using System.Text;

namespace MadClean.Core;

public sealed record ProcessResult(int ExitCode, string Output);

public sealed class ReadOnlyProcess
{
    public async Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        token.ThrowIfCancellationRequested();
        process.Start();
        try
        {
            var output = ReadBoundedAsync(process.StandardOutput, deadline.Token);
            var errors = ReadBoundedAsync(process.StandardError, deadline.Token);
            // A faulted reader must kill the process, rather than leave it blocked on a full pipe.
            var exited = process.WaitForExitAsync(deadline.Token);
            foreach (var task in new Task[] { output, errors })
                _ = task.ContinueWith(_ => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } },
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            await Task.WhenAll(output, errors, exited);
            return new(process.ExitCode, await output);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("Read-only Windows query timed out. Retry or check the Windows service."); }
        finally
        {
            try { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
            catch (InvalidOperationException) { }
        }
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder(); var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            if (text.Length + read > 4_194_304) throw new InvalidDataException("Query output exceeded the safety limit.");
            text.Append(buffer, 0, read);
        }
        return text.ToString();
    }
}
