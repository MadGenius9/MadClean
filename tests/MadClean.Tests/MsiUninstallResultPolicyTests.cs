using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class MsiUninstallResultPolicyTests
{
    private const string ProductCode = "{12345678-ABCD-1234-ABCD-123456789ABC}";

    private static (MsiUninstallRequestIntent Intent, MsiUninstallRequestResult Exit, DateTimeOffset Now) Fixture(bool reserve = true)
    {
        var observed = DateTimeOffset.UtcNow.AddSeconds(-5);
        const string source = "HKLM / 64-bit / Add or Remove Programs";
        string identity = @"HKLM/64/Software\Microsoft\Windows\CurrentVersion\Uninstall\" + ProductCode;
        InstalledApplication App(DateTimeOffset time) => new(identity, "Example app", "1.2.3", "Example publisher", source,
            null, "Windows Installer; action queued", new(identity, source, false, true, true, ProductCode, time));
        var selected = App(observed);
        var scope = Assert.Single(MsiActionBindingPolicy.CreateScope(new(CheckState.Available, "", [selected]),
            selected, observed.AddMilliseconds(10)).Items);
        var bindingTime = observed.AddMilliseconds(20);
        var binding = MsiActionBindingPolicy.Assess(scope, new(CheckState.Available, "", [App(bindingTime)]),
            [new(ProductCode, MsiRegistrationContext.Machine, MsiObservedProductState.Installed,
                selected.Name, selected.Version, selected.Publisher, bindingTime)], true, [], bindingTime);
        var prepared = observed.AddMilliseconds(30);
        var intent = Assert.Single(MsiUninstallRequestPolicy.CreatePreview(scope, binding, 7, prepared).Items).Intent;
        if (reserve) Assert.Equal(MsiUninstallDispatchReservation.Reserved,
            intent.TryReserveDispatch(true, 7, prepared.AddMilliseconds(10)));
        var result = new MsiUninstallRequestResult(intent.RequestId, MsiUninstallRequestState.ClientExited,
            prepared.AddMilliseconds(10), prepared.AddMilliseconds(20), prepared.AddMilliseconds(100), 123, 0,
            "Actual client facts only; no complete uninstall is inferred.")
        { ClientStartObserved = true, ExitDisposition = MsiUninstallExitDisposition.SuccessReported };
        return (intent, result, prepared.AddMilliseconds(200));
    }

    [Theory]
    [InlineData(MsiUninstallRequestState.NotStarted)]
    [InlineData(MsiUninstallRequestState.Blocked)]
    [InlineData(MsiUninstallRequestState.Preparing)]
    public void PreclientResultsNeedNoReservationButCannotSmuggleClientObservations(MsiUninstallRequestState state)
    {
        var f = Fixture(false);
        var empty = f.Exit with { State = state, StartedUtc = null, ExitedUtc = null, ClientProcessId = null,
            ExitCode = null, ExitDisposition = null, ClientStartObserved = null };
        Assert.True(Valid(f, empty));
        Assert.True(Valid(f, empty with { ClientStartObserved = false }));
        Assert.False(Valid(f, empty with { ClientStartObserved = true }));
        Assert.False(Valid(f, empty with { ClientProcessId = 123 }));
        Assert.False(Valid(f, empty with { StartedUtc = f.Exit.StartedUtc }));
        Assert.False(Valid(f, empty with { ExitedUtc = f.Exit.ExitedUtc }));
    }

    [Theory]
    [InlineData(false, false, false)] [InlineData(true, false, false)]
    [InlineData(false, true, false)] [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    public void ObservedExitSurvivesUnreadableOptionalPidStartAndCode(bool pid, bool start, bool code)
    {
        var f = Fixture();
        var result = f.Exit with { ClientProcessId = pid ? 123 : null, StartedUtc = start ? f.Exit.StartedUtc : null,
            ExitCode = code ? 0 : null, ExitDisposition = code ? MsiUninstallExitDisposition.SuccessReported : null };
        Assert.True(Valid(f, result));
        Assert.True(Valid(f, result with { State = MsiUninstallRequestState.MonitoringUnknown }));
    }

    [Theory]
    [InlineData(MsiUninstallRequestState.ClientStarted)]
    [InlineData(MsiUninstallRequestState.ClientExited)]
    [InlineData(MsiUninstallRequestState.MonitoringUnknown)]
    public void ActualClientFactsRequireBothTheOwnedReservationAndPositiveStartObservation(MsiUninstallRequestState state)
    {
        var reserved = Fixture();
        var result = state == MsiUninstallRequestState.ClientStarted
            ? reserved.Exit with { State = state, ExitedUtc = null, ExitCode = null, ExitDisposition = null }
            : reserved.Exit with { State = state };
        Assert.True(Valid(reserved, result));
        Assert.False(Valid(reserved, result with { ClientStartObserved = null }));
        Assert.False(Valid(reserved, result with { ClientStartObserved = false }));
        var unreserved = Fixture(false);
        Assert.False(Valid(unreserved, result with { RequestId = unreserved.Intent.RequestId,
            RequestedUtc = unreserved.Exit.RequestedUtc, StartedUtc = unreserved.Exit.StartedUtc,
            ExitedUtc = state == MsiUninstallRequestState.ClientStarted ? null : unreserved.Exit.ExitedUtc }));
    }

    [Fact]
    public void ClientStartedMayLackOptionalPropertiesButStillRequiresActualPositiveStartAndReservation()
    {
        var f = Fixture();
        var started = f.Exit with { State = MsiUninstallRequestState.ClientStarted, StartedUtc = null, ExitedUtc = null,
            ClientProcessId = null, ExitCode = null, ExitDisposition = null };
        Assert.True(Valid(f, started));
        Assert.False(Valid(f, started with { ClientStartObserved = null }));
        Assert.False(Valid(f, started with { ClientStartObserved = false }));
    }

    [Fact]
    public void FactFreeMonitoringUncertaintyDoesNotInventAStartOrRequireADispatch()
    {
        var f = Fixture(false);
        var uncertain = f.Exit with { State = MsiUninstallRequestState.MonitoringUnknown, StartedUtc = null,
            ExitedUtc = null, ClientProcessId = null, ExitCode = null, ExitDisposition = null, ClientStartObserved = null };
        Assert.True(Valid(f, uncertain));
        Assert.True(Valid(f, uncertain with { ClientStartObserved = false }));
        Assert.False(Valid(f, uncertain with { ClientStartObserved = true }));
        Assert.False(Valid(f, uncertain with { ClientProcessId = 123 }));
        var owned = Fixture();
        Assert.True(Valid(owned, owned.Exit with { State = MsiUninstallRequestState.MonitoringUnknown,
            StartedUtc = null, ExitedUtc = null, ClientProcessId = null, ExitCode = null, ExitDisposition = null }));
    }

    [Fact]
    public void ExpiryAndLaterInvalidationCannotEraseAnAlreadyObservedClientExit()
    {
        var f = Fixture();
        var lateExit = f.Intent.ExpiresUtc.AddHours(2);
        f.Intent.Invalidate(); // A committed request cannot be revoked by selection invalidation.
        Assert.True(MsiUninstallResultPolicy.IsConsistent(f.Intent, f.Exit with { ExitedUtc = lateExit }, lateExit.AddSeconds(1)));
        Assert.True(f.Intent.IsDispatchReserved);
    }

    [Fact]
    public void CleanupUncertaintyPreservesHistoricalExitWhileNotAuthenticatingDiagnosticsOrProviderText()
    {
        var f = Fixture();
        var uncertain = f.Exit with { State = MsiUninstallRequestState.MonitoringUnknown,
            Message = "Untrusted provider text /quiet /forcerestart secret", ToolIdentity =
                new((SystemToolIdentityStage)999, (SystemToolIdentityReason)999, 0) };
        Assert.True(Valid(f, uncertain));
        Assert.Equal(0, uncertain.ExitCode);
        Assert.Equal(MsiUninstallExitDisposition.SuccessReported, uncertain.ExitDisposition);
        // The contract qualifies literal client facts only. Diagnostics/message remain
        // independent, and must not become authentication, commands or action permission.
        Assert.True(f.Intent.IsDispatchReserved);
    }

    [Fact]
    public void WrongRequestNullsUndefinedStatesAndInvalidClockCannotQualify()
    {
        var f = Fixture();
        Assert.False(MsiUninstallResultPolicy.IsConsistent(null, f.Exit, f.Now));
        Assert.False(MsiUninstallResultPolicy.IsConsistent(f.Intent, null, f.Now));
        Assert.False(MsiUninstallResultPolicy.IsConsistent(f.Intent, f.Exit, default));
        Assert.False(MsiUninstallResultPolicy.IsConsistent(f.Intent, f.Exit, f.Now.ToOffset(TimeSpan.FromHours(1))));
        Assert.False(Valid(f, f.Exit with { RequestId = Guid.NewGuid() }));
        Assert.False(Valid(f, f.Exit with { RequestId = Guid.Empty }));
        Assert.False(Valid(f, f.Exit with { State = (MsiUninstallRequestState)999 }));
        Assert.False(Valid(f, f.Exit with { ExitDisposition = (MsiUninstallExitDisposition)999 }));
        Assert.False(Valid(f, f.Exit with { ClientProcessId = 0 }));
        Assert.False(Valid(f, f.Exit with { ClientProcessId = -1 }));
    }

    [Fact]
    public void EachTimeAxisIsExplicitUtcAndOrderedWithoutSubstitutingOtherTimes()
    {
        var f = Fixture();
        var bad = new[]
        {
            f.Exit with { RequestedUtc = default },
            f.Exit with { RequestedUtc = f.Intent.PreparedUtc.AddTicks(-1) },
            f.Exit with { RequestedUtc = f.Now.AddTicks(1) },
            f.Exit with { RequestedUtc = f.Exit.RequestedUtc.ToOffset(TimeSpan.FromHours(1)) },
            f.Exit with { StartedUtc = default(DateTimeOffset) },
            f.Exit with { StartedUtc = f.Exit.RequestedUtc.AddTicks(-1) },
            f.Exit with { StartedUtc = f.Now.AddTicks(1) },
            f.Exit with { StartedUtc = f.Exit.StartedUtc!.Value.ToOffset(TimeSpan.FromHours(1)) },
            f.Exit with { ExitedUtc = default(DateTimeOffset) },
            f.Exit with { ExitedUtc = f.Exit.RequestedUtc.AddTicks(-1) },
            f.Exit with { ExitedUtc = f.Exit.StartedUtc!.Value.AddTicks(-1) },
            f.Exit with { ExitedUtc = f.Now.AddTicks(1) },
            f.Exit with { ExitedUtc = f.Exit.ExitedUtc!.Value.ToOffset(TimeSpan.FromHours(1)) }
        };
        foreach (var result in bad) Assert.False(Valid(f, result));
    }

    [Fact]
    public void ExitClaimsRequireObservedExitAndSuppliedCodesRequireTheirExactClassification()
    {
        var f = Fixture();
        Assert.False(Valid(f, f.Exit with { ExitedUtc = null }));
        Assert.False(Valid(f, f.Exit with { State = MsiUninstallRequestState.ClientStarted }));
        Assert.False(Valid(f, f.Exit with { ExitCode = null }));
        Assert.False(Valid(f, f.Exit with { ExitDisposition = null }));
        Assert.False(Valid(f, f.Exit with { ExitDisposition = MsiUninstallExitDisposition.ProductNotInstalled }));
        Assert.False(Valid(f, f.Exit with { State = MsiUninstallRequestState.MonitoringUnknown, ExitedUtc = null }));
        Assert.False(Valid(f, f.Exit with { ExitedUtc = null, ExitCode = null, ExitDisposition = null }));
    }

    [Theory]
    [InlineData(0, MsiUninstallExitDisposition.SuccessReported)]
    [InlineData(1602, MsiUninstallExitDisposition.CancelledByUser)]
    [InlineData(1605, MsiUninstallExitDisposition.ProductNotInstalled)]
    [InlineData(1614, MsiUninstallExitDisposition.ProductNotInstalled)]
    [InlineData(1618, MsiUninstallExitDisposition.InstallerBusy)]
    [InlineData(3010, MsiUninstallExitDisposition.RestartRequired)]
    [InlineData(1641, MsiUninstallExitDisposition.RestartInitiatedUnexpectedly)]
    [InlineData(5, MsiUninstallExitDisposition.FailureReported)]
    [InlineData(-1, MsiUninstallExitDisposition.Unknown)]
    public void InstallerCodesRemainLiteralReportedDispositionsEvenUnderCleanupUncertainty(int code, MsiUninstallExitDisposition disposition)
    {
        var f = Fixture();
        var actual = f.Exit with { ExitCode = code, ExitDisposition = disposition };
        Assert.True(Valid(f, actual));
        Assert.True(Valid(f, actual with { State = MsiUninstallRequestState.MonitoringUnknown }));
        Assert.False(Valid(f, actual with { ExitDisposition = disposition == MsiUninstallExitDisposition.Unknown
            ? MsiUninstallExitDisposition.SuccessReported : MsiUninstallExitDisposition.Unknown }));
    }

    private static bool Valid((MsiUninstallRequestIntent Intent, MsiUninstallRequestResult Exit, DateTimeOffset Now) f,
        MsiUninstallRequestResult result) => MsiUninstallResultPolicy.IsConsistent(f.Intent, result, f.Now);
}
