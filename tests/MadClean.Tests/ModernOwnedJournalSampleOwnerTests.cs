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

    [Fact]
    public void PhaseFactsDistinguishEarlyRefusalFromAnObservedSampleRefusal()
    {
        var early = new ModernOwnedJournalSampleDomain().Run(new Frame { Preparing = () => false }).Observation;
        Assert.Equal(ModernJournalCallbackOutcome.Refused, early.Prepare);
        Assert.Equal(ModernJournalCallbackOutcome.NotEntered, early.Directory);
        Assert.Equal(ModernJournalCallbackOutcome.NotEntered, early.Exercise);
        Assert.Equal(ModernJournalCallbackOutcome.Confirmed, early.Release);
        var later = new ModernOwnedJournalSampleDomain().Run(new Frame { Observing = () => false }).Observation;
        Assert.Equal(ModernJournalCallbackOutcome.Confirmed, later.Prepare);
        Assert.Equal(ModernJournalCallbackOutcome.Confirmed, later.Directory);
        Assert.Equal(ModernJournalCallbackOutcome.Confirmed, later.Lease);
        Assert.Equal(ModernJournalCallbackOutcome.Confirmed, later.Journal);
        Assert.Equal(ModernJournalCallbackOutcome.Refused, later.Exercise);
        Assert.Equal(ModernJournalCallbackOutcome.Confirmed, later.Release);
        Assert.Equal(ModernJournalNamesObservation.NotObserved, later.Names);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void AcquisitionFactsDistinguishKnownRefusalUncertainAndThrownResults(int index)
    {
        foreach (int kind in new[] { 0, 1, 2, 3 })
        {
            var frame = new Frame { Acquiring = at => at != index ? ModernJournalAcquisition.Acquired : kind switch
            {
                0 => ModernJournalAcquisition.NotAcquired,
                1 => ModernJournalAcquisition.Uncertain,
                2 => (ModernJournalAcquisition)987,
                _ => throw new IOException("Controlled acquisition uncertainty")
            } };
            var result = new ModernOwnedJournalSampleDomain().Run(frame);
            var facts = result.Observation;
            var acquisitions = new[] { facts.Directory, facts.Lease, facts.Journal };
            for (int prior = 0; prior < index; prior++) Assert.Equal(ModernJournalCallbackOutcome.Confirmed, acquisitions[prior]);
            Assert.Equal(kind == 0 ? ModernJournalCallbackOutcome.Refused : ModernJournalCallbackOutcome.Uncertain, acquisitions[index]);
            for (int later = index + 1; later < 3; later++) Assert.Equal(ModernJournalCallbackOutcome.NotEntered, acquisitions[later]);
            Assert.Equal(ModernJournalCallbackOutcome.NotEntered, facts.Exercise);
            Assert.Equal(kind == 0 ? ModernJournalCallbackOutcome.Confirmed : ModernJournalCallbackOutcome.NotEntered, facts.Release);
        }
    }

    [Fact]
    public void ThrowingPreparationAndExerciseAreUnknownWithoutClaimingLaterCleanup()
    {
        var preparing = new ModernOwnedJournalSampleDomain().Run(new Frame { Preparing = () => throw new IOException("Controlled") }).Observation;
        Assert.Equal(ModernJournalCallbackOutcome.Uncertain, preparing.Prepare);
        Assert.Equal(ModernJournalCallbackOutcome.NotEntered, preparing.Directory);
        Assert.Equal(ModernJournalCallbackOutcome.NotEntered, preparing.Release);
        var exercising = new ModernOwnedJournalSampleDomain().Run(new Frame { Observing = () => throw new IOException("Controlled") }).Observation;
        Assert.Equal(ModernJournalCallbackOutcome.Confirmed, exercising.Journal);
        Assert.Equal(ModernJournalCallbackOutcome.Uncertain, exercising.Exercise);
        Assert.Equal(ModernJournalCallbackOutcome.NotEntered, exercising.Release);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void VerifiedSampleNeverConvertsUncertainReleaseIntoConfirmedCleanup(bool throws)
    {
        var result = new ModernOwnedJournalSampleDomain().Run(new Frame { Releasing = () => throws ? throw new IOException("Controlled") : false });
        Assert.True(result.SampleVerified);
        Assert.Equal(ModernJournalCallbackOutcome.Confirmed, result.Observation.Exercise);
        Assert.Equal(ModernJournalCallbackOutcome.Uncertain, result.Observation.Release);
        Assert.True(result.RequiresRetention);
    }

    [Fact]
    public void PrecancelledFactsDoNotInventAReleaseCallback()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel();
        var result = new ModernOwnedJournalSampleDomain().Run(new Frame(), stop.Token);
        Assert.True(result.ResourcesReleased); // Legacy no-resource settlement, not an observed callback.
        Assert.True(result.Observation.StopObserved);
        Assert.Equal(ModernJournalCallbackOutcome.NotEntered, result.Observation.Prepare);
        Assert.Equal(ModernJournalCallbackOutcome.NotEntered, result.Observation.Release);
    }

    [Theory]
    [InlineData(-1)] [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void StopFactsOnlyDescribeTheExistingBetweenCallChecks(int stopAt)
    {
        using var stop = new CancellationTokenSource();
        var frame = new Frame
        {
            Preparing = () => { if (stopAt == -1) stop.Cancel(); return true; },
            Acquiring = at => { if (at == stopAt) stop.Cancel(); return ModernJournalAcquisition.Acquired; }
        };
        var facts = new ModernOwnedJournalSampleDomain().Run(frame, stop.Token).Observation;
        Assert.True(facts.StopObserved);
        Assert.Equal(ModernJournalCallbackOutcome.Confirmed, facts.Prepare);
        var stages = new[] { facts.Directory, facts.Lease, facts.Journal };
        for (int at = 0; at < stages.Length; at++)
            Assert.Equal(at <= stopAt ? ModernJournalCallbackOutcome.Confirmed : ModernJournalCallbackOutcome.NotEntered, stages[at]);
        Assert.Equal(ModernJournalCallbackOutcome.NotEntered, facts.Exercise);
        Assert.Equal(ModernJournalCallbackOutcome.Confirmed, facts.Release);
    }

    [Fact]
    public async Task ConcurrentSnapshotsShowRunningCallbackUntilItsActualReturn()
    {
        using var entered = new ManualResetEventSlim(); using var finish = new ManualResetEventSlim();
        using var stop = new CancellationTokenSource();
        var domain = new ModernOwnedJournalSampleDomain();
        var frame = new Frame { Acquiring = _ =>
        {
            entered.Set();
            if (!finish.Wait(TimeSpan.FromSeconds(5))) throw new IOException("Controlled wait exceeded bound");
            return ModernJournalAcquisition.Acquired;
        } };
        var worker = Task.Run(() => domain.Run(frame, stop.Token));
        ModernJournalSampleObservation? running = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            running = domain.Observation;
            Assert.NotNull(running);
            Assert.Equal(ModernJournalCallbackOutcome.Running, running.Directory);
            Assert.Equal(ModernJournalCallbackOutcome.NotEntered, running.Lease);
            stop.Cancel();
            Assert.False(domain.Observation!.StopObserved); // Caller request is not callback completion.
            Assert.True(domain.HasPendingWork);
        }
        finally { finish.Set(); }
        var result = await worker;
        Assert.True(result.Observation.StopObserved);
        Assert.Equal(ModernJournalCallbackOutcome.Confirmed, result.Observation.Directory);
        Assert.Equal(ModernJournalCallbackOutcome.NotEntered, result.Observation.Lease);
        Assert.Equal(ModernJournalCallbackOutcome.Running, running!.Directory); // Copied immutable snapshot.
    }

    [Fact]
    public void NamespaceFactsRequireOwnedExerciseThreadAndCannotBeReplayed()
    {
        var domain = new ModernOwnedJournalSampleDomain();
        Frame? actual = null;
        var foreign = new Frame();
        Assert.False(domain.TryRecordNames(foreign, ModernJournalNamesObservation.InProgress));
        actual = new Frame
        {
            Preparing = () =>
            {
                Assert.False(domain.TryRecordNames(actual!, ModernJournalNamesObservation.InProgress));
                return true;
            },
            Observing = () =>
            {
                Assert.False(domain.TryRecordNames(foreign, ModernJournalNamesObservation.InProgress));
                Assert.False(domain.TryRecordNames(actual!, (ModernJournalNamesObservation)987));
                Assert.False(domain.TryRecordNames(actual!, ModernJournalNamesObservation.Complete));
                Assert.True(domain.TryRecordNames(actual!, ModernJournalNamesObservation.InProgress));
                Exception? failure = null;
                var other = new Thread(() =>
                {
                    try { Assert.False(domain.TryRecordNames(actual!, ModernJournalNamesObservation.Refused)); }
                    catch (Exception exception) { failure = exception; }
                }) { IsBackground = true };
                other.Start(); Assert.True(other.Join(TimeSpan.FromSeconds(5))); Assert.Null(failure);
                Assert.True(domain.TryRecordNames(actual!, ModernJournalNamesObservation.Complete));
                Assert.False(domain.TryRecordNames(actual!, ModernJournalNamesObservation.InProgress));
                Assert.False(domain.TryRecordNames(actual!, ModernJournalNamesObservation.Complete));
                return false; // Namespace observation alone is not overall sample verification.
            },
            Releasing = () =>
            {
                Assert.False(domain.TryRecordNames(actual!, ModernJournalNamesObservation.Refused));
                return true;
            }
        };
        var result = domain.Run(actual);
        Assert.Equal(ModernJournalNamesObservation.Complete, result.Observation.Names);
        Assert.Equal(ModernJournalCallbackOutcome.Refused, result.Observation.Exercise);
        Assert.False(result.SampleVerified);
        Assert.True(result.ResourcesReleased);
        Assert.False(domain.TryRecordNames(actual, ModernJournalNamesObservation.InProgress));
    }

    [Fact]
    public void CompletedNamesCannotQualifyAThrowingLaterSampleObservation()
    {
        var domain = new ModernOwnedJournalSampleDomain(); Frame? actual = null;
        actual = new Frame { Observing = () =>
        {
            Assert.True(domain.TryRecordNames(actual!, ModernJournalNamesObservation.InProgress));
            Assert.True(domain.TryRecordNames(actual!, ModernJournalNamesObservation.Complete));
            throw new IOException("Controlled post-name uncertainty");
        } };
        var result = domain.Run(actual);
        Assert.Equal(ModernJournalNamesObservation.Complete, result.Observation.Names);
        Assert.Equal(ModernJournalCallbackOutcome.Uncertain, result.Observation.Exercise);
        Assert.Equal(ModernJournalCallbackOutcome.NotEntered, result.Observation.Release);
        Assert.False(result.SampleVerified); Assert.False(result.ResourcesReleased);
        Assert.True(result.RequiresRetention); Assert.Equal(0, actual.ReleaseCalls);
    }

    [Theory]
    [InlineData(2)] [InlineData(4)]
    public void RefusedOrUncertainNamespaceFactsRemainTerminal(int terminal)
    {
        var domain = new ModernOwnedJournalSampleDomain(); Frame? actual = null;
        actual = new Frame { Observing = () =>
        {
            Assert.True(domain.TryRecordNames(actual!, ModernJournalNamesObservation.InProgress));
            Assert.True(domain.TryRecordNames(actual!, (ModernJournalNamesObservation)terminal));
            Assert.False(domain.TryRecordNames(actual!, ModernJournalNamesObservation.Complete));
            return false;
        } };
        var result = domain.Run(actual);
        Assert.Equal((ModernJournalNamesObservation)terminal, result.Observation.Names);
        Assert.True(result.ResourcesReleased);
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
