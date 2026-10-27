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
        const string layoutCheck = "wer-v1-managed-layout", readCheck = "wer-store-read-contract";
        if (!Environment.Is64BitProcess)
        {
            report.Unknown(layoutCheck, "Only the documented supported 64-bit V1 layout is checked; this process is not 64-bit.");
            report.Unknown(readCheck, "The supported 64-bit WER ABI prerequisite is unavailable; no store read was started.");
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
            return;
        }
        report.Pass(layoutCheck, "Independent CLR sequential declarations match documented 64-bit V1 size=7960, reportIdOffset=7928 and creationTimeOffset=7944. This is not a native installed-SDK sizeof/offsetof assertion or populated-buffer proof.");
        try
        {
            // Explicit existing-store read on the disposable CI host only. Never create reports,
            // force a crash, submit/upload, archive, purge, delete or open any report/dump file.
            var before = DateTimeOffset.UtcNow;
            var result = Query(ct => new WindowsWerMetadataInventory().ReadAsync(30, ct), token);
            var after = DateTimeOffset.UtcNow;
            if (result.State is not (CheckState.Available or CheckState.Partial))
            {
                report.Unknown(readCheck, $"Native WER state={result.State}; readableStoreCount=0; populatedMetadataVerified=false. Store access, API support or completion remains unverified; no empty-store or health conclusion.");
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
            int completedStores = stores.Count(store => data.Coverage.Any(note => note.StartsWith(store + ": Available;", StringComparison.Ordinal)));
            int populatedStores = data.Observations.Select(row => row.Store).Distinct(StringComparer.Ordinal).Count();
            if (completedStores == 0 && populatedStores == 0)
            {
                report.Unknown(readCheck, $"Native WER state={result.State}; completedStoreCount=0; populatedMetadataVerified=false. No completed readable store or usable metadata established; a partial response alone is not proof.");
                return;
            }
            report.Pass(readCheck, $"Native WER state={result.State}; completedStoreCount={completedStores}; populatedStoreCount={populatedStores}; projectedReferenceCount={data.Observations.Count}; populatedMetadataVerified={(data.Observations.Count > 0 ? "true" : "false")}. " +
                (data.Observations.Count > 0 ? "Existing GUID/time observations satisfy the minimized contract; no raw signature/key/file data is exported. " : "No usable in-window existing metadata returned: populated-buffer marshalling remains unverified. ") +
                "Empty/readable stores are not no-crash, health, complete-history or upload-success proof. No store mutation, report creation or filesystem fallback was used.");
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or ExternalException)
        {
            report.Unknown(readCheck, $"Native WER metadata read did not complete (code 0x{ex.HResult:X8}); populatedMetadataVerified=false. No provider content or report identifiers are exported.");
        }
    }
}
