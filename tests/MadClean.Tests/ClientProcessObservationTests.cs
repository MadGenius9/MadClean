using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class ClientProcessObservationTests
{
    private static readonly DateTimeOffset End = DateTimeOffset.UtcNow.AddSeconds(-1);
    private static readonly DateTimeOffset Start = End.AddSeconds(-3);
    private static ClientProcessObservation Exited => new(ClientProcessObservationState.Exited,
        ClientProcessObservationReason.ClientExited, Start, End, 123, Start.AddMinutes(-1), 0, false);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void ActualExitCodesRemainLiteralWithoutHealthOrSuccessMapping(int code)
    {
        var source = Exited with { ExitCode = code };
        var result = ClientProcessObservationProjection.Normalize(source);
        Assert.Equal(source, result); Assert.NotSame(source, result);
        Assert.Equal(ClientProcessObservationState.Exited, result.State);
        Assert.Equal(code, result.ExitCode);
    }

    [Fact]
    public void AnObservedExitWinsTheStopObservationRaceWithoutPretendingProcessCancellation()
    {
        var source = Exited with { StopRequested = true };
        var result = ClientProcessObservationProjection.Normalize(source);
        Assert.Equal(ClientProcessObservationState.Exited, result.State);
        Assert.True(result.StopRequested); Assert.Equal(0, result.ExitCode);
        Assert.Equal(ClientProcessObservationReason.ClientExited, result.Reason);
    }

    [Fact]
    public void ObservationStopDoesNotInventAnExitCodeOrClientStart()
    {
        var source = new ClientProcessObservation(ClientProcessObservationState.ObservationStopped,
            ClientProcessObservationReason.StopRequested, Start, End, 123, null, null, true);
        var result = ClientProcessObservationProjection.Normalize(source);
        Assert.Equal(source, result); Assert.Null(result.ClientStartUtc); Assert.Null(result.ExitCode);
        Assert.Equal(source with { ObservedProcessId = null },
            ClientProcessObservationProjection.Normalize(source with { ObservedProcessId = null }));
    }

    [Fact]
    public void ActualProcessStartMayPredateObservationAndCompletionIsNotActualExitTime()
    {
        var result = ClientProcessObservationProjection.Normalize(Exited);
        Assert.Equal(Start.AddMinutes(-1), result.ClientStartUtc);
        Assert.Equal(End, result.ObservationCompletedUtc);
        Assert.Equal(Exited with { ClientStartUtc = null },
            ClientProcessObservationProjection.Normalize(Exited with { ClientStartUtc = null }));
    }

    [Fact]
    public void MissingInvalidOrContradictoryExitEvidenceCannotBecomeExited()
    {
        ClientProcessObservation[] invalid =
        [ Exited with { ObservedProcessId = null }, Exited with { ObservedProcessId = 0 },
          Exited with { ObservedProcessId = -1 }, Exited with { ExitCode = null },
          Exited with { Reason = ClientProcessObservationReason.StopRequested },
          Exited with { State = ClientProcessObservationState.Unavailable, Reason = ClientProcessObservationReason.ProcessUnavailable },
          Exited with { State = ClientProcessObservationState.ObservationStopped, Reason = ClientProcessObservationReason.StopRequested, StopRequested = true },
          Exited with { State = (ClientProcessObservationState)999 }, Exited with { Reason = (ClientProcessObservationReason)999 } ];
        foreach (var source in invalid) AssertInvalid(ClientProcessObservationProjection.Normalize(source));
    }

    [Fact]
    public void StopStateRequiresAnActualStopRequestAndNeverInfersExit()
    {
        var source = new ClientProcessObservation(ClientProcessObservationState.ObservationStopped,
            ClientProcessObservationReason.StopRequested, Start, End, 123, null, null, false);
        AssertInvalid(ClientProcessObservationProjection.Normalize(source));
    }

    [Fact]
    public void UnavailableObservationMayRetainKnownMetadataWithoutCreatingExitEvidence()
    {
        foreach (var reason in new[] { ClientProcessObservationReason.ProcessUnavailable, ClientProcessObservationReason.ExitNotObserved })
        {
            var source = Exited with { State = ClientProcessObservationState.Unavailable, Reason = reason, ExitCode = null };
            Assert.Equal(source, ClientProcessObservationProjection.Normalize(source));
        }
    }

    [Fact]
    public void InvalidMandatoryUtcIntervalsAreNotPublishedAsObservedDates()
    {
        ClientProcessObservation[] invalid =
        [ Exited with { ObservationStartedUtc = null }, Exited with { ObservationCompletedUtc = null },
          Exited with { ObservationStartedUtc = default(DateTimeOffset) },
          Exited with { ObservationStartedUtc = End, ObservationCompletedUtc = Start },
          Exited with { ObservationCompletedUtc = DateTimeOffset.UtcNow.AddDays(1) },
          Exited with { ObservationStartedUtc = Start.ToOffset(TimeSpan.FromHours(1)) } ];
        foreach (var source in invalid) AssertInvalid(ClientProcessObservationProjection.Normalize(source));
    }

    [Fact]
    public void InvalidOptionalStartDoesNotDiscardActualObservedPidAndExitCode()
    {
        DateTimeOffset[] invalidStarts =
        [ End.AddTicks(1), default, Start.ToOffset(TimeSpan.FromHours(1)), DateTimeOffset.UtcNow.AddDays(1) ];
        foreach (var actualStart in invalidStarts)
        {
            var result = ClientProcessObservationProjection.Normalize(Exited with { ClientStartUtc = actualStart, ExitCode = -123, StopRequested = true });
            Assert.Equal(ClientProcessObservationState.Exited, result.State);
            Assert.Equal(ClientProcessObservationReason.ClientExited, result.Reason);
            Assert.Equal(123, result.ObservedProcessId); Assert.Equal(-123, result.ExitCode);
            Assert.Equal(Start, result.ObservationStartedUtc); Assert.Equal(End, result.ObservationCompletedUtc);
            Assert.True(result.StopRequested); Assert.Null(result.ClientStartUtc);
        }
    }

    [Fact]
    public void InvalidObservationsRetainOnlyTheStopRequestFact()
    {
        var result = ClientProcessObservationProjection.Normalize(Exited with { ObservedProcessId = -1, StopRequested = true });
        AssertInvalid(result); Assert.True(result.StopRequested);
    }

    private static void AssertInvalid(ClientProcessObservation value)
    {
        Assert.Equal(ClientProcessObservationState.Unavailable, value.State);
        Assert.Equal(ClientProcessObservationReason.InvalidObservation, value.Reason);
        Assert.Null(value.ObservationStartedUtc); Assert.Null(value.ObservationCompletedUtc);
        Assert.Null(value.ObservedProcessId); Assert.Null(value.ClientStartUtc); Assert.Null(value.ExitCode);
    }
}
