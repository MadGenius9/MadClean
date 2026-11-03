using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class MsiSelectedProductPreparationTests
{
    private const string Code = "{12345678-ABCD-1234-ABCD-123456789ABC}";
    private sealed record Fixture(MsiSelectedProductReadScope Scope, CheckResult<InstalledApplication> Original,
        MsiSelectedArpLookup[] Lookups, MsiRegistrationObservation[] Instances, DateTimeOffset CheckedUtc);
    private static Fixture Make(CheckState historical = CheckState.Partial, DateTimeOffset? start = null, string? historicalLocation = null)
    {
        var time = start ?? DateTimeOffset.UtcNow.AddSeconds(-1);
        string identity = MsiSelectedProductPreparationPolicy.RegistrationIdentity(MsiArpProvenance.LocalMachine64, Code);
        string source = MsiSelectedProductPreparationPolicy.SourceLabel(MsiArpProvenance.LocalMachine64);
        var selected = new InstalledApplication(identity, "Example", "1.2", "Publisher", source, historicalLocation,
            "Windows Installer; action queued", new(identity, source, false, true, true, Code, time.AddMilliseconds(-10)));
        var snapshot = new CheckResult<InstalledApplication>(historical, "private-provider-output", [selected]);
        var scope = Assert.Single(MsiSelectedProductPreparationPolicy.CreateScope(snapshot, selected, 7, time).Items);
        var observed = time.AddMilliseconds(100);
        var fresh = selected with { InstallLocation = null, RegistrationEvidence = selected.RegistrationEvidence! with { ObservedUtc = observed } };
        var lookups = Enum.GetValues<MsiArpProvenance>().Select(kind => kind == scope.OriginalSource
            ? new MsiSelectedArpLookup(kind, MsiSelectedArpLookupState.Present, fresh, false, observed, null)
            : new MsiSelectedArpLookup(kind, MsiSelectedArpLookupState.Absent, null, null, observed, 2)).ToArray();
        return new(scope, snapshot, lookups,
            [new(Code, MsiRegistrationContext.UserUnmanaged, MsiObservedProductState.Installed, "Example", "1.2", "Publisher", time.AddMilliseconds(150))],
            time.AddMilliseconds(200));
    }
    private static CheckResult<MsiSelectedProductPreparation> Assess(Fixture data, bool complete = true, int issues = 0, long generation = 7,
        CancellationToken token = default) => MsiSelectedProductPreparationPolicy.Assess(data.Scope, data.Lookups, data.Instances,
            complete, issues, generation, data.CheckedUtc, token);
    private static int Selected(Fixture data) => Array.FindIndex(data.Lookups, row => row.Source == data.Scope.OriginalSource);
    private static Fixture ChangeSelected(Fixture data, Func<MsiSelectedArpLookup, MsiSelectedArpLookup> change)
    {
        var rows = data.Lookups.ToArray(); int index = Selected(data); rows[index] = change(rows[index]);
        return data with { Lookups = rows };
    }

    [Fact]
    public void RequiredTargetMatchKeepsGlobalPartialAndCannotOverrideTheOldProvenancePolicy()
    {
        var data = Make();
        var result = Assess(data); var report = Assert.Single(result.Items);
        Assert.Equal(CheckState.Partial, result.State);
        Assert.Equal(CheckState.Partial, report.HistoricalGlobalSnapshotState);
        Assert.Equal(CheckState.Available, report.TargetCoverageState);
        Assert.Equal(MsiSelectedProductPreparationState.ObservedMatch, report.State);
        Assert.False(report.CurrentGlobalSnapshotWasRead);
        Assert.Equal(CheckState.Unknown, report.CurrentGlobalSnapshotState);
        Assert.False(report.CanExecute);
        Assert.Equal(4, data.Scope.NativeReadScope.ExpectedContextMask);
        var original = data.Original.Items[0];
        var oldScope = Assert.Single(MsiActionBindingPolicy.CreateScope(data.Original, original, data.Scope.ValidatedUtc).Items);
        var old = MsiActionBindingPolicy.Assess(oldScope, new(CheckState.Partial, "", [data.Lookups[Selected(data)].Registration!]),
            data.Instances, true, [], data.CheckedUtc);
        Assert.Equal(MsiActionBindingReason.SourceContextMismatch, Assert.Single(old.Items).Reason);
        Assert.False(old.Items[0].CanExecute);
        Assert.DoesNotContain("private-provider-output", result.Message);
    }

    [Fact]
    public void ScopeRequiresActualSelectedReferenceAndRejectsNullRowsWithoutThrowing()
    {
        var data = Make(); var actual = data.Original.Items[0];
        Assert.Empty(MsiSelectedProductPreparationPolicy.CreateScope(data.Original, actual with { }, 7, data.Scope.ValidatedUtc).Items);
        var malformed = data.Original with { Items = [actual, null!] };
        Assert.Empty(MsiSelectedProductPreparationPolicy.CreateScope(malformed, actual, 7, data.Scope.ValidatedUtc).Items);
    }

    [Fact]
    public void ExactThreeSourcesCannotBeReplacedByDuplicateUnknownOrInventedAbsence()
    {
        var data = Make();
        Assert.Empty(Assess(data with { Lookups = [data.Lookups[0], data.Lookups[0], data.Lookups[2]] }).Items);
        var unknown = data with { Lookups = data.Lookups.Select(row => row.Source == MsiArpProvenance.CurrentUserShared
            ? row with { State = MsiSelectedArpLookupState.Unknown, NativeError = 5 } : row).ToArray() };
        Assert.Equal(MsiSelectedProductPreparationState.Unknown, Assert.Single(Assess(unknown).Items).State);
        var invented = data with { Lookups = data.Lookups.Select(row => row.State == MsiSelectedArpLookupState.Absent
            ? row with { NativeError = null } : row).ToArray() };
        Assert.Empty(Assess(invented).Items);
    }

    [Fact]
    public void AnySecondGuidRegistrationBlocksEvenWhenTheNativeContextIsUnique()
    {
        var data = Make(); var selected = data.Lookups[Selected(data)].Registration!;
        var secondKind = MsiArpProvenance.LocalMachine32;
        string id = MsiSelectedProductPreparationPolicy.RegistrationIdentity(secondKind, Code), label = MsiSelectedProductPreparationPolicy.SourceLabel(secondKind);
        var second = selected with { Identity = id, Source = label,
            RegistrationEvidence = selected.RegistrationEvidence! with { Identity = id, Source = label } };
        var duplicate = data with { Lookups = data.Lookups.Select(row => row.Source == secondKind
            ? row with { State = MsiSelectedArpLookupState.Present, Registration = second, SystemComponent = false, NativeError = null } : row).ToArray() };
        var report = Assert.Single(Assess(duplicate).Items);
        Assert.Equal(MsiSelectedProductPreparationState.Blocked, report.State);
        Assert.Equal(MsiSelectedProductPreparationReason.MultipleArpRegistrations, report.Reason);
        Assert.False(report.CanExecute);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public void RemovalRestrictionsRemainIndependentAndNeverBecomeMatch(bool? restriction)
    {
        var data = ChangeSelected(Make(), row => row with { Registration = row.Registration! with
        { RegistrationEvidence = row.Registration.RegistrationEvidence! with { NoRemove = restriction } } });
        var report = Assert.Single(Assess(data).Items);
        Assert.NotEqual(MsiSelectedProductPreparationState.ObservedMatch, report.State);
        Assert.False(report.CanExecute);
        Assert.Equal(restriction, report.Lookups[Selected(data)].Registration!.RegistrationEvidence!.NoRemove);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public void HiddenOrUnobservedComponentFlagCannotQualify(bool? component)
    {
        var data = ChangeSelected(Make(), row => row with { SystemComponent = component });
        Assert.NotEqual(MsiSelectedProductPreparationState.ObservedMatch, Assert.Single(Assess(data).Items).State);
    }

    [Fact]
    public void FreshNameChangeCannotBeLaunderedByMatchingNativeName()
    {
        var data = ChangeSelected(Make(), row => row with { Registration = row.Registration! with { Name = "Changed" } });
        data = data with { Instances = [data.Instances[0] with { Name = "Changed" }] };
        Assert.Equal(MsiSelectedProductPreparationReason.SelectionChanged, Assert.Single(Assess(data).Items).Reason);
    }

    [Fact]
    public void IncompleteNativeEnumerationAndExtraContextsNeverGiveAnObservedMatch()
    {
        var data = Make();
        Assert.Equal(MsiSelectedProductPreparationState.Unknown, Assert.Single(Assess(data, complete: false).Items).State);
        Assert.Equal(MsiSelectedProductPreparationState.Unknown, Assert.Single(Assess(data, issues: 1).Items).State);
        var two = data with { Instances = [data.Instances[0], data.Instances[0] with { Context = MsiRegistrationContext.Machine }] };
        Assert.Equal(MsiSelectedProductPreparationReason.MultipleNativeContexts, Assert.Single(Assess(two).Items).Reason);
        Assert.Empty(Assess(data with { Instances = [data.Instances[0], data.Instances[0]] }).Items);
    }

    [Fact]
    public void UnknownNativeStateIsUnknownRatherThanEvidenceThatAProductIsAbsent()
    {
        var data = Make();
        var unknown = Assert.Single(Assess(data with { Instances = [data.Instances[0] with { ProductState = MsiObservedProductState.Unknown }] }).Items);
        Assert.Equal(MsiSelectedProductPreparationState.Unknown, unknown.State);
        Assert.NotEqual(MsiSelectedProductPreparationReason.NativeProductNotInstalled, unknown.Reason);
    }

    [Fact]
    public void OldCheckedTimeCannotReviveAnExpiredSelectionAndInvalidationIsOneWay()
    {
        var stale = Make(start: DateTimeOffset.UtcNow.AddMinutes(-3));
        Assert.Empty(Assess(stale).Items);
        var data = Make();
        Assert.Empty(Assess(data, generation: 8).Items);
        data.Scope.Invalidate(); data.Scope.Invalidate();
        Assert.True(data.Scope.IsInvalidated);
        Assert.Empty(Assess(data).Items);
    }

    [Fact]
    public void PrecancelledAssessmentPublishesNoNewReport()
    {
        var data = Make(); using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => Assess(data, token: stop.Token));
    }

    [Fact]
    public void ReportsFreezeInputCollectionsAndDoNotReadOrPromoteOptionalHistoricalPaths()
    {
        var data = Make(historicalLocation: "unverified optional path");
        var result = Assess(data); var report = Assert.Single(result.Items);
        Assert.Equal(CheckState.Partial, result.State);
        Assert.Null(report.Lookups[Selected(data)].Registration!.InstallLocation);
        var before = report.Lookups[Selected(data)].Registration;
        data.Lookups[Selected(data)] = data.Lookups[Selected(data)] with { Registration = before! with { Name = "Later" } };
        data.Instances[0] = data.Instances[0] with { Name = "Later" };
        Assert.Equal("Example", report.Lookups.Single(row => row.State == MsiSelectedArpLookupState.Present).Registration!.Name);
        Assert.Equal("Example", report.Instances[0].Name);
        Assert.NotSame(before, data.Lookups[Selected(data)].Registration);
    }
}
