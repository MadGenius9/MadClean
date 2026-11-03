using System.Text.Json;
using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class ComponentStoreStatusTests
{
    private static ComponentStoreStatusIntent Intent(bool reserve = true)
    {
        var now = DateTimeOffset.UtcNow.AddSeconds(-2);
        var preview = Assert.Single(ComponentStoreStatusPolicy.CreatePreview(now).Items);
        var intent = Assert.Single(ComponentStoreStatusPolicy.Confirm(preview, true, now).Items);
        if (reserve) Assert.Equal(ComponentStoreStatusDispatchReservation.Reserved, intent.TryReserveDispatch(now.AddMilliseconds(1)));
        return intent;
    }

    private static ComponentStoreStatusEvidence Completed(Guid id, string member = "Healthy") => new()
    {
        RequestId = id, Stage = ComponentStoreStatusStage.Completed, Outcome = ComponentStoreStatusOutcome.Completed,
        HostQualified = true, ModuleBound = true, CmdletQualified = true, OutputContractQualified = true,
        InvocationStarted = true, PipelineState = ComponentStorePipelineState.Completed, EndInvokeReturned = true,
        CancelEventObserved = false, BeginStopCalled = false, EndStopReturned = null,
        OutputCount = 1, ErrorCount = 0, WarningCount = 0, HealthState = member, PowerShellDisposed = true,
        RunspaceClosed = true, CancelHandleDisposed = true
    };

    private static ComponentStoreStatusTransport Exited() => new(true, true, true, 0, true)
    {
        HostStartAttempted = true, StdoutDrained = true, StderrDrained = true,
        OutputBoundExceeded = false, StderrCharacterCount = 0,
        HostIdentity = new(SystemToolIdentityStage.Completed, SystemToolIdentityReason.Observed, 0) { TrustRoute = SystemToolTrustRoute.File }
    };

    private static ComponentStoreStatusResult Assess(ComponentStoreStatusIntent intent, ComponentStoreStatusEvidence? evidence,
        ComponentStoreStatusTransport? transport = null, bool stopped = false) =>
        ComponentStoreStatusPolicy.Assess(intent, evidence, transport ?? Exited(), intent.PreparedUtc.AddMilliseconds(1),
            intent.PreparedUtc.AddMilliseconds(20), stopped);

    private static Dictionary<string, object?> Envelope(ComponentStoreStatusEvidence e) => new()
    {
        ["version"] = 1, ["requestId"] = e.RequestId.ToString("N"), ["stage"] = e.Stage.ToString(), ["outcome"] = e.Outcome.ToString(),
        ["hostQualified"] = e.HostQualified, ["moduleBound"] = e.ModuleBound, ["cmdletQualified"] = e.CmdletQualified,
        ["outputContractQualified"] = e.OutputContractQualified, ["invocationStarted"] = e.InvocationStarted,
        ["pipelineState"] = e.PipelineState?.ToString(), ["endInvokeReturned"] = e.EndInvokeReturned,
        ["cancelEventObserved"] = e.CancelEventObserved, ["beginStopCalled"] = e.BeginStopCalled,
        ["endStopReturned"] = e.EndStopReturned, ["outputCount"] = e.OutputCount, ["errorCount"] = e.ErrorCount, ["warningCount"] = e.WarningCount,
        ["healthState"] = e.HealthState, ["powerShellDisposed"] = e.PowerShellDisposed,
        ["runspaceClosed"] = e.RunspaceClosed, ["cancelHandleDisposed"] = e.CancelHandleDisposed,
        ["managedExceptionHResult"] = e.ManagedExceptionHResult
    };

    [Theory]
    [InlineData("Healthy", ComponentStoreRecordedFlag.NoRecordedCorruptionFlag)]
    [InlineData("Repairable", ComponentStoreRecordedFlag.RecordedRepairable)]
    [InlineData("NonRepairable", ComponentStoreRecordedFlag.RecordedNonRepairable)]
    public void OnlyActualQualifiedMemberNamesProduceScopedRecordedFlags(string member, ComponentStoreRecordedFlag expected)
    {
        var intent = Intent();
        var parsed = ComponentStoreStatusParser.Parse(JsonSerializer.Serialize(Envelope(Completed(intent.RequestId, member))), intent.RequestId);
        Assert.True(parsed.Accepted);
        var result = Assess(intent, parsed.Evidence);
        Assert.Equal(CheckState.Available, result.State);
        Assert.Equal(expected, result.Flag);
        Assert.Contains("No fresh scan or repair", result.Message);
        Assert.Contains("prior flag/scan date is unknown", result.Message);
    }

    [Fact]
    public void ZeroExitWithoutQualifiedSourceCannotBecomeHealthyOrAvailable()
    {
        var intent = Intent();
        var result = Assess(intent, null);
        Assert.Equal(CheckState.Unknown, result.State);
        Assert.Equal(ComponentStoreRecordedFlag.Unknown, result.Flag);
        Assert.Equal(0, result.Transport.HostExitCode);
    }

    [Fact]
    public void MissingHostIdentityProofWithholdsCurrentFlagButRetainsActualPipelineAndEnum()
    {
        var intent = Intent();
        var result = Assess(intent, Completed(intent.RequestId), Exited() with { HostIdentity = null });
        Assert.Equal(ComponentStoreRecordedFlag.Unknown, result.Flag);
        Assert.Equal("Healthy", result.Evidence!.HealthState);
        Assert.Equal(ComponentStorePipelineState.Completed, result.Evidence.PipelineState);
        Assert.Equal(0, result.Transport.HostExitCode);
    }

    [Fact]
    public void StopRetainsCompletedSourceAndExitFactsWithoutPublishingLateCurrentFlag()
    {
        var intent = Intent();
        var actual = Completed(intent.RequestId);
        var result = Assess(intent, actual, stopped: true);
        Assert.Equal(CheckState.Cancelled, result.State);
        Assert.Equal(ComponentStoreRecordedFlag.Unknown, result.Flag);
        Assert.Equal("Healthy", result.Evidence!.HealthState);
        Assert.Equal(ComponentStorePipelineState.Completed, result.Evidence.PipelineState);
        Assert.Equal(0, result.Transport.HostExitCode);
    }

    [Fact]
    public void ACooperativeStopRequestMayFinishNormallyWithoutProvingServicingCancellation()
    {
        var intent = Intent();
        var actual = Completed(intent.RequestId) with { CancelEventObserved = true, BeginStopCalled = true, EndStopReturned = true };
        Assert.True(ComponentStoreStatusParser.IsConsistent(actual, intent.RequestId));
        var result = Assess(intent, actual);
        Assert.Equal(CheckState.Cancelled, result.State);
        Assert.Equal(ComponentStoreRecordedFlag.Unknown, result.Flag);
        Assert.True(result.Evidence!.EndInvokeReturned);
        Assert.Contains("cancellation or rollback is not established", result.Message);
    }

    [Fact]
    public void CleanupAndDrainUncertaintyDoNotEraseActualPipelineOrHostExit()
    {
        var intent = Intent();
        var actual = Completed(intent.RequestId) with { Outcome = ComponentStoreStatusOutcome.CleanupUncertain, RunspaceClosed = false };
        var result = Assess(intent, actual, Exited() with { OwnershipReleased = false });
        Assert.Equal(ComponentStoreRecordedFlag.Unknown, result.Flag);
        Assert.Equal(ComponentStorePipelineState.Completed, result.Evidence!.PipelineState);
        Assert.Equal(0, result.Transport.HostExitCode);
        foreach (var transport in new[]
        {
            Exited() with { StdoutDrained = null }, Exited() with { StderrCharacterCount = 1 },
            Exited() with { OutputBoundExceeded = true }, Exited() with { HostExitCode = null }
        })
            Assert.Equal(ComponentStoreRecordedFlag.Unknown, Assess(intent, Completed(intent.RequestId), transport).Flag);
    }

    [Fact]
    public void ManagedExceptionMetadataIsNotANativeHresultOrAnImageAssessment()
    {
        var intent = Intent();
        var actual = Completed(intent.RequestId) with { ManagedExceptionHResult = unchecked((int)0x80131501) };
        var result = Assess(intent, actual);
        Assert.Equal(ComponentStoreRecordedFlag.Unknown, result.Flag);
        Assert.Equal(unchecked((int)0x80131501), result.Evidence!.ManagedExceptionHResult);
    }

    [Fact]
    public void NonzeroOrUnobservedWarningsWithholdCurrentFlagWithoutErasingActualOutput()
    {
        var intent = Intent();
        foreach (int? warnings in new int?[] { null, 1 })
        {
            var evidence = Completed(intent.RequestId) with { WarningCount = warnings };
            var parsed = ComponentStoreStatusParser.Parse(JsonSerializer.Serialize(Envelope(evidence)), intent.RequestId);
            Assert.True(parsed.Accepted);
            var result = Assess(intent, parsed.Evidence);
            Assert.Equal(ComponentStoreRecordedFlag.Unknown, result.Flag);
            Assert.Equal("Healthy", result.Evidence!.HealthState);
            Assert.Equal(ComponentStorePipelineState.Completed, result.Evidence.PipelineState);
            Assert.Equal(warnings, result.Evidence.WarningCount);
        }
    }

    [Fact]
    public void UnelevatedCallerHasNoFabricatedHostObservationsOrAutomaticElevation()
    {
        var intent = Intent(reserve: false);
        var result = Assess(intent, null, new(false, null, null, null, true) { HostStartAttempted = false });
        Assert.Equal(CheckState.Unavailable, result.State);
        Assert.Equal(ComponentStoreRecordedFlag.Unknown, result.Flag);
        Assert.Null(result.Transport.HostStartObserved);
        Assert.Null(result.Transport.HostExitCode);
        Assert.Contains("did not request elevation", result.Message);
    }

    [Fact]
    public void NonceMismatchAndUnreservedIntentCannotAcceptAFlag()
    {
        var intent = Intent();
        var mismatched = Assess(intent, Completed(Guid.NewGuid()));
        Assert.Equal(ComponentStoreRecordedFlag.Unknown, mismatched.Flag);
        Assert.Null(mismatched.Evidence);
        Assert.Equal(0, mismatched.Transport.HostExitCode);
        var unreserved = Intent(reserve: false);
        Assert.Equal(ComponentStoreRecordedFlag.Unknown, Assess(unreserved, Completed(unreserved.RequestId)).Flag);
    }

    [Fact]
    public void ContradictorySignalMetadataCannotInventAStopButActualCallerRequestSurvives()
    {
        var intent = Intent();
        var source = Completed(intent.RequestId);
        var contradictory = Exited() with { StopSignalSucceeded = true, StopSignalNativeError = 5 };
        var unknown = Assess(intent, source, contradictory);
        Assert.Equal(CheckState.Unknown, unknown.State);
        Assert.Equal(ComponentStoreRecordedFlag.Unknown, unknown.Flag);
        Assert.Same(source, unknown.Evidence);
        var stopped = Assess(intent, source, contradictory, stopped: true);
        Assert.Equal(CheckState.Cancelled, stopped.State);
        Assert.Equal(ComponentStoreRecordedFlag.Unknown, stopped.Flag);
        Assert.Same(source, stopped.Evidence);
    }

    [Fact]
    public void MissingDuplicateExtraAndNestedPayloadCannotDefaultIntoPositiveEvidence()
    {
        Guid id = Guid.NewGuid();
        var valid = Envelope(Completed(id));
        var missing = new Dictionary<string, object?>(valid); missing.Remove("errorCount");
        var extra = new Dictionary<string, object?>(valid) { ["rawOutput"] = "must not be accepted" };
        Assert.False(ComponentStoreStatusParser.Parse(JsonSerializer.Serialize(missing), id).Accepted);
        Assert.False(ComponentStoreStatusParser.Parse(JsonSerializer.Serialize(extra), id).Accepted);
        string duplicate = JsonSerializer.Serialize(valid).Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal);
        Assert.False(ComponentStoreStatusParser.Parse(duplicate, id).Accepted);
        var nested = new Dictionary<string, object?>(valid) { ["healthState"] = new { value = "Healthy" } };
        Assert.False(ComponentStoreStatusParser.Parse(JsonSerializer.Serialize(nested), id).Accepted);
    }

    [Theory]
    [InlineData("healthState", 0)]
    [InlineData("healthState", "0")]
    [InlineData("healthState", "healthy")]
    [InlineData("hostQualified", 1)]
    [InlineData("endInvokeReturned", "true")]
    [InlineData("stage", "10")]
    [InlineData("pipelineState", "completed")]
    [InlineData("outputCount", -1)]
    [InlineData("errorCount", 129)]
    [InlineData("warningCount", 129)]
    [InlineData("warningCount", "0")]
    public void PrimitiveTypesAndCanonicalMembersCannotBeCoerced(string field, object value)
    {
        Guid id = Guid.NewGuid();
        var payload = Envelope(Completed(id)); payload[field] = value;
        var result = ComponentStoreStatusParser.Parse(JsonSerializer.Serialize(payload), id);
        Assert.False(result.Accepted);
        Assert.Null(result.Evidence);
    }

    [Fact]
    public void QualificationFailuresAndStoppedPipelinesCannotMasqueradeAsCompletedOutput()
    {
        Guid id = Guid.NewGuid();
        foreach (var evidence in new[]
        {
            Completed(id) with { CmdletQualified = false }, Completed(id) with { ErrorCount = 1 },
            Completed(id) with { EndInvokeReturned = null }, Completed(id) with { PipelineState = ComponentStorePipelineState.Stopped },
            Completed(id) with { EndStopReturned = true }, Completed(id) with { HealthState = null }
        })
            Assert.False(ComponentStoreStatusParser.Parse(JsonSerializer.Serialize(Envelope(evidence)), id).Accepted);
    }

    [Fact]
    public void ReviewConfirmationAndDispatchAreDistinctOneShotSteps()
    {
        var now = DateTimeOffset.UtcNow;
        var preview = Assert.Single(ComponentStoreStatusPolicy.CreatePreview(now).Items);
        Assert.Empty(ComponentStoreStatusPolicy.Confirm(preview, false, now).Items);
        var intent = Assert.Single(ComponentStoreStatusPolicy.Confirm(preview, true, now).Items);
        Assert.Empty(ComponentStoreStatusPolicy.Confirm(preview, true, now).Items);
        var outcomes = new ComponentStoreStatusDispatchReservation[16];
        Parallel.For(0, outcomes.Length, i => outcomes[i] = intent.TryReserveDispatch(now));
        Assert.Equal(1, outcomes.Count(value => value == ComponentStoreStatusDispatchReservation.Reserved));
        intent.Invalidate();
        Assert.True(intent.IsDispatchReserved);
        Assert.False(intent.IsInvalidated);
    }

    [Fact]
    public void ExpiryAndPrelaunchCancellationDoNotCreateAHostRequest()
    {
        var now = DateTimeOffset.UtcNow;
        var expired = Assert.Single(ComponentStoreStatusPolicy.CreatePreview(now).Items);
        Assert.Empty(ComponentStoreStatusPolicy.Confirm(expired, true, expired.ExpiresUtc).Items);
        var intent = Intent(reserve: false);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.Equal(ComponentStoreStatusDispatchReservation.Cancelled, intent.TryReserveDispatch(intent.PreparedUtc, stop.Token));
        Assert.True(intent.IsInvalidated);
        Assert.False(intent.IsDispatchReserved);
    }

    [Fact]
    public void ALongObservedOperationDoesNotLoseItsResultWhenTheLaunchPreviewExpires()
    {
        var intent = Intent();
        var result = ComponentStoreStatusPolicy.Assess(intent, Completed(intent.RequestId), Exited(),
            intent.PreparedUtc, intent.ExpiresUtc.AddMinutes(1), false);
        Assert.Equal(CheckState.Available, result.State);
        Assert.Equal(ComponentStoreRecordedFlag.NoRecordedCorruptionFlag, result.Flag);
    }

    [Fact]
    public void ParserBoundsAndCancellationDoNotReturnPartialEvidence()
    {
        Guid id = Guid.NewGuid();
        Assert.False(ComponentStoreStatusParser.Parse(new string(' ', ComponentStoreStatusParser.MaximumCharacters + 1), id).Accepted);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => ComponentStoreStatusParser.Parse(JsonSerializer.Serialize(Envelope(Completed(id))), id, stop.Token));
    }
}
