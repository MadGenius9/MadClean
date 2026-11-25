using System.ComponentModel;
using System.Text;
using MadClean.Core;

namespace MadClean.Windows;

/// <summary>
/// Metadata-only probe for the disposable validation harness. It never calls Start-MpScan.
/// </summary>
internal static class WindowsDefenderCommandProbe
{
    internal static Task<DefenderScanCommandObservation> ObserveAsync(CancellationToken token) =>
        Task.Run(() => ObserveCoreAsync(token), token);

    private static async Task<DefenderScanCommandObservation> ObserveCoreAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            return DefenderScanCommandObservation.Unavailable;

        try
        {
            using var host = SystemToolTrustedLease.OpenWindowsPowerShell(token);
            var moduleFolder = Path.Combine(Path.GetDirectoryName(host.ToolPath)!, "Modules", "Defender");
            var manifestPath = Path.Combine(moduleFolder, "Defender.psd1");
            var directories = SystemToolTrustNative.LockDirectoryChain(moduleFolder);
            try
            {
                using var manifest = SystemToolTrustNative.OpenFile(manifestPath);
                SystemToolTrustNative.ValidateAttributes(manifest, directory: false);
                SystemToolTrustNative.ValidateFinalPath(manifest, manifestPath);
                var result = await new ReadOnlyProcess().RunAsync(host.ToolPath,
                    ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
                        Convert.ToBase64String(Encoding.Unicode.GetBytes(Script))],
                    TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                return DefenderScanCommandProfile.Interpret(result);
            }
            finally
            {
                for (int index = directories.Count - 1; index >= 0; index--)
                    directories[index].Dispose();
            }
        }
        catch (SystemToolTrustFailure)
        {
            token.ThrowIfCancellationRequested();
            return DefenderScanCommandObservation.Unavailable;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or
            TimeoutException or InvalidOperationException or System.Security.SecurityException)
        {
            token.ThrowIfCancellationRequested();
            return DefenderScanCommandObservation.Unknown;
        }
    }

    private const string Script = """
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        $env:PSModulePath = [IO.Path]::Combine($PSHOME, 'Modules')
        $PSModuleAutoLoadingPreference = 'None'
        $folder = [IO.Path]::Combine($PSHOME, 'Modules', 'Defender')
        $manifest = [IO.Path]::Combine($folder, 'Defender.psd1')
        function Report([string]$state) { [Console]::WriteLine('MadClean.DefenderProfile/1:' + $state) }
        try {
          $loaded = @(Microsoft.PowerShell.Core\Import-Module -Name $manifest -PassThru -ErrorAction Stop)
          if ($loaded.Count -ne 1 -or $loaded[0].Name -cne 'Defender' -or
              -not [string]::Equals($loaded[0].Path, $manifest, [StringComparison]::OrdinalIgnoreCase)) {
            Report 'module'; exit 0
          }
        } catch { Report 'module'; exit 0 }
        try {
          $commands = @(Microsoft.PowerShell.Core\Get-Command -Name 'Start-MpScan' -Module 'Defender' -All -ListImported -ErrorAction Stop)
        } catch { Report 'lookup'; exit 0 }
        if ($commands.Count -ne 1) { Report 'lookup'; exit 0 }
        $command = $commands[0]
        if ($command -isnot [Management.Automation.FunctionInfo] -or $command.Name -cne 'Start-MpScan') {
          Report 'shape'; exit 0
        }
        try {
          if (-not [object]::ReferenceEquals($command.Module, $loaded[0]) -or
              -not $loaded[0].ExportedFunctions.ContainsKey('Start-MpScan')) {
            Report 'binding'; exit 0
          }
        } catch { Report 'binding'; exit 0 }
        try {
          foreach ($set in $command.ParameterSets) {
            $path = $null
            $type = $null
            $otherMandatory = $false
            foreach ($parameter in $set.Parameters) {
              if ($parameter.Name -ceq 'ScanPath') { $path = $parameter }
              elseif ($parameter.Name -ceq 'ScanType') { $type = $parameter }
              elseif ($parameter.IsMandatory) { $otherMandatory = $true }
            }
            if (-not $otherMandatory -and $null -ne $path -and $path.ParameterType -eq [string] -and
                $null -ne $type -and $type.ParameterType.IsEnum -and
                ([Enum]::GetNames($type.ParameterType) -ccontains 'CustomScan')) {
              Report 'observed'; exit 0
            }
          }
        } catch { }
        Report 'parameters'
        """;
}
