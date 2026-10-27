using System.Text.Json;
using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class CleanupExclusionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "MadCleanExclusionTests-" + Guid.NewGuid());
    public CleanupExclusionTests() => Directory.CreateDirectory(root);
    private string SettingsPath => Path.Combine(root, "settings", "cleanup-exclusions.json");
    private string OldFile(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, "inventory", relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "temporary test fixture");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-10));
        return path;
    }
    private Task<CleanupReport> Scan(IReadOnlyList<CleanupExclusion> exclusions, CancellationToken token = default) =>
        new CleanupScanner().ScanAsync([new("Test temporary files", Path.Combine(root, "inventory"))], token, exclusions);

    [Fact]
    public async Task ExactFileExclusionIsCaseInsensitiveAndDoesNotHidePrefixSibling()
    {
        var excluded = OldFile("cache.tmp");
        var sibling = OldFile("cache.tmp.backup");
        var result = await Scan([new(excluded.ToUpperInvariant(), false)]);
        Assert.Equal(sibling, Assert.Single(result.Candidates).Path);
        Assert.Equal(1, result.ExcludedCount);
        Assert.True(File.Exists(excluded));
    }

    [Fact]
    public async Task FolderExclusionCoversNestedEntriesButHonorsPathSeparatorBoundary()
    {
        OldFile("cache/item.tmp"); OldFile("cache/nested/item.tmp");
        var sibling = OldFile("cache-other/item.tmp");
        var folder = Path.Combine(root, "inventory", "cache") + Path.DirectorySeparatorChar;
        var result = await Scan([new(folder.ToUpperInvariant(), true)]);
        Assert.Equal(sibling, Assert.Single(result.Candidates).Path);
        Assert.Equal(1, result.ExcludedCount);
    }

    [Fact]
    public async Task ExactFileModeDoesNotSilentlyBecomeAFolderTreeExclusion()
    {
        var candidate = OldFile("cache/item.tmp");
        var result = await Scan([new(Path.Combine(root, "inventory", "cache"), false)]);
        Assert.Equal(candidate, Assert.Single(result.Candidates).Path);
        Assert.Equal(0, result.ExcludedCount);
    }

    [Fact]
    public async Task ExcludedInventoryRootIsNotEnumerated()
    {
        OldFile("old.tmp");
        var result = await Scan([new(Path.Combine(root, "inventory"), true)]);
        Assert.Empty(result.Candidates); Assert.Empty(result.Issues);
        Assert.Equal(1, result.ExcludedCount);
    }

    [Fact]
    public async Task InvalidExclusionStopsInventoryRatherThanIgnoringUserProtection()
    {
        OldFile("old.tmp");
        var result = await Scan([new("relative.tmp", false)]);
        Assert.Empty(result.Candidates);
        Assert.Contains("no inventory performed", Assert.Single(result.Issues).Reason);
    }

    [Fact]
    public async Task CancellationIsHonoredEvenWhenAllRootsAreExcluded()
    {
        OldFile("old.tmp");
        using var source = new CancellationTokenSource();
        IEnumerable<CleanupLocation> Locations()
        {
            yield return new("First", Path.Combine(root, "inventory"));
            source.Cancel();
            yield return new("Second", Path.Combine(root, "inventory"));
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CleanupScanner().ScanAsync(
            Locations(), source.Token, [new(Path.Combine(root, "inventory"), true)]));
    }

    [Fact]
    public void SaveAndLoadPreserveExactAndFolderIntentWithoutReadingSelectedFiles()
    {
        var exact = Path.Combine(root, "does-not-exist.tmp");
        var folder = Path.Combine(root, "does-not-exist-folder");
        var store = new CleanupExclusionStore(SettingsPath);
        Assert.Null(store.Load().Error);
        Assert.Null(store.Save([new(exact, false), new(folder, true)]));
        var loaded = store.Load();
        Assert.Null(loaded.Error);
        Assert.Equal(new[] { new CleanupExclusion(exact, false), new CleanupExclusion(folder, true) }, loaded.Items);
        using var json = JsonDocument.Parse(File.ReadAllBytes(SettingsPath));
        Assert.Equal(new[] { "Version", "Items" }, json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.False(File.Exists(exact)); Assert.False(Directory.Exists(folder));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(SettingsPath)!, "*.tmp"));
    }

    [Fact]
    public void ExistingValidSettingsAreReplacedAtomicallyWithTheNewSelection()
    {
        var store = new CleanupExclusionStore(SettingsPath);
        Assert.Null(store.Save([new(Path.Combine(root, "first.tmp"), false)]));
        var replacement = new CleanupExclusion(Path.Combine(root, "replacement"), true);
        Assert.Null(store.Save([replacement]));
        Assert.Equal(replacement, Assert.Single(store.Load().Items));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(SettingsPath)!, "*.tmp"));
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("{\"Version\":2,\"Items\":[]}")]
    [InlineData("{\"Version\":1,\"Items\":[{\"Path\":\"relative.tmp\",\"IncludeDescendants\":false}]}")]
    [InlineData("{\"Version\":1,\"Items\":[],\"Items\":[]}")]
    public void MalformedOrUnsupportedSettingsArePreservedInsteadOfOverwritten(string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, contents);
        var store = new CleanupExclusionStore(SettingsPath);
        Assert.NotNull(store.Load().Error);
        Assert.NotNull(store.Save([new(Path.Combine(root, "selected.tmp"), false)]));
        Assert.Equal(contents, File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void InvalidPathsAndExcessiveListsAreRejectedWithoutReplacingValidSettings()
    {
        var store = new CleanupExclusionStore(SettingsPath);
        Assert.Null(store.Save([]));
        var original = File.ReadAllBytes(SettingsPath);
        Assert.NotNull(store.Save([new("relative.tmp", false)]));
        Assert.NotNull(store.Save([new(Path.Combine(root, "line\nbreak.tmp"), false)]));
        Assert.NotNull(store.Save(Enumerable.Range(0, 257).Select(i => new CleanupExclusion(Path.Combine(root, $"{i}.tmp"), false)).ToArray()));
        Assert.Equal(original, File.ReadAllBytes(SettingsPath));
    }

    [Fact]
    public void OversizedSettingsAreRejectedAndPreserved()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var original = new string('x', 262_145);
        File.WriteAllText(SettingsPath, original);
        var store = new CleanupExclusionStore(SettingsPath);
        Assert.NotNull(store.Load().Error); Assert.NotNull(store.Save([]));
        Assert.Equal(original, File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void RelativeSettingsDestinationIsRejected()
    {
        var store = new CleanupExclusionStore("cleanup-exclusions.json");
        Assert.NotNull(store.Load().Error); Assert.NotNull(store.Save([]));
    }

    [Fact]
    public void LockedSettingsAreNotOverwritten()
    {
        var store = new CleanupExclusionStore(SettingsPath);
        Assert.Null(store.Save([]));
        var original = File.ReadAllBytes(SettingsPath);
        using (var handle = new FileStream(SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.NotNull(store.Load().Error);
            Assert.NotNull(store.Save([new(Path.Combine(root, "selected.tmp"), false)]));
        }
        Assert.Equal(original, File.ReadAllBytes(SettingsPath));
    }

    [LinuxFact]
    public void LinkedSettingsDestinationIsNotFollowedOrReplaced()
    {
        var target = Path.Combine(root, "target.json");
        File.WriteAllText(target, "target must remain unchanged");
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.CreateSymbolicLink(SettingsPath, target);
        var store = new CleanupExclusionStore(SettingsPath);
        Assert.NotNull(store.Load().Error); Assert.NotNull(store.Save([]));
        Assert.Equal("target must remain unchanged", File.ReadAllText(target));
        Assert.NotNull(new FileInfo(SettingsPath).LinkTarget);
    }

    [LinuxFact]
    public void LinkedSettingsAncestorDoesNotReceiveNewFiles()
    {
        var destination = Path.Combine(root, "actual-settings");
        Directory.CreateDirectory(destination);
        Directory.CreateSymbolicLink(Path.GetDirectoryName(SettingsPath)!, destination);
        var store = new CleanupExclusionStore(SettingsPath);
        Assert.NotNull(store.Load().Error); Assert.NotNull(store.Save([]));
        Assert.Empty(Directory.GetFileSystemEntries(destination));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}
