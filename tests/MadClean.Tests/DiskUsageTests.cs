using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class DiskUsageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "MadCleanDiskUsage-" + Guid.NewGuid());
    public DiskUsageTests() => Directory.CreateDirectory(root);

    private string FileAt(string relativePath, int bytes)
    {
        string path = Path.GetFullPath(Path.Combine(root, relativePath));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public async Task MapUsesActualLogicalLengthsAndSortsKnownChildrenWithoutChangingFiles()
    {
        var large = FileAt("large/five.bin", 5);
        FileAt("small.bin", 3);
        FileAt("large/nested/two.bin", 2);
        Directory.CreateDirectory(Path.Combine(root, "empty"));

        var report = await new DiskUsageScanner().ScanAsync(root, default);

        Assert.False(report.Truncated);
        Assert.Empty(report.Issues);
        Assert.Equal(0, report.SkippedEntries);
        Assert.Equal(10, report.Root.Bytes);
        Assert.True(report.Root.IsDirectory);
        Assert.Equal(new[] { "large", "small.bin", "empty" }, report.Root.Children.Select(node => node.Name));
        var directory = report.Root.Children[0];
        Assert.Equal(7, directory.Bytes);
        Assert.Equal(new[] { "five.bin", "nested" }, directory.Children.Select(node => node.Name));
        Assert.Equal(3, Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length);
        Assert.Equal(new byte[5], File.ReadAllBytes(large));
    }

    [Fact]
    public async Task LockedFileCanBeCountedWithoutOpeningItsDataStream()
    {
        var path = FileAt("active.bin", 19);
        using var handle = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var report = await new DiskUsageScanner().ScanAsync(root, default);

        Assert.Equal(19, report.Root.Bytes);
        Assert.Empty(report.Issues);
        Assert.Equal(path, Assert.Single(report.Root.Children).Path);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative/folder")]
    public async Task UnselectedOrRelativeRootsAreRejectedRatherThanResolvedAgainstWorkingDirectory(string path)
    {
        var report = await new DiskUsageScanner().ScanAsync(path, default);

        Assert.Equal(0, report.Root.Bytes);
        Assert.Empty(report.Root.Children);
        Assert.Single(report.Issues);
        Assert.Contains("absolute", report.Issues[0].Reason);
    }

    [Fact]
    public async Task FileRootAndMissingFolderAreNotReportedAsSuccessfulEmptyFolders()
    {
        var file = FileAt("not-a-folder.bin", 17);
        var fileResult = await new DiskUsageScanner().ScanAsync(file, default);
        var missingResult = await new DiskUsageScanner().ScanAsync(Path.Combine(root, "missing"), default);

        Assert.Empty(fileResult.Root.Children);
        Assert.Single(fileResult.Issues);
        Assert.Empty(missingResult.Root.Children);
        Assert.Single(missingResult.Issues);
    }

    [Fact]
    public async Task EntryBudgetKeepsKnownTotalsButMarksRemainingWorkIncomplete()
    {
        FileAt("a.bin", 2); FileAt("b.bin", 2); FileAt("c.bin", 2);

        var report = await new DiskUsageScanner(maxEntries: 2).ScanAsync(root, default);

        Assert.True(report.Truncated);
        Assert.Equal(4, report.Root.Bytes);
        Assert.Equal(2, report.Root.Children.Count);
        Assert.True(report.SkippedEntries > 0);
        Assert.Contains(report.Issues, issue => issue.Reason.Contains("entry limit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DepthLimitDoesNotInventTotalsForUnvisitedDescendants()
    {
        FileAt("known.bin", 3);
        FileAt("child/also-known.bin", 5);
        FileAt("child/deeper/unknown.bin", 7);

        var report = await new DiskUsageScanner(maxDepth: 1).ScanAsync(root, default);

        Assert.True(report.Truncated);
        Assert.Equal(8, report.Root.Bytes);
        var child = Assert.Single(report.Root.Children, node => node.IsDirectory);
        Assert.Equal(5, child.Bytes);
        var notScanned = Assert.Single(child.Children, node => node.IsDirectory);
        Assert.Equal(0, notScanned.Bytes);
        Assert.Empty(notScanned.Children);
        Assert.Contains(report.Issues, issue => issue.Path == notScanned.Path && issue.Reason.Contains("unknown", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CancellationIsNotConvertedToAnEmptySuccessfulMap()
    {
        FileAt("known.bin", 5);
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DiskUsageScanner().ScanAsync(root, source.Token));
    }

    [LinuxFact]
    public async Task FileAndDirectoryLinksAreExcludedWithoutDuplicatingBytesOrFollowingCycles()
    {
        FileAt("real/known.bin", 11);
        File.CreateSymbolicLink(Path.Combine(root, "linked.bin"), Path.Combine(root, "real/known.bin"));
        Directory.CreateSymbolicLink(Path.Combine(root, "cycle"), root);

        var report = await new DiskUsageScanner().ScanAsync(root, default);

        Assert.Equal(11, report.Root.Bytes);
        Assert.Equal(2, report.SkippedEntries);
        Assert.Equal(2, report.Issues.Count);
        Assert.DoesNotContain(report.Root.Children, node => node.Name is "linked.bin" or "cycle");
    }

    [LinuxFact]
    public async Task ReparseAncestorInSelectedPathIsRejectedBeforeEnumeration()
    {
        FileAt("real/nested/known.bin", 11);
        Directory.CreateSymbolicLink(Path.Combine(root, "alias"), Path.Combine(root, "real"));

        var report = await new DiskUsageScanner().ScanAsync(Path.Combine(root, "alias/nested"), default);

        Assert.Equal(0, report.Root.Bytes);
        Assert.Empty(report.Root.Children);
        Assert.Single(report.Issues);
        Assert.Contains("reparse", report.Issues[0].Reason, StringComparison.OrdinalIgnoreCase);
    }

    [LinuxFact]
    public async Task InaccessibleDirectoryDoesNotHideReadableSiblingTotals()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        FileAt("known.bin", 7);
        FileAt("blocked/unknown.bin", 13);
        string blocked = Path.Combine(root, "blocked");
        var permissions = File.GetUnixFileMode(blocked);
        try
        {
            File.SetUnixFileMode(blocked, UnixFileMode.None);
            var report = await new DiskUsageScanner().ScanAsync(root, default);
            Assert.Equal(7, report.Root.Bytes);
            Assert.Contains(report.Issues, issue => issue.Path == blocked);
            Assert.True(report.SkippedEntries > 0);
        }
        finally { File.SetUnixFileMode(blocked, permissions); }
    }

    [LinuxFact]
    public async Task UnreadableContentsDoNotNeedToBeOpenedToCollectLogicalLength()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        string file = FileAt("metadata-only.bin", 23);
        var permissions = File.GetUnixFileMode(file);
        try
        {
            File.SetUnixFileMode(file, UnixFileMode.None);
            var report = await new DiskUsageScanner().ScanAsync(root, default);
            Assert.Equal(23, report.Root.Bytes);
            Assert.Empty(report.Issues);
        }
        finally { File.SetUnixFileMode(file, permissions); }
    }

    [LinuxFact]
    public async Task RepeatedExclusionsCannotGrowTheIssueListWithoutBound()
    {
        for (int i = 0; i < 1005; i++)
            File.CreateSymbolicLink(Path.Combine(root, $"link-{i}"), Path.Combine(root, "missing"));

        var report = await new DiskUsageScanner().ScanAsync(root, default);

        Assert.True(report.Truncated);
        Assert.Equal(1000, report.Issues.Count);
        Assert.Equal(1000, report.SkippedEntries);
        Assert.Equal(0, report.Root.Bytes);
        Assert.Empty(report.Root.Children);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
