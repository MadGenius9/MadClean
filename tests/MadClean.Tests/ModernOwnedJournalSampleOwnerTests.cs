using MadClean.Windows;
using Xunit;

namespace MadClean.Tests;

public sealed class ModernOwnedJournalSampleOwnerTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void UncertainAcquisitionRetainsActualPrefixWithoutCleanupOrSuccessor(int uncertainAt)
    {
        var frame = new Frame { Acquiring = index => index == uncertainAt ? ModernJournalAcquisition.Uncertain : ModernJournalAcquisition.Acquired };
        var domain = new ModernOwnedJournalSampleDomain();
        var result = domain.Run(frame);
        Assert.Equal(ModernJournalSampleState.Retained, result.State);
        Assert.Equal(uncertainAt + 1, frame.AcquisitionCalls);
        Assert.Equal(0, frame.ReleaseCalls); Assert.Equal(0, frame.ObservationCalls);
        Assert.False(result.ResourcesReleased); Assert.False(result.SampleVerified);
        Assert.False(domain.HasPendingWork); Assert.True(domain.RequiresRetention);
        Assert.False(result.Release());
        var blocked = new Frame(); var next = domain.Run(blocked);
        Assert.Equal(ModernJournalSampleState.Refused, next.State);
        Assert.False(next.Release()); Assert.Equal(0, blocked.PrepareCalls);
        Assert.Equal(0, blocked.ReleaseCalls);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void KnownNoAcquisitionClosesPrefixOnceWithoutLaterStagesOrSample(int refusedAt)
    {
        var frame = new Frame { Acquiring = index => index == refusedAt ? ModernJournalAcquisition.NotAcquired : ModernJournalAcquisition.Acquired };
        var domain = new ModernOwnedJournalSampleDomain(); var result = domain.Run(frame);
        Assert.Equal(refusedAt + 1, frame.AcquisitionCalls); Assert.Equal(0, frame.ObservationCalls);
        Assert.True(result.ResourcesReleased); Assert.False(result.SampleVerified);
        Assert.True(result.Release()); Assert.Equal(1, frame.ReleaseCalls);
        Assert.False(domain.RequiresRetention);
        var successor = domain.Run(new Frame()); Assert.True(successor.SampleVerified);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void ThrownOrUndefinedAcquisitionCannotBecomeKnownRefusal(int index)
    {
        foreach (bool throws in new[] { false, true })
        {
            var frame = new Frame { Acquiring = stage => stage != index ? ModernJournalAcquisition.Acquired :
                throws ? throw new IOException("Controlled uncertain call") : (ModernJournalAcquisition)987 };
            var domain = new ModernOwnedJournalSampleDomain(); var result = domain.Run(frame);
            Assert.True(result.RequiresRetention); Assert.True(domain.RequiresRetention);
            Assert.False(result.Release()); Assert.Equal(0, frame.ReleaseCalls);
            Assert.Equal(index + 1, frame.AcquisitionCalls);
        }
    }

    [Fact]
    public void PrecancelledClaimPerformsNoCallbackAndDoesNotBlockFreshFrame()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel();
        var frame = new Frame(); var domain = new ModernOwnedJournalSampleDomain();
        var cancelled = domain.Run(frame, stop.Token);
        Assert.Equal(ModernJournalSampleState.Refused, cancelled.State);
        Assert.Equal(0, frame.PrepareCalls); Assert.Equal(0, frame.ReleaseCalls);
        Assert.False(domain.HasPendingWork); Assert.False(domain.RequiresRetention);
        Assert.True(domain.Run(new Frame()).SampleVerified);
    }

    [Theory]
    [InlineData(-1)] [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void StopBetweenKnownStagesReleasesWithoutEnteringNextOperation(int stopAt)
    {
        using var stop = new CancellationTokenSource();
        var frame = new Frame
        {
            Preparing = () => { if (stopAt == -1) stop.Cancel(); return true; },
            Acquiring = index => { if (index == stopAt) stop.Cancel(); return ModernJournalAcquisition.Acquired; }
        };
        var domain = new ModernOwnedJournalSampleDomain(); var result = domain.Run(frame, stop.Token);
        Assert.Equal(stopAt + 1, frame.AcquisitionCalls);
        Assert.Equal(0, frame.ObservationCalls); Assert.Equal(1, frame.ReleaseCalls);
        Assert.True(result.ResourcesReleased); Assert.False(result.SampleVerified);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FailedOrThrowingReleaseIsStickyEvenWhenSampleWasVerified(bool throws)
    {
        var frame = new Frame { Releasing = () => throws ? throw new IOException("Controlled close uncertainty") : false };
        var domain = new ModernOwnedJournalSampleDomain(); var result = domain.Run(frame);
        Assert.True(result.SampleVerified); Assert.False(result.ResourcesReleased);
        Assert.True(result.RequiresRetention); Assert.True(domain.RequiresRetention);
        Assert.False(result.Release()); Assert.Equal(1, frame.ReleaseCalls);
        Assert.Equal(ModernJournalSampleState.Refused, domain.Run(new Frame()).State);
    }

    [Fact]
    public void ThrowingPreparationRetainsWithoutInventingPrefixCleanup()
    {
        var frame = new Frame { Preparing = () => throw new IOException("Controlled prefix uncertainty") };
        var domain = new ModernOwnedJournalSampleDomain(); var result = domain.Run(frame);
        Assert.True(result.RequiresRetention); Assert.Equal(0, frame.ReleaseCalls);
        Assert.Equal(0, frame.AcquisitionCalls);
    }

    [Fact]
    public void KnownPreparationRefusalReleasesPrefixWithoutAcquiring()
    {
        var frame = new Frame { Preparing = () => false };
        var result = new ModernOwnedJournalSampleDomain().Run(frame);
        Assert.True(result.ResourcesReleased); Assert.Equal(1, frame.ReleaseCalls);
        Assert.Equal(0, frame.AcquisitionCalls); Assert.False(result.SampleVerified);
    }

    [Fact]
    public void ForeignAndReplayedAttemptsCannotCallOrReleaseTheFrame()
    {
        var frame = new Frame(); var domain = new ModernOwnedJournalSampleDomain();
        var foreign = new ModernOwnedJournalSampleAttempt(domain, frame);
        foreign.Run(default); Assert.False(foreign.Release()); Assert.Equal(0, frame.PrepareCalls);
        var accepted = domain.Run(frame);
        accepted.Run(default); Assert.True(accepted.Release());
        Assert.Equal(1, frame.PrepareCalls); Assert.Equal(1, frame.ObservationCalls); Assert.Equal(1, frame.ReleaseCalls);
        var replay = new ModernOwnedJournalSampleDomain().Run(frame);
        replay.Run(default); Assert.False(replay.Release()); Assert.Equal(1, frame.ReleaseCalls);
    }

    [Fact]
    public void StageAuthorityBelongsToActualCallbackThreadAndForeignCompletionCannotDropOwner()
    {
        var domain = new ModernOwnedJournalSampleDomain();
        Frame? frame = null;
        var foreign = new ModernOwnedJournalSampleAttempt(domain, new Frame());
        frame = new Frame
        {
            Preparing = () =>
            {
                Assert.True(domain.IsActive(frame!, ModernJournalSamplePhase.Prepare));
                Assert.False(domain.IsActive(frame!, ModernJournalSamplePhase.Journal));
                domain.Complete(foreign);
                Assert.True(domain.Owns(frame!));
                bool otherThreadAllowed = true;
                Exception? observerFailure = null;
                var observer = new Thread(() =>
                {
                    try { otherThreadAllowed = domain.IsActive(frame!, ModernJournalSamplePhase.Prepare); }
                    catch (Exception exception) { observerFailure = exception; }
                }) { IsBackground = true };
                observer.Start();
                Assert.True(observer.Join(TimeSpan.FromSeconds(5)));
                Assert.Null(observerFailure);
                Assert.False(otherThreadAllowed);
                return true;
            },
            Acquiring = index =>
            {
                Assert.True(domain.IsActive(frame!, (ModernJournalSamplePhase)(index + 2)));
                return ModernJournalAcquisition.Acquired;
            },
            Observing = () => { Assert.True(domain.IsActive(frame!, ModernJournalSamplePhase.Exercise)); return true; },
            Releasing = () => { Assert.True(domain.IsActive(frame!, ModernJournalSamplePhase.Release)); return true; }
        };
        var result = domain.Run(frame);
        Assert.True(result.ResourcesReleased);
        Assert.False(domain.IsActive(frame, ModernJournalSamplePhase.Release));
        Assert.False(domain.Owns(frame));
    }

    [Fact]
    public void FalseSampleObservationRemainsSeparateFromKnownResourceRelease()
    {
        var frame = new Frame { Observing = () => false };
        var result = new ModernOwnedJournalSampleDomain().Run(frame);
        Assert.False(result.SampleVerified); Assert.True(result.ResourcesReleased);
        Assert.Equal(1, frame.ReleaseCalls);
    }

    [Fact]
    public void ActualSampleCallbackCanObservePendingAndRefuseCompetingWorkWithoutLockDeadlock()
    {
        var domain = new ModernOwnedJournalSampleDomain(); var competing = new Frame();
        var frame = new Frame
        {
            Observing = () =>
            {
                Exception? observerFailure = null;
                var observer = new Thread(() =>
                {
                    try
                    {
                        Assert.True(domain.HasPendingWork);
                        var rejected = domain.Run(competing);
                        Assert.Equal(ModernJournalSampleState.Refused, rejected.State);
                        Assert.False(rejected.Release());
                    }
                    catch (Exception exception) { observerFailure = exception; }
                }) { IsBackground = true };
                observer.Start();
                Assert.True(observer.Join(TimeSpan.FromSeconds(5)));
                Assert.Null(observerFailure);
                return true;
            }
        };
        var result = domain.Run(frame);
        Assert.True(result.SampleVerified); Assert.True(result.ResourcesReleased);
        Assert.Equal(0, competing.PrepareCalls); Assert.Equal(0, competing.ReleaseCalls);
    }

    [Fact]
    public void ThrowingCodecCallbackCannotFreeOrRewriteAnUncertainResult()
    {
        var frame = new Frame { Observing = () => throw new IOException("Controlled write/read failure") };
        var result = new ModernOwnedJournalSampleDomain().Run(frame);
        Assert.True(result.RequiresRetention); Assert.False(result.SampleVerified);
        Assert.Equal(0, frame.ReleaseCalls); Assert.False(result.Release());
    }

    [Fact]
    public async Task CallerStopDuringActualAcquisitionKeepsOwnerUntilCallReturnsAndStopsNextStage()
    {
        using var stop = new CancellationTokenSource();
        using var entered = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        var domain = new ModernOwnedJournalSampleDomain();
        var frame = new Frame
        {
            Acquiring = _ =>
            {
                entered.Set();
                if (!finish.Wait(TimeSpan.FromSeconds(5))) throw new IOException("Controlled wait exceeded its bound");
                return ModernJournalAcquisition.Acquired;
            }
        };
        var actual = Task.Run(() => domain.Run(frame, stop.Token));
        ModernOwnedJournalSampleAttempt? result = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            stop.Cancel();
            Assert.True(domain.HasPendingWork); Assert.False(actual.IsCompleted);
            Assert.Equal(0, frame.ReleaseCalls); Assert.Equal(0, frame.ObservationCalls);
            var competitor = new Frame(); var refused = domain.Run(competitor);
            Assert.Equal(ModernJournalSampleState.Refused, refused.State);
            Assert.False(refused.Release()); Assert.Equal(0, competitor.PrepareCalls);
        }
        finally
        {
            finish.Set();
            result = await actual;
        }
        Assert.True(result.ResourcesReleased); Assert.False(result.SampleVerified);
        Assert.Equal(1, frame.AcquisitionCalls); Assert.Equal(1, frame.ReleaseCalls);
        Assert.Equal(0, frame.ObservationCalls); Assert.False(domain.HasPendingWork);
        Assert.False(domain.RequiresRetention);
    }

    private sealed class Frame : ModernOwnedJournalSampleFrame
    {
        internal Func<bool> Preparing = () => true, Observing = () => true, Releasing = () => true;
        internal Func<int, ModernJournalAcquisition> Acquiring = _ => ModernJournalAcquisition.Acquired;
        internal int PrepareCalls, AcquisitionCalls, ObservationCalls, ReleaseCalls;
        internal override bool Prepare(CancellationToken token) { PrepareCalls++; return Preparing(); }
        private ModernJournalAcquisition Acquire() { int index = AcquisitionCalls++; return Acquiring(index); }
        internal override ModernJournalAcquisition AcquireDirectory() => Acquire();
        internal override ModernJournalAcquisition AcquireLease() => Acquire();
        internal override ModernJournalAcquisition AcquireJournal() => Acquire();
        internal override bool ObserveSyntheticIntent() { ObservationCalls++; return Observing(); }
        internal override bool ReleaseKnownOwnership() { ReleaseCalls++; return Releasing(); }
    }
}
