using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.WindowsSmoke;

internal static partial class NativeFixtures
{
    // Independent marshalled declarations, never populated with real report contents. These
    // validate this CLR's layout against documented SDK offsets, not an installed C++ SDK ABI.
    [StructLayout(LayoutKind.Sequential)]
    private struct WerParameterLayout
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 129, ArraySubType = UnmanagedType.U2)] public ushort[] Name;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 260, ArraySubType = UnmanagedType.U2)] public ushort[] Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WerSignatureLayout
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 65, ArraySubType = UnmanagedType.U2)] public ushort[] EventName;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 10)] public WerParameterLayout[] Parameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WerMetadataLayout
    {
        public WerSignatureLayout Signature;
        public Guid BucketId;
        public Guid ReportId;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public ulong SizeInBytes;
    }

    private static void WerMetadata(SmokeReport report, CancellationToken token)
    {
        const string layoutCheck = "wer-v1-managed-layout", readCheck = "wer-store-read-contract", countCheck = "wer-store-count-contract";
        if (!Environment.Is64BitProcess)
        {
            report.Unknown(layoutCheck, "Only the documented supported 64-bit V1 layout is checked; this process is not 64-bit.");
            report.Unknown(readCheck, "The supported 64-bit WER ABI prerequisite is unavailable; no store read was started.");
            report.Unknown(countCheck, "The supported 64-bit WER prerequisite is unavailable; no report-count observation was obtained.");
            return;
        }
        bool layout = Marshal.SizeOf<WerParameterLayout>() == 778 && Marshal.SizeOf<WerSignatureLayout>() == 7910 &&
            Marshal.OffsetOf<WerMetadataLayout>(nameof(WerMetadataLayout.BucketId)).ToInt64() == 7912 &&
            Marshal.OffsetOf<WerMetadataLayout>(nameof(WerMetadataLayout.ReportId)).ToInt64() == 7928 &&
            Marshal.OffsetOf<WerMetadataLayout>(nameof(WerMetadataLayout.CreationTime)).ToInt64() == 7944 &&
            Marshal.OffsetOf<WerMetadataLayout>(nameof(WerMetadataLayout.SizeInBytes)).ToInt64() == 7952 &&
            Marshal.SizeOf<WerMetadataLayout>() == 7960;
        if (!layout)
        {
            report.Fail(layoutCheck, "The independent marshalled V1 declaration contradicts documented 64-bit size/offset constants; no native store read was started.");
            report.Unknown(readCheck, "Managed layout prerequisite failed; native WER behavior remains unverified.");
            report.Unknown(countCheck, "Native prerequisite failed; no report-count observation was obtained.");
            return;
        }
        report.Pass(layoutCheck, "Independent CLR sequential declarations match documented 64-bit V1 size=7960, reportIdOffset=7928 and creationTimeOffset=7944. This is not a native installed-SDK sizeof/offsetof assertion or populated-buffer proof.");
        bool countReported = false;
        try
        {
            // Explicit existing-store read on the disposable CI host only. Never create reports,
            // force a crash, submit/upload, archive, purge, delete or open any report/dump file.
            var before = DateTimeOffset.UtcNow;
            var result = Query(ct => new WindowsWerMetadataInventory().ReadAsync(30, ct), token);
            var after = DateTimeOffset.UtcNow;
            CheckWerStoreCounts(result, before, after, report);
            countReported = true;
            string sourceStatus = "";
            if (result.Items.Count == 1)
            {
                var statuses = result.Items[0].StoreStatuses;
                if (statuses.Count != 2 || statuses.Select(status => status.Store).Distinct().Count() != 2 ||
                    statuses.Any(status => !Enum.IsDefined(status.Store) || !Enum.IsDefined(status.State) || !Enum.IsDefined(status.Reason) ||
                        status.InspectedKeys is < 0 or > WerMetadataProjection.MaximumRecordsPerStore || status.MetadataRecords < 0 ||
                        status.MetadataRecords > status.InspectedKeys || status.ProjectedReferences < 0 || status.ProjectedReferences > status.MetadataRecords ||
                        status.MetadataFailures < 0 || status.MetadataFailures > status.InspectedKeys))
                {
                    report.Fail(readCheck, "The native WER report omitted or contradicted its fixed two-source bounded diagnostic contract; no provider contents are exported.");
                    return;
                }
                sourceStatus = string.Join("; ", statuses.Select(status =>
                    $"{status.Store}={status.State}/{status.Reason}/keys:{status.InspectedKeys}/metadata:{status.MetadataRecords}/projected:{status.ProjectedReferences}/failures:{status.MetadataFailures}/limited:{(status.Truncated ? "true" : "false")}/result:{(status.LastNativeResult.HasValue ? "0x" + unchecked((uint)status.LastNativeResult.Value).ToString("X8") : "none")}"));
            }
            if (result.State is not (CheckState.Available or CheckState.Partial))
            {
                report.Unknown(readCheck, $"Native WER state={result.State}; {sourceStatus}; populatedMetadataVerified=false. Store access/API/completion unverified; S_OK/null is Unknown, never empty or an invented native error.");
                return;
            }
            if (result.Items.Count != 1)
            {
                report.Fail(readCheck, "A readable native WER result did not contain exactly one bounded report. No provider contents are exported.");
                return;
            }
            var data = result.Items[0];
            string[] stores = ["E_STORE_MACHINE_QUEUE", "E_STORE_MACHINE_ARCHIVE"];
            if (data.StartUtc.Offset != TimeSpan.Zero || data.EndUtc.Offset != TimeSpan.Zero ||
                data.EndUtc - data.StartUtc != TimeSpan.FromDays(30) || data.EndUtc < before || data.EndUtc > after ||
                data.Observations.Count > WerMetadataProjection.MaximumRecordsPerStore * 2 || data.Coverage.Count is < 5 or > 16 ||
                data.Observations.Any(row => !stores.Contains(row.Store, StringComparer.Ordinal) || row.ReportId == Guid.Empty ||
                    row.CreatedUtc.Offset != TimeSpan.Zero || row.CreatedUtc < data.StartUtc || row.CreatedUtc >= data.EndUtc ||
                    row.Category != "Stored WER report" || row.Source != "WerStoreQueryReportMetadataV1 / " + row.Store) ||
                data.Observations.GroupBy(row => (row.Store, row.ReportId)).Any(group => group.Count() > 1))
            {
                report.Fail(readCheck, "Native WER source/window/count/minimized-observation schema violated the fixed contract. No report identifiers, dates, coverage strings or payloads are exported.");
                return;
            }
            int completedStores = data.StoreStatuses.Count(status => status.State == CheckState.Available && status.Reason == WerMetadataStoreReason.EnumerationComplete);
            int populatedStores = data.Observations.Select(row => row.Store).Distinct(StringComparer.Ordinal).Count();
            if (completedStores == 0 && populatedStores == 0)
            {
                report.Unknown(readCheck, $"Native WER state={result.State}; {sourceStatus}; populatedMetadataVerified=false. No completed readable store or usable metadata established; a partial response alone is not proof.");
                return;
            }
            report.Pass(readCheck, $"Native WER state={result.State}; {sourceStatus}; completedStoreCount={completedStores}; populatedStoreCount={populatedStores}; projectedReferenceCount={data.Observations.Count}; populatedMetadataVerified={(data.Observations.Count > 0 ? "true" : "false")}. " +
                (data.Observations.Count > 0 ? "Existing GUID/time observations satisfy the minimized contract; no raw signature/key/file data is exported. " : "No usable in-window existing metadata returned: populated-buffer marshalling remains unverified. ") +
                "Empty/readable stores are not no-crash, health, complete-history or upload-success proof. No store mutation, report creation or filesystem fallback was used.");
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or ExternalException)
        {
            report.Unknown(readCheck, $"Native WER metadata read did not complete (code 0x{ex.HResult:X8}); populatedMetadataVerified=false. No provider content or report identifiers are exported.");
            if (!countReported) report.Unknown(countCheck, "The same native metadata query did not complete a report-count observation; no extra query or empty-store conclusion substituted.");
        }
    }

    private static void CheckWerStoreCounts(CheckResult<WerMetadataReport> result, DateTimeOffset before,
        DateTimeOffset after, SmokeReport report)
    {
        const string check = "wer-store-count-contract";
        if (result.Items.Count == 0)
        {
            report.Unknown(check, $"Native queryState={result.State}; validCountObservations=0. No per-store count report returned; availability and zero/nonzero store counts remain unverified.");
            return;
        }
        if (result.Items.Count != 1 || result.Items[0].StoreStatuses.Count != 2 ||
            result.Items[0].StoreStatuses.Any(status => status == null || !Enum.IsDefined(status.Store)) ||
            result.Items[0].StoreStatuses.Select(status => status.Store).Distinct().Count() != 2)
        {
            report.Fail(check, "The count report contradicted the exact two-fixed-source contract; no times, keys or metadata contents exported.");
            return;
        }
        int valid = 0;
        var details = new List<string>();
        foreach (var status in result.Items[0].StoreStatuses)
        {
            var observation = status.CountObservation;
            if (observation == null)
            {
                details.Add($"{status.Store}:count=unknown/nativeResult=none/observationPresent=false");
                continue;
            }
            if (observation.CheckedUtc == default || observation.CheckedUtc.Offset != TimeSpan.Zero ||
                observation.CheckedUtc < before || observation.CheckedUtc > after ||
                observation.NativeResult != 0 && observation.ReportCount.HasValue)
            {
                report.Fail(check, "Count observation violated fixed query-time bounds or claimed an undefined count from a failed native call. No actual times or provider payload exported.");
                return;
            }
            bool observed = observation.NativeResult == 0 && observation.ReportCount.HasValue;
            if (observed) valid++;
            details.Add($"{status.Store}:count={(observed ? observation.ReportCount!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unknown")}/nativeResult=0x{unchecked((uint)observation.NativeResult):X8}/observationPresent=true/countObserved={observed}");
        }
        string detail = $"queryState={result.State}; validCountObservations={valid}; " + string.Join("; ", details) +
            ". Counts are non-atomic all-retained-store observations, not the selected creation-time interval, unique crash counts, health or enumeration-completeness proof. A zero count never promotes S_OK/null-key enumeration; positive counts do not verify populated metadata parsing. No count is summed across stores; no times, report keys or contents exported. Same inventory query reused; no extra native query or mutation.";
        if (valid == 2) report.Pass(check, detail);
        else report.Unknown(check, detail);
    }
}
