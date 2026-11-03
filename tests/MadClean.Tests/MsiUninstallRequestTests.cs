using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class MsiUninstallRequestTests
{
    private const string ProductCode = "{12345678-ABCD-1234-ABCD-123456789ABC}";

    private static InstalledApplication App(DateTimeOffset observed, bool? noRemove = false)
    {
        const string source = "HKLM / 64-bit / Add or Remove Programs";
        string identity = @"HKLM/64/Software\Microsoft\Windows\CurrentVersion\Uninstall\" + ProductCode;
        return new(identity, "Example app", "1.2.3", "Example publisher", source, null, "Windows Installer; action queued",
            new(identity, source, noRemove, true, true, ProductCode, observed));
    }

    private static CheckResult<MsiActionBindingReport> Binding(MsiActionBindingScope scope, DateTimeOffset observed,
        CheckState freshState = CheckState.Available) => MsiActionBindingPolicy.Assess(scope,
            new(freshState, "Private provider text must not become an argument", [App(observed)]),
            [new(ProductCode, MsiRegistrationContext.Machine, MsiObservedProductState.Installed,
                "Example app", "1.2.3", "Example publisher", observed)], true, [], observed);

    private static (MsiActionBindingScope Scope, CheckResult<MsiActionBindingReport> Binding,
        DateTimeOffset Now) Fixture(CheckState historical = CheckState.Available, CheckState fresh = CheckState.Available)
    {
        var start = DateTimeOffset.UtcNow.AddSeconds(-2);
        var selected = App(start.AddMilliseconds(-1));
        var scope = Assert.Single(MsiActionBindingPolicy.CreateScope(new(historical, "", [selected]), selected, start).Items);
        return (scope, Binding(scope, start.AddMilliseconds(10), fresh), start.AddMilliseconds(20));
    }

    private static MsiUninstallRequestPreview Preview(long generation = 7)
    {
        var source = Fixture();
        return Assert.Single(MsiUninstallRequestPolicy.CreatePreview(source.Scope, source.Binding, generation, source.Now).Items);
    }

    [Fact]
    public void ExactRegisteredProductRequestHasOnlyFixedArgumentsAndDoesNotGrantOldMetadataPermission()
    {
        var source = Fixture();
        var preview = Assert.Single(MsiUninstallRequestPolicy.CreatePreview(source.Scope, source.Binding, 7, source.Now).Items);
        Assert.Equal(new[] { "/x", ProductCode, "/qf", "/norestart" }, preview.Arguments);
        Assert.False(Assert.Single(source.Binding.Items).CanExecute);
        Assert.Null(preview.Intent.SelectedProductScope);
        Assert.Equal(MsiRegistrationContext.Machine, preview.Context);
        Assert.Equal(source.Scope.ValidatedUtc.Add(MsiRegistrationReview.ScopeLifetime), preview.ExpiresUtc);
        Assert.Contains("Windows Installer resolves", preview.Limitations);
        Assert.Contains("not a frozen context", preview.Limitations);
        Assert.DoesNotContain("Private provider", string.Join(" ", preview.Arguments));
        Assert.Throws<NotSupportedException>(() => ((IList<string>)preview.Arguments)[1] = "other-product");
    }

    [Fact]
    public void DiscoveredDisplayTextCannotInjectInstallerOptions()
    {
        var start = DateTimeOffset.UtcNow.AddSeconds(-1);
        var selected = App(start.AddMilliseconds(-1)) with
        { Name = "Example /quiet /forcerestart", Publisher = "Publisher /i other-package.msi" };
        var scope = Assert.Single(MsiActionBindingPolicy.CreateScope(new(CheckState.Available, "", [selected]), selected, start).Items);
        var observed = start.AddMilliseconds(10);
        var fresh = selected with { RegistrationEvidence = selected.RegistrationEvidence! with { ObservedUtc = observed } };
        var binding = MsiActionBindingPolicy.Assess(scope, new(CheckState.Available, "", [fresh]),
            [new(ProductCode, MsiRegistrationContext.Machine, MsiObservedProductState.Installed,
                selected.Name, selected.Version, selected.Publisher, observed)], true, [], observed);
        var preview = Assert.Single(MsiUninstallRequestPolicy.CreatePreview(scope, binding, 0, start.AddMilliseconds(20)).Items);
        Assert.Equal(selected.Name, preview.Name);
        Assert.Equal(new[] { "/x", ProductCode, "/qf", "/norestart" }, preview.Arguments);
        Assert.DoesNotContain("/quiet", preview.Arguments);
        Assert.DoesNotContain("/forcerestart", preview.Arguments);
    }

    [Fact]
    public void IncompleteFreshCoverageCannotBecomeARequestWhileHistoricalCoverageIsPreserved()
    {
        var incomplete = Fixture(fresh: CheckState.Partial);
        Assert.Empty(MsiUninstallRequestPolicy.CreatePreview(incomplete.Scope, incomplete.Binding, 0, incomplete.Now).Items);
        var historical = Fixture(historical: CheckState.Partial);
        var result = MsiUninstallRequestPolicy.CreatePreview(historical.Scope, historical.Binding, 0, historical.Now);
        Assert.Equal(CheckState.Partial, result.State);
        Assert.Single(result.Items);
    }

    [Fact]
    public void RestrictionAmbiguityAndAdvertisedProductsCannotBeSmuggledThroughABoundLabel()
    {
        var source = Fixture();
        var report = Assert.Single(source.Binding.Items);
        MsiActionBindingReport[] changed =
        [
            report with { NoRemove = null }, report with { NoRemove = true }, report with { EnumerationComplete = false },
            report with { Instances = [report.Instances[0], report.Instances[0] with { Context = MsiRegistrationContext.UserManaged }] },
            report with { Instances = [report.Instances[0] with { ProductState = MsiObservedProductState.Advertised }] },
            report with { ProductCode = "{AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA}" },
            report with { Instances = [report.Instances[0] with { Context = MsiRegistrationContext.UserUnmanaged }] }
        ];
        foreach (var value in changed)
            Assert.Empty(MsiUninstallRequestPolicy.CreatePreview(source.Scope, source.Binding with { Items = [value] }, 0, source.Now).Items);
    }

    [Fact]
    public void DispatchRecheckMustActuallyPostdateThePreviewAndRetainItsExactObservedContextAndFields()
    {
        var preview = Preview();
        Assert.NotEmpty(MsiUninstallRequestPolicy.ValidateFreshBinding(preview.Intent,
            Binding(preview.Intent.Scope, preview.PreparedUtc.AddMilliseconds(-1)), preview.PreparedUtc.AddMilliseconds(20)));
        var fresh = Binding(preview.Intent.Scope, preview.PreparedUtc.AddMilliseconds(10));
        Assert.Empty(MsiUninstallRequestPolicy.ValidateFreshBinding(preview.Intent, fresh, preview.PreparedUtc.AddMilliseconds(20)));
        var report = fresh.Items[0];
        foreach (var row in new[]
        {
            report.Instances[0] with { Name = "Changed" }, report.Instances[0] with { Version = "9" },
            report.Instances[0] with { Publisher = "Changed" }, report.Instances[0] with { ObservedUtc = preview.PreparedUtc.AddTicks(-1) }
        })
            Assert.NotEmpty(MsiUninstallRequestPolicy.ValidateFreshBinding(preview.Intent,
                fresh with { Items = [report with { Instances = [row] }] }, preview.PreparedUtc.AddMilliseconds(20)));
    }

    [Fact]
    public void ConfirmationGenerationAndExpiryAreCheckedAtTheSingleDispatchBoundary()
    {
        var intent = Preview().Intent;
        Assert.Equal(MsiUninstallDispatchReservation.NotConfirmed, intent.TryReserveDispatch(false, 7, intent.PreparedUtc));
        Assert.False(intent.IsDispatchReserved);
        Assert.Equal(MsiUninstallDispatchReservation.SourceChanged, intent.TryReserveDispatch(true, 8, intent.PreparedUtc));
        Assert.True(intent.IsInvalidated);
        Assert.Equal(MsiUninstallDispatchReservation.Invalidated, intent.TryReserveDispatch(true, 7, intent.PreparedUtc));
        var expired = Preview().Intent;
        Assert.Equal(MsiUninstallDispatchReservation.Stale, expired.TryReserveDispatch(true, 7, expired.ExpiresUtc));
        Assert.False(expired.IsDispatchReserved);
    }

    [Fact]
    public void CancellationBeforeReservationStopsTheRequestButCannotRevokeReservedDispatch()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var cancelled = Preview().Intent;
        Assert.Equal(MsiUninstallDispatchReservation.Cancelled, cancelled.TryReserveDispatch(true, 7, cancelled.PreparedUtc, stop.Token));
        Assert.True(cancelled.IsInvalidated);
        var submitted = Preview().Intent;
        Assert.Equal(MsiUninstallDispatchReservation.Reserved, submitted.TryReserveDispatch(true, 7, submitted.PreparedUtc));
        submitted.Invalidate();
        Assert.Equal(MsiUninstallDispatchReservation.AlreadyReserved, submitted.TryReserveDispatch(true, 7, submitted.PreparedUtc, stop.Token));
        Assert.True(submitted.IsDispatchReserved);
        Assert.False(submitted.IsInvalidated);
    }

    [Fact]
    public void ConcurrentSubmissionsCanReserveOnlyOneProcessRequest()
    {
        var intent = Preview().Intent;
        var outcomes = new MsiUninstallDispatchReservation[32];
        Parallel.For(0, outcomes.Length, index => outcomes[index] = intent.TryReserveDispatch(true, 7, intent.PreparedUtc));
        Assert.Equal(1, outcomes.Count(value => value == MsiUninstallDispatchReservation.Reserved));
        Assert.Equal(31, outcomes.Count(value => value == MsiUninstallDispatchReservation.AlreadyReserved));
    }

    [Fact]
    public void InvalidationAndReservationHaveOneWinnerWithoutUndoingDispatch()
    {
        for (int iteration = 0; iteration < 32; iteration++)
        {
            var intent = Preview().Intent;
            MsiUninstallDispatchReservation result = default;
            Parallel.Invoke(intent.Invalidate, () => result = intent.TryReserveDispatch(true, 7, intent.PreparedUtc));
            if (result == MsiUninstallDispatchReservation.Reserved)
            { Assert.True(intent.IsDispatchReserved); Assert.False(intent.IsInvalidated); }
            else
            { Assert.Equal(MsiUninstallDispatchReservation.Invalidated, result); Assert.True(intent.IsInvalidated); Assert.False(intent.IsDispatchReserved); }
        }
    }

    [Fact]
    public void ExitInterpretationPreservesCancelContentionAndUnexpectedRestartWithoutAnAbsenceClaim()
    {
        Assert.Equal(MsiUninstallExitDisposition.SuccessReported, MsiUninstallRequestPolicy.ClassifyExit(0));
        Assert.Equal(MsiUninstallExitDisposition.CancelledByUser, MsiUninstallRequestPolicy.ClassifyExit(1602));
        Assert.Equal(MsiUninstallExitDisposition.InstallerBusy, MsiUninstallRequestPolicy.ClassifyExit(1618));
        Assert.Equal(MsiUninstallExitDisposition.RestartRequired, MsiUninstallRequestPolicy.ClassifyExit(3010));
        Assert.Equal(MsiUninstallExitDisposition.RestartInitiatedUnexpectedly, MsiUninstallRequestPolicy.ClassifyExit(1641));
        Assert.NotEqual(MsiUninstallExitDisposition.SuccessReported, MsiUninstallRequestPolicy.ClassifyExit(1605));
        Assert.Equal(MsiUninstallExitDisposition.Unknown, MsiUninstallRequestPolicy.ClassifyExit(unchecked((int)0x80004005)));
    }

    [Fact]
    public void PreparationCancellationDoesNotMintAnIntent()
    {
        var source = Fixture();
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => MsiUninstallRequestPolicy.CreatePreview(source.Scope, source.Binding, 0, source.Now, stop.Token));
    }

    private static (MsiSelectedProductReadScope Scope, DateTimeOffset Start) SelectedFixture()
    {
        var start = DateTimeOffset.UtcNow.AddSeconds(-5);
        var selected = App(start.AddMilliseconds(-10));
        var snapshot = new CheckResult<InstalledApplication>(CheckState.Partial, "Historical incomplete inventory", [selected]);
        return (Assert.Single(MsiSelectedProductPreparationPolicy.CreateScope(snapshot, selected, 7, start).Items), start);
    }
    private static CheckResult<MsiSelectedProductPreparation> SelectedFacts(MsiSelectedProductReadScope scope, DateTimeOffset observed)
    {
        var fresh = scope.Application with { InstallLocation = null,
            RegistrationEvidence = scope.Application.RegistrationEvidence! with { ObservedUtc = observed } };
        var lookups = Enum.GetValues<MsiArpProvenance>().Select(source => source == scope.OriginalSource
            ? new MsiSelectedArpLookup(source, MsiSelectedArpLookupState.Present, fresh, false, observed, null)
            : new MsiSelectedArpLookup(source, MsiSelectedArpLookupState.Absent, null, null, observed, 2)).ToArray();
        return MsiSelectedProductPreparationPolicy.Assess(scope, lookups,
            [new(ProductCode, MsiRegistrationContext.UserUnmanaged, MsiObservedProductState.Installed,
                scope.Application.Name, scope.Application.Version, scope.Application.Publisher, observed)],
            true, 0, 7, observed.AddMilliseconds(10));
    }

    [Fact]
    public void ExactProductFactsCanPrepareASeparateRequestWithoutChangingHistoricalCoverageOrOldContextAcceptance()
    {
        var data = SelectedFixture(); var facts = SelectedFacts(data.Scope, data.Start.AddMilliseconds(100));
        var result = MsiUninstallRequestPolicy.CreatePreview(data.Scope, facts, 7, data.Start.AddMilliseconds(200));
        var request = Assert.Single(result.Items).Intent;
        Assert.Equal(CheckState.Partial, result.State);
        Assert.Same(data.Scope, request.SelectedProductScope);
        Assert.Equal(4, request.Scope.ExpectedContextMask);
        Assert.Equal(MsiRegistrationContext.UserUnmanaged, request.Context);
        Assert.Equal(CheckState.Partial, request.Scope.SnapshotState);
        Assert.False(facts.Items[0].CurrentGlobalSnapshotWasRead);
        Assert.False(facts.Items[0].CanExecute);
        var legacy = new MsiActionBindingReport(MsiActionBindingState.Bound, request.RegistrationIdentity, ProductCode,
            CheckState.Partial, CheckState.Available, false, 4, facts.Items[0].Instances, true, data.Start.AddMilliseconds(100), []);
        Assert.Empty(MsiUninstallRequestPolicy.CreatePreview(request.Scope, new(CheckState.Partial, "", [legacy]), 7,
            data.Start.AddMilliseconds(200)).Items);
        Assert.Equal(new[] { "/x", ProductCode, "/qf", "/norestart" }, request.Arguments);
    }

    [Fact]
    public void AClaimedTargetSuccessCannotReplaceReassessmentOfActualRestrictionsAndLookupFacts()
    {
        var data = SelectedFixture(); var facts = SelectedFacts(data.Scope, data.Start.AddMilliseconds(100));
        var actual = facts.Items[0];
        Assert.Empty(MsiUninstallRequestPolicy.CreatePreview(data.Scope, facts with { State = CheckState.Available }, 7, data.Start.AddMilliseconds(200)).Items);
        Assert.Empty(MsiUninstallRequestPolicy.CreatePreview(data.Scope, facts, 8, data.Start.AddMilliseconds(200)).Items);
        var restricted = actual.Lookups.Select(row => row.State == MsiSelectedArpLookupState.Present
            ? row with { Registration = row.Registration! with { RegistrationEvidence = row.Registration!.RegistrationEvidence! with { NoRemove = true } } }
            : row).ToArray();
        var blocked = MsiSelectedProductPreparationPolicy.Assess(data.Scope, restricted, actual.Instances, true, 0, 7, actual.CheckedUtc);
        Assert.Equal(MsiSelectedProductPreparationState.Blocked, Assert.Single(blocked.Items).State);
        Assert.Empty(MsiUninstallRequestPolicy.CreatePreview(data.Scope, blocked with { State = CheckState.Available }, 7, data.Start.AddMilliseconds(200)).Items);
        var invented = actual.Lookups.Select(row => row.State == MsiSelectedArpLookupState.Absent ? row with { NativeError = null } : row).ToArray();
        var incomplete = MsiSelectedProductPreparationPolicy.Assess(data.Scope, invented, actual.Instances, true, 0, 7, actual.CheckedUtc);
        Assert.Empty(incomplete.Items);
        Assert.Empty(MsiUninstallRequestPolicy.CreatePreview(data.Scope, incomplete with { State = CheckState.Available }, 7, data.Start.AddMilliseconds(200)).Items);
    }

    [Fact]
    public void FreshSelectedRequestRequiresEveryActualLookupAndNativeObservationAfterPreparation()
    {
        var data = SelectedFixture(); var initial = SelectedFacts(data.Scope, data.Start.AddMilliseconds(100));
        var preview = Assert.Single(MsiUninstallRequestPolicy.CreatePreview(data.Scope, initial, 7, data.Start.AddMilliseconds(200)).Items);
        Assert.NotEmpty(MsiUninstallRequestPolicy.ValidateFreshPreparation(preview.Intent, initial, data.Start.AddMilliseconds(400)));
        var fresh = SelectedFacts(data.Scope, data.Start.AddMilliseconds(300));
        Assert.Empty(MsiUninstallRequestPolicy.ValidateFreshPreparation(preview.Intent, fresh, data.Start.AddMilliseconds(400)));
        var row = fresh.Items[0];
        var changedReports = new[]
        {
            MsiSelectedProductPreparationPolicy.Assess(data.Scope,
                row.Lookups.Select((lookup,index) => index == 0 ? lookup with { ObservedUtc = data.Start.AddMilliseconds(150) } : lookup).ToArray(),
                row.Instances, true, 0, 7, row.CheckedUtc),
            MsiSelectedProductPreparationPolicy.Assess(data.Scope, row.Lookups,
                [row.Instances[0] with { ObservedUtc = data.Start.AddMilliseconds(150) }], true, 0, 7, row.CheckedUtc),
            MsiSelectedProductPreparationPolicy.Assess(data.Scope, row.Lookups,
                [row.Instances[0] with { Context = MsiRegistrationContext.UserManaged }], true, 0, 7, row.CheckedUtc)
        };
        foreach (var changed in changedReports)
        {
            Assert.Equal(MsiSelectedProductPreparationState.ObservedMatch, Assert.Single(changed.Items).State);
            Assert.NotEmpty(MsiUninstallRequestPolicy.ValidateFreshPreparation(preview.Intent, changed, data.Start.AddMilliseconds(400)));
        }
    }

    [Fact]
    public void SelectedIntentCancellationInvalidatesItsReadSelectorWithoutRevokingCommittedDispatch()
    {
        var data = SelectedFixture(); var facts = SelectedFacts(data.Scope, data.Start.AddMilliseconds(100));
        var intent = Assert.Single(MsiUninstallRequestPolicy.CreatePreview(data.Scope, facts, 7, data.Start.AddMilliseconds(200)).Items).Intent;
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.Equal(MsiUninstallDispatchReservation.Cancelled, intent.TryReserveDispatch(true, 7, intent.PreparedUtc, stop.Token));
        Assert.True(intent.IsInvalidated); Assert.True(data.Scope.IsInvalidated);
        Assert.Empty(MsiUninstallRequestPolicy.CreatePreview(data.Scope, facts, 7, data.Start.AddMilliseconds(300)).Items);
        var next = SelectedFixture(); var nextFacts = SelectedFacts(next.Scope, next.Start.AddMilliseconds(100));
        var committed = Assert.Single(MsiUninstallRequestPolicy.CreatePreview(next.Scope, nextFacts, 7, next.Start.AddMilliseconds(200)).Items).Intent;
        Assert.Equal(MsiUninstallDispatchReservation.Reserved, committed.TryReserveDispatch(true, 7, committed.PreparedUtc));
        committed.Invalidate(); next.Scope.Invalidate();
        Assert.True(committed.IsDispatchReserved); Assert.False(committed.IsInvalidated); Assert.False(next.Scope.IsInvalidated);
    }

    [Fact]
    public void DirectSelectorInvalidationAndRequestReservationHaveOneAtomicWinner()
    {
        for (int iteration = 0; iteration < 16; iteration++)
        {
            var data = SelectedFixture(); var facts = SelectedFacts(data.Scope, data.Start.AddMilliseconds(100));
            var intent = Assert.Single(MsiUninstallRequestPolicy.CreatePreview(data.Scope, facts, 7, data.Start.AddMilliseconds(200)).Items).Intent;
            MsiUninstallDispatchReservation result = default;
            Parallel.Invoke(data.Scope.Invalidate, () => result = intent.TryReserveDispatch(true, 7, intent.PreparedUtc));
            if (result == MsiUninstallDispatchReservation.Reserved)
            { Assert.True(intent.IsDispatchReserved); Assert.False(data.Scope.IsInvalidated); }
            else
            { Assert.Equal(MsiUninstallDispatchReservation.Invalidated, result); Assert.True(data.Scope.IsInvalidated); Assert.False(intent.IsDispatchReserved); }
        }
    }

    [Fact]
    public void TwoPreviewsFromOneSelectorCannotCommitTwoRequestsAndAClonedScopeCannotRefreshAnIntent()
    {
        var data = SelectedFixture(); var facts = SelectedFacts(data.Scope, data.Start.AddMilliseconds(100));
        var a = Assert.Single(MsiUninstallRequestPolicy.CreatePreview(data.Scope, facts, 7, data.Start.AddMilliseconds(200)).Items).Intent;
        var b = Assert.Single(MsiUninstallRequestPolicy.CreatePreview(data.Scope, facts, 7, data.Start.AddMilliseconds(200)).Items).Intent;
        var original = data.Scope.Application;
        var otherScope = Assert.Single(MsiSelectedProductPreparationPolicy.CreateScope(new(CheckState.Partial, "", [original]), original, 7, data.Start).Items);
        Assert.NotEmpty(MsiUninstallRequestPolicy.ValidateFreshPreparation(a, SelectedFacts(otherScope, data.Start.AddMilliseconds(300)), data.Start.AddMilliseconds(400)));
        MsiUninstallDispatchReservation first = default, second = default;
        Parallel.Invoke(() => first = a.TryReserveDispatch(true, 7, a.PreparedUtc), () => second = b.TryReserveDispatch(true, 7, b.PreparedUtc));
        Assert.Equal(1, new[] { first, second }.Count(value => value == MsiUninstallDispatchReservation.Reserved));
        Assert.Equal(1, new[] { first, second }.Count(value => value == MsiUninstallDispatchReservation.Invalidated));
    }

}
