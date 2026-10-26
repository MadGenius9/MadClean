using System.ComponentModel;
using System.Text;
using MadClean.Core;

namespace MadClean.Windows;

/// <summary>Explicit local CIM projections only; no diagnostics, unique identifiers or upgrade actions.</summary>
public sealed class HardwareInventory : IHardwareInventory
{
    private readonly ReadOnlyProcess runner = new();
    public async Task<CheckResult<HardwareFact>> ReadAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return CheckResult<HardwareFact>.Empty(CheckState.Unsupported,
            "Hardware inventory requires Windows local CIM providers; this platform was not queried.");
        try
        {
            string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            if (!Path.IsPathFullyQualified(system)) return CheckResult<HardwareFact>.Empty(CheckState.Unavailable,
                "The Windows system directory is unavailable; no hardware command was executed.");
            string executable = Path.Combine(system, @"WindowsPowerShell\v1.0\powershell.exe");
            if (!File.Exists(executable)) return CheckResult<HardwareFact>.Empty(CheckState.Unavailable,
                "System Windows PowerShell is unavailable or inaccessible; hardware was not queried.");
            var result = await runner.RunAsync(executable,
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(Script))],
                TimeSpan.FromSeconds(90), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (result.ExitCode == 3) return CheckResult<HardwareFact>.Empty(CheckState.Unavailable,
                "The system CIM module is unavailable or blocked. Check Windows module/provider policy; no elevation was requested.");
            if (result.ExitCode != 0) return CheckResult<HardwareFact>.Empty(CheckState.Failed,
                $"Hardware query failed (code 0x{result.ExitCode:X8}); check local CIM provider/access policy and retry. No elevation was requested.");
            var parsed = HardwareInventoryParser.Parse(result.Output);
            token.ThrowIfCancellationRequested();
            return parsed;
        }
        catch (OperationCanceledException) { throw; }
        catch (TimeoutException)
        {
            token.ThrowIfCancellationRequested();
            return CheckResult<HardwareFact>.Empty(CheckState.Failed, "Hardware query timed out; check the local CIM service/provider and retry. No diagnostic or upgrade action ran.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or System.Security.SecurityException)
        {
            token.ThrowIfCancellationRequested();
            return CheckResult<HardwareFact>.Empty(CheckState.Failed,
                $"Hardware could not be queried (code 0x{ex.HResult:X8}); check Windows provider availability/access. No elevation was requested.");
        }
    }

    // Constant allowlist only. No serial/UUID/UniqueID/PNPDeviceID/service-tag properties or remote sessions.
    private const string Script = """
        $env:PSModulePath = [System.IO.Path]::Combine($PSHOME, 'Modules')
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
        $module = [System.IO.Path]::Combine($PSHOME, 'Modules', 'CimCmdlets', 'CimCmdlets.psd1')
        if (-not [System.IO.File]::Exists($module)) { exit 3 }
        try { Microsoft.PowerShell.Core\Import-Module -Name $module -ErrorAction Stop } catch { exit 3 }
        function Read-ReportedHardware([string]$class, [string[]]$properties) {
          try {
            $rows = @(CimCmdlets\Get-CimInstance -Namespace 'root/cimv2' -ClassName $class -Property $properties -OperationTimeoutSec 10 -ErrorAction Stop |
              Microsoft.PowerShell.Utility\Select-Object -First 129 -Property $properties)
            $truncated = $rows.Count -gt 128
            $records = @($rows | Microsoft.PowerShell.Utility\Select-Object -First 128)
            [pscustomobject]@{Class=$class; Status='Available'; Truncated=$truncated; Records=@($records)}
          } catch { [pscustomobject]@{Class=$class; Status='Failed'; Truncated=$false; Records=@()} }
        }
        $sources = @(
          Read-ReportedHardware 'Win32_ComputerSystem' @('Manufacturer','Model','SystemFamily','SystemSKUNumber','SystemType')
          Read-ReportedHardware 'Win32_BaseBoard' @('Manufacturer','Product','Version')
          Read-ReportedHardware 'Win32_Processor' @('Manufacturer','Name','NumberOfCores','NumberOfLogicalProcessors','MaxClockSpeed','SocketDesignation')
          Read-ReportedHardware 'Win32_PhysicalMemory' @('Manufacturer','PartNumber','Capacity','SMBIOSMemoryType','FormFactor','ConfiguredClockSpeed')
          Read-ReportedHardware 'Win32_VideoController' @('Name','VideoProcessor','DriverVersion')
          Read-ReportedHardware 'Win32_DiskDrive' @('Manufacturer','Model','MediaType','InterfaceType','Size','FirmwareRevision')
        )
        [pscustomobject]@{Version=1; Sources=@($sources)} | Microsoft.PowerShell.Utility\ConvertTo-Json -Depth 6 -Compress
        """;
}
