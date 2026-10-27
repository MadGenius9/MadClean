using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class InstalledAppActionPlanTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private const string Prefix = @"HKCU/shared/Software\Microsoft\Windows\CurrentVersion\Uninstall\";
    private const string Source = "HKCU / shared / Add or Remove Programs";
    private static InstalledApplication App(string leaf = "Example", bool installer = false)
    {
        string id = Prefix + leaf;
        return new(id, "Example", "1.0", "Publisher", Source, null, "Untrusted display hint",
            new(id, Source, false, installer, true, installer ? InstalledAppActionPlans.ProductCodeFromSubkey(leaf) : null, Now));
    }
    private static CheckResult<InstalledAppActionPlan> Plan(InstalledApplication app, CheckState state = CheckState.Available,
        IReadOnlyList<InstalledApplication>? entries = null) => InstalledAppActionPlans.Preview(new(state, "fixture", entries ?? [app]), app.Identity, Now);

    [Fact]
    public void RegisteredPresenceCreatesOnlyGeneralSettingsReviewNotSelectedExecution()
    {
        var plan = Assert.Single(Plan(App()).Items);
        Assert.Equal(InstalledAppPlanMechanism.WindowsInstalledAppsReview, plan.Mechanism);
        Assert.Equal(GuidedWindowsTask.InstalledApps, plan.Handoff);
        Assert.Contains("No application identifier, product code, command or argument is passed", plan.ExactEffect);
        Assert.Contains("completion", plan.Limitations);
        Assert.Contains("No raw UninstallString/QuietUninstallString value was read", plan.Limitations);
    }

    [Fact]
    public void ProductGuidIsBoundToExactRegistrationWithoutBecomingAnMsiCommand()
    {
        var app = App("{01234567-89ab-cdef-0123-456789abcdef}", true);
        var plan = Assert.Single(Plan(app).Items);
        Assert.Equal("{01234567-89AB-CDEF-0123-456789ABCDEF}", plan.Evidence.ProductCode);
        Assert.Equal(InstalledAppPlanMechanism.WindowsInstallerProductReview, plan.Mechanism);
        Assert.Contains("installation context", plan.Limitations);
        Assert.Equal(GuidedWindowsTask.InstalledApps, plan.Handoff);
        Assert.Empty(Plan(app with { RegistrationEvidence = app.RegistrationEvidence! with { ProductCode = "{11111111-1111-1111-1111-111111111111}" } }).Items);
    }

    [Theory]
    [InlineData("Example")]
    [InlineData("0123456789abcdef0123456789abcdef")]
    [InlineData("{00000000-0000-0000-0000-000000000000}")]
    [InlineData("{01234567-89ab-cdef-0123-456789abcdef} /x")]
    public void InstallerFlagAloneCannotProveProductIdentity(string leaf) => Assert.Empty(Plan(App(leaf, true)).Items);

    [Theory]
    [InlineData(CheckState.Partial)]
    [InlineData(CheckState.Failed)]
    [InlineData(CheckState.Cancelled)]
    [InlineData(CheckState.Unknown)]
    public void IncompleteSnapshotNeverProducesPlan(CheckState state) => Assert.Empty(Plan(App(), state).Items);

    [Fact]
    public void SelectionMustStillExistUniquelyIncludingCaseEquivalentDuplicates()
    {
        var app = App();
        Assert.Empty(Plan(app, entries: []).Items);
        Assert.Empty(Plan(app, entries: [app, app with { Identity = app.Identity.ToLowerInvariant() }]).Items);
    }

    [Fact]
    public void MissingTypedEvidenceCannotBeReplacedByFriendlySupportText()
    {
        Assert.Empty(Plan(App() with { RegistrationEvidence = null, UninstallSupport = "Windows Installer; action queued" }).Items);
        var app = App();
        Assert.Empty(Plan(app with { RegistrationEvidence = app.RegistrationEvidence! with { WindowsInstaller = null } }).Items);
        Assert.Empty(Plan(app with { RegistrationEvidence = app.RegistrationEvidence! with { HasRegisteredUninstaller = false } }).Items);
    }

    [Fact]
    public void RestrictedAndUnknownRemovalFlagsBlockEvenWithKnownMechanisms()
    {
        var app = App();
        Assert.Equal(CheckState.Unavailable, Plan(app with { RegistrationEvidence = app.RegistrationEvidence! with { NoRemove = true } }).State);
        Assert.Empty(Plan(app with { RegistrationEvidence = app.RegistrationEvidence! with { NoRemove = null } }).Items);
    }

    [Fact]
    public void EvidenceCannotBeReusedAcrossSourceIdentityOrObservationTime()
    {
        var app = App(); var evidence = app.RegistrationEvidence!;
        foreach (var changed in new[]
        {
            evidence with { Identity = Prefix + "Another" }, evidence with { Source = "HKLM / 64-bit / Add or Remove Programs" },
            evidence with { ObservedUtc = Now.AddSeconds(1) }, evidence with { ObservedUtc = Now.AddMinutes(-3) },
            evidence with { ObservedUtc = default }
        }) Assert.Empty(Plan(app with { RegistrationEvidence = changed }).Items);
    }

    [Theory]
    [InlineData("HKCU/shared/App", Source)]
    [InlineData(Prefix + "Nested\\Example", Source)]
    [InlineData(Prefix + "Example/Other", Source)]
    [InlineData(Prefix + "Example", "HKLM / 64-bit / Add or Remove Programs")]
    public void DisplayLabelsCannotAuthorizeArbitraryOrMismatchedSource(string identity, string source)
    {
        var app = App() with { Identity = identity, Source = source };
        app = app with { RegistrationEvidence = app.RegistrationEvidence! with { Identity = identity, Source = source } };
        Assert.Empty(Plan(app).Items);
    }

    [Fact]
    public void BoundedMetadataCancellationAndMalformedDisplayArePreserved()
    {
        var app = App();
        Assert.Empty(Plan(app with { Name = "hidden\u202ename" }).Items);
        Assert.Empty(Plan(app, entries: Enumerable.Repeat(app, 20_001).ToArray()).Items);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => InstalledAppActionPlans.Preview(new(CheckState.Available, "fixture", [app]), app.Identity, Now, stop.Token));
    }
}
