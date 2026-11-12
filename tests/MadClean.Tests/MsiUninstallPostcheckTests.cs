using System.Reflection;
using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class MsiUninstallPostcheckTests
{
    private const string Code = "{12345678-ABCD-1234-ABCD-123456789ABC}";
    private const long Generation = 8;
    private sealed record Fixture(MsiUninstallRequestIntent Intent, MsiUninstallRequestResult Result,
        MsiSelectedProductReadScope Scope, DateTimeOffset Now);

    private static Fixture Make(bool reserve = true, CheckState coverage = CheckState.Available)
    {
        var observed = DateTimeOffset.UtcNow.AddSeconds(-10);
        var provenance = MsiArpProvenance.CurrentUserShared;
        string identity = MsiSelectedProductPreparationPolicy.RegistrationIdentity(provenance, Code);
        string source = MsiSelectedProductPreparationPolicy.SourceLabel(provenance);
        var app = new InstalledApplication(identity, "Example", "1.2", "Publisher", source, @"C:\historical-only", "MSI",
            new(identity, source, false, true, true, Code, observed));
        var snapshot = new CheckResult<InstalledApplication>(coverage, "private snapshot details", [app]);
        var scope = Assert.Single(MsiSelectedProductPreparationPolicy.CreateScope(snapshot, app, Generation, observed.AddMilliseconds(10)).Items);
        var read = observed.AddMilliseconds(20);
        var current = app with { InstallLocation = null, RegistrationEvidence = app.RegistrationEvidence! with { ObservedUtc = read } };
        var lookups = Enum.GetValues<MsiArpProvenance>().Select(kind => kind == provenance
            ? new MsiSelectedArpLookup(kind, MsiSelectedArpLookupState.Present, current, false, read, null)
            : new MsiSelectedArpLookup(kind, MsiSelectedArpLookupState.Absent, null, null, read, 2)).ToArray();
        var instances = new[] { new MsiRegistrationObservation(Code, MsiRegistrationContext.UserUnmanaged,
            MsiObservedProductState.Installed, app.Name, app.Version, app.Publisher, read) };
        var preparation = MsiSelectedProductPreparationPolicy.Assess(scope, lookups, instances, true, 0, Generation, read.AddMilliseconds(10));
        var intent = Assert.Single(MsiUninstallRequestPolicy.CreatePreview(scope, preparation, Generation, read.AddMilliseconds(20)).Items).Intent;
        if (reserve) Assert.Equal(MsiUninstallDispatchReservation.Reserved,
            intent.TryReserveDispatch(true, Generation, intent.PreparedUtc.AddMilliseconds(10)));
        var result = new MsiUninstallRequestResult(intent.RequestId, MsiUninstallRequestState.ClientExited,
            intent.PreparedUtc.AddMilliseconds(20), intent.PreparedUtc.AddMilliseconds(30), intent.PreparedUtc.AddSeconds(1), 41, 0,
            "private client output is not a postcheck message")
        { ClientStartObserved = true, ExitDisposition = MsiUninstallExitDisposition.SuccessReported };
        return new(intent, result, scope, DateTimeOffset.UtcNow.AddSeconds(-1));
    }
    private static MsiUninstallPostcheckSession Session(Fixture f, MsiUninstallRequestResult? result = null) =>
        Assert.Single(MsiUninstallPostcheckPolicy.Prepare(f.Intent, result ?? f.Result, f.Now).Items);
    private static CheckResult<MsiRegistrationWatchReport> Read(MsiUninstallPostcheckSession session,
        bool complete = true, int issues = 0, bool deniedArp = false)
    {
        var started = session.Scope.RequestedUtc;
        var checkedUtc = started.AddMilliseconds(200);
        var arp = Enum.GetValues<MsiArpProvenance>().Select(kind =>
            new MsiSelectedArpLookup(kind, MsiSelectedArpLookupState.Absent, null, null, started.AddMilliseconds(100), 2)).ToArray();
        if (deniedArp) arp[0] = arp[0] with { State = MsiSelectedArpLookupState.Unknown, NativeError = 5 };
        return MsiRegistrationWatchPolicy.Assess(session.Scope, arp, [], complete, issues, started, checkedUtc);
    }

    [Fact]
    public void SelectedPreviewCopiesItsHistoricalTargetWithoutOptionalPathsOrAnotherActionCapability()
    {
        var f = Make(); var target = Assert.IsType<MsiRegistrationWatchTarget>(f.Intent.PostcheckTarget);
        Assert.Equal(f.Intent.ProductCode, target.ProductCode); Assert.Equal(f.Intent.RegistrationIdentity, target.RegistrationIdentity);
        Assert.Equal(f.Intent.Name, target.Name); Assert.Equal(f.Intent.Version, target.Version); Assert.Equal(f.Intent.Publisher, target.Publisher);
        Assert.Equal(f.Intent.Context, target.OriginalContext); Assert.Equal(f.Scope.OriginalSource, target.OriginalSource);
        Assert.Equal(f.Intent.PreparedUtc, target.CapturedUtc); Assert.False(target.HistoricalNoRemove);
        Assert.False(target.CanUninstall); Assert.False(target.CanDelete);
        Assert.Null(typeof(MsiRegistrationWatchTarget).GetProperty("InstallLocation"));
        var prepared = MsiUninstallPostcheckPolicy.Prepare(f.Intent, f.Result, f.Now);
        var session = Assert.Single(prepared.Items);
        Assert.Same(f.Intent, session.Intent); Assert.Same(f.Result, session.Result); Assert.Same(target, session.Target);
        Assert.Same(target, session.Scope.Target); Assert.NotSame(f.Scope, session.Scope);
        Assert.True(session.Scope.RequestedUtc >= f.Result.ExitedUtc); Assert.False(session.Scope.IsInvalidated);
        Assert.False(session.CanDelete); Assert.False(session.CanUninstall);
        Assert.Equal(CheckState.Unknown, session.UninstallCompletion); Assert.Equal(CheckState.Unknown, session.LeftoverOwnership);
        Assert.DoesNotContain("private", prepared.Message);
    }

    [Theory]
    [InlineData(0)] [InlineData(1602)] [InlineData(1603)] [InlineData(1605)] [InlineData(1618)]
    [InlineData(3010)] [InlineData(1641)] [InlineData(-1)]
    public void AnyConsistentLiteralExitCanBeCheckedWithoutPromotingItToSuccessfulUninstall(int code)
    {
        var f = Make(); var result = f.Result with { ExitCode = code, ExitDisposition = MsiUninstallRequestPolicy.ClassifyExit(code) };
        var session = Session(f, result); var read = Read(session);
        Assert.True(MsiUninstallPostcheckPolicy.IsConsistent(session, read, f.Now.AddMilliseconds(500)));
        Assert.Same(result, session.Result); Assert.Equal(code, session.Result.ExitCode);
        Assert.Equal(CheckState.Unknown, session.UninstallCompletion); Assert.Equal(CheckState.Unknown, session.LeftoverOwnership);
    }

    [Fact]
    public void MissingOptionalPidStartAndCodeStayUnknownEvenWithAnAcceptedActualExit()
    {
        var f = Make();
        var result = f.Result with { ClientProcessId = null, StartedUtc = null, ExitCode = null, ExitDisposition = null };
        var session = Session(f, result);
        Assert.Null(session.Result.ExitCode); Assert.Null(session.Result.StartedUtc); Assert.Null(session.Result.ClientProcessId);
        Assert.True(MsiUninstallPostcheckPolicy.IsConsistent(session, Read(session), f.Now.AddMilliseconds(500)));
    }

    [Theory]
    [InlineData(MsiUninstallRequestState.NotStarted)] [InlineData(MsiUninstallRequestState.Blocked)]
    [InlineData(MsiUninstallRequestState.Preparing)] [InlineData(MsiUninstallRequestState.ClientStarted)]
    [InlineData(MsiUninstallRequestState.MonitoringUnknown)] [InlineData((MsiUninstallRequestState)999)]
    public void AnythingOtherThanAcceptedExitedStateCannotStartAPostcheck(MsiUninstallRequestState state)
    {
        var f = Make();
        Assert.Empty(MsiUninstallPostcheckPolicy.Prepare(f.Intent, f.Result with { State = state }, f.Now).Items);
        Assert.True(f.Intent.IsDispatchReserved);
    }

    [Fact]
    public void ForeignRequestUnreservedRequestAndInconsistentStartExitOrClassificationAreRefused()
    {
        var f = Make(); var unreserved = Make(false);
        Assert.Empty(MsiUninstallPostcheckPolicy.Prepare(unreserved.Intent, unreserved.Result, unreserved.Now).Items);
        foreach (var result in new[]
        {
            f.Result with { RequestId = Guid.NewGuid() }, f.Result with { ClientStartObserved = false },
            f.Result with { ClientStartObserved = null }, f.Result with { ExitedUtc = null },
            f.Result with { ExitedUtc = f.Result.RequestedUtc.AddTicks(-1) },
            f.Result with { ExitCode = 1603, ExitDisposition = MsiUninstallExitDisposition.SuccessReported }
        }) Assert.Empty(MsiUninstallPostcheckPolicy.Prepare(f.Intent, result, f.Now).Items);
    }

    [Fact]
    public void LegacyBindingOnlyPreviewDoesNotAcquireAnInventedSelectedTarget()
    {
        var f = Make(); var app = f.Intent.Scope.Application;
        var scope = Assert.Single(MsiActionBindingPolicy.CreateScope(new(CheckState.Available, "", [app]), app, f.Intent.PreparedUtc).Items);
        var at = f.Intent.PreparedUtc.AddMilliseconds(1);
        var current = app with { RegistrationEvidence = app.RegistrationEvidence! with { ObservedUtc = at } };
        var binding = MsiActionBindingPolicy.Assess(scope, new(CheckState.Available, "", [current]),
            [new(Code, MsiRegistrationContext.UserUnmanaged, MsiObservedProductState.Installed, app.Name, app.Version, app.Publisher, at)], true, [], at);
        var legacy = Assert.Single(MsiUninstallRequestPolicy.CreatePreview(scope, binding, Generation, at.AddMilliseconds(1)).Items).Intent;
        Assert.Null(legacy.PostcheckTarget);
        Assert.Equal(MsiUninstallDispatchReservation.Reserved, legacy.TryReserveDispatch(true, Generation, at.AddMilliseconds(2)));
        Assert.Empty(MsiUninstallPostcheckPolicy.Prepare(legacy, f.Result with { RequestId = legacy.RequestId, RequestedUtc = at.AddMilliseconds(3) }, f.Now).Items);
    }

    [Fact]
    public void LaunchPreviewExpiryDoesNotReviveOrInvalidateAnAlreadyConsumedHistoricalRequest()
    {
        var f = Make();
        // Controlled short-deadline fixture after a real Core reservation avoids a
        // two-minute sleep. Production exposes no setter or expiry override.
        typeof(MsiUninstallRequestIntent).GetField("<ExpiresUtc>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(f.Intent, f.Intent.PreparedUtc.AddMilliseconds(500));
        Assert.True(f.Now > f.Intent.ExpiresUtc);
        Assert.False(MsiSelectedProductPreparationPolicy.IsFresh(f.Scope, Generation, f.Now));
        var session = Session(f);
        Assert.True(MsiRegistrationWatchPolicy.IsFresh(session.Scope, f.Now));
        Assert.Equal(MsiUninstallDispatchReservation.AlreadyReserved, f.Intent.TryReserveDispatch(true, Generation, f.Now));
        Assert.True(MsiUninstallPostcheckPolicy.IsConsistent(session, Read(session), f.Now.AddMilliseconds(500)));
    }

    [Fact]
    public void HistoricalPartialAndCurrentMissingSourcesRemainSeparateFromActualAbsence()
    {
        var partial = Make(coverage: CheckState.Partial); var session = Session(partial);
        var absence = Read(session); Assert.Equal(CheckState.Partial, absence.State);
        Assert.True(MsiUninstallPostcheckPolicy.IsConsistent(session, absence, partial.Now.AddMilliseconds(500)));
        Assert.All(absence.Items[0].Contexts, row => Assert.Equal(MsiRegistrationWatchPresence.Absent, row.Presence));
        var incomplete = Read(session, complete: false, issues: 1, deniedArp: true);
        Assert.True(MsiUninstallPostcheckPolicy.IsConsistent(session, incomplete, partial.Now.AddMilliseconds(500)));
        Assert.Equal(MsiSelectedArpLookupState.Unknown, incomplete.Items[0].ArpLookups[0].State);
        Assert.All(incomplete.Items[0].Contexts, row => Assert.Equal(MsiRegistrationWatchPresence.Unknown, row.Presence));
        Assert.Same(partial.Result, session.Result); Assert.Equal(MsiUninstallRequestState.ClientExited, session.Result.State);
    }

    [Fact]
    public void SameProductFromAnotherRequestOrReadScopeCannotSupplyThisPostcheck()
    {
        var first = Make(); var second = Make(); var one = Session(first); var two = Session(second);
        Assert.Equal(one.Target.ProductCode, two.Target.ProductCode); Assert.NotEqual(one.Target.TargetId, two.Target.TargetId);
        Assert.False(MsiUninstallPostcheckPolicy.IsConsistent(one, Read(two), second.Now.AddMilliseconds(500)));
        Assert.Empty(MsiUninstallPostcheckPolicy.Prepare(first.Intent, second.Result, second.Now).Items);
        var another = Session(first);
        Assert.NotEqual(one.Scope.RequestId, another.Scope.RequestId);
        Assert.False(MsiUninstallPostcheckPolicy.IsConsistent(one, Read(another), first.Now.AddMilliseconds(500)));
    }

    [Fact]
    public void InvalidatedPostcheckAndPrematureReceiptCannotRewriteTheOriginalRequestResult()
    {
        var f = Make(); var session = Session(f); var read = Read(session);
        Assert.False(MsiUninstallPostcheckPolicy.IsConsistent(session, read, session.Scope.RequestedUtc));
        session.Scope.Invalidate();
        Assert.False(MsiUninstallPostcheckPolicy.IsConsistent(session, read, f.Now.AddMilliseconds(500)));
        Assert.Same(f.Result, session.Result); Assert.True(f.Intent.IsDispatchReserved);
        Assert.Equal(MsiUninstallExitDisposition.SuccessReported, session.Result.ExitDisposition);
    }

    [Theory]
    [InlineData(CheckState.Unknown)] [InlineData(CheckState.Unavailable)] [InlineData(CheckState.Unsupported)]
    [InlineData(CheckState.Failed)] [InlineData(CheckState.Cancelled)]
    public void EmptyNegativeReaderStatesNeverBecomeSuccessfulAbsenceReports(CheckState state)
    {
        var f = Make(); var session = Session(f);
        Assert.False(MsiUninstallPostcheckPolicy.IsConsistent(session,
            CheckResult<MsiRegistrationWatchReport>.Empty(state, "fixed source status"), f.Now.AddMilliseconds(500)));
        Assert.Equal(MsiUninstallRequestState.ClientExited, session.Result.State);
    }

    [Fact]
    public void NullFutureNonUtcAndPrecancelledPreparationPublishesNoReadSelector()
    {
        var f = Make();
        Assert.Empty(MsiUninstallPostcheckPolicy.Prepare(null, f.Result, f.Now).Items);
        Assert.Empty(MsiUninstallPostcheckPolicy.Prepare(f.Intent, null, f.Now).Items);
        Assert.Empty(MsiUninstallPostcheckPolicy.Prepare(f.Intent, f.Result, default).Items);
        Assert.Empty(MsiUninstallPostcheckPolicy.Prepare(f.Intent, f.Result, f.Now.ToOffset(TimeSpan.FromHours(1))).Items);
        Assert.Empty(MsiUninstallPostcheckPolicy.Prepare(f.Intent, f.Result, DateTimeOffset.UtcNow.AddMinutes(1)).Items);
        Assert.Empty(MsiUninstallPostcheckPolicy.Prepare(f.Intent, f.Result, f.Result.ExitedUtc!.Value.AddTicks(-1)).Items);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => MsiUninstallPostcheckPolicy.Prepare(f.Intent, f.Result, f.Now, stop.Token));
        Assert.True(f.Intent.IsDispatchReserved); Assert.NotNull(f.Intent.PostcheckTarget);
    }

    private static CheckResult<InstalledApplication> HistoricalBaseline(Fixture fixture, params InstalledApplication[] others) =>
        new(CheckState.Available, "complete historical ARP coverage", new[] { fixture.Intent.Scope.Application }.Concat(others).ToArray());
    private static DateTimeOffset BaselineReceived(Fixture fixture) => fixture.Intent.Scope.Application.RegistrationEvidence!.ObservedUtc.AddMilliseconds(1);
    private static InstalledApplication Other(string identity, string path) =>
        new(identity, "Other registration", "1", "Other publisher", "Other covered source", path, "Unknown");
    private static CheckResult<LeftoverCandidate> ReviewHistorical(Fixture fixture, MsiUninstallPostcheckSession session,
        CheckResult<MsiRegistrationWatchReport> postcheck, CheckResult<InstalledApplication> baseline,
        CheckResult<InstalledApplication> after, Func<string, bool?> probe) => MsiUninstallLeftoverReview.Compare(session,
            postcheck, fixture.Now.AddMilliseconds(300), baseline, BaselineReceived(fixture), after,
            fixture.Now.AddMilliseconds(400), fixture.Now.AddMilliseconds(500), probe);

    [Fact]
    public void HistoricalLeftoverReviewProbesOnlyThisRequestAndRetainsAllPriorRootsForSharedCautions()
    {
        var f = Make(); var session = Session(f); var postcheck = Read(session);
        var baseline = HistoricalBaseline(f, Other("other-departed", @"C:\unrelated"), Other("prior-overlap", @"C:\historical-only\shared"));
        List<string> probes = [];
        var review = ReviewHistorical(f, session, postcheck, baseline,
            new(CheckState.Available, "complete new inventory", []), path => { probes.Add(path); return true; });
        Assert.Equal(new[] { @"C:\historical-only" }, probes);
        var candidate = Assert.Single(review.Items);
        Assert.Equal(f.Intent.RegistrationIdentity, candidate.ApplicationIdentity);
        Assert.Contains("overlaps another application's", candidate.Caution);
        Assert.Contains("not proven junk", candidate.Caution);
        Assert.Contains("do not establish complete uninstall", review.Message);
        Assert.Equal(CheckState.Unknown, session.UninstallCompletion); Assert.Equal(CheckState.Unknown, session.LeftoverOwnership);
        Assert.Same(f.Result, session.Result); Assert.Equal(3, baseline.Items.Count);
    }

    [Fact]
    public void HistoricalLeftoverReviewAlsoRetainsCurrentOtherRootsAndExistingMetadataProtections()
    {
        var f = Make(); var session = Session(f);
        var baseline = HistoricalBaseline(f);
        var after = new CheckResult<InstalledApplication>(CheckState.Available, "complete", [Other("currently-shared", @"C:\historical-only\current")]);
        var review = ReviewHistorical(f, session, Read(session), baseline, after, _ => true);
        var candidate = Assert.Single(review.Items);
        Assert.Contains("overlaps another application's", candidate.Caution);
        Assert.Empty(LeftoverContentReview.ValidateScope(candidate, baseline, after, f.Now.AddMilliseconds(600)).Items);
    }

    [Fact]
    public void HistoricalLeftoverReviewCannotProbeAnAppThatRemainsRegisteredOrAnUnrelatedDepartedRoot()
    {
        var f = Make(); var session = Session(f);
        var baseline = HistoricalBaseline(f, Other("gone-other", @"C:\other"));
        int probes = 0;
        var review = ReviewHistorical(f, session, Read(session), baseline,
            new(CheckState.Available, "complete current", [f.Intent.Scope.Application]), _ => { probes++; return true; });
        Assert.Empty(review.Items); Assert.Equal(0, probes);
    }

    [Theory]
    [InlineData(CheckState.Partial)] [InlineData(CheckState.Unknown)] [InlineData(CheckState.Unavailable)]
    [InlineData(CheckState.Unsupported)] [InlineData(CheckState.Failed)] [InlineData(CheckState.Cancelled)]
    public void HistoricalLeftoverReviewRequiresIndependentCompleteBeforeAndAfterInventories(CheckState coverage)
    {
        var f = Make(); var session = Session(f); var postcheck = Read(session);
        var baseline = HistoricalBaseline(f); int probes = 0;
        bool? Probe(string path) { probes++; return true; }
        Assert.Empty(ReviewHistorical(f, session, postcheck, baseline with { State = coverage },
            new(CheckState.Available, "complete", []), Probe).Items);
        Assert.Empty(ReviewHistorical(f, session, postcheck, baseline,
            new(coverage, "not complete", []), Probe).Items);
        Assert.Equal(0, probes);
    }

    [Fact]
    public void HistoricalLeftoverReviewRejectsDuplicateMissingAndAmbiguousHistoricalIdentitiesBeforeProbing()
    {
        var f = Make(); var session = Session(f); var baseline = HistoricalBaseline(f); var postcheck = Read(session); int probes = 0;
        bool? Probe(string path) { probes++; return true; }
        foreach (var rows in new IReadOnlyList<InstalledApplication>[]
        {
            [], [Other("unrelated", @"C:\other")], [baseline.Items[0], baseline.Items[0]],
            [baseline.Items[0], Other(baseline.Items[0].Identity.ToLowerInvariant(), @"C:\other")],
            [baseline.Items[0], Other("", @"C:\other")]
        }) Assert.Empty(ReviewHistorical(f, session, postcheck, baseline with { Items = rows },
            new(CheckState.Available, "complete", []), Probe).Items);
        Assert.Empty(ReviewHistorical(f, session, postcheck, baseline,
            new(CheckState.Available, "ambiguous", [Other("x", @"C:\a"), Other("X", @"C:\b")]), Probe).Items);
        Assert.Equal(0, probes);
    }

    [Theory]
    [InlineData("name")] [InlineData("version")] [InlineData("publisher")] [InlineData("source")]
    [InlineData("path")] [InlineData("support")] [InlineData("restriction")] [InlineData("code")]
    public void HistoricalLeftoverReviewRequiresOriginalMetadataAndRecordedInstallPath(string changed)
    {
        var f = Make(); var session = Session(f); var app = f.Intent.Scope.Application;
        var changedApp = changed switch
        {
            "name" => app with { Name = "Different" }, "version" => app with { Version = "9" },
            "publisher" => app with { Publisher = "Different" }, "source" => app with { Source = "Different" },
            "path" => app with { InstallLocation = @"C:\different" }, "support" => app with { UninstallSupport = "Different" },
            "restriction" => app with { RegistrationEvidence = app.RegistrationEvidence! with { NoRemove = true } },
            _ => app with { RegistrationEvidence = app.RegistrationEvidence! with { ProductCode = Guid.NewGuid().ToString("B").ToUpperInvariant() } }
        };
        int probes = 0;
        Assert.Empty(ReviewHistorical(f, session, Read(session), new(CheckState.Available, "complete", [changedApp]),
            new(CheckState.Available, "complete", []), _ => { probes++; return true; }).Items);
        Assert.Equal(0, probes);
    }

    [Fact]
    public void HistoricalLeftoverReviewPermitsEquivalentNormalizedPathWithoutRetargeting()
    {
        var f = Make(); var session = Session(f);
        var app = f.Intent.Scope.Application with { InstallLocation = "c:/HISTORICAL-ONLY/" };
        string? probed = null;
        var review = ReviewHistorical(f, session, Read(session), new(CheckState.Available, "complete", [app]),
            new(CheckState.Available, "complete", []), path => { probed = path; return true; });
        Assert.Single(review.Items); Assert.Equal(@"C:\HISTORICAL-ONLY", probed);
    }

    [Fact]
    public void HistoricalLeftoverReviewRejectsLateBaselineFutureBaselineAndNonUtcReceipt()
    {
        var f = Make(); var session = Session(f); var postcheck = Read(session); var baseline = HistoricalBaseline(f);
        foreach (var received in new[] { default, f.Intent.PreparedUtc.AddTicks(1), f.Intent.SelectedProductScope!.ValidatedUtc.AddTicks(1),
            BaselineReceived(f).ToOffset(TimeSpan.FromHours(1)), DateTimeOffset.UtcNow.AddMinutes(1),
            f.Intent.Scope.Application.RegistrationEvidence!.ObservedUtc.AddTicks(-1) })
            Assert.False(MsiUninstallLeftoverReview.CanReview(session, postcheck, f.Now.AddMilliseconds(300), baseline, received, f.Now.AddMilliseconds(500)));
        Assert.True(MsiUninstallLeftoverReview.CanReview(session, postcheck, f.Now.AddMilliseconds(300), baseline,
            BaselineReceived(f), f.Now.AddMilliseconds(500)));
    }

    [Fact]
    public void HistoricalLeftoverReviewNeedsThisExactAcceptedPostcheckNotAnotherSameProductRead()
    {
        var f = Make(); var one = Session(f); var two = Session(f); var baseline = HistoricalBaseline(f); int probes = 0;
        Assert.Empty(ReviewHistorical(f, one, Read(two), baseline, new(CheckState.Available, "complete", []), _ => { probes++; return true; }).Items);
        Assert.Empty(ReviewHistorical(f, one, CheckResult<MsiRegistrationWatchReport>.Empty(CheckState.Failed, "failed"), baseline,
            new(CheckState.Available, "complete", []), _ => { probes++; return true; }).Items);
        one.Scope.Invalidate();
        Assert.Empty(ReviewHistorical(f, one, Read(two), baseline, new(CheckState.Available, "complete", []), _ => { probes++; return true; }).Items);
        Assert.Equal(0, probes);
    }

    [Fact]
    public void HistoricalLeftoverReviewRejectsGlobalReadsBeforePostcheckOutOfOrderOrFuture()
    {
        var f = Make(); var session = Session(f); var postcheck = Read(session); var baseline = HistoricalBaseline(f); int probes = 0;
        var receipt = f.Now.AddMilliseconds(300);
        foreach (var pair in new[]
        {
            (Start: receipt.AddTicks(-1), End: receipt.AddMilliseconds(100)),
            (Start: receipt.AddMilliseconds(100), End: receipt),
            (Start: receipt, End: DateTimeOffset.UtcNow.AddMinutes(1)),
            (Start: receipt.ToOffset(TimeSpan.FromHours(1)), End: receipt.AddMilliseconds(100))
        }) Assert.Empty(MsiUninstallLeftoverReview.Compare(session, postcheck, receipt, baseline, BaselineReceived(f),
            new(CheckState.Available, "complete", []), pair.Start, pair.End, _ => { probes++; return true; }).Items);
        Assert.Equal(0, probes);
    }

    [Theory]
    [InlineData(0)] [InlineData(1602)] [InlineData(1603)] [InlineData(1605)] [InlineData(1618)] [InlineData(3010)] [InlineData(1641)]
    public void HistoricalLeftoverReviewCanFollowAnyAcceptedLiteralExitWithoutPromotingItToUninstallSuccess(int code)
    {
        var f = Make(); var actual = f.Result with { ExitCode = code, ExitDisposition = MsiUninstallRequestPolicy.ClassifyExit(code) };
        var session = Session(f, actual); var review = ReviewHistorical(f, session, Read(session), HistoricalBaseline(f),
            new(CheckState.Available, "complete", []), _ => true);
        Assert.Single(review.Items); Assert.Equal(CheckState.Unknown, session.UninstallCompletion);
        Assert.Equal(CheckState.Unknown, session.LeftoverOwnership); Assert.Same(actual, session.Result);
        Assert.Equal(code, session.Result.ExitCode); Assert.Contains("do not establish complete uninstall", review.Message);
    }

    [Fact]
    public void HistoricalLeftoverReviewPreservesUnknownInaccessibleAndCancelledProbeOutcomes()
    {
        var f = Make(); var session = Session(f); var postcheck = Read(session); var baseline = HistoricalBaseline(f);
        var after = new CheckResult<InstalledApplication>(CheckState.Available, "complete", []);
        foreach (Func<string, bool?> probe in new Func<string, bool?>[] { _ => null, _ => throw new UnauthorizedAccessException() })
        {
            var review = ReviewHistorical(f, session, postcheck, baseline, after, probe);
            Assert.Equal(CheckState.Partial, review.State); Assert.Empty(review.Items);
        }
        var cancelled = ReviewHistorical(f, session, postcheck, baseline, after, _ => throw new OperationCanceledException());
        Assert.Equal(CheckState.Cancelled, cancelled.State); Assert.Empty(cancelled.Items);
    }
}
