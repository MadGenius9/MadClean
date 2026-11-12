using System.Text.Json;
using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class ActivityLogBoundaryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "MadCleanLogBoundary-" + Guid.NewGuid());
    private string LogPath => Path.Combine(root, "nested", "activity.jsonl");
    private static bool Write(string path) => new ActivityLog(path).TryWrite(ActivityArea.Session, ActivityOutcome.Completed);

    [Fact]
    public void UndefinedEnumsAndMalformedPathsCannotCreateTheLogDirectory()
    {
        Assert.False(new ActivityLog(LogPath).TryWrite((ActivityArea)(-1), ActivityOutcome.Completed));
        Assert.False(new ActivityLog(LogPath).TryWrite(ActivityArea.Session, (ActivityOutcome)999));
        foreach (var path in new[] { "", "relative/activity.jsonl", LogPath + '\0', LogPath + '\n', root + Path.DirectorySeparatorChar,
                     Path.Combine(root, "parent", "..", "activity.jsonl"), new string('x', 5000) })
            Assert.False(Write(path));
        Assert.False(Write(null!));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void MissingAncestorsAreCreatedAndEveryRecordHasOnlyUtcAndDefinedEnumFields()
    {
        Assert.True(Write(LogPath));
        Assert.True(new ActivityLog(LogPath).TryWrite(ActivityArea.AppPackageMetadata, ActivityOutcome.Partial));
        var lines = File.ReadAllLines(LogPath);
        Assert.Equal(2, lines.Length);
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            Assert.Equal(new[] { "utc", "area", "outcome" }, document.RootElement.EnumerateObject().Select(p => p.Name));
            var utc = document.RootElement.GetProperty("utc").GetDateTimeOffset();
            Assert.Equal(TimeSpan.Zero, utc.Offset);
            Assert.InRange(utc, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow);
            Assert.True(Enum.TryParse<ActivityArea>(document.RootElement.GetProperty("area").GetString(), out var area) && Enum.IsDefined(area));
            Assert.True(Enum.TryParse<ActivityOutcome>(document.RootElement.GetProperty("outcome").GetString(), out var outcome) && Enum.IsDefined(outcome));
            Assert.True(line.Length < 1024);
        }
        Assert.False(File.Exists(LogPath + ".previous"));
    }

    [Fact]
    public void RotationKeepsOnlyCurrentAndOnePreviousFileAndRetainsTheThresholdPolicy()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        File.WriteAllText(LogPath, new string('a', 1_048_576));
        Assert.True(Write(LogPath)); // Exactly the limit is appended; the next write rotates.
        Assert.False(File.Exists(LogPath + ".previous"));
        var firstGeneration = File.ReadAllBytes(LogPath);
        Assert.True(Write(LogPath));
        Assert.Equal(firstGeneration, File.ReadAllBytes(LogPath + ".previous"));
        Assert.Single(File.ReadAllLines(LogPath));
        using (JsonDocument.Parse(File.ReadAllText(LogPath))) { }
        File.WriteAllText(LogPath, new string('b', 1_048_577));
        Assert.True(Write(LogPath));
        Assert.Equal(new string('b', 1_048_577), File.ReadAllText(LogPath + ".previous"));
        Assert.Equal(2, Directory.GetFiles(Path.GetDirectoryName(LogPath)!).Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectoryDestinationsAreRefusedWithoutModifyingTheOtherCandidate(bool previous)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        var directory = previous ? LogPath + ".previous" : LogPath;
        var sentinel = Path.Combine(directory, "sentinel");
        Directory.CreateDirectory(directory);
        File.WriteAllText(sentinel, "preserve directory");
        if (previous) File.WriteAllText(LogPath, new string('x', 1_048_577));
        Assert.False(Write(LogPath));
        Assert.Equal("preserve directory", File.ReadAllText(sentinel));
        if (previous) Assert.Equal(1_048_577, new FileInfo(LogPath).Length);
        Assert.Equal(previous ? 2 : 1, Directory.GetFileSystemEntries(Path.GetDirectoryName(LogPath)!).Length);
    }

    [Fact]
    public void FileAncestorIsNotReplacedOrWrittenThrough()
    {
        Directory.CreateDirectory(root);
        var parent = Path.GetDirectoryName(LogPath)!;
        File.WriteAllText(parent, "not a directory");
        Assert.False(Write(LogPath));
        Assert.Equal("not a directory", File.ReadAllText(parent));
        Assert.Single(Directory.GetFileSystemEntries(root));
    }

    [Fact]
    public void FailureThenRecoveryWritesOnlyTheLaterSuccessfulAttemptWithoutReplay()
    {
        Directory.CreateDirectory(root);
        var blocked = Path.GetDirectoryName(LogPath)!;
        File.WriteAllText(blocked, "owned blocking file");
        var log = new ActivityLog(LogPath);
        Assert.False(log.TryWrite(ActivityArea.Defender, ActivityOutcome.Failed));
        Assert.Equal("owned blocking file", File.ReadAllText(blocked));
        File.Delete(blocked); // Change only this test's owned obstacle.
        Assert.True(log.TryWrite(ActivityArea.Cleanup, ActivityOutcome.Completed));
        var line = Assert.Single(File.ReadAllLines(LogPath));
        using var document = JsonDocument.Parse(line);
        Assert.Equal("Cleanup", document.RootElement.GetProperty("area").GetString());
        Assert.Equal("Completed", document.RootElement.GetProperty("outcome").GetString());
        Assert.False(File.Exists(LogPath + ".previous"));
    }

    [Fact]
    public void ExclusiveAppendFailurePreservesExistingCandidates()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        File.WriteAllText(LogPath, "current");
        File.WriteAllText(LogPath + ".previous", "previous");
        using (var held = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.False(Write(LogPath));
        Assert.Equal("current", File.ReadAllText(LogPath));
        Assert.Equal("previous", File.ReadAllText(LogPath + ".previous"));
    }

    [LinuxFact]
    public void LinkedCurrentAndPreviousDestinationsNeverChangeTheirTargetsOrRotateCurrent()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        var target = Path.Combine(root, "outside");
        File.WriteAllText(target, "outside must stay intact");
        File.CreateSymbolicLink(LogPath, target);
        Assert.False(Write(LogPath));
        Assert.Equal("outside must stay intact", File.ReadAllText(target));
        Assert.True((File.GetAttributes(LogPath) & FileAttributes.ReparsePoint) != 0);
        File.Delete(LogPath);
        File.WriteAllText(LogPath, new string('c', 1_048_577));
        File.CreateSymbolicLink(LogPath + ".previous", target);
        Assert.False(Write(LogPath));
        Assert.Equal(new string('c', 1_048_577), File.ReadAllText(LogPath));
        Assert.Equal("outside must stay intact", File.ReadAllText(target));
        Assert.True((File.GetAttributes(LogPath + ".previous") & FileAttributes.ReparsePoint) != 0);
    }

    [LinuxFact]
    public void LinkedAncestorCannotReceiveMissingDescendants()
    {
        Directory.CreateDirectory(root);
        var target = Path.Combine(root, "outside");
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(Path.GetDirectoryName(LogPath)!, target);
        Assert.False(Write(Path.Combine(Path.GetDirectoryName(LogPath)!, "new", "activity.jsonl")));
        Assert.Empty(Directory.GetFileSystemEntries(target));
    }

    [LinuxFact]
    public void DanglingDestinationPreviousAndAncestorArePreservedWithoutCreatingTheirTargets()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        var absent = Path.Combine(root, "absent");
        File.CreateSymbolicLink(LogPath, absent);
        Assert.False(Write(LogPath));
        Assert.False(File.Exists(absent));
        Assert.True((File.GetAttributes(LogPath) & FileAttributes.ReparsePoint) != 0);
        File.Delete(LogPath);
        File.CreateSymbolicLink(LogPath + ".previous", absent);
        Assert.False(Write(LogPath));
        Assert.False(File.Exists(LogPath));
        File.Delete(LogPath + ".previous");
        Directory.Delete(Path.GetDirectoryName(LogPath)!);
        Directory.CreateSymbolicLink(Path.GetDirectoryName(LogPath)!, absent);
        Assert.False(Write(LogPath));
        Assert.False(Directory.Exists(absent));
        Assert.True((File.GetAttributes(Path.GetDirectoryName(LogPath)!) & FileAttributes.ReparsePoint) != 0);
    }

    [WindowsFact]
    public void WindowsNonordinaryPathsAreRefusedWithoutCreatingAnOrdinarySibling()
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var path in new[] { LogPath + ":alternate", @"\\server\share\activity.jsonl", @"\\?\C:\activity.jsonl",
                     Path.Combine(root, "NUL.log"), Path.Combine(root, "trailing. ", "activity.jsonl"), Path.Combine(root, "COM¹.log") })
            Assert.False(Write(path));
        Assert.False(Directory.Exists(root));
    }

    private sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "Requires Windows path semantics.";
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
