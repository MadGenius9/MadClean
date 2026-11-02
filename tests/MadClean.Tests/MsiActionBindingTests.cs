using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class MsiActionBindingTests
{
    private const string Code = "{12345678-ABCD-1234-ABCD-123456789ABC}";
    private static InstalledApplication App(DateTimeOffset observed, string hive = "HKLM/64/", bool? noRemove = false)
    {
        string source = hive == "HKCU/shared/" ? "HKCU / shared / Add or Remove Programs" : "HKLM / 64-bit / Add or Remove Programs";
        string identity = hive + @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + Code;
        return new(identity, "Example", "1.2", "Publisher", source, null, "Windows Installer; action queued")
        { RegistrationEvidence = new(identity, source, noRemove, true, true, Code, observed) };
    }
    private static (MsiActionBindingScope Scope, CheckResult<InstalledApplication> Fresh) Fixture(CheckState state = CheckState.Available, string hive = "HKLM/64/")
    {
        var selected = App(DateTimeOffset.UtcNow.AddSeconds(-1), hive);
        var snapshot = new CheckResult<InstalledApplication>(state, "", [selected]);
        var scope = Assert.Single(MsiActionBindingPolicy.CreateScope(snapshot, selected, DateTimeOffset.UtcNow).Items);
        var fresh = App(DateTimeOffset.UtcNow, hive);
        return (scope, new(state, "private-provider-message", [fresh]));
    }
    private static MsiRegistrationObservation Instance(MsiRegistrationContext context) =>
        new(Code, context, MsiObservedProductState.Installed, "Example", "1.2", "Publisher", DateTimeOffset.UtcNow);
    private static CheckResult<MsiActionBindingReport> Assess(MsiActionBindingScope scope, CheckResult<InstalledApplication> fresh,
        params MsiRegistrationObservation[] instances) => MsiActionBindingPolicy.Assess(scope, fresh, instances, true, [], DateTimeOffset.UtcNow);

    [Theory]
    [InlineData(CheckState.Available)]
    [InlineData(CheckState.Partial)]
    public void UniqueMatchingContextIsObservedBindingNeverActionPermission(CheckState state)
    {
        var f = Fixture(state); var result = Assess(f.Scope, f.Fresh, Instance(MsiRegistrationContext.Machine));
        Assert.Equal(state, result.State);
        var report = Assert.Single(result.Items); Assert.Equal(MsiActionBindingState.Bound, report.State);
        Assert.False(report.CanExecute); Assert.Equal(state, report.CurrentSnapshotState);
        Assert.Contains(report.Coverage, line => line.Contains("No global absence", StringComparison.Ordinal));
        Assert.Contains(report.Coverage, line => line.Contains("non-atomic", StringComparison.Ordinal));
        Assert.DoesNotContain("private-provider-message", string.Join("", report.Coverage));
    }

    [Theory]
    [InlineData("HKLM/64/", MsiRegistrationContext.Machine, MsiRegistrationContext.UserUnmanaged)]
    [InlineData("HKCU/shared/", MsiRegistrationContext.UserManaged, MsiRegistrationContext.Machine)]
    public void AdditionalContextBlocksBindingEvenWhenSelectedSourceItselfMatches(string hive, MsiRegistrationContext first, MsiRegistrationContext second)
    {
        var f = Fixture(hive: hive); var result = Assess(f.Scope, f.Fresh, Instance(first), Instance(second));
        Assert.Equal(CheckState.Unavailable, result.State);
        var report = Assert.Single(result.Items); Assert.Equal(MsiActionBindingState.Blocked, report.State);
        Assert.Equal(2, report.Instances.Count); Assert.False(report.CanExecute);
    }

    [Fact]
    public void FreshRestrictionSourceOrDisplayChangesCannotReuseSelection()
    {
        var f = Fixture(); var row = f.Fresh.Items[0];
        InstalledApplication[] changed =
        [ row with { Name = "Changed" }, row with { Version = "2" }, row with { Publisher = "Other" },
          row with { RegistrationEvidence = row.RegistrationEvidence! with { NoRemove = true } },
          row with { RegistrationEvidence = row.RegistrationEvidence! with { NoRemove = null } } ];
        foreach (var fresh in changed)
        {
            var result = Assess(f.Scope, f.Fresh with { Items = [fresh] }, Instance(MsiRegistrationContext.Machine));
            Assert.NotEqual(MsiActionBindingState.Bound, Assert.Single(result.Items).State);
        }
    }

    [Fact]
    public void UnknownRestrictionStaysUnknownAndRestrictedCannotBeOverriddenByMsi()
    {
        foreach (bool? restriction in new bool?[] { null, true })
        {
            var selected = App(DateTimeOffset.UtcNow.AddSeconds(-1), noRemove: restriction);
            var scope = Assert.Single(MsiActionBindingPolicy.CreateScope(new(CheckState.Available, "", [selected]), selected, DateTimeOffset.UtcNow).Items);
            var fresh = new CheckResult<InstalledApplication>(CheckState.Available, "", [App(DateTimeOffset.UtcNow, noRemove: restriction)]);
            var result = Assess(scope, fresh, Instance(MsiRegistrationContext.Machine));
            Assert.NotEqual(MsiActionBindingState.Bound, Assert.Single(result.Items).State);
            Assert.Equal(restriction, result.Items[0].NoRemove); Assert.False(result.Items[0].CanExecute);
        }
    }

    [Fact]
    public void IncompleteDuplicateAdvertisedOrMismatchingContextEvidenceIsNeverBound()
    {
        var f = Fixture(); var row = Instance(MsiRegistrationContext.Machine);
        var incomplete = MsiActionBindingPolicy.Assess(f.Scope, f.Fresh, [row], false, [], DateTimeOffset.UtcNow);
        Assert.Equal(CheckState.Unknown, incomplete.State);
        foreach (var rows in new MsiRegistrationObservation[][]
        { [], [row, row], [row with { ProductState = MsiObservedProductState.Advertised }], [row with { Name = "Unknown" }],
          [row with { Publisher = "Other" }], [row with { Context = MsiRegistrationContext.UserManaged }],
          [row with { ProductCode = Guid.NewGuid().ToString("B") }] })
            Assert.NotEqual(MsiActionBindingState.Bound, Assert.Single(Assess(f.Scope, f.Fresh, rows).Items).State);
        var issue = MsiActionBindingPolicy.Assess(f.Scope, f.Fresh, [row], true, ["Native query incomplete"], DateTimeOffset.UtcNow);
        Assert.Equal(CheckState.Unknown, issue.State);
    }

    [Fact]
    public void CanonicalProductGuidCaseDoesNotCreateFalseMismatch()
    {
        var f = Fixture(); var row = Instance(MsiRegistrationContext.Machine) with { ProductCode = Code.ToLowerInvariant() };
        Assert.Equal(MsiActionBindingState.Bound, Assert.Single(Assess(f.Scope, f.Fresh, row).Items).State);
    }

    [Fact]
    public void MissingAmbiguousUnrefreshedOrFailedArpCannotBecomeAbsenceOrBinding()
    {
        var f = Fixture();
        var old = f.Fresh.Items[0] with { RegistrationEvidence = f.Fresh.Items[0].RegistrationEvidence! with { ObservedUtc = f.Scope.ValidatedUtc.AddTicks(-1) } };
        CheckResult<InstalledApplication>[] invalid =
        [ f.Fresh with { Items = [] }, f.Fresh with { Items = [f.Fresh.Items[0], f.Fresh.Items[0]] },
          f.Fresh with { Items = [old] }, f.Fresh with { State = CheckState.Failed } ];
        foreach (var fresh in invalid)
        {
            var result = Assess(f.Scope, fresh, Instance(MsiRegistrationContext.Machine));
            Assert.Equal(MsiActionBindingState.Unknown, Assert.Single(result.Items).State);
        }
    }

    [Fact]
    public void ScopeRequiresExactActualSelectionAndCancellationPublishesNothing()
    {
        var selected = App(DateTimeOffset.UtcNow);
        var snapshot = new CheckResult<InstalledApplication>(CheckState.Available, "", [selected]);
        Assert.Empty(MsiActionBindingPolicy.CreateScope(snapshot, selected with { }, DateTimeOffset.UtcNow).Items);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => MsiActionBindingPolicy.CreateScope(snapshot, selected, DateTimeOffset.UtcNow, cancellation.Token));
        var f = Fixture();
        Assert.ThrowsAny<OperationCanceledException>(() => MsiActionBindingPolicy.Assess(f.Scope, f.Fresh, [], true, [], DateTimeOffset.UtcNow, cancellation.Token));
    }
}
