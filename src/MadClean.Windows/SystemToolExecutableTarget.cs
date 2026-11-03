using MadClean.Core;

namespace MadClean.Windows;

// Identity targets only. Windows Installer is deliberately not a servicing-operation
// descriptor or a member of the diagnostic execution catalog.
internal enum SystemToolExecutableTarget { Dism, Sfc, WindowsInstaller, WindowsPowerShell }

internal static class SystemToolExecutableTargets
{
    internal static SystemToolExecutableTarget? FromOperation(SystemToolOperation operation) => operation switch
    {
        SystemToolOperation.DismCheckHealth or SystemToolOperation.DismScanHealth or SystemToolOperation.DismRestoreHealth => SystemToolExecutableTarget.Dism,
        SystemToolOperation.SfcVerifyOnly or SystemToolOperation.SfcScanNow => SystemToolExecutableTarget.Sfc,
        _ => null
    };
    internal static string FileName(SystemToolExecutableTarget target) => target switch
    {
        SystemToolExecutableTarget.Dism => "Dism.exe",
        SystemToolExecutableTarget.Sfc => "sfc.exe",
        SystemToolExecutableTarget.WindowsInstaller => "msiexec.exe",
        SystemToolExecutableTarget.WindowsPowerShell => "powershell.exe",
        _ => throw new ArgumentOutOfRangeException(nameof(target))
    };
    internal static string RelativePath(SystemToolExecutableTarget target) => target == SystemToolExecutableTarget.WindowsPowerShell
        ? @"WindowsPowerShell\v1.0\powershell.exe" : FileName(target);
}
