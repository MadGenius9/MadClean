using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class SfcVerificationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
    // Synthetic metadata only: these tests never inspect or execute a Windows tool.
    private static SystemToolActionPreview Identity() => new(Guid.NewGuid(),
        SystemToolActionsCatalog.Find(SystemToolOperation.SfcVerifyOnly)!, @"C:\Windows\System32",
        @"C:\Windows\System32\sfc.exe", "10.0.fixture", new string('a', 64),
        SystemToolTrustState.MicrosoftSignatureVerified, "Synthetic trust metadata", Now.AddSeconds(-10),
        Now.AddSeconds(110), false, "Legacy production execution disabled");
    private static SfcVerificationPreview Preview() => Assert.Single(SfcVerificationPolicy.CreatePreview(Identity(), Now).Items);
    private static SfcVerificationIntent Intent(bool reserve = true)
    {
        var intent = Assert.Single(SfcVerificationPolicy.Confirm(Preview(), true, true, Now).Items);
        if (reserve) Assert.Equal(SfcVerificationDispatchReservation.Reserved, intent.TryReserveDispatch(Now));
        return intent;
    }
    private static SfcVerificationTransport Exited(int code = 0) => new()
    {
        AdministratorObserved = true, ClientStartAttempted = true, ClientStartObserved = true,
        ProcessId = 123, ClientStartedUtc = Now.AddSeconds(1), ClientExitObserved = true,
        ClientExitCode = code, ClientExitedUtc = Now.AddSeconds(2), StdoutDrained = true,
        StderrDrained = true, OwnershipReleased = true
    };

    [Fact]
    public void FreshIdentityDoesNotExtendItsLifetimeOrEnableLegacyExecution()
    {
        var identity = Identity();
        var preview = Assert.Single(SfcVerificationPolicy.CreatePreview(identity, Now).Items);
        Assert.Equal(identity.ExpiresUtc, preview.ExpiresUtc);
        Assert.Equal(identity.PreviewId, preview.IdentityPreviewId);
        Assert.Equal(identity.Sha256, preview.Sha256);
        Assert.Equal("sfc.exe /verifyonly", preview.CommandPreview);
        Assert.False(identity.CanLaunch);
        Assert.Contains("does not request elevation", preview.Permissions);
    }

    [Fact]
    public void RepairDescriptorChangedPathAndUntrustedIdentityCannotProduceVerificationIntent()
    {
        var identity = Identity();
        var invalid = new[]
        {
            identity with { Definition = SystemToolActionsCatalog.Find(SystemToolOperation.SfcScanNow)! },
            identity with { Definition = identity.Definition with { Arguments = ["/verifyonly", "/offwindir=C:\\other"] } },
            identity with { ToolPath = @"C:\Temp\sfc.exe" },
            identity with { TrustState = SystemToolTrustState.Unknown },
            identity with { ToolVersion = "Unknown" },
            identity with { CheckedUtc = Now.AddSeconds(1) },
            identity with { ExpiresUtc = Now },
            identity with { ExpiresUtc = Now.AddMinutes(3) },
            identity with { CheckedUtc = Now.ToOffset(TimeSpan.FromHours(1)) }
        };
        foreach (var item in invalid) Assert.Empty(SfcVerificationPolicy.CreatePreview(item, Now).Items);
    }

    [Fact]
    public void SelectionAcknowledgementFreshnessAndInvalidationAreIndependentBoundaries()
    {
        var preview = Preview();
        Assert.Empty(SfcVerificationPolicy.Confirm(preview, false, true, Now).Items);
        Assert.Empty(SfcVerificationPolicy.Confirm(preview, true, false, Now).Items);
        Assert.Empty(SfcVerificationPolicy.Confirm(preview, true, true, Now.AddSeconds(-1)).Items);
        Assert.Single(SfcVerificationPolicy.Confirm(preview, true, true, Now).Items);
        Assert.Empty(SfcVerificationPolicy.Confirm(preview, true, true, Now).Items);
        var expired = Preview();
        Assert.Empty(SfcVerificationPolicy.Confirm(expired, true, true, expired.ExpiresUtc).Items);
        var invalidated = Preview(); invalidated.Invalidate();
        Assert.Empty(SfcVerificationPolicy.Confirm(invalidated, true, true, Now).Items);
    }

    [Fact]
    public void ConcurrentConfirmationAndReservationAdmitOnlyOneRequest()
    {
        var preview = Preview();
        var intents = new System.Collections.Concurrent.ConcurrentBag<SfcVerificationIntent>();
        Parallel.For(0, 16, _ =>
        {
            foreach (var intent in SfcVerificationPolicy.Confirm(preview, true, true, Now).Items) intents.Add(intent);
        });
        var one = Assert.Single(intents);
        var reservations = new System.Collections.Concurrent.ConcurrentBag<SfcVerificationDispatchReservation>();
        Parallel.For(0, 16, _ => reservations.Add(one.TryReserveDispatch(Now)));
        Assert.Single(reservations, result => result == SfcVerificationDispatchReservation.Reserved);
        Assert.Equal(15, reservations.Count(result => result == SfcVerificationDispatchReservation.AlreadyReserved));
    }

    [Fact]
    public void ExpiryCancellationAndInvalidationPermanentlyRefuseUnreservedDispatch()
    {
        var stale = Intent(false);
        Assert.Equal(SfcVerificationDispatchReservation.Stale, stale.TryReserveDispatch(stale.ExpiresUtc));
        Assert.Equal(SfcVerificationDispatchReservation.Invalidated, stale.TryReserveDispatch(Now));
        var cancelled = Intent(false);
        Assert.Equal(SfcVerificationDispatchReservation.Cancelled, cancelled.TryReserveDispatch(Now, new CancellationToken(true)));
        Assert.Equal(SfcVerificationDispatchReservation.Invalidated, cancelled.TryReserveDispatch(Now));
        var invalidated = Intent(false); invalidated.Invalidate();
        Assert.Equal(SfcVerificationDispatchReservation.Invalidated, invalidated.TryReserveDispatch(Now));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3010)]
    [InlineData(-1)]
    public void EveryActualExitCodeRemainsClientEvidenceWithoutIntegrityOrSuccessMapping(int code)
    {
        var result = SfcVerificationPolicy.Assess(Intent(), Exited(code), Now, Now.AddSeconds(3));
        Assert.Equal(CheckState.Available, result.State);
        Assert.Equal(code, result.Transport.ClientExitCode);
        Assert.Equal(SfcIntegrityAssessment.Unknown, result.Integrity);
        Assert.Contains("not proof of successful verification", result.Message);
        Assert.Equal(@"%windir%\Logs\CBS\CBS.log", result.WindowsLogReference);
    }

    [Fact]
    public void MissingExitPendingOwnershipAndUnreservedRequestCannotBecomeCompletedObservation()
    {
        var intent = Intent();
        var pending = Exited() with { ClientExitObserved = null, ClientExitCode = null, ClientExitedUtc = null, OwnershipReleased = false };
        Assert.Equal(CheckState.Unknown, SfcVerificationPolicy.Assess(intent, pending, Now, null).State);
        Assert.Equal(CheckState.Unknown, SfcVerificationPolicy.Assess(intent, Exited() with { OwnershipReleased = null }, Now, Now.AddSeconds(3)).State);
        Assert.Equal(CheckState.Unknown, SfcVerificationPolicy.Assess(Intent(false), Exited(), Now, Now.AddSeconds(3)).State);
    }

    [Fact]
    public void MissingOrFailedOutputDrainCannotBecomeAvailableClientEvidence()
    {
        var completed = Exited();
        foreach (var facts in new[]
        {
            completed with { StdoutDrained = null }, completed with { StderrDrained = null },
            completed with { StdoutDrained = false }, completed with { StderrDrained = false }
        })
        {
            var result = SfcVerificationPolicy.Assess(Intent(), facts, Now, Now.AddSeconds(3));
            Assert.Equal(CheckState.Unknown, result.State);
            Assert.Equal(facts, result.Transport);
            Assert.Equal(SfcIntegrityAssessment.Unknown, result.Integrity);
        }
    }

    [Fact]
    public void DrainFactsRequireAnObservedClientButEofCanPrecedeItsExit()
    {
        foreach (bool observedDrain in new[] { true, false })
        {
            var denied = new SfcVerificationTransport
            {
                AdministratorObserved = false, ClientStartAttempted = false, ClientStartObserved = false,
                OwnershipReleased = true, StdoutDrained = observedDrain
            };
            Assert.False(SfcVerificationPolicy.IsTransportConsistent(denied));
            Assert.Equal(CheckState.Unknown, SfcVerificationPolicy.Assess(Intent(false), denied, Now, Now.AddSeconds(3)).State);
            var otherPipe = denied with { StdoutDrained = null, StderrDrained = observedDrain };
            Assert.False(SfcVerificationPolicy.IsTransportConsistent(otherPipe));
            Assert.Equal(CheckState.Unknown, SfcVerificationPolicy.Assess(Intent(false), otherPipe, Now, Now.AddSeconds(3)).State);
        }
        var running = Exited() with
        {
            ClientExitObserved = null, ClientExitCode = null, ClientExitedUtc = null, OwnershipReleased = false
        };
        Assert.True(SfcVerificationPolicy.IsTransportConsistent(running));
        Assert.Equal(CheckState.Unknown, SfcVerificationPolicy.Assess(Intent(), running, Now, null).State);
    }

    [Fact]
    public void StopObservationPreservesAnActualExitWithoutPromotingTheInterruptedCheck()
    {
        var facts = Exited(23) with { StopObservationRequested = true };
        var result = SfcVerificationPolicy.Assess(Intent(), facts, Now, Now.AddSeconds(3));
        Assert.Equal(CheckState.Cancelled, result.State);
        Assert.Equal(23, result.Transport.ClientExitCode);
        Assert.True(result.Transport.ClientExitObserved);
        Assert.Equal(SfcIntegrityAssessment.Unknown, result.Integrity);
        Assert.Contains("no SFC cancellation", result.Message);
    }

    [Fact]
    public void ContradictoryTimesPrivilegesAndOwnershipAreNeverQualified()
    {
        var actual = Exited();
        var invalid = new[]
        {
            actual with { AdministratorObserved = false }, actual with { ClientStartAttempted = false },
            actual with { ProcessId = 0 }, actual with { ClientExitCode = null },
            actual with { ClientExitObserved = false }, actual with { ClientStartedUtc = Now.AddSeconds(4) },
            actual with { ClientExitedUtc = Now.AddSeconds(-1) },
            actual with { ClientExitedUtc = Now.AddSeconds(4) },
            actual with { ClientStartedUtc = Now.ToOffset(TimeSpan.FromHours(1)) },
            new SfcVerificationTransport { ClientStartAttempted = true, OwnershipReleased = true },
            actual with { ClientExitObserved = null, ClientExitCode = null, ClientExitedUtc = null }
        };
        foreach (var facts in invalid)
            Assert.Equal(CheckState.Unknown, SfcVerificationPolicy.Assess(Intent(), facts, Now, Now.AddSeconds(3)).State);
    }

    [Fact]
    public void MissingStartTimestampCannotHideAnExitBeforeThisRequest()
    {
        var facts = Exited() with { ClientStartedUtc = null, ClientExitedUtc = Now.AddSeconds(-1) };
        var invalid = SfcVerificationPolicy.Assess(Intent(), facts, Now, Now.AddSeconds(3));
        Assert.Equal(CheckState.Unknown, invalid.State);
        Assert.Equal(facts, invalid.Transport);
        var actual = SfcVerificationPolicy.Assess(Intent(), facts with { ClientExitedUtc = Now.AddSeconds(2) }, Now, Now.AddSeconds(3));
        Assert.Equal(CheckState.Available, actual.State);
        Assert.Null(actual.Transport.ClientStartedUtc);
        Assert.Equal(SfcIntegrityAssessment.Unknown, actual.Integrity);
    }

    [Fact]
    public void DeniedAdministratorCheckDoesNotRequestElevationOrClaimHealth()
    {
        var facts = new SfcVerificationTransport { AdministratorObserved = false, ClientStartAttempted = false, OwnershipReleased = true };
        var result = SfcVerificationPolicy.Assess(Intent(false), facts, Now, Now.AddSeconds(1));
        Assert.Equal(CheckState.Unavailable, result.State);
        Assert.Equal(SfcIntegrityAssessment.Unknown, result.Integrity);
        Assert.Contains("No elevation was requested", result.Message);
    }

    [Fact]
    public void PrecancelledPreparationAndConfirmationProduceNoIntent()
    {
        var token = new CancellationToken(true);
        Assert.Throws<OperationCanceledException>(() => SfcVerificationPolicy.CreatePreview(Identity(), Now, token));
        var preview = Preview();
        Assert.Throws<OperationCanceledException>(() => SfcVerificationPolicy.Confirm(preview, true, true, Now, token));
        Assert.Single(SfcVerificationPolicy.Confirm(preview, true, true, Now).Items);
    }
}
