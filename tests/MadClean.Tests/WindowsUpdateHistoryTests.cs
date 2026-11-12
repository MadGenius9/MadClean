using System.Text.Json;
using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class WindowsUpdateHistoryTests
{
    private static readonly DateTimeOffset Started = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Checked = Started.AddSeconds(2);
    private static readonly Guid UpdateId = Guid.Parse("968a906e-1563-42f0-83c0-5dc30abda611");
    private static Dictionary<string, object?> Field(object? value, string state = "Known") => new() { ["State"] = state, ["Value"] = value };
    private static Dictionary<string, object?> Row(int index = 0) => new()
    {
        ["Index"] = index, ["Title"] = Field("Security update"), ["DateUtc"] = Field(Started.AddDays(-1).UtcDateTime.ToOADate()),
        ["Operation"] = Field(1), ["ResultCode"] = Field(2), ["HResult"] = Field(0), ["UpdateId"] = Field(UpdateId.ToString("D")), ["Revision"] = Field(3)
    };
    private static Dictionary<string, object?> Root(int total, params Dictionary<string, object?>[] rows) => new()
    {
        ["FormatVersion"] = 1, ["QueryStartedUtc"] = Started.ToString("O"), ["CheckedUtc"] = Checked.ToString("O"),
        ["TotalBefore"] = total, ["RequestedCount"] = Math.Min(total, WindowsUpdateHistoryPolicy.MaximumEntries), ["Entries"] = rows
    };
    private static CheckResult<WindowsUpdateHistoryReport> Parse(Dictionary<string, object?> root) => WindowsUpdateHistoryPolicy.Parse(JsonSerializer.Serialize(root));
    private static WindowsUpdateHistoryReport Accepted(CheckResult<WindowsUpdateHistoryReport> result)
    {
        Assert.Equal(CheckState.Partial, result.State);
        Assert.True(WindowsUpdateHistoryPolicy.IsConsistent(result, Checked));
        return Assert.Single(result.Items);
    }
    private static void Rejected(string json)
    {
        var result = WindowsUpdateHistoryPolicy.Parse(json);
        Assert.Equal(CheckState.Unknown, result.State); Assert.Empty(result.Items);
    }

    [Fact]
    public void RepeatedAttemptsForSameIdentityRemainDistinctAndHistorical()
    {
        var second = Row(1); second["Operation"] = Field(2); second["ResultCode"] = Field(4); second["HResult"] = Field(unchecked((int)0x80240022));
        var report = Accepted(Parse(Root(2, Row(), second)));
        Assert.Equal(2, report.Entries.Count);
        Assert.Equal(UpdateId, report.Entries[0].UpdateId.Value);
        Assert.Equal(UpdateId, report.Entries[1].UpdateId.Value);
        Assert.Equal(2, report.Entries[0].ResultCode.Value); Assert.Equal(4, report.Entries[1].ResultCode.Value);
        Assert.Equal(unchecked((int)0x80240022), report.Entries[1].HResult.Value);
    }

    [Fact]
    public void EmptyHistoryDoesNotBecomeCurrentInstallationOrUpdateCoverage()
    {
        var result = Parse(Root(0)); var report = Accepted(result);
        Assert.Empty(report.Entries); Assert.Equal(0, report.RequestedCount);
        Assert.DoesNotContain("up to date", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("non-atomic", result.Message);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(8, 2)]
    [InlineData(500, 128)]
    public void FewerRowsThanCountArePermittedWithoutInventingAnAtomicSnapshot(int total, int returned)
    {
        var rows = Enumerable.Range(0, returned).Select(Row).ToArray();
        var report = Accepted(Parse(Root(total, rows)));
        Assert.Equal(returned, report.Entries.Count); Assert.Equal(Math.Min(total, 128), report.RequestedCount);
    }

    [Theory]
    [InlineData("TotalBefore", -1)]
    [InlineData("RequestedCount", 0)]
    [InlineData("RequestedCount", 129)]
    [InlineData("FormatVersion", 2)]
    public void InvalidRootCountsOrVersionCannotProduceHistory(string field, int value)
    {
        var root = Root(1, Row()); root[field] = value; Rejected(JsonSerializer.Serialize(root));
    }

    [Fact]
    public void ZeroCountAndRowsOrOutOfOrderIndicesAreRejected()
    {
        Rejected(JsonSerializer.Serialize(Root(0, Row())));
        Rejected(JsonSerializer.Serialize(Root(2, Row(), Row(0))));
        Rejected(JsonSerializer.Serialize(Root(2, Row(1))));
        Rejected(JsonSerializer.Serialize(Root(1, Row(), Row(1))));
    }

    [Theory]
    [InlineData("Operation", 3)]
    [InlineData("Operation", 0)]
    [InlineData("ResultCode", 6)]
    [InlineData("ResultCode", -1)]
    [InlineData("Revision", -1)]
    public void UnrecognizedProviderIntegersStayUnknownAndDoNotEraseCompanions(string name, int value)
    {
        var row = Row(); row[name] = Field(value);
        var observed = Accepted(Parse(Root(1, row))).Entries[0];
        var field = name switch { "Operation" => observed.Operation, "ResultCode" => observed.ResultCode, _ => observed.Revision };
        Assert.Equal(UpdateHistoryFieldState.Unknown, field.State); Assert.Null(field.Value);
        Assert.Equal("Security update", observed.Title.Value); Assert.Equal(UpdateId, observed.UpdateId.Value);
    }

    [Theory]
    [InlineData("Operation", "1")]
    [InlineData("ResultCode", "2")]
    [InlineData("HResult", "0x0")]
    [InlineData("DateUtc", "2026-11-11T10:00:00Z")]
    [InlineData("UpdateId", "not-a-guid")]
    public void IncorrectOptionalTypesFailThatFieldWithoutDroppingUsefulRows(string name, string value)
    {
        var row = Row(); row[name] = Field(value);
        var observed = Accepted(Parse(Root(1, row))).Entries[0];
        Assert.Equal("Security update", observed.Title.Value);
        Assert.Equal(UpdateHistoryFieldState.Failed, name switch
        {
            "Operation" => observed.Operation.State, "ResultCode" => observed.ResultCode.State,
            "HResult" => observed.HResult.State, "DateUtc" => observed.DateUtc.State, _ => observed.UpdateId.State
        });
    }

    [Theory]
    [InlineData("Title")]
    [InlineData("DateUtc")]
    [InlineData("Operation")]
    [InlineData("ResultCode")]
    [InlineData("HResult")]
    [InlineData("UpdateId")]
    [InlineData("Revision")]
    public void MissingOptionalFieldLeavesCompanionsAndOrdinalIntact(string name)
    {
        var row = Row(); row.Remove(name);
        var observed = Accepted(Parse(Root(1, row))).Entries[0];
        Assert.Equal(0, observed.Index);
        Assert.Equal(UpdateHistoryFieldState.Failed, name switch
        {
            "Title" => observed.Title.State, "DateUtc" => observed.DateUtc.State, "Operation" => observed.Operation.State,
            "ResultCode" => observed.ResultCode.State, "HResult" => observed.HResult.State, "UpdateId" => observed.UpdateId.State, _ => observed.Revision.State
        });
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("Failed")]
    public void GetterUnknownAndGetterFailureRemainDistinct(string state)
    {
        var row = Row(); row["HResult"] = Field(null, state);
        var observed = Accepted(Parse(Root(1, row))).Entries[0];
        Assert.Equal(Enum.Parse<UpdateHistoryFieldState>(state), observed.HResult.State); Assert.Null(observed.HResult.Value);
        row["HResult"] = Field(0, state);
        Assert.Equal(UpdateHistoryFieldState.Failed, Accepted(Parse(Root(1, row))).Entries[0].HResult.State);
    }

    [Theory]
    [InlineData(0.0, 1899, 12, 30)]
    [InlineData(-1.25, 1899, 12, 29)]
    [InlineData(2.25, 1900, 1, 1)]
    public void OleDateUsesUtcFieldsRatherThanTheLocalTimezone(double value, int year, int month, int day)
    {
        var converted = WindowsUpdateHistoryPolicy.FromOleDateUtc(value);
        Assert.NotNull(converted); Assert.Equal(TimeSpan.Zero, converted.Value.Offset);
        Assert.Equal(year, converted.Value.Year); Assert.Equal(month, converted.Value.Month); Assert.Equal(day, converted.Value.Day);
        Assert.Equal(DateTime.SpecifyKind(DateTime.FromOADate(value), DateTimeKind.Utc), converted.Value.UtcDateTime);
    }

    [Fact]
    public void AnUnspecifiedOrLocalManagedKindCannotShiftTheDocumentedUtcDate()
    {
        var fields = new DateTime(2026, 7, 12, 13, 45, 29);
        foreach (DateTimeKind kind in Enum.GetValues<DateTimeKind>())
        {
            double oa = DateTime.SpecifyKind(fields, kind).ToOADate();
            Assert.Equal(new DateTimeOffset(2026, 7, 12, 13, 45, 29, TimeSpan.Zero), WindowsUpdateHistoryPolicy.FromOleDateUtc(oa));
        }
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-657435.0)]
    [InlineData(2958466.0)]
    public void InvalidOleDatesNeverBecomeCurrentTime(double value) => Assert.Null(WindowsUpdateHistoryPolicy.FromOleDateUtc(value));

    [Fact]
    public void ADateOutsideOleRangeFailsOnlyTheDateField()
    {
        var row = Row(); row["DateUtc"] = Field(1e300);
        var observed = Accepted(Parse(Root(1, row))).Entries[0];
        Assert.Equal(UpdateHistoryFieldState.Failed, observed.DateUtc.State); Assert.Null(observed.DateUtc.Value);
        Assert.Equal(2, observed.ResultCode.Value);
    }

    [Fact]
    public void QueryTimesRequireUtcOrderingAndActualReceipt()
    {
        var root = Root(1, Row()); root["CheckedUtc"] = Started.AddSeconds(-1).ToString("O"); Rejected(JsonSerializer.Serialize(root));
        root = Root(1, Row()); root["QueryStartedUtc"] = Started.ToOffset(TimeSpan.FromHours(2)).ToString("O"); Rejected(JsonSerializer.Serialize(root));
        Assert.False(WindowsUpdateHistoryPolicy.IsConsistent(Parse(Root(1, Row())), Started));
        Assert.False(WindowsUpdateHistoryPolicy.IsConsistent(Parse(Root(1, Row())), Checked.ToOffset(TimeSpan.FromHours(-7))));
    }

    [Theory]
    [InlineData("2026-11-12T10:00:00")]
    [InlineData("2026-11-12T10:00:00-00:00")]
    public void AmbiguousOrUnspecifiedRootZonesCannotUseTheTestHostsLocalZone(string timestamp)
    {
        var root = Root(1, Row()); root["QueryStartedUtc"] = timestamp;
        Rejected(JsonSerializer.Serialize(root));
    }

    [Theory]
    [InlineData("bad\ntext")]
    [InlineData("bad\u202Etext")]
    [InlineData("   ")]
    public void ProviderTitlesCannotCarryControlsOrDirectionOverrides(string title)
    {
        var row = Row(); row["Title"] = Field(title);
        Assert.Equal(UpdateHistoryFieldState.Failed, Accepted(Parse(Root(1, row))).Entries[0].Title.State);
    }

    [Fact]
    public void TitleBoundAndUnicodeAreHandledWithoutEchoingMalformedText()
    {
        var row = Row(); row["Title"] = Field("Mise à jour 更新");
        Assert.Equal("Mise à jour 更新", Accepted(Parse(Root(1, row))).Entries[0].Title.Value);
        row["Title"] = Field(new string('x', 1025));
        Assert.Equal(UpdateHistoryFieldState.Failed, Accepted(Parse(Root(1, row))).Entries[0].Title.State);
    }

    [Fact]
    public void AmbiguousSchemasAndExcessiveTransportAreRejectedWithoutPartialInventedRows()
    {
        string json = JsonSerializer.Serialize(Root(1, Row()));
        Rejected(json.Replace("\"FormatVersion\":1", "\"FormatVersion\":1,\"FormatVersion\":1", StringComparison.Ordinal));
        Rejected(json.Replace("\"Index\":0", "\"Index\":0,\"Other\":0", StringComparison.Ordinal));
        Rejected(json.Replace("\"State\":\"Known\"", "\"State\":\"Known\",\"State\":\"Known\"", StringComparison.Ordinal));
        Rejected("{broken"); Rejected(new string(' ', WindowsUpdateHistoryPolicy.MaximumCharacters + 1));
        Rejected(json[..^2]);
    }

    [Fact]
    public void PositiveResultAndRowsCannotBeMutatedThroughTheReturnedLists()
    {
        var result = Parse(Root(1, Row())); var report = Accepted(result);
        Assert.Throws<NotSupportedException>(() => ((IList<WindowsUpdateHistoryEntry>)report.Entries).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<WindowsUpdateHistoryReport>)result.Items).Clear());
        var mutable = report.Entries.ToList();
        var copy = new WindowsUpdateHistoryReport(report.QueryStartedUtc, report.CheckedUtc, report.TotalBefore, report.RequestedCount, mutable);
        mutable.Clear(); Assert.Single(copy.Entries);
    }

    [Fact]
    public void TypedInjectedReportsCannotRelabelHistoryAsAvailabilityOrKeepUnknownValues()
    {
        var result = Parse(Root(1, Row())); var report = Accepted(result);
        Assert.False(WindowsUpdateHistoryPolicy.IsConsistent(result with { State = CheckState.Available }, Checked));
        var badRow = report.Entries[0] with { ResultCode = new(UpdateHistoryFieldState.Unknown, 2) };
        var forged = new WindowsUpdateHistoryReport(Started, Checked, 1, 1, [badRow]);
        Assert.False(WindowsUpdateHistoryPolicy.IsConsistent(new(CheckState.Partial, "sample", [forged]), Checked));
    }
}
