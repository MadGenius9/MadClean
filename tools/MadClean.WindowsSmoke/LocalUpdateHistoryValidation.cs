using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using MadClean.Core;
using MadClean.Validation;
using MadClean.Windows;

namespace MadClean.WindowsSmoke;

/// <summary>
/// One explicit local history read, not an online applicability check or an action.
/// The real adapter is constructed here; no provider/CI/configuration substitution exists.
/// </summary>
internal static class LocalUpdateHistoryValidation
{
    private const int MaximumReportBytes = 8192;
    private static Owner? rootedOwner;

    private sealed class Owner
    {
        internal readonly WindowsUpdateHistoryInventory Inventory = new();
        internal Task<CheckResult<WindowsUpdateHistoryReport>>? Actual;
    }

    internal static int Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task<int> RunAsync()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) ||
            RuntimeInformation.ProcessArchitecture != Architecture.X64) return 2;

        string output;
        try
        {
            string repository = Path.GetFullPath(Environment.CurrentDirectory);
            CheckAncestors(repository);
            CheckRegularFile(Path.Combine(repository, "MadClean.sln"));
            foreach (string project in new[] { "MadClean.App", "MadClean.Windows" })
            {
                string folder = Path.Combine(repository, "src", project);
                CheckAncestors(folder);
                CheckRegularFile(Path.Combine(folder, project + ".csproj"));
            }
            output = ReserveOutput(repository);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or System.Security.SecurityException)
        { return 2; }

        // Root the concrete adapter before a worker can enter even its synchronous prefix.
        // The task slot records the real worker; a deadline never replaces that owner.
        var owner = new Owner();
        if (Interlocked.CompareExchange(ref rootedOwner, owner, null) != null) return 1;
        using var stopTimer = new CancellationTokenSource();
        DateTimeOffset started = DateTimeOffset.UtcNow;
        try
        {
            // Capture before either timer or worker scheduling: a delayed timer callback
            // cannot let an over-budget worker manufacture an in-budget observation.
            long observationStamp = TimeProvider.System.GetTimestamp();
            Task deadline = Task.Delay(LocalUpdateHistoryEvidence.ObservationBudget, stopTimer.Token);
            owner.Actual = Task.Run(() => owner.Inventory.ReadAsync(CancellationToken.None), CancellationToken.None);
            var facts = await LocalUpdateHistoryEvidence.ObserveAsync(owner.Actual, deadline, started,
                observationStartedTimestamp: observationStamp).ConfigureAwait(false);
            // ObserveAsync must drain the actual worker, including after its budget expires.
            // Do not turn a completed managed task into independent native cleanup proof.
            if (!owner.Actual.IsCompleted) return 1;
            var report = LocalUpdateHistoryEvidence.Create(started, DateTimeOffset.UtcNow, facts);
            if (!LocalUpdateHistoryEvidence.IsConsistent(report, DateTimeOffset.UtcNow)) return 1;
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(report, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            });
            if (bytes.Length > MaximumReportBytes) return 1;
            CheckAncestors(output);
            using (var file = new FileStream(Path.Combine(output, "report.json"), FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            Console.WriteLine("Local history report: artifacts/local-update-history-validation/" + Path.GetFileName(output) + "/report.json");
            Console.WriteLine("Bounded history response only; current installation, Windows service completion, maintenance and physical input remain unverified.");
            return report.State switch { "passed" => 0, "inconclusive" => 2, _ => 1 };
        }
        catch (Exception exception)
        {
            // Fixed numeric failure only: never export provider messages or raw history.
            Console.Error.WriteLine($"Local history observation failed (0x{exception.HResult:X8}); no Windows qualification is claimed.");
            return 1;
        }
        finally
        {
            // Output/observer faults cannot release a still-running query. This is not a
            // request to cancel WUA service work, kill a maintenance process or retry.
            if (owner.Actual is { } actual)
            {
                try { await actual.ConfigureAwait(false); }
                catch (Exception) { /* The task's actual fault is observed; no result is promoted. */ }
            }
            stopTimer.Cancel(); // Timer only; never passed to the inventory or its client.
            Interlocked.CompareExchange(ref rootedOwner, null, owner);
            GC.KeepAlive(owner);
        }
    }

    private static string ReserveOutput(string repository)
    {
        CheckAncestors(repository);
        string parent = repository;
        foreach (string name in new[] { "artifacts", "local-update-history-validation" })
        {
            CheckAncestors(parent);
            string child = Path.Combine(parent, name);
            var attributes = ExistingAttributes(child);
            if (attributes is { } value && (value & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != FileAttributes.Directory)
                throw new IOException();
            if (attributes == null) Directory.CreateDirectory(child);
            CheckAncestors(child);
            parent = child;
        }
        string output = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        CheckAncestors(parent);
        if (ExistingAttributes(output) != null) throw new IOException();
        Directory.CreateDirectory(output);
        CheckAncestors(output);
        if (Directory.EnumerateFileSystemEntries(output).Any()) throw new IOException();
        return output;
    }

    private static void CheckRegularFile(string path)
    {
        var attributes = ExistingAttributes(path);
        if (attributes is not { } value || (value & (FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.Device)) != 0)
            throw new IOException();
    }

    private static FileAttributes? ExistingAttributes(string path)
    {
        if (new DirectoryInfo(path).LinkTarget != null || new FileInfo(path).LinkTarget != null) throw new IOException();
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static void CheckAncestors(string path)
    {
        string full = Path.GetFullPath(path);
        string? root = Path.GetPathRoot(full);
        if (root is not { Length: 3 } || !char.IsAsciiLetter(root[0]) || root[1] != ':' ||
            root[2] != Path.DirectorySeparatorChar || full.StartsWith(@"\\", StringComparison.Ordinal) ||
            new DriveInfo(root).DriveType != DriveType.Fixed) throw new IOException();
        var ancestry = new Stack<string>();
        for (DirectoryInfo? directory = new(full); directory != null; directory = directory.Parent)
            ancestry.Push(directory.FullName);
        foreach (string component in ancestry)
        {
            var attributes = ExistingAttributes(component);
            if (attributes is not { } value || (value & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != FileAttributes.Directory)
                throw new IOException();
        }
    }
}
