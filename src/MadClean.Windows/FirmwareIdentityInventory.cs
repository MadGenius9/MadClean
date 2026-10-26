using System.ComponentModel;
using System.Text;
using MadClean.Core;

namespace MadClean.Windows;

/// <summary>Reads explicitly projected local CIM firmware identity; it never searches for or applies updates.</summary>
public sealed class FirmwareIdentityInventory : IFirmwareIdentityInventory
{
    private readonly ReadOnlyProcess runner = new();

    public async Task<CheckResult<FirmwareIdentity>> ReadAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            return CheckResult<FirmwareIdentity>.Empty(CheckState.Unsupported, "Firmware identity requires Windows local CIM providers; this platform was not queried.");
        try
        {
            var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            if (!Path.IsPathFullyQualified(system))
                return CheckResult<FirmwareIdentity>.Empty(CheckState.Unavailable, "The Windows system directory could not be resolved; no firmware identity command was executed.");
            string executable = Path.Combine(system, @"WindowsPowerShell\v1.0\powershell.exe");
            if (!File.Exists(executable))
                return CheckResult<FirmwareIdentity>.Empty(CheckState.Unavailable, "System Windows PowerShell is unavailable or inaccessible; no firmware identity command was executed.");
            var result = await runner.RunAsync(executable,
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(Script))],
                TimeSpan.FromSeconds(45), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (result.ExitCode == 3)
                return CheckResult<FirmwareIdentity>.Empty(CheckState.Unavailable, "The system CIM module is unavailable or blocked. Check Windows provider/module policy; no elevation was requested.");
            if (result.ExitCode != 0)
                return CheckResult<FirmwareIdentity>.Empty(CheckState.Failed,
                    $"Firmware identity query failed (code 0x{result.ExitCode:X8}). Check local CIM provider availability and access, then retry. No elevation was requested.");
            var parsed = FirmwareIdentityParser.Parse(result.Output);
            token.ThrowIfCancellationRequested();
            return parsed;
        }
        catch (OperationCanceledException) { throw; }
        catch (TimeoutException)
        {
            return CheckResult<FirmwareIdentity>.Empty(CheckState.Failed, "Firmware identity query timed out. Check the local CIM service/provider and retry; no firmware update operation was performed.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or System.Security.SecurityException)
        {
            return CheckResult<FirmwareIdentity>.Empty(CheckState.Failed,
                $"Firmware identity could not be queried (code 0x{ex.HResult:X8}). Check system provider availability and access; no elevation was requested.");
        }
    }

    // Only identity properties are requested/projected. Never request serial, UUID, service-tag or key fields.
    private const string Script = """
        $env:PSModulePath = [System.IO.Path]::Combine($PSHOME, 'Modules')
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
        $modulePath = [System.IO.Path]::Combine($PSHOME, 'Modules', 'CimCmdlets', 'CimCmdlets.psd1')
        if (-not [System.IO.File]::Exists($modulePath)) { exit 3 }
        try { Microsoft.PowerShell.Core\Import-Module -Name $modulePath -ErrorAction Stop } catch { exit 3 }
        try {
          $records = @(CimCmdlets\Get-CimInstance -Namespace 'root/cimv2' -ClassName Win32_ComputerSystem -Property Manufacturer,Model,SystemSKUNumber -OperationTimeoutSec 10 -ErrorAction Stop |
            Microsoft.PowerShell.Utility\Select-Object -First 2 -Property Manufacturer,Model,SystemSKUNumber)
          $computer = [pscustomobject]@{Status='Available'; Records=@($records)}
        } catch { $computer = [pscustomobject]@{Status='Failed'; Records=@()} }
        try {
          $records = @(CimCmdlets\Get-CimInstance -Namespace 'root/cimv2' -ClassName Win32_BaseBoard -Property Manufacturer,Product,Version -OperationTimeoutSec 10 -ErrorAction Stop |
            Microsoft.PowerShell.Utility\Select-Object -First 2 -Property Manufacturer,Product,Version)
          $board = [pscustomobject]@{Status='Available'; Records=@($records)}
        } catch { $board = [pscustomobject]@{Status='Failed'; Records=@()} }
        try {
          $records = @(CimCmdlets\Get-CimInstance -Namespace 'root/cimv2' -ClassName Win32_BIOS -Property Manufacturer,SMBIOSBIOSVersion,ReleaseDate -OperationTimeoutSec 10 -ErrorAction Stop |
            Microsoft.PowerShell.Utility\Select-Object -First 2 -Property Manufacturer,SMBIOSBIOSVersion,@{n='ReleaseDate';e={
              if ($_.ReleaseDate -is [DateTime]) { $_.ReleaseDate.ToString('yyyy-MM-dd', [System.Globalization.CultureInfo]::InvariantCulture) }
              elseif ($_.ReleaseDate -is [string]) { $_.ReleaseDate }
              else { $null }
            }})
          $bios = [pscustomobject]@{Status='Available'; Records=@($records)}
        } catch { $bios = [pscustomobject]@{Status='Failed'; Records=@()} }
        [pscustomobject]@{SchemaVersion=1; ComputerSystem=$computer; BaseBoard=$board; Bios=$bios} |
          Microsoft.PowerShell.Utility\ConvertTo-Json -Depth 6 -Compress
        """;
}
