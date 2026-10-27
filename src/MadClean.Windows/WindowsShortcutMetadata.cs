using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using MadClean.Core;

namespace MadClean.Windows;

internal static class WindowsShortcutMetadata
{
    private const uint RawPath = 4;
    private const uint HasIdList = 0x1;
    private const uint HasArguments = 0x20;
    private const uint HasEnvironmentString = 0x200;
    private const uint HasDarwinId = 0x1000;
    private const uint RunAsUser = 0x2000;
    private const uint RunWithShim = 0x20000;
    private const uint EnableTargetMetadata = 0x80000;
    private const uint AllowLinkToLink = 0x800000;
    private const uint PreferEnvironmentPath = 0x2000000;
    private const int ReadOnlyDenyWrite = 0x20;
    private static readonly Guid ShellLinkClass = new("00021401-0000-0000-C000-000000000046");

    internal static ShortcutStoredMetadata Read(string path, CancellationToken token)
    {
        object? shell = null;
        try
        {
            token.ThrowIfCancellationRequested();
            var type = Type.GetTypeFromCLSID(ShellLinkClass, throwOnError: true)!;
            shell = Activator.CreateInstance(type);
            if (shell == null) return Problem("The Windows Shell link reader was unavailable.");
            // Initialization only, not activation. No Resolve/Save/arguments/working-directory/icon methods are exposed or called.
            ((IPersistFile)shell).Load(path, ReadOnlyDenyWrite);
            token.ThrowIfCancellationRequested();
            if (shell is not IShortcutDataList data || data.GetFlags(out uint flags) != 0)
                return Problem("Stored Shell link flags were not available; advertised/context semantics were not assumed.");
            // Do not ask even the path getter to interpret advertised or extended activation
            // forms. Reading flags is sufficient to defer them without MSI/package involvement.
            if ((flags & HasDarwinId) != 0)
                return new(null, HasItemIdList: (flags & HasIdList) != 0, IsAdvertised: true);
            if ((flags & (RunAsUser | RunWithShim | EnableTargetMetadata | AllowLinkToLink)) != 0)
                return new(null, HasItemIdList: (flags & HasIdList) != 0, HasUnsupportedActivation: true);
            var link = (IShortcutShellLinkW)shell;
            var target = new StringBuilder(260);
            int status = link.GetPath(target, target.Capacity, IntPtr.Zero, RawPath);
            token.ThrowIfCancellationRequested();
            if (status < 0) return Problem($"Stored target metadata could not be read (code 0x{status:X8}); no target absence was inferred.");
            bool idList = (flags & HasIdList) != 0;
            if (target.Length == 0 && !idList)
            {
                IntPtr items = IntPtr.Zero;
                try { idList = link.GetIDList(out items) == 0 && items != IntPtr.Zero; }
                finally { if (items != IntPtr.Zero) Marshal.FreeCoTaskMem(items); }
            }
            return new(target.ToString(), idList, (flags & HasDarwinId) != 0, (flags & HasArguments) != 0,
                (flags & (HasEnvironmentString | PreferEnvironmentPath)) != 0,
                (flags & (RunAsUser | RunWithShim | EnableTargetMetadata | AllowLinkToLink)) != 0);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is COMException or InvalidCastException or IOException or UnauthorizedAccessException or ArgumentException or TypeLoadException)
        { return Problem($"Read-only Shell link metadata was unavailable or malformed (code 0x{ex.HResult:X8}); no missing-target conclusion was made."); }
        finally
        {
            if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    private static ShortcutStoredMetadata Problem(string reason) => new(null, MetadataProblem: reason);

    // Only the two leading read-only methods of IShellLinkW are exposed. All later vtable entries,
    // including arguments, working directory, icons, Resolve and mutation methods, are intentionally absent.
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShortcutShellLinkW
    {
        [PreserveSig]
        int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder target, int capacity, IntPtr fileData, uint flags);
        [PreserveSig]
        int GetIDList(out IntPtr itemList);
    }

    [ComImport, Guid("45E2B4AE-B1C3-11D0-B92F-00A0C90312E1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShortcutDataList
    {
        // Uncalled placeholders retain the native vtable positions preceding read-only GetFlags.
        [PreserveSig] int ReservedAdd(IntPtr data);
        [PreserveSig] int ReservedCopy(uint signature, out IntPtr data);
        [PreserveSig] int ReservedRemove(uint signature);
        [PreserveSig] int GetFlags(out uint flags);
    }
}
