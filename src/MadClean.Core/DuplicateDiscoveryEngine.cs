using System.Buffers;
using System.Security.Cryptography;

namespace MadClean.Core;

public sealed record DuplicateScanItem(string Path, bool IsFile, string? Reason = null, bool LimitReached = false);
public sealed record DuplicateFileStamp(string Identity, long Bytes, long LastWrite, long ChangeTime, uint LinkCount);

// Production uses WindowsDuplicateInventory's Windows handle adapter. Test adapters are explicitly
// supplied by portable tests; there is no sample/fallback adapter in the production application.
public interface IDuplicateReadHandle : IDisposable
{
    DuplicateFileStamp Stamp { get; }
    Stream Content { get; }
    bool IsStable();
}
public interface IDuplicateFileAccess
{
    IEnumerable<DuplicateScanItem> Enumerate(string root, CancellationToken token);
    IDuplicateReadHandle Open(string path, bool content, CancellationToken token);
}
public sealed class DuplicateReadException(string reason) : IOException(reason) { }

/// <summary>Bounded, ephemeral content observations. This engine does not authorize any removal.</summary>
public sealed class DuplicateDiscoveryEngine
{
    private readonly int maxFiles;
    private readonly int maxOpenFiles;
    private readonly long maxFileBytes;
    private readonly long maxReadBytes;
    private readonly int maxIssues;
    private readonly Func<Stream, CancellationToken, byte[]> hash;

    public DuplicateDiscoveryEngine(int maxFiles = 512, int maxOpenFiles = 128,
        long maxFileBytes = 67_108_864, long maxReadBytes = 536_870_912, int maxIssues = 200,
        Func<Stream, CancellationToken, byte[]>? hashForTests = null)
    {
        if (maxFiles < 1 || maxFiles > 10_000 || maxOpenFiles < 2 || maxOpenFiles > 512 ||
            maxFileBytes < 0 || maxReadBytes < 0 || maxIssues < 1 || maxIssues > 1000)
            throw new ArgumentOutOfRangeException(nameof(maxFiles));
        this.maxFiles = maxFiles; this.maxOpenFiles = maxOpenFiles; this.maxFileBytes = maxFileBytes;
        this.maxReadBytes = maxReadBytes; this.maxIssues = maxIssues;
        hash = hashForTests ?? Sha256;
    }

    public CheckResult<DuplicateReport> Scan(string root, IDuplicateFileAccess access, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        List<FileReviewIssue> issues = [];
        List<(string Path, DuplicateFileStamp Stamp)> observed = [];
        List<OpenFile> opened = [];
        List<Group> groups = [];
        int visited = 0, skipped = 0; bool truncated = false, stopComparisons = false;
        var budget = new ReadBudget(maxReadBytes);
        void Issue(string path, string reason)
        {
            if (issues.Count < maxIssues) issues.Add(new(path, reason));
            else { truncated = true; stopComparisons = true; }
        }
        try
        {
            try
            {
                foreach (var item in access.Enumerate(root, token))
                {
                    token.ThrowIfCancellationRequested();
                    if (truncated) break;
                    if (item.IsFile && ++visited > maxFiles)
                    { visited--; truncated = true; Issue(item.Path, "The file-count limit was reached. Select a smaller folder; remaining files were not checked."); break; }
                    if (item.Reason != null)
                    { if (item.IsFile) skipped++; Issue(item.Path, item.Reason); if (item.LimitReached) truncated = true; continue; }
                    if (!item.IsFile) continue;
                    try
                    {
                        using var metadata = access.Open(item.Path, content: false, token);
                        var stamp = metadata.Stamp;
                        if (!Valid(stamp) || !metadata.IsStable()) throw new DuplicateReadException("File identity or metadata could not be established consistently; skipped.");
                        if (stamp.Bytes > maxFileBytes)
                        { skipped++; Issue(item.Path, "The per-file content limit was exceeded; contents were not read."); continue; }
                        observed.Add((item.Path, stamp));
                    }
                    catch (Exception ex) when (ReadError(ex))
                    { skipped++; Issue(item.Path, SafeReason(ex)); }
                }
            }
            catch (Exception ex) when (ReadError(ex)) { Issue(root, SafeReason(ex)); }

            foreach (var sizeGroup in observed.GroupBy(file => file.Stamp.Bytes).Where(group => group.Count() > 1))
            {
                foreach (var file in sizeGroup)
                {
                    token.ThrowIfCancellationRequested();
                    if (stopComparisons) break;
                    IDuplicateReadHandle? content = null;
                    try
                    {
                        using var metadata = access.Open(file.Path, content: false, token);
                        if (metadata.Stamp != file.Stamp || !metadata.IsStable()) throw new DuplicateReadException("File changed since metadata enumeration; not compared.");
                        var same = opened.FirstOrDefault(candidate => candidate.Handle.Stamp.Identity == metadata.Stamp.Identity);
                        if (same != null)
                        {
                            if (same.Handle.Stamp != metadata.Stamp || !same.Handle.IsStable()) throw new DuplicateReadException("Same-file alias metadata changed; relationship not verified.");
                            same.Aliases.Add(file.Path);
                            continue;
                        }
                        if (opened.Count >= maxOpenFiles)
                        { truncated = true; stopComparisons = true; Issue(file.Path, "The stable-open-file limit was reached. Select a smaller folder; remaining candidates were not compared."); break; }
                        content = access.Open(file.Path, content: true, token);
                        if (content.Stamp != metadata.Stamp || !content.IsStable()) throw new DuplicateReadException("The opened object changed identity or metadata; contents were not trusted.");
                        content.Content.Position = 0;
                        var digest = Convert.ToHexString(hash(new MeteredStream(content.Content, budget, token), token));
                        if (!content.IsStable()) throw new DuplicateReadException("File changed while hashing; no content match was recorded.");
                        var candidate = new OpenFile(file.Path, content, digest); content = null;
                        opened.Add(candidate);
                        var matches = groups.Where(group => group.Reference.Handle.Stamp.Bytes == candidate.Handle.Stamp.Bytes && group.Reference.Digest == digest);
                        Group? equal = null;
                        foreach (var group in matches)
                        {
                            if (!group.Reference.Handle.IsStable()) { group.Invalid = true; continue; }
                            if (EqualBytes(new MeteredStream(group.Reference.Handle.Content, budget, token), new MeteredStream(candidate.Handle.Content, budget, token), token))
                            { equal = group; break; }
                        }
                        if (!candidate.Handle.IsStable()) throw new DuplicateReadException("File changed during comparison; content verification was discarded.");
                        if (equal == null) { equal = new(candidate); groups.Add(equal); }
                        else equal.Files.Add(candidate);
                    }
                    catch (WorkLimitException)
                    { truncated = true; stopComparisons = true; Issue(file.Path, "The aggregate content-read limit was reached. Remaining data was not compared; choose a smaller scope."); }
                    catch (Exception ex) when (ReadError(ex))
                    { skipped++; Issue(file.Path, SafeReason(ex)); }
                    finally { content?.Dispose(); }
                }
                if (stopComparisons) break;
            }

            List<DuplicateEntry> entries = [];
            int number = 0;
            foreach (var group in groups)
            {
                token.ThrowIfCancellationRequested();
                bool stable = !group.Invalid;
                foreach (var file in group.Files)
                {
                    try { stable &= file.Handle.IsStable(); }
                    catch (Exception ex) when (ReadError(ex)) { stable = false; }
                }
                if (!stable)
                { Issue(group.Reference.Path, "A group member changed or could not be revalidated. The affected group was discarded."); continue; }
                foreach (var file in group.Files)
                foreach (var alias in file.Aliases.ToArray())
                {
                    try
                    {
                        using var metadata = access.Open(alias, content: false, token);
                        if (metadata.Stamp != file.Handle.Stamp || !metadata.IsStable()) throw new DuplicateReadException("Alias changed.");
                    }
                    catch (Exception ex) when (ReadError(ex))
                    { file.Aliases.Remove(alias); skipped++; Issue(alias, "A hard-link alias path changed or could not be revalidated; alias observation discarded."); }
                }
                bool independent = group.Files.Count > 1;
                if (!independent && group.Reference.Aliases.Count == 0) continue;
                string time = DateTimeOffset.UtcNow.ToString("O");
                string groupId = (independent ? "D" : "A") + (++number).ToString("D3");
                foreach (var file in group.Files)
                {
                    string verification = independent
                        ? $"Full SHA-256 narrowing + complete byte comparison; stable handles rechecked {time}; no ADS present. Observation only, not deletion eligibility."
                        : $"Same-file handle identity observed {time}; no independent duplicate copy established.";
                    entries.Add(new(groupId, file.Path, file.Handle.Stamp.Bytes, file.Handle.Stamp.Identity,
                        $"{(independent ? "Independent file object" : "File object with aliases")}; {file.Handle.Stamp.LinkCount} links reported", verification));
                    foreach (var alias in file.Aliases)
                        entries.Add(new(groupId, alias, file.Handle.Stamp.Bytes, file.Handle.Stamp.Identity,
                            "Same-file hard-link alias, not an independent copy", $"128-bit handle identity + volume verified {time}; no additional recoverable-byte claim."));
                }
            }
            token.ThrowIfCancellationRequested();
            string coverage = $"Explicit selected scope; {visited} file candidates visited, {skipped} files skipped, {budget.Consumed} content bytes read. " +
                "Only supported stable file identities with one unnamed data stream are compared. Logical bytes are not physical/reclaimable bytes. " +
                "Exclusions, failures and limits leave coverage incomplete; no groups does not prove no duplicates. No hashes or contents persist; no removal performed.";
            var report = new DuplicateReport(entries, issues, visited, skipped, truncated, coverage);
            return new(issues.Count > 0 || truncated ? CheckState.Partial : CheckState.Available,
                "Read-only duplicate observations. Review scope, identities, verification and limitations; no item is classified as junk or selected for removal.", [report]);
        }
        finally { foreach (var file in opened) file.Handle.Dispose(); }
    }

    private static bool Valid(DuplicateFileStamp stamp) => !string.IsNullOrWhiteSpace(stamp.Identity) && stamp.Bytes >= 0 && stamp.LinkCount > 0;
    private static bool ReadError(Exception ex) => ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException or InvalidOperationException;
    private static string SafeReason(Exception ex) => ex is DuplicateReadException ? ex.Message : "File or folder is inaccessible, unsupported, locked or changed; no complete comparison was established.";
    private static byte[] Sha256(Stream stream, CancellationToken token)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65_536]; int count;
        while ((count = stream.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); digest.AppendData(buffer, 0, count); }
        return digest.GetHashAndReset();
    }
    private static bool EqualBytes(Stream left, Stream right, CancellationToken token)
    {
        left.Position = right.Position = 0;
        var a = new byte[65_536]; var b = new byte[65_536];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            int count = left.Read(a);
            int other = 0;
            while (other < count)
            {
                int read = right.Read(b.AsSpan(other, count - other));
                if (read == 0) return false;
                other += read;
            }
            if (count == 0) { int extra = right.ReadByte(); return extra < 0; }
            if (!a.AsSpan(0, count).SequenceEqual(b.AsSpan(0, count))) return false;
        }
    }
    private sealed class ReadBudget(long maximum)
    {
        public long Consumed { get; private set; }
        public int Allowed(int requested)
        {
            long remaining = maximum - Consumed;
            if (remaining <= 0) throw new WorkLimitException();
            return (int)Math.Min(requested, remaining);
        }
        public void Add(int count) => Consumed += count;
    }
    private sealed class MeteredStream(Stream source, ReadBudget budget, CancellationToken token) : Stream
    {
        public override bool CanRead => source.CanRead;
        public override bool CanSeek => source.CanSeek;
        public override bool CanWrite => false;
        public override long Length => source.Length;
        public override long Position { get => source.Position; set => source.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            token.ThrowIfCancellationRequested();
            if (buffer.Length == 0 || source.Position >= source.Length) return 0;
            int allowed = budget.Allowed(buffer.Length);
            var temporary = ArrayPool<byte>.Shared.Rent(allowed);
            try
            {
                // Windows supplies an overlapped FileStream, so the token can cancel pending native IO.
                int read = source.ReadAsync(temporary.AsMemory(0, allowed), token).AsTask().GetAwaiter().GetResult();
                temporary.AsSpan(0, read).CopyTo(buffer);
                budget.Add(read);
                return read;
            }
            finally { ArrayPool<byte>.Shared.Return(temporary, clearArray: true); }
        }
        public override long Seek(long offset, SeekOrigin origin) => source.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class WorkLimitException : Exception { }
    private sealed class OpenFile(string path, IDuplicateReadHandle handle, string digest)
    {
        public string Path { get; } = path;
        public IDuplicateReadHandle Handle { get; } = handle;
        public string Digest { get; } = digest;
        public List<string> Aliases { get; } = [];
    }
    private sealed class Group(OpenFile reference)
    {
        public OpenFile Reference { get; } = reference;
        public List<OpenFile> Files { get; } = [reference];
        public bool Invalid { get; set; }
    }
}
