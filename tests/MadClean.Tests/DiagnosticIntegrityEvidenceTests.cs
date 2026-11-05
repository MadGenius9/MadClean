using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class DiagnosticIntegrityEvidenceTests
{
    // Synthetic metadata only. These fixtures never inspect, launch or qualify an actual Windows tool.
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow.AddMinutes(-1);
    private static ComponentStoreStatusIntent ComponentIntent(ComponentStoreCheckKind kind)
    {
        var preview = Assert.Single(ComponentStoreStatusPolicy.CreatePreview(kind, Now).Items);
        var intent = Assert.Single(ComponentStoreStatusPolicy.Confirm(preview, true, Now).Items);
        Assert.Equal(ComponentStoreStatusDispatchReservation.Reserved, intent.TryReserveDispatch(Now));
        return intent;
    }
    private static ComponentStoreStatusResult ComponentResult(ComponentStoreStatusIntent intent, string health = "Healthy")
    {
        var evidence = new ComponentStoreStatusEvidence
        {
            RequestId = intent.RequestId, Kind = intent.Kind, Stage = ComponentStoreStatusStage.Completed,
            Outcome = ComponentStoreStatusOutcome.Completed, HostQualified = true, ModuleBound = true,
            CmdletQualified = true, OutputContractQualified = true, InvocationStarted = true,
            PipelineState = ComponentStorePipelineState.Completed, EndInvokeReturned = true,
            CancelEventObserved = false, BeginStopCalled = false, OutputCount = 1, ErrorCount = 0,
            WarningCount = 0, HealthState = health, PowerShellDisposed = true, RunspaceClosed = true,
            CancelHandleDisposed = true
        };
        var transport = new ComponentStoreStatusTransport(true, true, true, 0, true)
        {
            HostStartAttempted = true, StdoutDrained = true, StderrDrained = true,
            OutputBoundExceeded = false, StderrCharacterCount = 0,
            HostIdentity = new(SystemToolIdentityStage.Completed, SystemToolIdentityReason.Observed, 0)
            { TrustRoute = SystemToolTrustRoute.File }
        };
        return ComponentStoreStatusPolicy.Assess(intent, evidence, transport, Now.AddSeconds(1), Now.AddSeconds(2), false);
    }
    private static SfcVerificationIntent SfcIntent()
    {
        var identity = new SystemToolActionPreview(Guid.NewGuid(),
            SystemToolActionsCatalog.Find(SystemToolOperation.SfcVerifyOnly)!, @"C:\Windows\System32",
            @"C:\Windows\System32\sfc.exe", "10.0.synthetic-test", new string('a', 64),
            SystemToolTrustState.MicrosoftSignatureVerified, "Synthetic test metadata only", Now.AddSeconds(-1),
            Now.AddSeconds(110), false, "Synthetic test, no execution");
        var preview = Assert.Single(SfcVerificationPolicy.CreatePreview(identity, Now).Items);
        var intent = Assert.Single(SfcVerificationPolicy.Confirm(preview, true, true, Now).Items);
        Assert.Equal(SfcVerificationDispatchReservation.Reserved, intent.TryReserveDispatch(Now));
        return intent;
    }
    private static SfcVerificationResult SfcResult(SfcVerificationIntent intent, int code = 0) =>
        SfcVerificationPolicy.Assess(intent, new()
        {
            AdministratorObserved = true, ClientStartAttempted = true, ClientStartObserved = true,
            ProcessId = 9876, ClientStartedUtc = Now.AddSeconds(1), ClientExitObserved = true,
            ClientExitCode = code, ClientExitedUtc = Now.AddSeconds(2), StdoutDrained = true,
            StderrDrained = true, OwnershipReleased = true
        }, Now, Now.AddSeconds(3));

    [Fact]
    public void RecordedFlagsAndFreshScanHaveDistinctSourcesAndScope()
    {
        var recorded = ComponentIntent(ComponentStoreCheckKind.RecordedFlags);
        var scan = ComponentIntent(ComponentStoreCheckKind.ScanHealth);
        var a = DiagnosticIntegrityEvidence.ComponentStore(recorded, ComponentResult(recorded), Now.AddSeconds(5));
        var b = DiagnosticIntegrityEvidence.ComponentStore(scan, ComponentResult(scan), Now.AddSeconds(5));
        Assert.Equal(CheckState.Available, a.State);
        Assert.Equal(CheckState.Available, b.State);
        Assert.Equal(DiagnosticEvidenceArea.ComponentStoreRecordedFlags, a.Area);
        Assert.Equal(DiagnosticEvidenceArea.ComponentStoreScan, b.Area);
        Assert.NotEqual(a.Title, b.Title);
        Assert.Contains("CheckHealth", a.Source);
        Assert.Contains("ScanHealth", b.Source);
        Assert.Contains("not a fresh scan", a.Uncertainty);
        Assert.Contains("prior flag or scan date is unknown", a.Coverage);
        Assert.Contains("not a system-file", b.Uncertainty);
        Assert.Contains("no corruption", b.Observation);
    }

    [Fact]
    public void CrossRequestCrossKindAndForgedFindingsAreUnknown()
    {
        var intent = ComponentIntent(ComponentStoreCheckKind.RecordedFlags);
        var result = ComponentResult(intent);
        foreach (var forged in new[]
        {
            result with { RequestId = Guid.NewGuid() },
            result with { Kind = ComponentStoreCheckKind.ScanHealth },
            result with { Flag = ComponentStoreRecordedFlag.RecordedRepairable },
            result with { ScanFinding = ComponentStoreScanFinding.NoCorruptionReported },
            result with { State = (CheckState)987 },
            result with { Evidence = result.Evidence! with { Kind = ComponentStoreCheckKind.ScanHealth } },
            result with { Evidence = result.Evidence! with { RequestId = Guid.NewGuid() } },
            result with { Evidence = result.Evidence! with { Stage = (ComponentStoreStatusStage)987 } }
        }) Assert.Equal(CheckState.Unknown, DiagnosticIntegrityEvidence.ComponentStore(intent, forged, Now.AddSeconds(5)).State);
    }

    [Theory]
    [InlineData(CheckState.Unknown)]
    [InlineData(CheckState.Cancelled)]
    [InlineData(CheckState.Failed)]
    [InlineData(CheckState.Unavailable)]
    [InlineData(CheckState.Unsupported)]
    public void OriginalNonAvailableStateCannotBecomeCurrentFromHealthyRawOutput(CheckState original)
    {
        var intent = ComponentIntent(ComponentStoreCheckKind.ScanHealth);
        var result = ComponentResult(intent) with { State = original };
        var entry = DiagnosticIntegrityEvidence.ComponentStore(intent, result, Now.AddSeconds(5));
        Assert.NotEqual(CheckState.Available, entry.State);
        Assert.DoesNotContain("reported no corruption", entry.Observation);
    }

    [Fact]
    public void AProperlyAssessedStoppedHealthyResultRetainsNoCurrentFinding()
    {
        var intent = ComponentIntent(ComponentStoreCheckKind.ScanHealth);
        var healthy = ComponentResult(intent);
        var stopped = ComponentStoreStatusPolicy.Assess(intent, healthy.Evidence, healthy.Transport,
            healthy.AttemptedUtc, healthy.CompletedUtc, true);
        var entry = DiagnosticIntegrityEvidence.ComponentStore(intent, stopped, Now.AddSeconds(5));
        Assert.Equal(CheckState.Cancelled, entry.State);
        Assert.Contains("Integrity remains Unknown", entry.Observation);
        Assert.Contains("historical", entry.Coverage);
    }

    [Fact]
    public void MissingTransportQualificationAndInvalidTimesNeverPublishComponentFindings()
    {
        var intent = ComponentIntent(ComponentStoreCheckKind.RecordedFlags);
        var result = ComponentResult(intent);
        foreach (var changed in new[]
        {
            result with { Transport = result.Transport with { StdoutDrained = null } },
            result with { Transport = result.Transport with { OwnershipReleased = false } },
            result with { CompletedUtc = null },
            result with { AttemptedUtc = Now.AddSeconds(3) },
            result with { CompletedUtc = DateTimeOffset.UtcNow.AddDays(1) },
            result with { CompletedUtc = result.CompletedUtc!.Value.ToOffset(TimeSpan.FromHours(1)) }
        }) Assert.Equal(CheckState.Unknown, DiagnosticIntegrityEvidence.ComponentStore(intent, changed, Now.AddSeconds(5)).State);
        Assert.Equal(CheckState.Unknown, DiagnosticIntegrityEvidence.ComponentStore(intent, result, DateTimeOffset.UtcNow.AddDays(1)).State);
        Assert.Equal(CheckState.Unknown, DiagnosticIntegrityEvidence.ComponentStore(intent, result, Now).State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3010)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void EverySfcExitIsOnlyClientEvidenceWithUnknownIntegrity(int code)
    {
        var intent = SfcIntent();
        var entry = DiagnosticIntegrityEvidence.Sfc(intent, SfcResult(intent, code), Now.AddSeconds(5));
        Assert.Equal(CheckState.Available, entry.State);
        Assert.Equal(DiagnosticEvidenceArea.SystemFiles, entry.Area);
        Assert.Contains("integrity remains Unknown", entry.Observation);
        Assert.Contains("not proof of successful verification", entry.Coverage);
        Assert.Contains("CBS.log", entry.NextStep);
        Assert.DoesNotContain("9876", entry.ToString());
    }

    [Fact]
    public void SfcCrossRequestUnsettledStoppedAndMissingTimesCannotBecomeIntegrityFindings()
    {
        var intent = SfcIntent();
        var result = SfcResult(intent);
        foreach (var changed in new[]
        {
            result with { RequestId = Guid.NewGuid() },
            result with { State = CheckState.Unknown },
            result with { Transport = result.Transport with { StdoutDrained = false } },
            result with { Transport = result.Transport with { StderrDrained = null } },
            result with { Transport = result.Transport with { OwnershipReleased = null } },
            result with { Transport = result.Transport with { ClientStartedUtc = null } },
            result with { CompletedUtc = null },
            result with { CompletedUtc = DateTimeOffset.UtcNow.AddDays(1) }
        }) Assert.Equal(CheckState.Unknown, DiagnosticIntegrityEvidence.Sfc(intent, changed, Now.AddSeconds(5)).State);
        var stopped = SfcVerificationPolicy.Assess(intent, result.Transport with { StopObservationRequested = true }, result.AttemptedUtc, result.CompletedUtc);
        var entry = DiagnosticIntegrityEvidence.Sfc(intent, stopped, Now.AddSeconds(5));
        Assert.Equal(CheckState.Cancelled, entry.State);
        Assert.Contains("Integrity remains Unknown", entry.Observation);
    }

    [Fact]
    public void ProviderMessagesAndToolIdentityMetadataNeverReachRows()
    {
        const string secret = "password=synthetic-secret; C:\\private\\filename; token=synthetic";
        var component = ComponentIntent(ComponentStoreCheckKind.RecordedFlags);
        var a = DiagnosticIntegrityEvidence.ComponentStore(component, ComponentResult(component) with { Message = secret }, Now.AddSeconds(5));
        var sfc = SfcIntent();
        var b = DiagnosticIntegrityEvidence.Sfc(sfc, SfcResult(sfc) with { Message = secret }, Now.AddSeconds(5));
        foreach (var entry in new[] { a, b })
        {
            Assert.DoesNotContain(secret, entry.ToString());
            Assert.DoesNotContain("synthetic", entry.ToString());
            Assert.DoesNotContain(new string('a', 64), entry.ToString());
        }
    }

    [Fact]
    public void AllNewLifecycleRowsKeepTheirOwnFixedSources()
    {
        foreach (var area in new[] { DiagnosticEvidenceArea.ComponentStoreRecordedFlags,
                     DiagnosticEvidenceArea.ComponentStoreScan, DiagnosticEvidenceArea.SystemFiles })
        {
            foreach (var entry in new[] { DiagnosticInspectionEvidence.NotChecked(area),
                         DiagnosticInspectionEvidence.Pending(area, Now),
                         DiagnosticInspectionEvidence.Interrupted(area, CheckState.Cancelled, Now, Now.AddSeconds(1)) })
            {
                Assert.DoesNotContain("Defender", entry.Source);
                Assert.NotEqual("Unknown source", entry.Title);
                Assert.DoesNotContain("protection", entry.Uncertainty);
            }
        }
        Assert.Equal(0, (int)DiagnosticEvidenceArea.Storage);
        Assert.Equal(1, (int)DiagnosticEvidenceArea.Defender);
    }
}
