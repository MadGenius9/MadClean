using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class MsiActionBindingDiagnosticsTests
{
    private const string Code = "{12345678-ABCD-1234-ABCD-123456789ABC}";

    private static InstalledApplication App(DateTimeOffset observed, bool? noRemove = false)
    {
        const string source = "HKLM / 64-bit / Add or Remove Programs";
        string identity = @"HKLM/64/Software\Microsoft\Windows\CurrentVersion\Uninstall\" + Code;
        return new(identity, "Example", "1.2", "Publisher", source, null, "Windows Installer; action queued",
            new(identity, source, noRemove, true, true, Code, observed));
    }

    private static (MsiActionBindingScope Scope, CheckResult<InstalledApplication> Fresh, MsiRegistrationObservation Row) Fixture(
        bool? noRemove = false, CheckState state = CheckState.Available)
    {
        var start = DateTimeOffset.UtcNow.AddSeconds(-1);
        var selected = App(start.AddMilliseconds(-1), noRemove);
        var scope = Assert.Single(MsiActionBindingPolicy.CreateScope(new(state, "", [selected]), selected, start).Items);
        var observed = start.AddMilliseconds(10);
        return (scope, new(state, "private-provider-text", [App(observed, noRemove)]),
            new(Code, MsiRegistrationContext.Machine, MsiObservedProductState.Installed,
                "Example", "1.2", "Publisher", observed));
    }

    private static CheckResult<MsiActionBindingReport> Assess(MsiActionBindingScope scope,
        CheckResult<InstalledApplication> fresh, IReadOnlyList<MsiRegistrationObservation> rows, bool complete = true) =>
        MsiActionBindingPolicy.Assess(scope, fresh, rows, complete, [], DateTimeOffset.UtcNow);

    [Fact]
    public void ChangedFieldsDescribeOnlyTheActualSelectedToFreshComparisons()
    {
        var f = Fixture();
        var changed = f.Fresh.Items[0] with
        {
            Name = "Changed name", Version = "2.0", Publisher = "Changed publisher",
            RegistrationEvidence = f.Fresh.Items[0].RegistrationEvidence! with
            { HasRegisteredUninstaller = false, NoRemove = true }
        };
        var result = Assess(f.Scope, f.Fresh with { Items = [changed] }, [f.Row]);
        var report = Assert.Single(result.Items);
        Assert.Equal(CheckState.Unavailable, result.State);
        Assert.Equal(MsiActionBindingState.Blocked, report.State);
        Assert.Equal(MsiActionBindingReason.SelectionChanged, report.Reason);
        Assert.Equal(MsiActionBindingChangedFields.Name | MsiActionBindingChangedFields.Version |
            MsiActionBindingChangedFields.Publisher | MsiActionBindingChangedFields.RegisteredUninstaller |
            MsiActionBindingChangedFields.NoRemove, report.ChangedFields);
        Assert.False(report.CanExecute);
    }

    [Fact]
    public void UnqualifiedSourceLeavesComparisonUnobservedRatherThanInventingNoChanges()
    {
        var f = Fixture();
        foreach (var fresh in new[]
        {
            f.Fresh with { Items = [] },
            f.Fresh with { Items = [f.Fresh.Items[0] with
                { RegistrationEvidence = f.Fresh.Items[0].RegistrationEvidence! with { WindowsInstaller = false } }] }
        })
        {
            var result = Assess(f.Scope, fresh, [f.Row]);
            var report = Assert.Single(result.Items);
            Assert.Equal(CheckState.Unknown, result.State);
            Assert.Equal(MsiActionBindingReason.SourceUnavailable, report.Reason);
            Assert.Null(report.ChangedFields);
        }
    }

    [Fact]
    public void UnchangedKnownAndUnknownRemovalRestrictionsKeepTheirDistinctReasons()
    {
        var restricted = Fixture(noRemove: true);
        var blocked = Assert.Single(Assess(restricted.Scope, restricted.Fresh, [restricted.Row]).Items);
        Assert.Equal(MsiActionBindingReason.RemovalRestricted, blocked.Reason);
        Assert.Equal(MsiActionBindingState.Blocked, blocked.State);
        Assert.Equal(MsiActionBindingChangedFields.None, blocked.ChangedFields);
        var unknown = Fixture(noRemove: null);
        var undecided = Assert.Single(Assess(unknown.Scope, unknown.Fresh, [unknown.Row]).Items);
        Assert.Equal(MsiActionBindingReason.RemovalRestrictionUnknown, undecided.Reason);
        Assert.Equal(MsiActionBindingState.Unknown, undecided.State);
        Assert.Equal(MsiActionBindingChangedFields.None, undecided.ChangedFields);
    }

    [Fact]
    public void ContextAndCoverageDiagnosticsDoNotMislabelStableRegistrationFields()
    {
        var f = Fixture();
        var extra = Assert.Single(Assess(f.Scope, f.Fresh,
            [f.Row, f.Row with { Context = MsiRegistrationContext.UserUnmanaged }]).Items);
        Assert.Equal(MsiActionBindingReason.MultipleContexts, extra.Reason);
        Assert.Equal(MsiActionBindingState.Blocked, extra.State);
        Assert.Equal(MsiActionBindingChangedFields.None, extra.ChangedFields);
        var mismatch = Assert.Single(Assess(f.Scope, f.Fresh,
            [f.Row with { Context = MsiRegistrationContext.UserManaged }]).Items);
        Assert.Equal(MsiActionBindingReason.SourceContextMismatch, mismatch.Reason);
        Assert.Equal(MsiActionBindingState.Blocked, mismatch.State);
        Assert.Equal(MsiActionBindingChangedFields.None, mismatch.ChangedFields);
        var incomplete = Assert.Single(Assess(f.Scope, f.Fresh, [f.Row], complete: false).Items);
        Assert.Equal(MsiActionBindingReason.CoverageIncomplete, incomplete.Reason);
        Assert.Equal(MsiActionBindingState.Unknown, incomplete.State);
        Assert.Equal(MsiActionBindingChangedFields.None, incomplete.ChangedFields);
    }

    [Fact]
    public void NativeDisplayMismatchIsSeparateFromChangesToTheSelectedArpRegistration()
    {
        var f = Fixture();
        var report = Assert.Single(Assess(f.Scope, f.Fresh, [f.Row with { Publisher = "Different MSI publisher" }]).Items);
        Assert.Equal(MsiActionBindingReason.MetadataNotMatched, report.Reason);
        Assert.Equal(MsiActionBindingState.Unknown, report.State);
        Assert.Equal(MsiActionBindingChangedFields.None, report.ChangedFields);
    }

    [Fact]
    public void ForgedBoundDiagnosticCannotTurnARestrictedReportIntoARequest()
    {
        var f = Fixture(noRemove: true);
        var result = Assess(f.Scope, f.Fresh, [f.Row]);
        var forged = result.Items[0] with
        { Reason = MsiActionBindingReason.Bound, ChangedFields = MsiActionBindingChangedFields.None };
        Assert.Empty(MsiUninstallRequestPolicy.CreatePreview(f.Scope, result with { Items = [forged] }, 0, DateTimeOffset.UtcNow).Items);
        Assert.False(forged.CanExecute);
    }

    [Fact]
    public void GenuineBindingWithLegacyDiagnosticsHasTheSamePolicyOutcome()
    {
        var f = Fixture();
        var result = Assess(f.Scope, f.Fresh, [f.Row]);
        var report = Assert.Single(result.Items);
        Assert.Equal(MsiActionBindingReason.Bound, report.Reason);
        Assert.Equal(MsiActionBindingChangedFields.None, report.ChangedFields);
        var legacy = report with { Reason = MsiActionBindingReason.Unknown, ChangedFields = null };
        Assert.Single(MsiUninstallRequestPolicy.CreatePreview(f.Scope, result with { Items = [legacy] }, 0, DateTimeOffset.UtcNow).Items);
        Assert.False(legacy.CanExecute);
    }

    [Fact]
    public void BoundPartialCoverageRemainsPartialDespiteTheBoundDiagnostic()
    {
        var f = Fixture(state: CheckState.Partial);
        var result = Assess(f.Scope, f.Fresh, [f.Row]);
        Assert.Equal(CheckState.Partial, result.State);
        var report = Assert.Single(result.Items);
        Assert.Equal(MsiActionBindingReason.Bound, report.Reason);
        Assert.Equal(MsiActionBindingChangedFields.None, report.ChangedFields);
        Assert.False(report.CanExecute);
    }
}
