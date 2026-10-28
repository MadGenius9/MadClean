using System.ComponentModel;
using System.Text;
using MadClean.Core;

namespace MadClean.Windows;

/// <summary>Explicit current-user Main/Bundle Appx metadata only; never package activation or deployment.</summary>
public sealed class WindowsPackagedApplicationInventory : IPackagedApplicationInventory
{
    private readonly ReadOnlyProcess runner = new();

    public async Task<CheckResult<PackagedApplication>> ReadAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return CheckResult<PackagedApplication>.Empty(CheckState.Unsupported,
            "Current-user packaged application inventory requires Windows; no package query ran on this platform.");
        try
        {
            string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            if (!Path.IsPathFullyQualified(system)) return CheckResult<PackagedApplication>.Empty(CheckState.Unavailable,
                "The Windows system directory could not be resolved; no package query ran.");
            string executable = Path.Combine(system, @"WindowsPowerShell\v1.0\powershell.exe");
            if (!File.Exists(executable)) return CheckResult<PackagedApplication>.Empty(CheckState.Unavailable,
                "System Windows PowerShell is missing or inaccessible; current-user packages were not queried.");
            var result = await runner.RunAsync(executable,
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(Script))],
                TimeSpan.FromSeconds(60), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (result.ExitCode != 0) return CheckResult<PackagedApplication>.Empty(CheckState.Failed,
                $"The bounded packaged-app query did not complete (code 0x{result.ExitCode:X8}). Check system Appx module/provider access and retry; no elevation or deployment action was requested.");
            var parsed = PackagedApplicationParser.Parse(result.Output, token);
            token.ThrowIfCancellationRequested();
            return parsed;
        }
        catch (OperationCanceledException) { throw; }
        catch (TimeoutException)
        {
            token.ThrowIfCancellationRequested();
            return CheckResult<PackagedApplication>.Empty(CheckState.Failed,
                "The read-only packaged-app query timed out. Check system Appx provider availability and retry; no package action or successful inventory is claimed.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or
            InvalidOperationException or System.Security.SecurityException)
        {
            token.ThrowIfCancellationRequested();
            return CheckResult<PackagedApplication>.Empty(CheckState.Failed,
                $"Current-user packaged apps could not be queried (code 0x{ex.HResult:X8}). Check system provider/module availability and access. No elevation or package action was requested.");
        }
    }

    // Fixed read-only PowerShell 5.1 script: no caller input, remote scope, user SID, package paths,
    // manifests, dependencies, raw provider serialization or deployment/repair/removal commands.
    // Public WinRT property types do not guarantee the Appx cmdlet wrapper's CLR types: absent,
    // unsupported or oversize property observations become explicit null, never coerced defaults.
    private const string Script = """
        $env:PSModulePath = [System.IO.Path]::Combine($PSHOME, 'Modules')
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        $WarningPreference = 'SilentlyContinue'
        $VerbosePreference = 'SilentlyContinue'
        $DebugPreference = 'SilentlyContinue'
        $InformationPreference = 'SilentlyContinue'
        [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
        function Write-PackageEnvelope([string]$status, [bool]$truncated, [object[]]$packages) {
          [pscustomobject]@{
            Version=1; Scope='CurrentUser'; PackageTypes=@('Main','Bundle')
            Status=$status; Truncated=$truncated; Packages=@($packages)
          } | Microsoft.PowerShell.Utility\ConvertTo-Json -Depth 5 -Compress
        }
        function Read-PackageText([object]$record, [string]$name, [int]$maximum, [bool]$versionAllowed, [bool]$enumAllowed) {
          try {
            $property = $record.PSObject.Properties[$name]
            if ($null -eq $property) { return $null }
            $value = $property.Value
            if ($value -is [string]) { $text = $value }
            elseif ($versionAllowed -and $value -is [System.Version]) { $text = $value.ToString() }
            elseif ($enumAllowed -and $value -is [System.Enum]) { $text = $value.ToString() }
            else { return $null }
            if ($text.Length -gt $maximum) { return $null }
            return $text
          } catch { return $null }
        }
        function Read-PackageBoolean([object]$record, [string]$name) {
          try {
            $property = $record.PSObject.Properties[$name]
            if ($null -eq $property) { return $null }
            $value = $property.Value
            if ($value -is [bool]) { return $value }
            return $null
          } catch { return $null }
        }
        $module = [System.IO.Path]::Combine($PSHOME, 'Modules', 'Appx', 'Appx.psd1')
        if (-not [System.IO.File]::Exists($module)) {
          Write-PackageEnvelope 'Unavailable' $false @()
          exit 0
        }
        try { Microsoft.PowerShell.Core\Import-Module -Name $module -ErrorAction Stop }
        catch {
          Write-PackageEnvelope 'Unavailable' $false @()
          exit 0
        }
        try {
          $queryWarnings = @()
          $reported = @(Appx\Get-AppxPackage -PackageTypeFilter Main,Bundle -ErrorAction Stop -WarningAction SilentlyContinue -WarningVariable queryWarnings |
            Microsoft.PowerShell.Utility\Select-Object -First 1001)
          $truncated = $reported.Count -gt 1000
          $projected = @($reported | Microsoft.PowerShell.Utility\Select-Object -First 1000 |
            Microsoft.PowerShell.Core\ForEach-Object {
              [pscustomobject]@{
                Name=(Read-PackageText $_ 'Name' 256 $false $false)
                PackageFullName=(Read-PackageText $_ 'PackageFullName' 256 $false $false)
                PackageFamilyName=(Read-PackageText $_ 'PackageFamilyName' 128 $false $false)
                Version=(Read-PackageText $_ 'Version' 64 $true $false)
                Publisher=(Read-PackageText $_ 'Publisher' 1024 $false $false)
                Architecture=(Read-PackageText $_ 'Architecture' 32 $false $true)
                IsBundle=(Read-PackageBoolean $_ 'IsBundle')
                NonRemovable=(Read-PackageBoolean $_ 'NonRemovable')
                IsFramework=(Read-PackageBoolean $_ 'IsFramework')
                IsResourcePackage=(Read-PackageBoolean $_ 'IsResourcePackage')
                SignatureKind=(Read-PackageText $_ 'SignatureKind' 32 $false $true)
              }
            })
          # Only warning presence affects coverage; never serialize or display warning text.
          $status = if ($queryWarnings.Count -gt 0) { 'Partial' } else { 'Available' }
          $queryWarnings = $null
          Write-PackageEnvelope $status $truncated $projected
        } catch { Write-PackageEnvelope 'Failed' $false @() }
        """;
}
