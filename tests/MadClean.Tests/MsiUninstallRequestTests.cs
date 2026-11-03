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
        Assert.Equal(new[] { "/x", ProductCode, "/norestart" }, preview.Arguments);
        Assert.False(Assert.Single(source.Binding.Items).CanExecute);
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
        Assert.Equal(new[] { "/x", ProductCode, "/norestart" }, preview.Arguments);
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
}
