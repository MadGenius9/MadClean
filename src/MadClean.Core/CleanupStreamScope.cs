using System.Buffers.Binary;
using System.Text;

namespace MadClean.Core;

public enum CleanupStreamScopeState { Unknown, UnnamedDataOnly, AlternateStreamsObserved }
public enum CleanupStreamScopeReason
{ NotChecked, Observed, NativeQueryFailed, NoStreamsReturned, MalformedMetadata, LimitExceeded, DefaultSizeMismatch, ChangedDuringRead }
public sealed record CleanupStreamScopeObservation(CleanupStreamScopeState State, CleanupStreamScopeReason Reason,
    int? StreamCount, long? UnnamedStreamBytes, int QueryCount, int? FirstNativeError, int? SecondNativeError, bool? BookendsMatch);

// Descriptors are retained privately for exact bookend comparisons, never exposed as names,
// raw UTF16, paths or persisted fingerprints. This is metadata, never content/deletion proof.
public sealed class CleanupStreamSnapshot
{
    public CleanupStreamScopeState State { get; }
    public CleanupStreamScopeReason Reason { get; }
    public int? StreamCount { get; }
    public long? UnnamedStreamBytes { get; }
    internal IReadOnlyList<(string Name, long Bytes, long Allocation)> Entries { get; }
    internal CleanupStreamSnapshot(CleanupStreamScopeState state, CleanupStreamScopeReason reason,
        (string, long, long)[] entries, long? unnamedBytes)
    {
        State = state; Reason = reason; Entries = Array.AsReadOnly(entries);
        StreamCount = state == CleanupStreamScopeState.Unknown ? null : entries.Length; UnnamedStreamBytes = unnamedBytes;
    }
}

public static class CleanupStreamScope
{
    public const int MaximumBufferBytes = 65_536, MaximumStreams = 32;
    private const int HeaderBytes = 24, MaximumNameBytes = 8192;
    private static readonly UnicodeEncoding StrictUtf16 = new(false, false, true);
    public static CleanupStreamSnapshot NoStreamsReturned() => Unknown(CleanupStreamScopeReason.NoStreamsReturned);
    private static CleanupStreamSnapshot Unknown(CleanupStreamScopeReason reason) => new(CleanupStreamScopeState.Unknown, reason, [], null);

    /// <summary>Decode the initialized buffer, not a claimed returned-byte count. Names are counted UTF16, never NUL-scanned.
    /// The final offset must be zero. Gaps/tail must remain zero: a conservative policy because the API supplies no written length
    /// and does not guarantee padding contents. Unsupported/nonzero padding remains Unknown, not evidence of an invalid Windows file.</summary>
    public static CleanupStreamSnapshot Decode(ReadOnlySpan<byte> buffer, long expectedUnnamedBytes, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (expectedUnnamedBytes < 0 || buffer.Length < HeaderBytes || buffer.Length > MaximumBufferBytes)
            return Unknown(buffer.Length > MaximumBufferBytes ? CleanupStreamScopeReason.LimitExceeded : CleanupStreamScopeReason.MalformedMetadata);
        List<(string Name, long Bytes, long Allocation)> entries = [];
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int offset = 0; long? unnamed = null;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (entries.Count >= MaximumStreams) return Unknown(CleanupStreamScopeReason.LimitExceeded);
            if ((offset & 7) != 0 || offset > buffer.Length - HeaderBytes) return Unknown(CleanupStreamScopeReason.MalformedMetadata);
            var header = buffer[offset..];
            uint next = BinaryPrimitives.ReadUInt32LittleEndian(header), nameBytes = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            long size = BinaryPrimitives.ReadInt64LittleEndian(header[8..]), allocation = BinaryPrimitives.ReadInt64LittleEndian(header[16..]);
            if (nameBytes > MaximumNameBytes) return Unknown(CleanupStreamScopeReason.LimitExceeded);
            if (nameBytes == 0 || (nameBytes & 1) != 0 || nameBytes > header.Length - HeaderBytes || size < 0 || allocation < 0)
                return Unknown(CleanupStreamScopeReason.MalformedMetadata);
            int end = offset + HeaderBytes + (int)nameBytes;
            string name;
            try { name = StrictUtf16.GetString(buffer.Slice(offset + HeaderBytes, (int)nameBytes)); }
            catch (DecoderFallbackException) { return Unknown(CleanupStreamScopeReason.MalformedMetadata); }
            if (name.IndexOf('\0') >= 0 || !ShortcutPathPolicy.HasSafeUnicode(name) || name[0] != ':' || !names.Add(name))
                return Unknown(CleanupStreamScopeReason.MalformedMetadata);
            entries.Add((name, size, allocation));
            if (name == "::$DATA") unnamed = size;
            if (next == 0)
            {
                if (!Zero(buffer[end..], token)) return Unknown(CleanupStreamScopeReason.MalformedMetadata);
                break;
            }
            if ((next & 7) != 0 || next < HeaderBytes + nameBytes || next > buffer.Length - offset - HeaderBytes)
                return Unknown(CleanupStreamScopeReason.MalformedMetadata);
            int following = offset + (int)next;
            if (!Zero(buffer[end..following], token)) return Unknown(CleanupStreamScopeReason.MalformedMetadata);
            offset = following;
        }
        if (!unnamed.HasValue) return Unknown(CleanupStreamScopeReason.MalformedMetadata);
        if (unnamed != expectedUnnamedBytes) return Unknown(CleanupStreamScopeReason.DefaultSizeMismatch);
        token.ThrowIfCancellationRequested();
        var canonical = entries.OrderBy(entry => entry.Name, StringComparer.Ordinal).ToArray();
        return new(entries.Count == 1 ? CleanupStreamScopeState.UnnamedDataOnly : CleanupStreamScopeState.AlternateStreamsObserved,
            CleanupStreamScopeReason.Observed, canonical, unnamed);
    }

    public static CleanupStreamScopeObservation Compare(CleanupStreamSnapshot? first, CleanupStreamSnapshot? second,
        int? firstError = null, int? secondError = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (firstError.HasValue || secondError.HasValue)
            return new(CleanupStreamScopeState.Unknown, CleanupStreamScopeReason.NativeQueryFailed, null, null, 2, firstError, secondError, null);
        if (first == null || second == null)
            return new(CleanupStreamScopeState.Unknown, CleanupStreamScopeReason.MalformedMetadata, null, null, 2, null, null, null);
        if (first.State == CleanupStreamScopeState.Unknown || second.State == CleanupStreamScopeState.Unknown)
            return new(CleanupStreamScopeState.Unknown, first.State == CleanupStreamScopeState.Unknown ? first.Reason : second.Reason, null, null, 2, null, null, null);
        bool equal = first.Entries.Count == second.Entries.Count;
        for (int i = 0; equal && i < first.Entries.Count; i++)
        { token.ThrowIfCancellationRequested(); equal = first.Entries[i] == second.Entries[i]; }
        return equal
            ? new(first.State, CleanupStreamScopeReason.Observed, first.StreamCount, first.UnnamedStreamBytes, 2, null, null, true)
            : new(CleanupStreamScopeState.Unknown, CleanupStreamScopeReason.ChangedDuringRead, null, null, 2, null, null, false);
    }
    public static bool IsConsistent(CleanupStreamScopeObservation? observation, long expectedUnnamedBytes)
    {
        if (expectedUnnamedBytes < 0) return false;
        if (observation == null) return true; // Permitted unknown/NotChecked, never positive evidence.
        if (!Enum.IsDefined(observation.State) || !Enum.IsDefined(observation.Reason) || observation.QueryCount is < 0 or > 2) return false;
        bool errors = observation.FirstNativeError.HasValue || observation.SecondNativeError.HasValue;
        if (observation.State != CleanupStreamScopeState.Unknown)
            return observation.Reason == CleanupStreamScopeReason.Observed && observation.QueryCount == 2 && !errors && observation.BookendsMatch == true &&
                observation.UnnamedStreamBytes == expectedUnnamedBytes && (observation.State == CleanupStreamScopeState.UnnamedDataOnly
                    ? observation.StreamCount == 1 : observation.StreamCount is >= 2 and <= MaximumStreams);
        if (observation.StreamCount.HasValue || observation.UnnamedStreamBytes.HasValue || observation.Reason == CleanupStreamScopeReason.Observed) return false;
        return observation.Reason switch
        {
            CleanupStreamScopeReason.NotChecked => observation.QueryCount == 0 && !errors && observation.BookendsMatch == null,
            CleanupStreamScopeReason.NativeQueryFailed => observation.QueryCount is 1 or 2 && errors && observation.BookendsMatch == null &&
                (observation.QueryCount == 2 || !observation.SecondNativeError.HasValue),
            CleanupStreamScopeReason.ChangedDuringRead => observation.QueryCount == 2 && !errors && observation.BookendsMatch == false,
            _ => observation.QueryCount is 1 or 2 && !errors && observation.BookendsMatch == null
        };
    }

    private static bool Zero(ReadOnlySpan<byte> bytes, CancellationToken token)
    {
        for (int i = 0; i < bytes.Length; i++)
        { if ((i & 255) == 0) token.ThrowIfCancellationRequested(); if (bytes[i] != 0) return false; }
        return true;
    }
}
