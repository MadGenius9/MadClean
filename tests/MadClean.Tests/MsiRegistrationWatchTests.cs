using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class MsiRegistrationWatchTests
{
    private const string Code = "{12345678-ABCD-1234-ABCD-123456789ABC}";
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private sealed record Fixture(CheckResult<InstalledApplication> Snapshot, InstalledApplication Selected,
        CheckResult<MsiRegistrationReport> Verified, MsiRegistrationWatchTarget Target, MsiRegistrationWatchReadScope Scope);
    private static Fixture Make(CheckState coverage = CheckState.Available, bool? noRemove = true)
    {
        var source = MsiArpProvenance.CurrentUserShared;
        var identity = MsiSelectedProductPreparationPolicy.RegistrationIdentity(source, Code);
        var label = MsiSelectedProductPreparationPolicy.SourceLabel(source);
        var app = new InstalledApplication(identity, "Example", "1.2", "Publisher", label, @"C:\not-copied", "Restricted",
            new(identity, label, noRemove, true, true, Code, Now.AddSeconds(-30)));
        var snapshot = new CheckResult<InstalledApplication>(coverage, "private provider message", [app]);
        var instance = new MsiRegistrationObservation(Code, MsiRegistrationContext.UserUnmanaged, MsiObservedProductState.Installed,
            app.Name, app.Version, app.Publisher, Now.AddSeconds(-10));
        var verified = new CheckResult<MsiRegistrationReport>(coverage, "private verification message",
            [new(identity, Code, coverage, noRemove, [instance], Now.AddSeconds(-5), ["Coverage"])]);
        var target = Assert.Single(MsiRegistrationWatchPolicy.Capture(snapshot, app, verified, Now).Items);
        var scope = Assert.Single(MsiRegistrationWatchPolicy.Prepare(target, Now).Items);
        return new(snapshot, app, verified, target, scope);
    }
    private static MsiSelectedArpLookup[] Absent(DateTimeOffset? time = null) => Enum.GetValues<MsiArpProvenance>()
        .Select(source => new MsiSelectedArpLookup(source, MsiSelectedArpLookupState.Absent, null, null, time ?? Now.AddSeconds(1), 2)).ToArray();
    private static CheckResult<MsiRegistrationWatchReport> Assess(Fixture data, MsiSelectedArpLookup[]? arp = null,
        MsiRegistrationObservation[]? native = null, bool complete = true, int issues = 0) =>
        MsiRegistrationWatchPolicy.Assess(data.Scope, arp ?? Absent(), native ?? [], complete, issues, Now, Now.AddSeconds(2));

    [Fact]
    public void RememberingRestrictedVerifiedRegistrationIsReadOnlyAndHasSessionLifetime()
    {
        var data = Make();
        Assert.True(data.Selected.RegistrationEvidence!.NoRemove);
        Assert.True(data.Target.HistoricalNoRemove);
        Assert.False(data.Target.CanUninstall); Assert.False(data.Target.CanDelete);
        Assert.Equal(data.Selected.Name, data.Target.Name);
        Assert.Equal(MsiRegistrationContext.UserUnmanaged, data.Target.OriginalContext);
        Assert.Equal(MsiArpProvenance.CurrentUserShared, data.Target.OriginalSource);
        Assert.NotEmpty(MsiRegistrationWatchPolicy.Prepare(data.Target, Now.AddHours(23)).Items);
        Assert.Empty(MsiRegistrationWatchPolicy.Prepare(data.Target, Now.AddHours(24)).Items);
        Assert.Empty(MsiRegistrationWatchPolicy.Prepare(data.Target, Now.AddTicks(-1)).Items);
    }

    [Fact]
    public void CaptureRejectsClonedStaleAmbiguousMismatchedAndUnverifiedMetadata()
    {
        var data = Make(); var report = data.Verified.Items[0];
        Assert.Empty(MsiRegistrationWatchPolicy.Capture(data.Snapshot, data.Selected with { }, data.Verified, Now).Items);
        Assert.Empty(MsiRegistrationWatchPolicy.Capture(data.Snapshot with { Items = [data.Selected, data.Selected with { }] }, data.Selected, data.Verified, Now).Items);
        foreach (var invalid in new[] { report with { ProductCode = "Other" }, report with { RegistrationIdentity = "Other" },
                     report with { SnapshotState = CheckState.Partial }, report with { NoRemove = false },
                     report with { CheckedUtc = Now.AddMinutes(-3) }, report with { CheckedUtc = Now.AddTicks(1) },
                     report with { Instances = [] }, report with { Instances = [report.Instances[0], report.Instances[0]] },
                     report with { Instances = [report.Instances[0] with { Context = MsiRegistrationContext.Machine }] },
                     report with { Instances = [report.Instances[0] with { Name = "Different" }] },
                     report with { Instances = [report.Instances[0] with { ProductState = MsiObservedProductState.Advertised }] } })
            Assert.Empty(MsiRegistrationWatchPolicy.Capture(data.Snapshot, data.Selected, data.Verified with { Items = [invalid] }, Now).Items);
        Assert.Empty(MsiRegistrationWatchPolicy.Capture(data.Snapshot, data.Selected, data.Verified with { State = CheckState.Unknown }, Now).Items);
        var stale = data.Selected with { RegistrationEvidence = data.Selected.RegistrationEvidence! with { ObservedUtc = Now.AddMinutes(-3) } };
        Assert.Empty(MsiRegistrationWatchPolicy.Capture(data.Snapshot with { Items = [stale] }, stale, data.Verified, Now).Items);
    }

    [Fact]
    public void CompleteAbsenceObservationsNeverProveCompletedUninstallOrLeftoverOwnership()
    {
        var data = Make(); var result = Assess(data); var report = Assert.Single(result.Items);
        Assert.Equal(CheckState.Available, result.State);
        Assert.All(report.Contexts, context => Assert.Equal(MsiRegistrationWatchPresence.Absent, context.Presence));
        Assert.Equal(CheckState.Unknown, report.ReportUninstallCompletion);
        Assert.Equal(CheckState.Unknown, report.LeftoverOwnership);
        Assert.False(report.CanUninstall); Assert.False(report.CanDelete);
        Assert.True(MsiRegistrationWatchPolicy.IsConsistent(data.Scope, result, Now.AddSeconds(2)));
        Assert.DoesNotContain("private", result.Message);
        Assert.False(MsiRegistrationWatchPolicy.IsConsistent(data.Scope, result with { State = CheckState.Partial }, Now.AddSeconds(2)));
    }

    [Fact]
    public void NativeCoverageGapsKeepMissingContextsUnknownAndPreserveActuallyObservedPresence()
    {
        var data = Make(CheckState.Partial);
        var native = new MsiRegistrationObservation(Code, MsiRegistrationContext.Machine, MsiObservedProductState.Advertised, "Example", "", "", Now.AddSeconds(1));
        var result = Assess(data, native: [native], complete: false, issues: 1); var report = Assert.Single(result.Items);
        Assert.Equal(CheckState.Partial, result.State);
        Assert.Equal(MsiRegistrationWatchPresence.Advertised, report.Contexts.Single(row => row.Context == MsiRegistrationContext.Machine).Presence);
        Assert.All(report.Contexts.Where(row => row.Context != MsiRegistrationContext.Machine), context =>
        { Assert.Equal(MsiRegistrationWatchPresence.Unknown, context.Presence); Assert.Null(context.ObservedUtc); });
        Assert.Equal(CheckState.Partial, Assess(data).State); // Old partial coverage cannot become complete history.
        Assert.Equal(CheckState.Partial, Assess(Make(), complete: true, issues: 1).State);
    }

    [Fact]
    public void UnknownArpIsNotAbsenceAndOnlyNativeMissingCodesCanSupportAbsentArp()
    {
        var data = Make(); var arp = Absent();
        arp[0] = arp[0] with { State = MsiSelectedArpLookupState.Unknown, NativeError = 5 };
        var result = Assess(data, arp); Assert.Equal(CheckState.Partial, result.State);
        Assert.Equal(MsiSelectedArpLookupState.Unknown, result.Items[0].ArpLookups[0].State);
        arp[0] = arp[0] with { State = MsiSelectedArpLookupState.Absent };
        Assert.Empty(Assess(data, arp).Items);
        arp[0] = arp[0] with { NativeError = 3 }; Assert.NotEmpty(Assess(data, arp).Items);
        arp[1] = arp[1] with { Source = arp[0].Source }; Assert.Empty(Assess(data, arp).Items);
    }

    [Fact]
    public void ExactPresentArpIsObservedWithoutRequiringOriginalDisplayMetadataOrRemovalEligibility()
    {
        var data = Make(); var arp = Absent(); var observed = Now.AddSeconds(1);
        var current = data.Selected with { Name = "Changed display name", InstallLocation = null,
            RegistrationEvidence = data.Selected.RegistrationEvidence! with { ObservedUtc = observed } };
        arp[0] = new(MsiArpProvenance.CurrentUserShared, MsiSelectedArpLookupState.Present, current, true, observed, null);
        Assert.NotEmpty(Assess(data, arp).Items);
        arp[0] = arp[0] with { Registration = current with { InstallLocation = @"C:\not-accepted" } };
        Assert.Empty(Assess(data, arp).Items);
        arp[0] = arp[0] with { Registration = current with { RegistrationEvidence = current.RegistrationEvidence! with { ProductCode = "Foreign" } } };
        Assert.Empty(Assess(data, arp).Items);
    }

    [Fact]
    public void MalformedNativeContextsAndObservationTimesCannotBecomeAbsence()
    {
        var data = Make();
        var native = new MsiRegistrationObservation(Code, MsiRegistrationContext.UserManaged, MsiObservedProductState.Installed,
            "Example", "1", "Publisher", Now.AddSeconds(1));
        foreach (var invalid in new[] { native with { ProductCode = "Foreign" }, native with { Context = (MsiRegistrationContext)3 },
                     native with { ProductState = (MsiObservedProductState)999 }, native with { Name = "bad\u202e" },
                     native with { ObservedUtc = Now.AddTicks(-1) }, native with { ObservedUtc = Now.AddSeconds(3) } })
            Assert.Empty(Assess(data, native: [invalid]).Items);
        Assert.Empty(Assess(data, native: [native, native]).Items);
        Assert.Empty(Assess(data, issues: -1).Items); Assert.Empty(Assess(data, issues: 33).Items);
        var arp = Absent(); arp[0] = arp[0] with { ObservedUtc = Now.AddTicks(-1) };
        Assert.Empty(Assess(data, arp).Items);
    }

    [Fact]
    public void FreshScopesExpireForAdmissionButLongOwnedReadsCanReturnAndInvalidationIsPermanent()
    {
        var data = Make();
        Assert.True(MsiRegistrationWatchPolicy.IsFresh(data.Scope, Now));
        Assert.False(MsiRegistrationWatchPolicy.IsFresh(data.Scope, Now.AddMinutes(2)));
        var late = MsiRegistrationWatchPolicy.Assess(data.Scope, Absent(Now.AddMinutes(3)), [], true, 0, Now, Now.AddMinutes(3));
        Assert.NotEmpty(late.Items);
        Assert.True(MsiRegistrationWatchPolicy.IsConsistent(data.Scope, late, Now.AddMinutes(3)));
        Assert.False(MsiRegistrationWatchPolicy.IsConsistent(Make().Scope, late, Now.AddMinutes(3)));
        Assert.False(MsiRegistrationWatchPolicy.IsConsistent(data.Scope, late, Now.AddMinutes(3).AddTicks(-1)));
        data.Scope.Invalidate(); data.Scope.Invalidate();
        Assert.True(data.Scope.IsInvalidated);
        Assert.False(MsiRegistrationWatchPolicy.IsFresh(data.Scope, Now));
        Assert.Empty(Assess(data).Items);
        Assert.False(MsiRegistrationWatchPolicy.IsConsistent(data.Scope, late, Now.AddMinutes(3)));
    }

    [Fact]
    public void CopiedReportObservationsCannotBeRewrittenThroughCallerArraysAndCancellationPublishesNothing()
    {
        var data = Make(); var arp = Absent(); var result = Assess(data, arp); var report = result.Items[0];
        arp[0] = arp[0] with { State = MsiSelectedArpLookupState.Unknown };
        Assert.Equal(MsiSelectedArpLookupState.Absent, report.ArpLookups[0].State);
        Assert.True(MsiRegistrationWatchPolicy.IsConsistent(data.Scope, result, Now.AddSeconds(2)));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => MsiRegistrationWatchPolicy.Capture(null, null, null, Now, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => MsiRegistrationWatchPolicy.Prepare(null, Now, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => MsiRegistrationWatchPolicy.Assess(data.Scope, Absent(), [], true, 0, Now, Now.AddSeconds(2), cancellation.Token));
    }
}
