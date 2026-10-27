using System.Collections;
using System.Text.Json;
using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class WerMetadataTests
{
    private static readonly DateTimeOffset End = new(2026, 1, 8, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Start = End.AddDays(-7);
    private static WerMetadataRecord Row(Guid id, DateTimeOffset? created = null) => new(id, (created ?? Start.AddDays(2)).UtcDateTime.ToFileTimeUtc());
    private static WerMetadataStoreSnapshot Source(WerMetadataStore store, params WerMetadataRecord[] rows) =>
        new(store, CheckState.Available, rows, rows.Length, false, 0);
    private static CheckResult<WerMetadataReport> Read(params WerMetadataStoreSnapshot[] sources) => WerMetadataProjection.Create(Start, End, sources);
    private static WerMetadataReport Report(CheckResult<WerMetadataReport> result) => Assert.Single(result.Items);
    private static WerMetadataStoreSnapshot EmptyArchive => Source(WerMetadataStore.MachineArchive);

    [Fact]
    public void StoredReferencePreservesCreationTimeAndSourceWithoutAppSignatureFields()
    {
        var id = Guid.NewGuid(); var created = Start.AddDays(2);
        var result = Read(Source(WerMetadataStore.MachineQueue, Row(id, created)), EmptyArchive);
        Assert.Equal(CheckState.Available, result.State);
        var row = Assert.Single(Report(result).Observations);
        Assert.Equal(id, row.ReportId); Assert.Equal(created, row.CreatedUtc);
        Assert.Equal("E_STORE_MACHINE_QUEUE", row.Store);
        Assert.Equal("Stored WER report", row.Category);
        Assert.Contains("WerStoreQueryReportMetadataV1", row.Source);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(row));
        Assert.Equal(new[] { "Store", "ReportId", "CreatedUtc", "Category", "Source" }, json.RootElement.EnumerateObject().Select(field => field.Name));
    }

    [Fact]
    public void FixedWindowUsesReportCreationTimeWithInclusiveStartAndExclusiveEnd()
    {
        var included = Guid.NewGuid();
        var result = Read(Source(WerMetadataStore.MachineQueue,
            Row(included, Start), Row(Guid.NewGuid(), Start.AddTicks(-1)), Row(Guid.NewGuid(), End), Row(Guid.NewGuid(), End.AddDays(1))), EmptyArchive);
        Assert.Equal(included, Assert.Single(Report(result).Observations).ReportId);
        Assert.Equal(Start, Report(result).StartUtc); Assert.Equal(End, Report(result).EndUtc);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MaxValue)]
    public void InvalidNativeFileTimesNeverBecomeInventedReportDates(long fileTime)
    {
        var result = Read(Source(WerMetadataStore.MachineQueue, new WerMetadataRecord(Guid.NewGuid(), fileTime)), EmptyArchive);
        Assert.Equal(CheckState.Partial, result.State); Assert.Empty(Report(result).Observations);
        Assert.Contains(Report(result).Coverage, line => line.Contains("invalid records", StringComparison.Ordinal));
    }

    [Fact]
    public void EmptyGuidDoesNotBecomeAUsableReportIdentifier()
    {
        var result = Read(Source(WerMetadataStore.MachineQueue, Row(Guid.Empty)), EmptyArchive);
        Assert.Equal(CheckState.Partial, result.State); Assert.Empty(Report(result).Observations);
    }

    [Fact]
    public void DuplicateSameStoreReferencesAreNotDoubleCounted()
    {
        var row = Row(Guid.NewGuid());
        var result = Read(Source(WerMetadataStore.MachineQueue, row, row), EmptyArchive);
        Assert.Equal(CheckState.Partial, result.State); Assert.Single(Report(result).Observations);
        Assert.Contains(Report(result).Coverage, line => line.Contains("duplicate/conflicting", StringComparison.Ordinal));
    }

    [Fact]
    public void ConflictingCreationDatesDiscardAReferenceEvenWhenOneIsOutsideTheWindow()
    {
        var id = Guid.NewGuid();
        var result = Read(Source(WerMetadataStore.MachineQueue, Row(id), Row(id, Start.AddDays(-1))), EmptyArchive);
        Assert.Equal(CheckState.Partial, result.State); Assert.Empty(Report(result).Observations);
    }

    [Fact]
    public void SameGuidAcrossStoresStaysSourceSpecificAndCannotBeSummedIntoCrashes()
    {
        var row = Row(Guid.NewGuid());
        var result = Read(Source(WerMetadataStore.MachineQueue, row), Source(WerMetadataStore.MachineArchive, row));
        Assert.Equal(CheckState.Partial, result.State); Assert.Equal(2, Report(result).Observations.Count);
        Assert.Equal(2, Report(result).Observations.Select(item => item.Store).Distinct().Count());
        Assert.Contains(Report(result).Coverage, line => line.Contains("must not be added", StringComparison.Ordinal));
    }

    [Fact]
    public void EmptyAccessibleStoresAreNotAHealthySystemOrNoIncidentVerdict()
    {
        var result = Read(Source(WerMetadataStore.MachineQueue), EmptyArchive);
        Assert.Equal(CheckState.Available, result.State); Assert.Empty(Report(result).Observations);
        Assert.Contains(Report(result).Coverage, line => line.Contains("no incidents", StringComparison.Ordinal));
        Assert.DoesNotContain("healthy", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InaccessibleStoresRemainUnavailableWithoutElevationOrAnEmptySuccess()
    {
        var queue = Source(WerMetadataStore.MachineQueue) with { State = CheckState.Unavailable, NativeError = unchecked((int)0x80070005) };
        var archive = EmptyArchive with { State = CheckState.Unavailable };
        var result = Read(queue, archive);
        Assert.Equal(CheckState.Unavailable, result.State); Assert.Empty(Report(result).Observations);
        Assert.Contains(Report(result).Coverage, line => line.Contains("No elevation", StringComparison.Ordinal));
    }

    [Fact]
    public void AReadableSourceSurvivesAnUnavailableSecondStoreWithPartialCoverage()
    {
        var id = Guid.NewGuid();
        var result = Read(Source(WerMetadataStore.MachineQueue, Row(id)), EmptyArchive with { State = CheckState.Unavailable });
        Assert.Equal(CheckState.Partial, result.State); Assert.Equal(id, Assert.Single(Report(result).Observations).ReportId);
    }

    [Theory]
    [InlineData(CheckState.Unsupported)]
    [InlineData(CheckState.Failed)]
    [InlineData(CheckState.Cancelled)]
    public void EntirelyUncompletedSourcesKeepTheirDistinctOutcome(CheckState state)
    {
        var result = Read(Source(WerMetadataStore.MachineQueue) with { State = state }, EmptyArchive with { State = state });
        Assert.Equal(state, result.State); Assert.Empty(Report(result).Observations);
    }

    [Theory]
    [InlineData(CheckState.Unknown)]
    [InlineData(CheckState.Unavailable)]
    [InlineData(CheckState.Unsupported)]
    [InlineData(CheckState.Failed)]
    [InlineData(CheckState.Cancelled)]
    [InlineData((CheckState)99)]
    public void PlausibleMetadataCannotOverrideAnUncompletedSourceState(CheckState state)
    {
        var result = Read(Source(WerMetadataStore.MachineQueue, Row(Guid.NewGuid())) with { State = state }, EmptyArchive);
        Assert.Equal(CheckState.Partial, result.State); Assert.Empty(Report(result).Observations);
    }

    [Fact]
    public void MissingSourcesDoNotFabricateAnEmptyStoreRead()
    {
        var result = Read();
        Assert.Equal(CheckState.Unknown, result.State); Assert.Empty(Report(result).Observations);
        Assert.All(Report(result).StoreStatuses, status =>
        {
            Assert.Equal(WerMetadataStoreReason.MissingSourceResponse, status.Reason);
            Assert.Null(status.LastNativeResult);
        });
    }

    [Theory]
    [InlineData(WerMetadataStoreReason.SuccessfulOpenReturnedNullHandle)]
    [InlineData(WerMetadataStoreReason.SuccessfulEnumerationReturnedNullKey)]
    public void SuccessfulNativeReturnWithMissingObjectStaysUnknownWithoutInventingAnHresult(WerMetadataStoreReason reason)
    {
        var queue = Source(WerMetadataStore.MachineQueue) with { State = CheckState.Unknown, Reason = reason, LastNativeResult = 0 };
        var archive = EmptyArchive with { State = CheckState.Unknown, Reason = reason, LastNativeResult = 0 };
        var result = Read(queue, archive);
        Assert.Equal(CheckState.Unknown, result.State); Assert.Empty(Report(result).Observations);
        Assert.All(Report(result).StoreStatuses, status =>
        {
            Assert.Equal(CheckState.Unknown, status.State); Assert.Equal(reason, status.Reason);
            Assert.Equal(0, status.LastNativeResult); Assert.Equal(0, status.ProjectedReferences);
        });
        Assert.DoesNotContain(Report(result).Coverage, line => line.Contains("8000FFFF", StringComparison.Ordinal));
    }

    [Fact]
    public void TypedDiagnosticsPreserveActualDenialAndOnlyExposeMinimizedStatusFields()
    {
        int denied = unchecked((int)0x80070005);
        var queue = Source(WerMetadataStore.MachineQueue) with
        { State = CheckState.Unavailable, NativeError = denied, LastNativeResult = denied, Reason = WerMetadataStoreReason.StoreOpenFailed };
        var result = Read(queue, EmptyArchive);
        var status = Assert.Single(Report(result).StoreStatuses, status => status.Store == WerMetadataStore.MachineQueue);
        Assert.Equal(denied, status.LastNativeResult); Assert.Equal(CheckState.Unavailable, status.State);
        Assert.Equal(WerMetadataStoreReason.StoreOpenFailed, status.Reason);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(status));
        Assert.Equal(new[] { "Store", "State", "InspectedKeys", "MetadataRecords", "ProjectedReferences", "MetadataFailures", "Truncated", "LastNativeResult", "Reason" },
            json.RootElement.EnumerateObject().Select(field => field.Name));
    }

    [Fact]
    public void EnumerationEndIsDistinctFromFailureAndRetainsObservedNoMoreFilesResult()
    {
        var queue = Source(WerMetadataStore.MachineQueue) with
        { Reason = WerMetadataStoreReason.EnumerationComplete, LastNativeResult = unchecked((int)0x80070012) };
        var result = Read(queue, EmptyArchive with { Reason = WerMetadataStoreReason.EnumerationComplete, LastNativeResult = 18 });
        Assert.Equal(CheckState.Available, result.State); Assert.Empty(Report(result).Observations);
        Assert.All(Report(result).StoreStatuses, status => Assert.Equal(WerMetadataStoreReason.EnumerationComplete, status.Reason));
        Assert.Equal(unchecked((int)0x80070012), Report(result).StoreStatuses.Single(status => status.Store == WerMetadataStore.MachineQueue).LastNativeResult);
    }

    [Fact]
    public void ContradictoryNullObjectDiagnosticCannotAuthorizeAvailableSourceRows()
    {
        var queue = Source(WerMetadataStore.MachineQueue, Row(Guid.NewGuid())) with
        { Reason = WerMetadataStoreReason.SuccessfulEnumerationReturnedNullKey, LastNativeResult = 0 };
        var result = Read(queue, EmptyArchive);
        Assert.Equal(CheckState.Partial, result.State); Assert.Empty(Report(result).Observations);
        var status = Report(result).StoreStatuses.Single(status => status.Store == WerMetadataStore.MachineQueue);
        Assert.Equal(CheckState.Unknown, status.State); Assert.Equal(WerMetadataStoreReason.InvalidSourceResponse, status.Reason);
        Assert.Equal(0, status.LastNativeResult);
    }

    [Fact]
    public void DuplicateSourceRepliesAreAmbiguousAndDiscarded()
    {
        var accepted = Guid.NewGuid();
        var result = Read(Source(WerMetadataStore.MachineQueue, Row(Guid.NewGuid())), Source(WerMetadataStore.MachineQueue, Row(Guid.NewGuid())),
            Source(WerMetadataStore.MachineArchive, Row(accepted)));
        Assert.Equal(CheckState.Partial, result.State); Assert.Equal(accepted, Assert.Single(Report(result).Observations).ReportId);
        var status = Report(result).StoreStatuses.Single(status => status.Store == WerMetadataStore.MachineQueue);
        Assert.Equal(WerMetadataStoreReason.AmbiguousSourceResponse, status.Reason); Assert.Null(status.LastNativeResult);
    }

    [Fact]
    public void UnrecognizedStoreCannotInjectAReportOrArbitrarySourceName()
    {
        var result = Read(Source((WerMetadataStore)99, Row(Guid.NewGuid())), Source(WerMetadataStore.MachineQueue), EmptyArchive);
        Assert.Equal(CheckState.Partial, result.State); Assert.Empty(Report(result).Observations);
    }

    [Fact]
    public void BoundedOrInconsistentNativeCountsDoNotTrustTheSourceRows()
    {
        var row = Row(Guid.NewGuid());
        var excessive = Enumerable.Range(0, 129).Select(_ => Row(Guid.NewGuid())).ToArray();
        var tooMany = Read(Source(WerMetadataStore.MachineQueue, excessive), EmptyArchive);
        Assert.Equal(CheckState.Partial, tooMany.State); Assert.Empty(Report(tooMany).Observations);
        var wrongCount = Read(Source(WerMetadataStore.MachineQueue, row) with { InspectedKeys = 0 }, EmptyArchive);
        Assert.Equal(CheckState.Partial, wrongCount.State); Assert.Empty(Report(wrongCount).Observations);
    }

    [Fact]
    public void NativeLimitAndMetadataFailuresKeepUsableRowsButMarkTheReportPartial()
    {
        var id = Guid.NewGuid();
        var source = Source(WerMetadataStore.MachineQueue, Row(id)) with { InspectedKeys = 2, Truncated = true, MetadataFailures = 1, State = CheckState.Partial };
        var result = Read(source, EmptyArchive);
        Assert.Equal(CheckState.Partial, result.State); Assert.Equal(id, Assert.Single(Report(result).Observations).ReportId);
        Assert.Contains(Report(result).Coverage, line => line.Contains("TRUNCATED", StringComparison.Ordinal));
    }

    [Fact]
    public void ExactRecordCeilingsCanBeRepresentedWithoutInventingATruncation()
    {
        var queue = Enumerable.Range(0, 128).Select(i => Row(Guid.NewGuid(), Start.AddSeconds(i))).ToArray();
        var archive = Enumerable.Range(0, 128).Select(i => Row(Guid.NewGuid(), Start.AddSeconds(i))).ToArray();
        var result = Read(Source(WerMetadataStore.MachineQueue, queue), Source(WerMetadataStore.MachineArchive, archive));
        Assert.Equal(CheckState.Available, result.State); Assert.Equal(256, Report(result).Observations.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(31)]
    public void UnsupportedWindowCannotProduceTrustedObservations(int days)
    {
        var result = WerMetadataProjection.Create(End.AddDays(-days), End, [Source(WerMetadataStore.MachineQueue), EmptyArchive]);
        Assert.Equal(CheckState.Unknown, result.State); Assert.Empty(result.Items);
    }

    [Fact]
    public void NonUtcBoundaryOffsetsAreRejectedRatherThanGuessed()
    {
        var result = WerMetadataProjection.Create(Start.ToOffset(TimeSpan.FromHours(1)), End.ToOffset(TimeSpan.FromHours(1)), [Source(WerMetadataStore.MachineQueue), EmptyArchive]);
        Assert.Equal(CheckState.Unknown, result.State); Assert.Empty(result.Items);
    }

    [Fact]
    public void CancellationBeforeProjectionDoesNotReturnAnEmptySuccess()
    {
        using var source = new CancellationTokenSource(); source.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => WerMetadataProjection.Create(Start, End, [Source(WerMetadataStore.MachineQueue), EmptyArchive], source.Token));
    }

    [Fact]
    public void CancellationDuringRowsDoesNotReturnAPartialSuccessfulProjection()
    {
        using var source = new CancellationTokenSource();
        var rows = new CancelRows(source);
        var queue = new WerMetadataStoreSnapshot(WerMetadataStore.MachineQueue, CheckState.Available, rows, 2, false, 0);
        Assert.ThrowsAny<OperationCanceledException>(() => WerMetadataProjection.Create(Start, End, [queue, EmptyArchive], source.Token));
    }

    private sealed class CancelRows(CancellationTokenSource source) : IReadOnlyList<WerMetadataRecord>
    {
        public int Count => 2;
        public WerMetadataRecord this[int index] => Row(Guid.NewGuid());
        public IEnumerator<WerMetadataRecord> GetEnumerator()
        {
            yield return Row(Guid.NewGuid());
            source.Cancel();
            yield return Row(Guid.NewGuid());
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
