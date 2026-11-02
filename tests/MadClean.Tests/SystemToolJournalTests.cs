using System.Text;
using System.Text.Json.Nodes;
using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class SystemToolJournalTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Execution = Guid.Parse("12345678-abcd-1234-abcd-123456789abc");
    private static SystemToolJournalEntry Intent => new(Execution, SystemToolOperation.DismCheckHealth, 1,
        Now.AddSeconds(-3), SystemToolJournalPhase.DispatchIntent, null, null, null, null);
    private static SystemToolJournalEntry Observed => Intent with { Sequence = 2, TimestampUtc = Now.AddSeconds(-2),
        Phase = SystemToolJournalPhase.ClientObserved, ProcessId = 123, ClientStartUtc = Now.AddSeconds(-2) };
    private static SystemToolJournalEntry Exit => Observed with { Sequence = 3, TimestampUtc = Now.AddSeconds(-1),
        Phase = SystemToolJournalPhase.ClientExited, ExitCode = 0 };
    private static byte[] Encode(params SystemToolJournalEntry[] rows)
    {
        Assert.True(SystemToolJournal.TryEncode(rows, Now, out var bytes)); return Assert.IsType<byte[]>(bytes);
    }
    private static byte[] Snapshots(params SystemToolJournalEntry[][] history) =>
        history.SelectMany(rows => Encode(rows).Append((byte)'\n')).ToArray();
    private static void Unknown(SystemToolJournalReview value)
    { Assert.Equal(SystemToolJournalState.Unknown, value.State); Assert.Empty(value.Entries); }

    [Fact]
    public void IntentAndObservationLossAreUnresolvedNeverRunningOrTerminalSuccess()
    {
        Assert.Equal(SystemToolJournalState.Unresolved, SystemToolJournal.Decode(Encode(Intent), Now).State);
        Assert.Equal(SystemToolJournalState.Unresolved, SystemToolJournal.Decode(Encode(Intent, Observed), Now).State);
        var lost = Intent with { Sequence = 2, TimestampUtc = Now, Phase = SystemToolJournalPhase.ObservationLost, NativeError = 6 };
        var review = SystemToolJournal.Decode(Encode(Intent, lost), Now);
        Assert.Equal(SystemToolJournalState.Unresolved, review.State);
        Assert.Contains("not proof that a process is running", review.Message);
        Assert.Null(review.Entries[1].ProcessId);
        Assert.Equal(6, review.Entries[1].NativeError);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void LiteralClientExitCodeRoundTripsWithoutHealthOrAuthorshipVerdict(int code)
    {
        var bytes = Encode(Intent, Observed, Exit with { ExitCode = code });
        Assert.InRange(bytes.Length, 1, SystemToolJournal.MaximumUtf8Bytes);
        var review = SystemToolJournal.Decode(bytes, Now);
        Assert.Equal(SystemToolJournalState.RecordedClientExit, review.State);
        Assert.Equal(code, review.Entries[2].ExitCode);
        Assert.Contains("not an integrity, health or repair verdict", review.Message);
        Assert.Contains("does not prove authorship", review.Message);
    }

    [Fact]
    public void DenialRequiresActual1223AndCannotCarryClientMetadata()
    {
        var denied = Intent with { Sequence = 2, TimestampUtc = Now, Phase = SystemToolJournalPhase.LaunchDenied, NativeError = 1223 };
        Assert.Equal(SystemToolJournalState.RecordedLaunchDenied, SystemToolJournal.Decode(Encode(Intent, denied), Now).State);
        foreach (var invalid in new[] { denied with { NativeError = null }, denied with { NativeError = 5 },
            denied with { ProcessId = 123 }, denied with { ExitCode = 0 }, denied with { ClientStartUtc = Now } })
            Unknown(SystemToolJournal.Review([Intent, invalid], Now));
    }

    [Fact]
    public void TerminalRecordsMustRetainExactPidOperationExecutionAndNullableStart()
    {
        SystemToolJournalEntry[] invalid =
        [ Exit with { ProcessId = 124 }, Exit with { ExecutionId = Guid.NewGuid() },
          Exit with { Operation = SystemToolOperation.SfcVerifyOnly }, Exit with { ClientStartUtc = null },
          Exit with { ClientStartUtc = Observed.ClientStartUtc!.Value.AddTicks(1) },
          Exit with { ExitCode = null }, Exit with { NativeError = 0 }, Exit with { Sequence = 2 },
          Exit with { TimestampUtc = Intent.TimestampUtc.AddTicks(-1) } ];
        foreach (var row in invalid) Unknown(SystemToolJournal.Review([Intent, Observed, row], Now));
        var noStartObserved = Observed with { ClientStartUtc = null };
        var noStartExit = Exit with { ClientStartUtc = null };
        Assert.Equal(SystemToolJournalState.RecordedClientExit, SystemToolJournal.Review([Intent, noStartObserved, noStartExit], Now).State);
        Unknown(SystemToolJournal.Review([Intent, noStartObserved, Exit], Now));
        var lost = Exit with { Phase = SystemToolJournalPhase.ObservationLost, ExitCode = null, NativeError = 6 };
        Assert.Equal(SystemToolJournalState.Unresolved, SystemToolJournal.Review([Intent, Observed, lost], Now).State);
    }

    [Fact]
    public void UnsupportedSequencesAndMalformedTypedMetadataCannotEncode()
    {
        SystemToolJournalEntry[][] invalid =
        [ [], [Observed], [Intent, Exit], [Intent, Observed, Exit, Exit],
          [Intent with { ExecutionId = Guid.Empty }], [Intent with { Operation = (SystemToolOperation)999 }],
          [Intent with { ProcessId = 1 }], [Intent with { NativeError = 0 }],
          [Intent, Observed with { ProcessId = 0 }], [Intent, Observed with { TimestampUtc = Now.AddTicks(1) }],
          [Intent, Observed with { ClientStartUtc = Now }],
          [Intent, Observed with { Phase = (SystemToolJournalPhase)999 }],
          [Intent, Observed with { ClientStartUtc = Observed.TimestampUtc.ToOffset(TimeSpan.FromHours(1)) }] ];
        foreach (var rows in invalid)
        {
            Unknown(SystemToolJournal.Review(rows, Now));
            Assert.False(SystemToolJournal.TryEncode(rows, Now, out var bytes)); Assert.Null(bytes);
        }
        Unknown(SystemToolJournal.Review([Intent], default));
    }

    [Fact]
    public void TruncationInvalidUtf8TrailingDataAndOversizeNeverBecomeAnEmptySuccess()
    {
        var bytes = Encode(Intent, Observed, Exit);
        foreach (var payload in new[] { Array.Empty<byte>(), bytes[..^1], bytes[..(bytes.Length / 2)],
            bytes.Concat(new byte[] { 0xff }).ToArray(), Encoding.UTF8.GetBytes("{}{}"), new byte[4097] })
            Unknown(SystemToolJournal.Decode(payload, Now));
    }

    [Fact]
    public void ExactSchemaRejectsExtraMissingDuplicateAndUnknownVersionFields()
    {
        var bytes = Encode(Intent);
        JsonObject Root() => JsonNode.Parse(bytes)!.AsObject();
        var extra = Root(); extra["Path"] = "private-sentinel";
        var missing = Root(); missing["Entries"]![0]!.AsObject().Remove("NativeError");
        var unknownVersion = Root(); unknownVersion["Version"] = 2;
        var extraRow = Root(); extraRow["Entries"]![0]!["Output"] = "private-sentinel";
        foreach (var value in new[] { extra, missing, unknownVersion, extraRow })
        {
            var result = SystemToolJournal.Decode(Encoding.UTF8.GetBytes(value.ToJsonString()), Now);
            Unknown(result); Assert.DoesNotContain("private-sentinel", result.Message);
        }
        string json = Encoding.UTF8.GetString(bytes);
        Unknown(SystemToolJournal.Decode(Encoding.UTF8.GetBytes(json.Replace("\"Version\":1", "\"Version\":1,\"Version\":1", StringComparison.Ordinal)), Now));
        Unknown(SystemToolJournal.Decode(Encoding.UTF8.GetBytes(json.Replace("\"NativeError\":null", "\"NativeError\":null,\"NativeError\":null", StringComparison.Ordinal)), Now));
    }

    [Fact]
    public void NoncanonicalEnumGuidOrDateFormsAreRejected()
    {
        var bytes = Encode(Intent);
        byte[] Alter(string field, string value)
        {
            var root = JsonNode.Parse(bytes)!;
            root["Entries"]![0]![field] = value;
            return Encoding.UTF8.GetBytes(root.ToJsonString());
        }
        foreach (var altered in new[]
        {
            Alter("Operation", "dismcheckhealth"), Alter("Operation", "0"),
            Alter("ExecutionId", Execution.ToString("D").ToUpperInvariant()),
            Alter("TimestampUtc", "2026-11-02T11:59:57.0000000Z"),
            Alter("TimestampUtc", "2026-11-02T11:59:57+00:00")
        }) Unknown(SystemToolJournal.Decode(altered, Now));
    }

    [Fact]
    public void ReviewCopiesInputAndExposesNoMutableArrayOrAppendableCollection()
    {
        var rows = new[] { Intent, Observed, Exit };
        var review = SystemToolJournal.Review(rows, Now);
        rows[2] = Exit with { ProcessId = 999 };
        Assert.Equal(123, review.Entries[2].ProcessId);
        var list = Assert.IsAssignableFrom<IList<SystemToolJournalEntry>>(review.Entries);
        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => list.Add(Intent));
        Assert.Throws<NotSupportedException>(() => list[0] = Exit);
    }

    [Fact]
    public void AppendOnlySnapshotsRetainEntireHistoryAndReturnOnlyFinalRecordedState()
    {
        var pending = SystemToolJournal.DecodeSnapshots(Snapshots([Intent], [Intent, Observed]), Now);
        Assert.Equal(SystemToolJournalState.Unresolved, pending.State); Assert.Equal(2, pending.Entries.Count);
        var terminal = SystemToolJournal.DecodeSnapshots(Snapshots([Intent], [Intent, Observed], [Intent, Observed, Exit]), Now);
        Assert.Equal(SystemToolJournalState.RecordedClientExit, terminal.State);
        Assert.Equal(0, terminal.Entries[2].ExitCode);
        var denied = Intent with { Sequence = 2, TimestampUtc = Now, Phase = SystemToolJournalPhase.LaunchDenied, NativeError = 1223 };
        Assert.Equal(SystemToolJournalState.RecordedLaunchDenied,
            SystemToolJournal.DecodeSnapshots(Snapshots([Intent], [Intent, denied]), Now).State);
        var lost = Exit with { Phase = SystemToolJournalPhase.ObservationLost, ExitCode = null };
        Assert.Equal(SystemToolJournalState.Unresolved,
            SystemToolJournal.DecodeSnapshots(Snapshots([Intent], [Intent, Observed], [Intent, Observed, lost]), Now).State);
    }

    [Fact]
    public void InvalidTailNeverFallsBackToAnEarlierValidPrefixOrRecordedExit()
    {
        var valid = Snapshots([Intent], [Intent, Observed], [Intent, Observed, Exit]);
        byte[][] malformed =
        [ valid[..^1], valid[..^8], valid.Concat(Encoding.UTF8.GetBytes("invalid\n")).ToArray(),
          valid.Append((byte)'\n').ToArray(), valid.Concat(new byte[] { 0xff, (byte)'\n' }).ToArray(),
          Array.Empty<byte>(), new byte[] { (byte)'\n' }, Encoding.UTF8.GetBytes(" \n"), new byte[4097] ];
        foreach (var bytes in malformed) Unknown(SystemToolJournal.DecodeSnapshots(bytes, Now));
    }

    [Fact]
    public void MissingRepeatedSkippedOrTerminalRewrittenSnapshotsAreRejected()
    {
        byte[][] invalid =
        [ Snapshots([Intent, Observed]), Snapshots([Intent], [Intent]),
          Snapshots([Intent], [Intent, Observed, Exit]),
          Snapshots([Intent], [Intent, Observed], [Intent, Observed]),
          Snapshots([Intent], [Intent, Observed], [Intent, Observed, Exit], [Intent, Observed, Exit with { ExitCode = 1 }]) ];
        foreach (var bytes in invalid) Unknown(SystemToolJournal.DecodeSnapshots(bytes, Now));
    }

    [Fact]
    public void StructurallyValidLaterEnvelopeCannotChangeAnyEarlierRecordedFact()
    {
        var changedIntent = Intent with { TimestampUtc = Intent.TimestampUtc.AddTicks(-1) };
        Unknown(SystemToolJournal.DecodeSnapshots(Snapshots([Intent], [changedIntent, Observed]), Now));
        var changedPidObserved = Observed with { ProcessId = 124 };
        var changedPidExit = Exit with { ProcessId = 124 };
        Unknown(SystemToolJournal.DecodeSnapshots(Snapshots([Intent], [Intent, Observed], [Intent, changedPidObserved, changedPidExit]), Now));
        var noStartObserved = Observed with { ClientStartUtc = null };
        var noStartExit = Exit with { ClientStartUtc = null };
        Unknown(SystemToolJournal.DecodeSnapshots(Snapshots([Intent], [Intent, Observed], [Intent, noStartObserved, noStartExit]), Now));
    }

    [Fact]
    public void WholeSnapshotLogByteCeilingAppliesEvenWhenIndividualEnvelopeWouldParse()
    {
        var envelope = Encode(Intent);
        byte[] Padded(int length)
        {
            var bytes = Enumerable.Repeat((byte)' ', length).ToArray();
            envelope.CopyTo(bytes, 0); bytes[^1] = (byte)'\n'; return bytes;
        }
        Assert.Equal(SystemToolJournalState.Unresolved,
            SystemToolJournal.DecodeSnapshots(Padded(SystemToolJournal.MaximumUtf8Bytes), Now).State);
        Unknown(SystemToolJournal.DecodeSnapshots(Padded(SystemToolJournal.MaximumUtf8Bytes + 1), Now));
    }
}
