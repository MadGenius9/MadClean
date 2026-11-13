using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using MadClean.Core;
using MadClean.Validation;

namespace MadClean.WindowsSmoke;

internal static partial class NativeFixtures
{
    // One independent metadata question. Neither restricted probe nor production
    // servicing host is modified, retried, promoted or invoked by this control.
    private static void DismDefaultRunspaceImportControl(SmokeReport report, CancellationToken token)
    {
        const string check = "dism-default-runspace-import-control";
        string stage = "Host";
        int? exit = null, characters = null;
        string Transport() => $"stage={stage}; exit={Number(exit)}; outputChars={Number(characters)}. ";
        try
        {
            token.ThrowIfCancellationRequested();
            string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            if (!OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(system))
            { report.Unknown(check, Transport() + "The fixed Windows metadata host is unavailable; no import observation was made."); return; }
            string executable = Path.Combine(system, @"WindowsPowerShell\v1.0\powershell.exe");
            if (!DismCapabilitySafeFile(executable))
            { report.Unknown(check, Transport() + "The fixed metadata host path was unavailable or unsafe; no publisher trust was inferred."); return; }
            string[] arguments = ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
                Convert.ToBase64String(Encoding.Unicode.GetBytes(DismDefaultRunspaceImportScript))];
            stage = "CommandBound";
            if (!DismCapabilityCommandLineFits(executable, arguments))
            { report.Unknown(check, Transport() + "The complete command exceeded its bound; nothing was truncated or launched."); return; }
            stage = "Process";
            var result = new ReadOnlyProcess().RunAsync(executable, arguments, TimeSpan.FromSeconds(20), token).GetAwaiter().GetResult();
            exit = result.ExitCode; characters = result.Output.Length;
            token.ThrowIfCancellationRequested();
            if (result.ExitCode != 0 || result.Output.Length is < 1 or > 4096)
            { report.Unknown(check, Transport() + "No bounded default-runspace metadata response was accepted. Raw output is withheld."); return; }
            stage = "Projection";
            if (!DismDefaultImportEvidence.TryRead(result.Output, out var facts, out _) || facts is null)
            { report.Unknown(check, Transport() + "The fixed default-runspace metadata projection was unrecognized; no import or cleanup success was inferred."); return; }
            stage = "Completed";
            string detail = Transport() +
                $"profile=CreateDefault-AutoloadAll-InboxOnly; host={facts.HostQualified}; manifest={facts.ManifestQualified}; " +
                $"runspaceCreated={facts.RunspaceCreated}; opened={Flag(facts.RunspaceOpened)}; powerShellCreated={facts.PowerShellCreated}; " +
                $"attempted={facts.ImportAttempted}; returned={Flag(facts.ImportReturned)}; errors={Flag(facts.ImportHadErrors)}; " +
                $"modules={Number(facts.ReturnedModuleCount)}; bound={Flag(facts.ModuleBound)}; " +
                $"psDisposed={Flag(facts.PowerShellDisposed)}; rsClosed={Flag(facts.RunspaceClosed)}; rsDisposed={Flag(facts.RunspaceDisposed)}; " +
                $"observedObjectsSettled={facts.ResourcesSettled}; qualifiedImport={facts.IsImportBound}; exceptionHResult={Hex(facts.ExceptionHResult)}. " +
                "Import/binding observation only; original restricted checks unchanged. No health command was added, " +
                "and no publisher trust, output contract, elevation suitability, Windows integrity or action gate is qualified.";
            if (detail.Length > 1000)
            { report.Unknown(check, Transport() + "The fixed detail exceeded its export bound; no evidence was truncated or promoted."); return; }
            if (facts.IsImportBound && facts.ResourcesSettled) report.Pass(check, detail);
            else report.Unknown(check, detail);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or
            InvalidOperationException or ArgumentException or System.Security.SecurityException or OperationCanceledException or TimeoutException)
        {
            report.Unknown(check, Transport() + $"exceptionHResult={Hex(exception.HResult)}. " +
                "The metadata control did not complete; no raw exception, retry, fallback or servicing permission is exported.");
        }

        static string Number(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "unobserved";
        static string Flag(bool? value) => value?.ToString() ?? "unobserved";
        static string Hex(int? value) => value.HasValue ? "0x" + unchecked((uint)value.Value).ToString("X8", CultureInfo.InvariantCulture) : "unobserved";
    }

    // Only the fixed inbox manifest is imported. This script contains no health
    // command or output getter, and has no caller-selected path/script argument.
    private const string DismDefaultRunspaceImportScript = """
        $ErrorActionPreference='Stop'
        $ProgressPreference='SilentlyContinue'; $WarningPreference='SilentlyContinue'
        $VerbosePreference='SilentlyContinue'; $DebugPreference='SilentlyContinue'
        $InformationPreference='SilentlyContinue'; $PSModuleAutoLoadingPreference='None'
        [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
        $f=[ordered]@{
          hostQualified=$false; manifestQualified=$false; runspaceCreated=$false
          powerShellCreated=$false; importAttempted=$false; runspaceOpened=$null
          importReturned=$null; importHadErrors=$null; moduleBound=$null
          powerShellDisposed=$null; runspaceClosed=$null; runspaceDisposed=$null
          returnedModuleCount=$null; exceptionHResult=$null
        }
        $rs=$null; $ps=$null
        function Save-DefaultImportError([object]$record) {
          if ($null -eq $f.exceptionHResult -and $null -ne $record -and $null -ne $record.Exception) {
            $f.exceptionHResult=[int]$record.Exception.HResult
          }
        }
        function Ordinary-DefaultImportPath([string]$path,[bool]$directory) {
          if ([string]::IsNullOrEmpty($path) -or -not [IO.Path]::IsPathRooted($path)) { return $false }
          $full=[IO.Path]::GetFullPath($path)
          if ($full.Length -gt 1024 -or $full.Length -lt 3 -or $full[1] -ne ':' -or $full[2] -ne '\') { return $false }
          $chain=[Collections.Generic.Stack[string]]::new(); $count=0
          for ($current=$full; $null -ne $current; $current=[IO.Path]::GetDirectoryName($current)) {
            $count++; if ($count -gt 64) { return $false }; $chain.Push($current)
          }
          while ($chain.Count -gt 0) {
            $current=$chain.Pop(); $attributes=[IO.File]::GetAttributes($current)
            if (($attributes -band 0x445400) -ne 0) { return $false }
            if ($chain.Count -eq 0) {
              if ((($attributes -band [IO.FileAttributes]::Directory) -ne 0) -ne $directory) { return $false }
            } elseif (($attributes -band [IO.FileAttributes]::Directory) -eq 0) { return $false }
          }
          return $true
        }
        try {
          do {
            $system=[Environment]::GetFolderPath([System.Environment+SpecialFolder]::System)
            $homeDirectory=[IO.Path]::Combine($system,'WindowsPowerShell','v1.0')
            $f.hostQualified=($PSVersionTable.PSEdition -ceq 'Desktop') -and ($PSVersionTable.PSVersion.Major -eq 5) -and
              ($PSVersionTable.PSVersion.Minor -eq 1) -and [string]::Equals([IO.Path]::GetFullPath($PSHOME),$homeDirectory,[StringComparison]::OrdinalIgnoreCase) -and
              (Ordinary-DefaultImportPath ([IO.Path]::Combine($homeDirectory,'powershell.exe')) $false)
            if (-not $f.hostQualified) { break }
            $env:PSModulePath=[IO.Path]::Combine($homeDirectory,'Modules')
            $directory=[IO.Path]::Combine($homeDirectory,'Modules','Dism')
            $manifest=[IO.Path]::Combine($directory,'Dism.psd1')
            $f.manifestQualified=Ordinary-DefaultImportPath $manifest $false
            if (-not $f.manifestQualified) { break }
            $rs=[Management.Automation.Runspaces.RunspaceFactory]::CreateRunspace([Management.Automation.Runspaces.InitialSessionState]::CreateDefault())
            $f.runspaceCreated=$true; $f.runspaceOpened=$false
            $rs.Open(); $f.runspaceOpened=$true
            $rs.SessionStateProxy.SetVariable('ErrorActionPreference',[Management.Automation.ActionPreference]::Stop)
            $rs.SessionStateProxy.SetVariable('ProgressPreference',[Management.Automation.ActionPreference]::SilentlyContinue)
            $rs.SessionStateProxy.SetVariable('WarningPreference',[Management.Automation.ActionPreference]::Continue)
            $rs.SessionStateProxy.SetVariable('VerbosePreference',[Management.Automation.ActionPreference]::SilentlyContinue)
            $rs.SessionStateProxy.SetVariable('DebugPreference',[Management.Automation.ActionPreference]::SilentlyContinue)
            $rs.SessionStateProxy.SetVariable('InformationPreference',[Management.Automation.ActionPreference]::SilentlyContinue)
            $rs.SessionStateProxy.SetVariable('PSModuleAutoLoadingPreference','All')
            $rs.SessionStateProxy.SetVariable('PSDefaultParameterValues',@{})
            $ps=[Management.Automation.PowerShell]::Create(); $f.powerShellCreated=$true
            $ps.Runspace=$rs
            [void]$ps.AddCommand('Microsoft.PowerShell.Core\Import-Module').AddParameter('Name',$manifest).AddParameter('PassThru').AddParameter('ErrorAction','Stop')
            $f.importAttempted=$true; $f.importReturned=$false
            $modules=@($ps.Invoke()); $f.importReturned=$true
            $f.importHadErrors=[bool]$ps.HadErrors; $f.returnedModuleCount=[int]$modules.Count
            if ($f.importHadErrors -or $f.returnedModuleCount -ne 1) { break }
            $f.moduleBound=$false; $module=$modules[0]
            if ($null -eq $module -or $module.GetType() -ne [Management.Automation.PSModuleInfo]) { break }
            $f.moduleBound=($module.Name -ceq 'Dism') -and
              [string]::Equals([IO.Path]::GetFullPath($module.ModuleBase),$directory,[StringComparison]::OrdinalIgnoreCase) -and
              (Ordinary-DefaultImportPath $module.Path $false) -and
              [IO.Path]::GetFullPath($module.Path).StartsWith($directory+'\',[StringComparison]::OrdinalIgnoreCase)
          } while ($false)
        } catch { Save-DefaultImportError $_ }
        finally {
          if ($null -ne $ps) {
            $f.powerShellDisposed=$false
            try { $ps.Dispose(); $f.powerShellDisposed=$true } catch { Save-DefaultImportError $_ }
          }
          if ($null -ne $rs) {
            $f.runspaceClosed=$false; $f.runspaceDisposed=$false
            # A failed Close does not authorize another implicit close through Dispose.
            try {
              $rs.Close(); $f.runspaceClosed=$true
              $rs.Dispose(); $f.runspaceDisposed=$true
            } catch { Save-DefaultImportError $_ }
          }
        }
        $json=[Text.StringBuilder]::new()
        [void]$json.Append('{"schema":1,"context":"dism-default-runspace-import","profile":"CreateDefault-AutoloadAll-InboxOnly"')
        foreach ($entry in $f.GetEnumerator()) {
          [void]$json.Append(',"').Append($entry.Key).Append('":')
          if ($null -eq $entry.Value) { [void]$json.Append('null') }
          elseif ($entry.Value -is [bool]) { [void]$json.Append($entry.Value.ToString().ToLowerInvariant()) }
          else { [void]$json.Append(([int]$entry.Value).ToString([Globalization.CultureInfo]::InvariantCulture)) }
        }
        [void]$json.Append('}'); [Console]::Write($json.ToString())
        """;
}
