using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MadClean.Core;

namespace MadClean.Windows;

/// <summary>Local present-device INF properties, not loaded binaries, trust or update applicability.</summary>
public sealed class DeviceDriverInventory : IDeviceDriverInventory
{
    private static readonly object Sync = new();
    private static Task<CheckResult<DeviceDriverObservation>>? worker;
    // Failed destruction is never retried, finalized or treated as completed ownership release.
    private static readonly List<nint> retainedSets = [];
    private static bool retention;
    public static bool HasPendingNativeQuery { get { lock (Sync) return worker is { IsCompleted: false }; } }
    public static bool RequiresRetention { get { lock (Sync) return retention; } }
    private const string Scope = "Local present-device SetupAPI INF metadata only; not loaded driver binary versions, signature trust, hardware health or Windows Update offer matching. Driver date is INF DriverVer metadata, not installation time. No elevation was requested.";

    public async Task<CheckResult<DeviceDriverObservation>> ReadAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return Empty(CheckState.Unsupported, "Present-device driver properties require Windows; no query ran.");
        Task<CheckResult<DeviceDriverObservation>> current;
        lock (Sync)
        {
            if (retention) return Empty(CheckState.Unknown, "Previous device-information-set release was unconfirmed; further native queries are blocked until this app exits.");
            if (worker is { IsCompleted: false }) return Empty(CheckState.Unavailable, "A previous native device-property query is still pending; wait before retrying.");
            // Retain the task before callers can wait/cancel. Only this worker touches native resources.
            current = worker = Task.Run(() => ReadNative(token), CancellationToken.None);
            _ = current.ContinueWith(static failed => { _ = failed.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        try
        {
            var result = await current.WaitAsync(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (TimeoutException)
        {
            token.ThrowIfCancellationRequested();
            return Empty(CheckState.Unknown, "The native device-property query is still pending or exceeded its deadline; cancellation cannot interrupt a Windows API call. Resources remain worker-owned until it returns.");
        }
    }

    private static CheckResult<DeviceDriverObservation> Empty(CheckState state, string message) =>
        CheckResult<DeviceDriverObservation>.Empty(state, message + " " + Scope);

    private static CheckResult<DeviceDriverObservation> ReadNative(CancellationToken token)
    {
        nint set = -1;
        List<DeviceDriverObservation> rows = [];
        byte[]? buffer = null;
        CheckState state = CheckState.Unknown;
        string detail = "No completed enumeration was observed.";
        bool released = true;
        int? releaseError = null;
        var elapsed = Stopwatch.StartNew();
        try
        {
            CheckStop();
            set = Native.SetupDiGetClassDevsW(0, 0, 0, 0x00000006); // PRESENT | ALLCLASSES; no interfaces/IDs/remote scope.
            int createError = set == -1 ? Marshal.GetLastWin32Error() : 0;
            CheckStop();
            if (set == -1)
            {
                int error = createError;
                state = CheckState.Unavailable;
                detail = $"The local device information set could not be obtained (Windows code {error}). Check device-provider/access policy.";
            }
            else
            {
                released = false;
                buffer = new byte[4096]; // One fixed per-worker buffer; never resized or retried.
                bool complete = false;
                bool partial = false;
                int? enumerationError = null;
                for (uint index = 0; index <= DeviceDriverPropertyDecoder.MaximumObservations; index++)
                {
                    CheckStop();
                    var data = new Native.DeviceInfo { Size = (uint)Marshal.SizeOf<Native.DeviceInfo>() };
                    bool enumerated = Native.SetupDiEnumDeviceInfo(set, index, ref data);
                    int enumError = enumerated ? 0 : Marshal.GetLastWin32Error();
                    CheckStop();
                    if (!enumerated)
                    {
                        int error = enumError;
                        if (error == 259) complete = true; // ERROR_NO_MORE_ITEMS is the only complete terminator.
                        else { partial = true; enumerationError = error; }
                        break;
                    }
                    if (index == DeviceDriverPropertyDecoder.MaximumObservations) { partial = true; break; }
                    DateTimeOffset checkedUtc = DateTimeOffset.UtcNow;
                    var name = TextProperty(ref data, FriendlyName);
                    var description = TextProperty(ref data, DeviceDescription);
                    var provider = TextProperty(ref data, DriverProvider);
                    var version = TextProperty(ref data, DriverVersion);
                    var date = DateProperty(ref data);
                    var row = new DeviceDriverObservation((int)index + 1, name, description, provider, version, date, checkedUtc);
                    if (!DeviceDriverPropertyDecoder.IsConsistent(row))
                    { state = CheckState.Failed; detail = "The bounded property projection failed consistency validation; observations were withheld."; rows.Clear(); break; }
                    rows.Add(row);
                    partial |= name.State != DeviceDriverFieldState.Available || description.State != DeviceDriverFieldState.Available ||
                        provider.State != DeviceDriverFieldState.Available || version.State != DeviceDriverFieldState.Available || date.State != DeviceDriverFieldState.Available;
                }
                if (state != CheckState.Failed)
                {
                    state = complete && !partial ? CheckState.Available : CheckState.Partial;
                    detail = complete
                        ? $"Enumerated {rows.Count} present-device observations; {(partial ? "some reported properties are unknown or unavailable" : "all requested properties were observed")}."
                        : enumerationError.HasValue
                            ? $"Enumeration was incomplete (Windows code {enumerationError.Value}); {rows.Count} observations were collected."
                            : $"The 512-observation bound was reached; coverage is incomplete.";
                }

                DeviceDriverText TextProperty(ref Native.DeviceInfo data, PropertyKey key)
                {
                    CheckStop(); Array.Clear(buffer!);
                    bool found = Native.SetupDiGetDevicePropertyW(set, ref data, in key, out uint type, buffer!, (uint)buffer!.Length, out uint bytes, 0);
                    int error = found ? 0 : Marshal.GetLastWin32Error();
                    CheckStop();
                    if (!found) return new(FieldState(error), null, error);
                    if (bytes > buffer!.Length) return new(DeviceDriverFieldState.Invalid, null, null);
                    return DeviceDriverPropertyDecoder.Text(type, buffer!.AsSpan(0, (int)bytes));
                }
                DeviceDriverDate DateProperty(ref Native.DeviceInfo data)
                {
                    CheckStop(); Array.Clear(buffer!);
                    bool found = Native.SetupDiGetDevicePropertyW(set, ref data, in DriverDate, out uint type, buffer!, (uint)buffer!.Length, out uint bytes, 0);
                    int error = found ? 0 : Marshal.GetLastWin32Error();
                    CheckStop();
                    if (!found) return new(FieldState(error), null, error);
                    if (bytes > buffer!.Length) return new(DeviceDriverFieldState.Invalid, null, null);
                    return DeviceDriverPropertyDecoder.Date(type, buffer!.AsSpan(0, (int)bytes));
                }
            }
        }
        catch (OperationCanceledException) { state = CheckState.Cancelled; detail = "The query stopped between native calls; completed observations are historical and coverage is incomplete."; }
        catch (TimeoutException) { state = CheckState.Partial; detail = "The query deadline was reached between native calls; coverage is incomplete."; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or Win32Exception or InvalidOperationException or System.Security.SecurityException)
        { state = CheckState.Failed; detail = $"The fixed Windows property API was unavailable or failed (code 0x{ex.HResult:X8}); no raw provider output was retained."; }
        finally
        {
            if (set != -1)
            {
                // No SafeHandle finalizer/CloseHandle fallback: exactly one destruction attempt by the owner.
                try
                {
                    released = Native.SetupDiDestroyDeviceInfoList(set);
                    if (!released) releaseError = Marshal.GetLastWin32Error();
                }
                catch (Exception)
                { released = false; }
                if (!released) lock (Sync) { retention = true; retainedSets.Add(set); }
            }
            if (buffer is not null) Array.Clear(buffer);
        }
        if (!released) return Empty(CheckState.Unknown, $"Device-information-set destruction was unconfirmed{(releaseError.HasValue ? $" (Windows code {releaseError.Value})" : "")}; observations were withheld and further queries are blocked.");
        token.ThrowIfCancellationRequested();
        if ((state is CheckState.Available or CheckState.Partial) && elapsed.Elapsed >= TimeSpan.FromSeconds(30))
        {
            state = CheckState.Partial;
            detail = "The query or checked cleanup exceeded its deadline; completed observations are historical and coverage is incomplete.";
        }
        return new(state, detail + " " + Scope, rows.AsReadOnly());

        void CheckStop()
        {
            token.ThrowIfCancellationRequested();
            if (elapsed.Elapsed >= TimeSpan.FromSeconds(30)) throw new TimeoutException();
        }
    }

    private static DeviceDriverFieldState FieldState(int error) => error switch
    {
        1168 => DeviceDriverFieldState.Missing,
        5 => DeviceDriverFieldState.AccessDenied,
        122 => DeviceDriverFieldState.Oversized,
        13 => DeviceDriverFieldState.Invalid,
        _ => DeviceDriverFieldState.Failed
    };

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey(Guid format, uint property)
    { public readonly Guid Format = format; public readonly uint Property = property; }
    private static readonly Guid DriverFormat = new("a8b865dd-2e3d-4094-ad97-e593a70c75d6");
    private static readonly Guid DeviceFormat = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    private static readonly PropertyKey DriverDate = new(DriverFormat, 2);
    private static readonly PropertyKey DriverVersion = new(DriverFormat, 3);
    private static readonly PropertyKey DriverProvider = new(DriverFormat, 9);
    private static readonly PropertyKey DeviceDescription = new(DeviceFormat, 2);
    private static readonly PropertyKey FriendlyName = new(DeviceFormat, 14);

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct DeviceInfo { public uint Size; public Guid ClassGuid; public uint DevInst; public nuint Reserved; }
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
        internal static extern nint SetupDiGetClassDevsW(nint classGuid, nint enumerator, nint window, uint flags);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiEnumDeviceInfo(nint set, uint index, ref DeviceInfo data);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiGetDevicePropertyW(nint set, ref DeviceInfo data, in PropertyKey key,
            out uint type, [Out] byte[] buffer, uint capacity, out uint required, uint flags);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiDestroyDeviceInfoList(nint set);
    }
}
