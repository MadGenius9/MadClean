using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class CleanupPreviewIndexTests
{
    private sealed record Row(string Path, string Detail = "Candidate");
    private static CleanupCandidate Candidate(string path = @"C:\Temp\example.tmp") =>
        new("Unverified category", path, 12, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    private static CleanupReport Report(params CleanupCandidate[] candidates) => new(candidates, [], 0, false);
    private static CleanupPreviewIndex<Row>? Create(CleanupReport? source, params CleanupPreviewBinding<Row>[] bindings) =>
        CleanupPreviewIndex<Row>.Create(source, bindings);

    [Fact]
    public void IssueWithSamePathNeverBecomesACandidateAndEqualDisplayClonesCannotJoinByValue()
    {
        var candidate = Candidate(); var row = new Row(candidate.Path); var issue = new Row(candidate.Path, "Error");
        var source = Report(candidate) with { Issues = [new("Temporary files", candidate.Path, "Access unavailable")], Truncated = true, ExcludedCount = 2 };
        var index = Assert.IsType<CleanupPreviewIndex<Row>>(Create(source, new CleanupPreviewBinding<Row>(row, candidate)));
        Assert.Same(candidate, index.Find(source, row, [issue, row]));
        Assert.Null(index.Find(source, issue, [issue, row]));
        Assert.Null(index.Find(source, row with { }, [issue, row]));
        Assert.Null(index.Find(source, row with { }, [issue, row with { }]));
        Assert.True(source.Truncated); Assert.Single(source.Issues); Assert.Equal(2, source.ExcludedCount);
    }

    [Fact]
    public void ForeignCandidateClonesDuplicateCandidatesAndAmbiguousPathsAreRejected()
    {
        var candidate = Candidate(); var row = new Row(candidate.Path);
        Assert.Null(Create(Report(candidate), new CleanupPreviewBinding<Row>(row, candidate with { })));
        Assert.Null(Create(Report(candidate, candidate), new CleanupPreviewBinding<Row>(row, candidate), new CleanupPreviewBinding<Row>(new("Other"), candidate)));
        var ambiguous = candidate with { Path = candidate.Path.ToUpperInvariant() };
        Assert.Null(Create(Report(candidate, ambiguous), new CleanupPreviewBinding<Row>(row, candidate), new CleanupPreviewBinding<Row>(new("Other"), ambiguous)));
        var other = Candidate(@"C:\Temp\other.tmp");
        Assert.Null(Create(Report(candidate, other), new CleanupPreviewBinding<Row>(row, candidate), new CleanupPreviewBinding<Row>(row, other)));
        Assert.Null(Create(Report(candidate, other), new CleanupPreviewBinding<Row>(row, candidate), new CleanupPreviewBinding<Row>(new("Other"), candidate)));
    }

    [Fact]
    public void BindingOrderAndPublicationOrderMayDifferButExactReferencesMustSurvive()
    {
        var first = Candidate(); var second = Candidate(@"C:\Temp\second.tmp");
        var a = new Row(first.Path); var b = new Row(second.Path);
        var source = Report(first, second);
        var index = Assert.IsType<CleanupPreviewIndex<Row>>(Create(source, new CleanupPreviewBinding<Row>(b, second), new CleanupPreviewBinding<Row>(a, first)));
        Assert.Same(first, index.Find(source, a, [b, a]));
        Assert.Same(second, index.Find(source, b, [a, b]));
        Assert.Null(index.Find(source with { }, a, [a, b]));
        Assert.Null(index.Find(Report(first with { }, second with { }), a, [a, b]));
        Assert.Null(index.Find(source, a, [a]));
        Assert.Null(index.Find(source, a, [a, a]));
        Assert.Null(index.Find(source, a, [a with { }, b]));
        Assert.Null(index.Find(source, null, [a, b]));
        Assert.Null(index.Find(source, a, null));
    }

    [Fact]
    public void AcceptedBindingsAreCopiedAndMutableSourceCannotSilentlyReplaceCandidateReferences()
    {
        var candidate = Candidate(); var row = new Row(candidate.Path);
        var candidates = new List<CleanupCandidate> { candidate };
        var bindings = new List<CleanupPreviewBinding<Row>> { new CleanupPreviewBinding<Row>(row, candidate) };
        var source = new CleanupReport(candidates, [], 0, false);
        var index = Assert.IsType<CleanupPreviewIndex<Row>>(CleanupPreviewIndex<Row>.Create(source, bindings));
        bindings[0] = new CleanupPreviewBinding<Row>(new Row("Foreign"), candidate with { });
        Assert.Same(candidate, index.Find(source, row, [row]));
        candidates[0] = candidate with { };
        Assert.Null(index.Find(source, row, [row]));
    }

    [Fact]
    public void IssuesOnlyAndEmptyReportsProduceNoSelectableCandidate()
    {
        var row = new Row("Unavailable", "Error");
        var source = Report() with { Issues = [new("Temporary files", "", "Unavailable")], Truncated = true };
        var index = Assert.IsType<CleanupPreviewIndex<Row>>(Create(source));
        Assert.Null(index.Find(source, row, [row]));
        var empty = Report();
        Assert.NotNull(Create(empty));
        Assert.Null(Create(empty)!.Find(empty, row, [row]));
    }

    [Fact]
    public void MissingContainersNullRowsAndIncompleteBindingsAreRejected()
    {
        var candidate = Candidate(); var row = new Row(candidate.Path); var source = Report(candidate);
        Assert.Null(Create(null));
        Assert.Null(CleanupPreviewIndex<Row>.Create(source, null));
        Assert.Null(Create(source));
        Assert.Null(Create(source with { Candidates = null! }, new CleanupPreviewBinding<Row>(row, candidate)));
        Assert.Null(Create(source with { Issues = null! }, new CleanupPreviewBinding<Row>(row, candidate)));
        Assert.Null(Create(source with { ExcludedCount = -1 }, new CleanupPreviewBinding<Row>(row, candidate)));
        Assert.Null(Create(Report(null!), new CleanupPreviewBinding<Row>(row, candidate)));
        Assert.Null(Create(source, null!));
        Assert.Null(Create(source, new CleanupPreviewBinding<Row>(null!, candidate)));
        Assert.Null(Create(source, new CleanupPreviewBinding<Row>(row, null!)));
        Assert.Null(Create(source with { Issues = [null!] }, new CleanupPreviewBinding<Row>(row, candidate)));
    }

    [Fact]
    public void InvalidCandidateMetadataCannotEnterTheDisplayIndex()
    {
        var candidate = Candidate(); var row = new Row(candidate.Path);
        foreach (var invalid in new[] { candidate with { Bytes = -1 }, candidate with { Category = "" }, candidate with { Category = new string('x', 257) },
                     candidate with { Path = "\u202e" }, candidate with { Path = new string('\ud800', 1) }, candidate with { Path = new string('x', 32768) },
                     candidate with { ModifiedUtc = default }, candidate with { ModifiedUtc = DateTime.SpecifyKind(candidate.ModifiedUtc, DateTimeKind.Local) },
                     candidate with { ModifiedUtc = DateTime.UtcNow.AddDays(1) } })
            Assert.Null(Create(Report(invalid), new CleanupPreviewBinding<Row>(row, invalid)));
        // Display binding is not temporary-root or action validation.
        var unsupported = candidate with { Path = "relative display hint", Bytes = long.MaxValue };
        Assert.NotNull(Create(Report(unsupported), new CleanupPreviewBinding<Row>(row, unsupported)));
    }

    [Fact]
    public void CancellationAndInventoryBoundsAreEnforcedBeforePublishingAnIndex()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => CleanupPreviewIndex<Row>.Create(null, null, cancellation.Token));
        var candidate = Candidate(); var row = new Row(candidate.Path);
        Assert.Null(Create(Report(candidate) with { Issues = Enumerable.Repeat(new ScanIssue("Category", "", "Unavailable"), 1001).ToArray() }, new CleanupPreviewBinding<Row>(row, candidate)));
        var excessive = Enumerable.Repeat(candidate, 100001).ToArray();
        Assert.Null(CleanupPreviewIndex<Row>.Create(Report(excessive), Enumerable.Repeat(new CleanupPreviewBinding<Row>(row, candidate), excessive.Length).ToArray()));
    }
}
