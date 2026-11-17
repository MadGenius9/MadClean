using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using MadClean.App;
using Xunit;

namespace MadClean.Tests;

public sealed class DiagnosticPublicationScopeTests
{
    [Theory]
    [InlineData("clear")]
    [InlineData("finding")]
    [InlineData("coverage")]
    [InlineData("summary")]
    [InlineData("channel")]
    [InlineData("failure")]
    public void ReentrantRangeChangeKeepsNewRowsAndStatusFromOldPublication(string boundary)
    {
        int generation = 0;
        var target = new Target();
        target.Rows.Add("previous finding");
        bool changed = false;
        void ChangeRange(string observed)
        {
            if (changed || observed != boundary) return;
            changed = true;
            generation++;
            target.ApplyNewRange();
        }
        target.Rows.CollectionChanged += (_, e) => ChangeRange(e.Action == NotifyCollectionChangedAction.Reset ? "clear" : "finding");
        target.Coverage.CollectionChanged += (_, _) => ChangeRange("coverage");
        target.Channels.CollectionChanged += (_, _) => ChangeRange("channel");
        target.PropertyChanged += (_, e) => ChangeRange(e.PropertyName == nameof(Target.Failure) ? "failure" : "summary");
        var scope = new DiagnosticPublicationScope(() => generation == 0);

        // Continue offering old mutations even after refusal: the fence itself must stay closed.
        scope.Publish(target.Rows.Clear);
        scope.Publish(() => target.Rows.Add("old finding"));
        scope.Publish(() => target.Coverage.Add("old coverage"));
        scope.Publish(() => target.Summary = "old summary");
        scope.Publish(() => target.Channels.Add("old channel"));
        scope.Publish(() => target.Failure = "old failure");
        int lateWrites = 0;
        Assert.False(scope.Publish(() => lateWrites++));

        Assert.True(changed);
        Assert.Equal(0, lateWrites);
        Assert.Equal(new[] { "new finding" }, target.Rows);
        Assert.Equal(new[] { "new coverage" }, target.Coverage);
        Assert.Equal(new[] { "new channel" }, target.Channels);
        Assert.Equal("new summary", target.Summary);
        Assert.Equal("new failure", target.Failure);
    }

    [Fact]
    public void ARangeThatMovesAwayAndBackCannotReviveAnObservedStaleScope()
    {
        bool current = true;
        var scope = new DiagnosticPublicationScope(() => current);
        Assert.True(scope.IsCurrent);
        current = false;
        Assert.False(scope.IsCurrent);
        current = true;
        Assert.False(scope.IsCurrent);
        Assert.False(scope.Publish(() => throw new InvalidOperationException("Must not run")));
    }

    [Fact]
    public void CapturedGenerationDetectsAwayAndBackInsideOneNotification()
    {
        int generation = 7;
        var scope = new DiagnosticPublicationScope(() => generation == 7);
        Assert.False(scope.Publish(() => { generation++; generation++; }));
        Assert.False(scope.IsCurrent);
    }

    [Fact]
    public void AnExceptionPoisonsOriginalScopeAndForeignRangeCleanupCannotWrite()
    {
        int generation = 0, writes = 0;
        var original = new DiagnosticPublicationScope(() => generation == 0);
        Assert.Throws<InvalidOperationException>(() => original.Publish(() =>
        {
            generation++;
            throw new InvalidOperationException("Controlled notification failure");
        }));
        Assert.False(original.Publish(() => writes++));
        var failure = new DiagnosticPublicationScope(() => generation == 0);
        Assert.False(failure.Publish(() => writes++));
        Assert.Equal(0, writes);
    }

    [Fact]
    public void SameRangeFailureCanUseFreshFenceWithoutRevivingFailedPublisher()
    {
        var original = new DiagnosticPublicationScope(() => true);
        Assert.Throws<InvalidOperationException>(() => original.Publish(() => throw new InvalidOperationException("Controlled")));
        int writes = 0;
        Assert.False(original.Publish(() => writes++));
        var failure = new DiagnosticPublicationScope(() => true);
        Assert.True(failure.Publish(() => writes++));
        Assert.Equal(1, writes);
    }

    [Fact]
    public void PredicateFailureIsPropagatedOnceAndNeverReevaluated()
    {
        int checks = 0;
        var scope = new DiagnosticPublicationScope(() => { checks++; throw new InvalidOperationException("Controlled"); });
        Assert.Throws<InvalidOperationException>(() => _ = scope.IsCurrent);
        Assert.False(scope.IsCurrent);
        Assert.False(scope.Publish(() => throw new InvalidOperationException("Must not run")));
        Assert.Equal(1, checks);
    }

    [Fact]
    public void AlreadyStaleScopeDoesNotRunMutation()
    {
        int calls = 0;
        var scope = new DiagnosticPublicationScope(() => false);
        Assert.False(scope.Publish(() => calls++));
        Assert.False(scope.Publish(() => calls++));
        Assert.Equal(0, calls);
    }

    private sealed class Target : INotifyPropertyChanged
    {
        internal ObservableCollection<string> Rows { get; } = [];
        internal ObservableCollection<string> Coverage { get; } = [];
        internal ObservableCollection<string> Channels { get; } = [];
        private string summary = "", failure = "";
        internal string Summary { get => summary; set { summary = value; PropertyChanged?.Invoke(this, new(nameof(Summary))); } }
        internal string Failure { get => failure; set { failure = value; PropertyChanged?.Invoke(this, new(nameof(Failure))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
        internal void ApplyNewRange()
        {
            Rows.Clear(); Rows.Add("new finding");
            Coverage.Clear(); Coverage.Add("new coverage");
            Channels.Clear(); Channels.Add("new channel");
            Summary = "new summary"; Failure = "new failure";
        }
    }
}
