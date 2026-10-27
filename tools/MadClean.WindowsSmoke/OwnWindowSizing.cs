using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MadClean.WindowsSmoke;

/// <summary>Development-only sizing of this harness's own live HWND; no desktop/display settings change.</summary>
internal sealed class OwnWindowSizing : IDisposable
{
    private const int GetMinMaxInfo = 0x0024;
    private const uint NoMoveNoZOrderNoActivate = 0x0002 | 0x0004 | 0x0010;
    private readonly Window window;
    private readonly HwndSource? source;
    private readonly nint handle;
    private int maximumWidth;
    private int maximumHeight;
    internal int LimitsMessages { get; private set; }

    internal OwnWindowSizing(Window window)
    {
        this.window = window;
        handle = new WindowInteropHelper(window).Handle;
        source = handle == 0 ? null : HwndSource.FromHwnd(handle);
        SetRequestedMaximum(1420, 1000);
        source?.AddHook(WindowMessage);
    }

    internal bool TryResize(double width, double height, out int error)
    {
        error = 0;
        if (source == null || source.IsDisposed || handle == 0 || !SetRequestedMaximum(width, height)) return false;
        var transform = source.CompositionTarget?.TransformToDevice ?? System.Windows.Media.Matrix.Identity;
        int pixelsWide = checked((int)Math.Ceiling(width * transform.M11));
        int pixelsHigh = checked((int)Math.Ceiling(height * transform.M22));
        if (pixelsWide is < 1 or > 6000 || pixelsHigh is < 1 or > 6000) return false;
        // This is the already shown application window only. Preserve its position, z-order,
        // activation and native frame; the real ActualWidth/Height are checked by the caller.
        bool resized = SetWindowPos(handle, 0, 0, 0, pixelsWide, pixelsHigh, NoMoveNoZOrderNoActivate);
        if (!resized) error = Marshal.GetLastWin32Error();
        return resized;
    }

    private bool SetRequestedMaximum(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width is < 100 or > 3000 || height is < 100 or > 3000) return false;
        var transform = source?.CompositionTarget?.TransformToDevice ?? System.Windows.Media.Matrix.Identity;
        double pixelsWide = Math.Ceiling(width * transform.M11), pixelsHigh = Math.Ceiling(height * transform.M22);
        if (!double.IsFinite(pixelsWide) || !double.IsFinite(pixelsHigh) || pixelsWide is < 1 or > 6000 || pixelsHigh is < 1 or > 6000) return false;
        maximumWidth = Math.Max(maximumWidth, (int)pixelsWide);
        maximumHeight = Math.Max(maximumHeight, (int)pixelsHigh);
        return true;
    }

    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (hwnd == handle && message == GetMinMaxInfo && lParam != 0 && maximumWidth > 0 && maximumHeight > 0)
        {
            var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            // A hosted runner can have a smaller desktop than the concept. Raise only this
            // window's maximum tracking bounds, leaving minimum/maximize geometry unchanged.
            limits.MaximumTrack.X = Math.Max(limits.MaximumTrack.X, maximumWidth);
            limits.MaximumTrack.Y = Math.Max(limits.MaximumTrack.Y, maximumHeight);
            Marshal.StructureToPtr(limits, lParam, fDeleteOld: false);
            LimitsMessages++;
            handled = true;
        }
        return 0;
    }

    public void Dispose()
    {
        if (source is { IsDisposed: false }) source.RemoveHook(WindowMessage);
        GC.KeepAlive(window);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { internal int X; internal int Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        internal NativePoint Reserved;
        internal NativePoint MaximumSize;
        internal NativePoint MaximumPosition;
        internal NativePoint MinimumTrack;
        internal NativePoint MaximumTrack;
    }

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
}
