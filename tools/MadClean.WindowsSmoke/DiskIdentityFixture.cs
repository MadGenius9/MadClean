using System.ComponentModel;
using System.IO;
using System.Text;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.WindowsSmoke;

internal static partial class NativeFixtures
{
    private static void DiskIdentity(string fixture, SmokeReport report, CancellationToken token)
    {
        const string check = "disk-identity-fixture";
        const string allocationCheck = "disk-allocation-metadata-fixture";
        string scope = Path.Combine(fixture, "disk-identity");
        Directory.CreateDirectory(scope);
        string first = Path.Combine(scope, "independent-one.txt"), second = Path.Combine(scope, "independent-two.txt");
        string different = Path.Combine(scope, "same-size-different.txt"), alias = Path.Combine(scope, "owned-hardlink.txt");
        byte[] bytes = Encoding.ASCII.GetBytes("MadClean disposable disk identity fixture\r\n");
        byte[] other = (byte[])bytes.Clone(); other[0] = (byte)'X';
        File.WriteAllBytes(first, bytes); File.WriteAllBytes(second, bytes); File.WriteAllBytes(different, other);
        bool linked = CreateHardLinkW(alias, first, 0);
        try
        {
            // The real logical map feeds the public native metadata adapter. Only fixture setup
            // and the final fixture-integrity audit read bytes; identity accounting must not.
            var map = Query(ct => new DiskUsageScanner().ScanAsync(scope, ct), token);
            int expectedPaths = linked ? 4 : 3;
            long expectedPathBytes = checked((long)bytes.Length * expectedPaths);
            if (map.Truncated || map.SkippedEntries != 0 || map.Issues.Count != 0 || map.Root.Bytes != expectedPathBytes)
            {
                report.Unknown(check, "The owned logical map was incomplete; no complete native file-identity accounting proof is claimed.");
                return;
            }
            var result = Query(ct => new WindowsDiskIdentityAccounting().AnalyzeAsync(map, ct), token);
            if (result.State is not (CheckState.Available or CheckState.Partial))
            {
                report.Unknown(check, $"Native identity state={result.State}; native aliases and unique logical accounting remain unverified.");
                return;
            }
            if (result.Items.Count != 1)
            {
                report.Fail(check, "A readable native identity result did not contain exactly one report.");
                return;
            }
            var data = result.Items[0];
            string[] expected = linked ? [first, second, different, alias] : [first, second, different];
            if (data.Observations.Any(row => !expected.Any(path => PathEquals(row.Path, path)) || row.SnapshotBytes != bytes.Length) ||
                data.Observations.Select(row => Path.GetFullPath(row.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != data.Observations.Count)
            {
                report.Fail(check, "Native identity observations contradicted the exact owned fixture paths/lengths or repeated a pathname. No paths/identifiers are exported.");
                return;
            }
            if (data.Observations.Count != expectedPaths)
            {
                if (result.State == CheckState.Available) report.Fail(check, "Completed native identity accounting omitted an owned fixture file.");
                else report.Unknown(check, $"Partial native identity coverage; observedPaths={data.Observations.Count}; expectedPaths={expectedPaths}; issues={data.Issues.Count}. No complete alias/accounting proof.");
                return;
            }
            var a = data.Observations.Single(row => PathEquals(row.Path, first));
            var b = data.Observations.Single(row => PathEquals(row.Path, second));
            var c = data.Observations.Single(row => PathEquals(row.Path, different));
            if (new[] { a.Identity, b.Identity, c.Identity }.Distinct(StringComparer.Ordinal).Count() != 3 ||
                b.ObservedAliasCount != 1 || c.ObservedAliasCount != 1 || b.LinkCount != 1 || c.LinkCount != 1 ||
                data.PathLogicalBytes != expectedPathBytes || data.ObservedPathLogicalBytes != expectedPathBytes ||
                data.ObservedUniqueFileLogicalBytes != checked(3L * bytes.Length))
            {
                report.Fail(check, "Independent equal-size fixture identities or known path/unique logical totals were inconsistent; content equality must not merge distinct file identities.");
                return;
            }
            if (!linked)
            {
                report.Unknown(check, "Three independent equal-size files retained distinct identities and logical totals; native hard-link creation was unavailable, so alias accounting remains unverified.");
                return;
            }
            var link = data.Observations.Single(row => PathEquals(row.Path, alias));
            if (link.Identity != a.Identity || link.LinkCount != 2 || a.LinkCount != 2 || link.ObservedAliasCount != 2 || a.ObservedAliasCount != 2)
            {
                report.Fail(check, "The actual owned hard link did not share its original identity/link-count/observed-alias relationship.");
                return;
            }
            // Fixture audit only; no contents or native identity strings go into CI evidence.
            if (!File.ReadAllBytes(first).SequenceEqual(bytes) || !File.ReadAllBytes(second).SequenceEqual(bytes) ||
                !File.ReadAllBytes(different).SequenceEqual(other) || !File.ReadAllBytes(alias).SequenceEqual(bytes))
            {
                report.Fail(check, "Owned fixture bytes changed during read-only map/identity accounting.");
                return;
            }
            report.Pass(check, $"Native identity state={result.State}; observedPaths=4; observedUniqueFiles=3; hardLinkRelationshipVerified=true; fixtureBytesUnchanged=true. Independent equal-content and same-size different-content files remain separate identities; the actual alias counts once in observed unique logical bytes. No identities/paths/contents exported; outside-scope links, physical allocation, content deduplication and deletion safety are not established.");
            CheckDiskAllocationMetadata(data, result.State, report);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            report.Unknown(check, $"Owned disk identity fixture did not complete (code 0x{ex.HResult:X8}); no native alias/accounting proof is claimed.");
        }
        finally
        {
            if (!report.HasCheck(allocationCheck))
                report.Unknown(allocationCheck, "The existing owned identity fixture did not establish complete file/alias observations. No reported allocation or physical-consumption proof was substituted.");
        }
    }

    private static void CheckDiskAllocationMetadata(DiskIdentityAccountingReport data, CheckState sourceState, SmokeReport report)
    {
        const string check = "disk-allocation-metadata-fixture";
        var groups = data.Observations.GroupBy(row => row.Identity, StringComparer.Ordinal).ToArray();
        if (groups.Length != 3 || data.Observations.Any(row => row.ReportedAllocationBytes is < 0) ||
            groups.Any(group => group.Select(row => row.ReportedAllocationBytes).Distinct().Count() != 1))
        {
            report.Fail(check, "Reported allocation metadata contradicted the actual owned identity groups or aliases. No allocation values, identities or paths are exported.");
            return;
        }
        int known = groups.Count(group => group.First().ReportedAllocationBytes.HasValue);
        int unknown = groups.Length - known;
        CheckState expected = known == 0 ? CheckState.Unknown
            : unknown != 0 || sourceState == CheckState.Partial ? CheckState.Partial : CheckState.Available;
        if (data.AllocationKnownIdentities != known || data.AllocationUnknownIdentities != unknown || data.AllocationState != expected)
        {
            report.Fail(check, "Reported allocation coverage did not match actual distinct observed identities. No allocation values or source identifiers are exported.");
            return;
        }
        if (unknown != 0)
        {
            report.Unknown(check, $"allocationState={data.AllocationState}; observedIdentityGroups=3; knownAllocationGroups={known}; unknownAllocationGroups={unknown}. Some existing production allocation observations remained unavailable; logical accounting stays separate. No physical, all-stream or reclaimable-size conclusion is made.");
            return;
        }
        report.Pass(check, $"allocationState={data.AllocationState}; observedIdentityGroups=3; knownAllocationGroups=3; unknownAllocationGroups=0; actualHardLinkAllocationConsistent=true; fixtureBytesUnchanged=true. The existing production probe's agreeing allocation bookends supplied nonnegative default-stream metadata for all observed fixture identities and aliases. This is data-flow/shape evidence, not an independent native allocation query or a physical-space measurement. No values, identities, paths, all-stream sum, reclaimable bytes or deletion permission are exported or inferred.");
    }
}
