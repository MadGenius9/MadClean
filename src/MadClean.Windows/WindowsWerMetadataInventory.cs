using System.Diagnostics;
using System.Runtime.InteropServices;
using MadClean.Core;

namespace MadClean.Windows;

/// <summary>Explicit read-only WER store metadata. No report/dump path reads, submissions or mutations.</summary>
public sealed class WindowsWerMetadataInventory : IWerMetadataInventory
{
    private static int activeWorker;
    private static readonly TimeSpan CooperativeBudget = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CallerWaitLimit = TimeSpan.FromSeconds(22);
    public static bool HasPendingNativeRead => Volatile.Read(ref activeWorker) != 0;

    public Task<CheckResult<WerMetadataReport>> ReadAsync(int days, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (days is not (1 or 7 or 30)) throw new ArgumentOutOfRangeException(nameof(days), "Choose 1, 7 or 30 days.");
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 15063) || !Environment.Is64BitProcess)
            return Task.FromResult(CheckResult<WerMetadataReport>.Empty(CheckState.Unsupported,
                "WER store metadata requires supported 64-bit Windows 10 version 1703 or later APIs; no fallback scan ran."));
        if (Interlocked.CompareExchange(ref activeWorker, 1, 0) != 0)
            return Task.FromResult(CheckResult<WerMetadataReport>.Empty(CheckState.Unavailable,
                "A previous native WER metadata worker is still running. Native calls cannot be forcibly cancelled; wait before retrying. No second query started."));
        var end = DateTimeOffset.UtcNow;
        var start = end.AddDays(-days);
        // Do not attach the scheduling token: even a cancellation before execution must release the gate.
        var worker = Task.Run(() =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                if (!Native.Available(out bool countAvailable)) return CheckResult<WerMetadataReport>.Empty(CheckState.Unsupported,
                    "Required documented WER store exports are unavailable. No report-folder or dump-file fallback was used.");
                var clock = Stopwatch.StartNew();
                var snapshots = new List<WerMetadataStoreSnapshot>();
                foreach (var store in new[] { WerMetadataStore.MachineQueue, WerMetadataStore.MachineArchive })
                {
                    token.ThrowIfCancellationRequested();
                    snapshots.Add(ReadStore(store, countAvailable, clock, token));
                }
                token.ThrowIfCancellationRequested();
                return WerMetadataProjection.Create(start, end, snapshots, token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            { return CheckResult<WerMetadataReport>.Empty(CheckState.Unsupported, "WER store API/ABI support is unavailable; no metadata or filesystem fallback was trusted."); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
            { return CheckResult<WerMetadataReport>.Empty(CheckState.Failed, "Native WER metadata query failed. Check existing provider access; no raw report data, elevation or report action was used."); }
            finally { Volatile.Write(ref activeWorker, 0); }
        }, CancellationToken.None);
        return WerMetadataWait.AwaitAsync(worker, CallerWaitLimit, token);
    }

    private static WerMetadataStoreSnapshot ReadStore(WerMetadataStore store, bool countAvailable, Stopwatch clock, CancellationToken token)
    {
        List<WerMetadataRecord> rows = [];
        int inspected = 0, failures = 0; int? lastError = null, lastResult = null;
        WerMetadataCountObservation? countObservation = null;
        IntPtr handle = IntPtr.Zero, buffer = IntPtr.Zero;
        WerMetadataStoreSnapshot Result(CheckState state, bool limited = false, int? code = null,
            WerMetadataStoreReason reason = WerMetadataStoreReason.Unspecified) =>
            new(store, state, rows.ToArray(), inspected, limited, failures, code ?? lastError, reason, lastResult)
                { CountObservation = countObservation };
        bool Expired() => clock.Elapsed >= CooperativeBudget;
        try
        {
            if (Expired()) return Result(CheckState.Unknown, limited: true, reason: WerMetadataStoreReason.BudgetExpired);
            token.ThrowIfCancellationRequested();
            int opened = Native.WerStoreOpen(store == WerMetadataStore.MachineQueue ? 3 : 2, ref handle);
            lastResult = opened;
            token.ThrowIfCancellationRequested();
            if (opened != 0) { handle = IntPtr.Zero; return Result(Classify(opened), code: opened, reason: WerMetadataStoreReason.StoreOpenFailed); }
            if (handle == IntPtr.Zero) return Result(CheckState.Unknown, reason: WerMetadataStoreReason.SuccessfulOpenReturnedNullHandle);
            token.ThrowIfCancellationRequested();
            if (Expired()) return Result(CheckState.Unknown, limited: true, reason: WerMetadataStoreReason.BudgetExpired);
            if (countAvailable)
            {
                try
                {
                    // Independent all-times count on this same open store. Never substitute its result
                    // for enumeration's result or use zero as proof of an empty/healthy history.
                    int countResult = Native.WerStoreGetReportCount(handle, out uint reportCount);
                    var checkedUtc = DateTimeOffset.UtcNow;
                    countObservation = new(countResult == 0 ? reportCount : null, checkedUtc, countResult);
                }
                catch (EntryPointNotFoundException)
                {
                    // Optional capability only: preserve the established metadata reader.
                }
                token.ThrowIfCancellationRequested();
                if (Expired()) return Result(CheckState.Unknown, limited: true, reason: WerMetadataStoreReason.BudgetExpired);
            }
            bool first = true;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (Expired()) return Result(CheckState.Partial, limited: true, reason: WerMetadataStoreReason.BudgetExpired);
                IntPtr key = IntPtr.Zero;
                try
                {
                    int keyResult = first ? Native.WerStoreGetFirstReportKey(handle, ref key) : Native.WerStoreGetNextReportKey(handle, ref key);
                    lastResult = keyResult;
                    first = false;
                    token.ThrowIfCancellationRequested();
                    if (Expired()) return Result(CheckState.Partial, limited: true, reason: WerMetadataStoreReason.BudgetExpired);
                    if (NoMoreFiles(keyResult)) return Result(failures > 0 ? CheckState.Partial : CheckState.Available,
                        reason: failures > 0 ? WerMetadataStoreReason.MetadataQueryFailed : WerMetadataStoreReason.EnumerationComplete);
                    if (keyResult != 0) return Result(inspected > 0 ? CheckState.Partial : Classify(keyResult), code: keyResult, reason: WerMetadataStoreReason.EnumerationFailed);
                    if (key == IntPtr.Zero) return Result(CheckState.Unknown, reason: WerMetadataStoreReason.SuccessfulEnumerationReturnedNullKey);
                    if (inspected >= WerMetadataProjection.MaximumRecordsPerStore) return Result(CheckState.Partial, limited: true, reason: WerMetadataStoreReason.RecordLimitReached);
                    inspected++;
                    if (buffer == IntPtr.Zero) buffer = Marshal.AllocHGlobal(Native.MetadataBytes);
                    Native.Zero(buffer);
                    // The key remains an opaque API-owned pointer. Never marshal it into a managed string.
                    int result = Native.WerStoreQueryReportMetadataV1(handle, key, buffer);
                    lastResult = result;
                    token.ThrowIfCancellationRequested();
                    if (Expired()) return Result(CheckState.Partial, limited: true, reason: WerMetadataStoreReason.BudgetExpired);
                    if (result != 0) { failures++; lastError = result; continue; }
                    var guidBytes = new byte[16];
                    Marshal.Copy(IntPtr.Add(buffer, Native.ReportIdOffset), guidBytes, 0, guidBytes.Length);
                    rows.Add(new(new Guid(guidBytes), Marshal.ReadInt64(buffer, Native.CreationTimeOffset)));
                }
                finally
                {
                    // Signature parameters may contain private values, so clear caller-owned native bytes
                    // without decoding them. API-owned const keys must only be freed with WerFreeString.
                    if (buffer != IntPtr.Zero) Native.Zero(buffer);
                    if (key != IntPtr.Zero) Native.WerFreeString(key);
                }
            }
        }
        finally
        {
            if (buffer != IntPtr.Zero) { Native.Zero(buffer); Marshal.FreeHGlobal(buffer); }
            if (handle != IntPtr.Zero) Native.WerStoreClose(handle);
        }
    }

    private static bool NoMoreFiles(int result) => result == 18 || result == unchecked((int)0x80070012);
    private static CheckState Classify(int result) => unchecked((uint)result) switch
    {
        2 or 3 or 5 or 0x80070002 or 0x80070003 or 0x80070005 => CheckState.Unavailable,
        50 or 120 or 0x80070032 or 0x80070078 or 0x80004001 => CheckState.Unsupported,
        _ => CheckState.Failed
    };

    private static class Native
    {
        // Verified against Microsoft's current SDK WerApi.h, not the private Report.wer file format:
        // Signature = 65 WCHAR + 10*(129 WCHAR + 260 WCHAR) = 7910 bytes. GUID alignment pads to
        // BucketId7912, ReportId7928, FILETIME7944, ULONGLONG7952, V1 total7960 (64-bit ABI).
        internal const int MetadataBytes = 7960, ReportIdOffset = 7928, CreationTimeOffset = 7944;
        private static readonly byte[] Zeros = new byte[MetadataBytes];
        internal static void Zero(IntPtr buffer) => Marshal.Copy(Zeros, 0, buffer, MetadataBytes);
        internal static bool Available(out bool countAvailable)
        {
            countAvailable = false;
            if (!NativeLibrary.TryLoad("Wer.dll", typeof(WindowsWerMetadataInventory).Assembly, DllImportSearchPath.System32, out var library)) return false;
            try
            {
                countAvailable = NativeLibrary.TryGetExport(library, "WerStoreGetReportCount", out _);
                return new[] { "WerStoreOpen", "WerStoreClose", "WerStoreGetFirstReportKey", "WerStoreGetNextReportKey", "WerStoreQueryReportMetadataV1", "WerFreeString" }
                    .All(name => NativeLibrary.TryGetExport(library, name, out _));
            }
            finally { NativeLibrary.Free(library); }
        }
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("Wer.dll", ExactSpelling = true)]
        internal static extern int WerStoreOpen(int storeType, ref IntPtr store);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("Wer.dll", ExactSpelling = true)]
        internal static extern void WerStoreClose(IntPtr store);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("Wer.dll", ExactSpelling = true)]
        internal static extern int WerStoreGetReportCount(IntPtr store, out uint reportCount);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("Wer.dll", ExactSpelling = true)]
        internal static extern int WerStoreGetFirstReportKey(IntPtr store, ref IntPtr key);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("Wer.dll", ExactSpelling = true)]
        internal static extern int WerStoreGetNextReportKey(IntPtr store, ref IntPtr key);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("Wer.dll", ExactSpelling = true)]
        internal static extern int WerStoreQueryReportMetadataV1(IntPtr store, IntPtr key, IntPtr metadata);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("Wer.dll", ExactSpelling = true)]
        internal static extern void WerFreeString(IntPtr key);
    }
}
