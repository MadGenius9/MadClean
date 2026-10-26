using MadClean.Core;
using Xunit;
namespace MadClean.Tests;

public sealed class CleanupTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "MadCleanTests-" + Guid.NewGuid());
    public CleanupTests() => Directory.CreateDirectory(root);
    private string FileAt(string name, int size = 17, bool old = true)
    {
        var path = Path.Combine(root, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]); if (old) File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-10));
        return path;
    }
    private Task<CleanupReport> Scan(CancellationToken token = default, int limit = 100_000) =>
        new CleanupScanner(limit: limit).ScanAsync([new("Test temporary folder", root)], token);
    [Fact]
    public async Task OnlyOldReadableTemporaryFilesAreInventoriedAndNothingIsDeleted()
    {
        var candidate = FileAt("cache/old.tmp", 31);
        FileAt("recent.tmp", old: false); FileAt("Downloads/download.tmp"); FileAt("Profiles/session.tmp");
        FileAt("private.key"); FileAt("session.sqlite"); FileAt("credentials.json"); FileAt("Firefox/history.tmp");
        var report = await Scan();
        Assert.Equal(candidate, Assert.Single(report.Candidates).Path); Assert.Equal(31, report.TotalBytes);
        Assert.True(report.ExcludedCount >= 5); Assert.True(File.Exists(candidate));
        Assert.Equal(8, Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length);
    }
    [Fact]
    public async Task DuplicateLocationsAreNotDoubleCounted()
    {
        FileAt("item.tmp"); var scanner = new CleanupScanner();
        var report = await scanner.ScanAsync([new("First", root), new("Second", root)], default);
        Assert.Single(report.Candidates); Assert.Equal(17, report.TotalBytes);
    }
    [Fact]
    public async Task MissingLocationIsReportedAndDoesNotHideOtherResults()
    {
        FileAt("old.tmp");
        var report = await new CleanupScanner().ScanAsync([new("Missing", Path.Combine(root, "missing")), new("Readable", root)], default);
        Assert.Single(report.Candidates); Assert.Single(report.Issues);
    }
    [Fact]
    public async Task LockedFileIsExcludedRatherThanCounted()
    {
        var path = FileAt("locked.tmp");
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = await Scan(); Assert.Empty(result.Candidates); Assert.Single(result.Issues);
    }
    [LinuxFact]
    public async Task LinkedDirectoryCannotExposeFilesOutsideScopeOrCreateCycle()
    {
        FileAt("old.tmp");
        // Windows symlink creation requires Developer Mode or privilege. Linux exercises this branch.
        Directory.CreateSymbolicLink(Path.Combine(root, "loop"), root);
        var report = await Scan(); Assert.Single(report.Candidates); Assert.True(report.ExcludedCount >= 1);
    }
    [LinuxFact]
    public async Task ReparsePointInRootAncestorsIsRejected()
    {
        FileAt("real/nested/old.tmp");
        Directory.CreateSymbolicLink(Path.Combine(root, "alias"), Path.Combine(root, "real"));
        var result = await new CleanupScanner().ScanAsync([new("Alias", Path.Combine(root, "alias/nested"))], default);
        Assert.Empty(result.Candidates); Assert.Single(result.Issues);
    }
    [LinuxFact]
    public async Task UnreadableDirectoryProducesAnIssueWhenRunningWithoutRoot()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var blocked = Path.Combine(root, "blocked"); Directory.CreateDirectory(blocked);
        var original = File.GetUnixFileMode(blocked);
        try
        {
            File.SetUnixFileMode(blocked, UnixFileMode.None);
            var report = await Scan();
            Assert.Contains(report.Issues, i => i.Path == blocked);
        }
        finally { File.SetUnixFileMode(blocked, original); }
    }
    [Fact]
    public async Task CancelledInventoryDoesNotReturnASuccessfulEmptyReport()
    {
        using var source = new CancellationTokenSource(); source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Scan(source.Token));
    }
    [Fact]
    public async Task CancellationDuringEnumerationStopsWork()
    {
        for (int i = 0; i < 400; i++) FileAt($"{i}.tmp");
        using var source = new CancellationTokenSource();
        IEnumerable<CleanupLocation> Locations()
        {
            yield return new("First", root);
            source.Cancel();
            yield return new("Second", root);
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CleanupScanner().ScanAsync(Locations(), source.Token));
    }
    [Fact]
    public async Task SafetyLimitProducesPartialInventory()
    {
        FileAt("a.tmp"); FileAt("b.tmp");
        var result = await Scan(limit: 1); Assert.True(result.Truncated); Assert.Single(result.Candidates);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}

internal sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "Requires Linux filesystem permissions/symlink support; Windows junction/ACL checks are in the manual matrix.";
    }
}
