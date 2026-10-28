using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class MsiRegistrationReviewTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
    private const string Code = "{01234567-89AB-CDEF-0123-456789ABCDEF}";
    private static InstalledApplication App(bool? noRemove = false, bool machine = false)
    {
        string source = machine ? "HKLM / 64-bit / Add or Remove Programs" : "HKCU / shared / Add or Remove Programs";
        string id = (machine ? "HKLM/64/" : "HKCU/shared/") + @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + Code;
        return new(id, "Example", "1.2.3", "Example publisher", source, null, "Display hint only",
            new(id, source, noRemove, true, true, Code, Now.AddSeconds(-1)));
    }
    private static CheckResult<MsiRegistrationScope> ScopeResult(InstalledApplication? app = null, CheckState state = CheckState.Available)
    {
        app ??= App();
        return MsiRegistrationReview.CreateScope(new(state, "Synthetic observed registration fixture; not actual MSI", [app]), app.Identity, Now);
    }
    private static MsiRegistrationScope Scope(InstalledApplication? app = null, CheckState state = CheckState.Available) => Assert.Single(ScopeResult(app, state).Items);
    private static MsiRegistrationObservation Instance(MsiRegistrationContext context = MsiRegistrationContext.UserUnmanaged,
        MsiObservedProductState state = MsiObservedProductState.Installed) => new(Code, context, state, "Example", "1.2.3", "Example publisher", Now.AddSeconds(1));
    private static CheckResult<MsiRegistrationReport> Assess(MsiRegistrationScope scope, params MsiRegistrationObservation[] instances) =>
        MsiRegistrationReview.Assess(scope, instances, true, [], Now.AddSeconds(2));

    [Theory]
    [InlineData(CheckState.Available)] [InlineData(CheckState.Partial)]
    public void PresentSelectedReadOnlyScopeDoesNotRequireAllUnrelatedArpSourcesComplete(CheckState state)
    {
        var scope = Scope(state: state);
        Assert.Equal(state, scope.SnapshotState); Assert.Equal(3, scope.ExpectedContextMask);
        var result = Assess(scope, Instance());
        Assert.Equal(state, result.State);
        var report = Assert.Single(result.Items);
        Assert.Equal(state, report.SnapshotState);
        Assert.Contains(report.Coverage, line => line.Contains("no absence", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("no action eligibility", result.Message);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)] [InlineData(null)]
    public void ReadOnlyEvidenceNeverOverridesNoRemoveOrConvertsUnknownToFalse(bool? restriction)
    {
        var result = Assess(Scope(App(restriction)), Instance());
        var report = Assert.Single(result.Items); Assert.Equal(restriction, report.NoRemove);
        Assert.Equal(restriction == null ? CheckState.Partial : CheckState.Available, result.State);
        Assert.Contains(report.Coverage, line => line.StartsWith("NoRemove:", StringComparison.Ordinal));
        Assert.Contains(report.Coverage, line => line.Contains("NoRemove", StringComparison.Ordinal));
    }

    [Fact]
    public void HkLmAndHkCuCannotSubstituteContextsAndViewDoesNotClaimArchitecture()
    {
        var scope = Scope(App(machine: true)); Assert.Equal(4, scope.ExpectedContextMask);
        Assert.Equal(CheckState.Available, Assess(scope, Instance(MsiRegistrationContext.Machine)).State);
        Assert.Empty(Assess(scope, Instance()).Items);
        Assert.Empty(Assess(Scope(), Instance(MsiRegistrationContext.Machine)).Items);
        Assert.Contains(Assert.Single(Assess(scope, Instance(MsiRegistrationContext.Machine)).Items).Coverage,
            line => line.Contains("does not establish MSI architecture", StringComparison.Ordinal));
    }

    [Fact]
    public void MultipleUserContextsRemainAmbiguousEvenWithMatchingDisplayMetadata()
    {
        var result = Assess(Scope(), Instance(MsiRegistrationContext.UserManaged), Instance());
        Assert.Equal(CheckState.Unknown, result.State); Assert.Equal(2, Assert.Single(result.Items).Instances.Count);
        Assert.Empty(Assess(Scope(), Instance(), Instance()).Items);
        Assert.Empty(Assess(Scope(), Instance((MsiRegistrationContext)3)).Items);
    }

    [Fact]
    public void AdvertisedAndEmptyResultsAreObservationsWithoutInstalledOrAbsentInference()
    {
        var advertised = Instance(state: MsiObservedProductState.Advertised) with { Version = "Unknown", Publisher = "Unknown" };
        var result = Assess(Scope(), advertised);
        Assert.Equal(CheckState.Unknown, result.State);
        Assert.Equal(MsiObservedProductState.Advertised, Assert.Single(Assert.Single(result.Items).Instances).ProductState);
        Assert.Equal(CheckState.Unknown, Assess(Scope()).State);
        Assert.Contains("No absence", Assess(Scope()).Message);
    }

    [Fact]
    public void InstalledStateCannotSubstituteForExactKnownDisplayMetadata()
    {
        foreach (var observation in new[]
        {
            Instance() with { Name = "example" }, Instance() with { Version = "Unknown" },
            Instance() with { Publisher = "" }, Instance() with { Name = "Another product" },
            Instance() with { ProductState = MsiObservedProductState.Unknown }
        }) Assert.Equal(CheckState.Unknown, Assess(Scope(), observation).State);
        Assert.Equal(CheckState.Unknown, Assess(Scope(App() with { Publisher = "Unknown" }), Instance()).State);
    }

    [Fact]
    public void ScopeRequiresFreshUniqueSourceBoundInstallerGuidRatherThanHintsOrWhitespaceAliases()
    {
        var app = App(); var evidence = app.RegistrationEvidence!;
        foreach (var changed in new[]
        {
            app with { RegistrationEvidence = null }, app with { RegistrationEvidence = evidence with { WindowsInstaller = false } },
            app with { RegistrationEvidence = evidence with { ObservedUtc = Now.AddMinutes(-3) } },
            app with { RegistrationEvidence = evidence with { ObservedUtc = Now.AddSeconds(1) } },
            app with { RegistrationEvidence = evidence with { ProductCode = "{11111111-1111-1111-1111-111111111111}" } },
            app with { Source = "HKLM / 64-bit / Add or Remove Programs" }
        }) Assert.Empty(ScopeResult(changed).Items);
        string alias = app.Identity[..^Code.Length] + " " + Code + " ";
        Assert.Empty(ScopeResult(app with { Identity = alias, RegistrationEvidence = evidence with { Identity = alias } }).Items);
        Assert.Empty(MsiRegistrationReview.CreateScope(new(CheckState.Partial, "ambiguous", [app, app]), app.Identity, Now).Items);
        Assert.Empty(ScopeResult(state: CheckState.Failed).Items);
    }

    [Fact]
    public void IncompleteEnumerationAndIssuesCannotBecomeVerifiedBinding()
    {
        var scope = Scope();
        var result = MsiRegistrationReview.Assess(scope, [Instance()], false, [], Now.AddSeconds(2));
        Assert.Equal(CheckState.Unknown, result.State); Assert.Single(result.Items);
        result = MsiRegistrationReview.Assess(scope, [Instance()], true, ["State query failed (code 1608)"], Now.AddSeconds(2));
        Assert.Equal(CheckState.Unknown, result.State);
    }

    [Fact]
    public void NativeProjectionBoundsMalformedUtf16AndStaleTimesFailClosed()
    {
        foreach (var observation in new[]
        {
            Instance() with { ProductCode = Code.ToLowerInvariant() }, Instance() with { Name = "bad\u202evalue" },
            Instance() with { Name = new string('a', 1_025) }, Instance() with { Version = new string('1', 257) },
            Instance() with { Publisher = "bad\ud800" }, Instance() with { ObservedUtc = Now.AddSeconds(-1) },
            Instance() with { ObservedUtc = Now.AddSeconds(3) }
        }) Assert.Empty(Assess(Scope(), observation).Items);
        Assert.Empty(MsiRegistrationReview.Assess(Scope(), [Instance()], true, [], Now.AddMinutes(3)).Items);
        Assert.Empty(MsiRegistrationReview.Assess(Scope(), Enumerable.Repeat(Instance(), 4).ToArray(), true, [], Now.AddSeconds(2)).Items);
        Assert.Empty(MsiRegistrationReview.Assess(Scope(), [], true, Enumerable.Repeat("issue", 33).ToArray(), Now.AddSeconds(2)).Items);
    }

    [Fact]
    public void CancellationDoesNotProduceACompletedReadOnlyScopeOrAssessment()
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var app = App(); var scope = Scope();
        Assert.ThrowsAny<OperationCanceledException>(() => MsiRegistrationReview.CreateScope(new(CheckState.Available, "fixture", [app]), app.Identity, Now, cancelled.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => MsiRegistrationReview.Assess(scope, [Instance()], true, [], Now.AddSeconds(2), cancelled.Token));
    }
}
