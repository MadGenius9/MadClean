using System.Text;

namespace MadClean.Windows;

/// <summary>One fixed PS5.1 primary OS-cmdlet host; never a repair, scan, fallback or metadata-query kill runner.</summary>
internal static class ComponentStoreStatusHost
{
    internal const string EventPrefix = @"Local\MadClean.ComponentStoreCheck.";
    internal const int MaximumBodyCharacters = 16384;
    internal static string EncodedBootstrap => Convert.ToBase64String(Encoding.Unicode.GetBytes(Bootstrap));
    internal static string Body(Guid requestId, string eventName)
    {
        if (requestId == Guid.Empty || !eventName.StartsWith(EventPrefix, StringComparison.Ordinal) ||
            eventName.Length != EventPrefix.Length + 32 || !eventName.AsSpan(EventPrefix.Length).ToArray().All(c => c is >= 'a' and <= 'f' or >= '0' and <= '9'))
            throw new ArgumentException("The private operation correlation is invalid.");
        string source = Script.Replace("__REQUEST_ID__", requestId.ToString("N"), StringComparison.Ordinal)
            .Replace("__CANCEL_EVENT__", eventName, StringComparison.Ordinal).ReplaceLineEndings("\n");
        // Fixed ordinary source only: no here-strings, line continuations or multiline literals.
        string compact = string.Join('\n', source.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith('#')));
        if (compact.Length > MaximumBodyCharacters || compact.Length == 0) throw new InvalidOperationException("The fixed component-store host body exceeded its input bound.");
        return compact;
    }

    // The only source is Body above, written once by this owning parent through a private
    // redirected pipe. This is not an arbitrary caller/configuration script interface.
    private const string Bootstrap = """
        [Console]::InputEncoding=[Text.UTF8Encoding]::new($false,$true)
        $b=[char[]]::new(16385)
        $n=0
        while ($n -lt $b.Length) {
          $c=[Console]::In.Read($b,$n,$b.Length-$n)
          if ($c -eq 0) { break }
          $n+=$c
        }
        if ($n -eq 0 -or $n -gt 16384 -or $b[0] -eq [char]0xFEFF) { throw 'Fixed host input was not within bounds' }
        $s=[string]::new($b,0,$n)
        [Array]::Clear($b,0,$b.Length)
        if ($s.IndexOf([char]0) -ge 0) { throw 'Fixed host input contained a NUL' }
        [void]([ScriptBlock]::Create($s).Invoke())
        """;

    private const string Script = """
        $env:PSModulePath=[IO.Path]::Combine([Environment]::GetFolderPath([System.Environment+SpecialFolder]::System),'WindowsPowerShell','v1.0','Modules')
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        $WarningPreference = 'SilentlyContinue'
        $VerbosePreference = 'SilentlyContinue'
        $DebugPreference = 'SilentlyContinue'
        $InformationPreference = 'SilentlyContinue'
        [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false,$true)
        $r = [ordered]@{
          version=1; requestId='__REQUEST_ID__'; stage='NotStarted'; outcome='NotStarted'
          hostQualified=$null; moduleBound=$null; cmdletQualified=$null; outputContractQualified=$null; invocationStarted=$null
          pipelineState=$null; endInvokeReturned=$null; cancelEventObserved=$null; beginStopCalled=$null; endStopReturned=$null
          outputCount=$null; errorCount=$null; warningCount=$null; healthState=$null; powerShellDisposed=$null; runspaceClosed=$null; cancelHandleDisposed=$null
          managedExceptionHResult=$null
        }
        $rs=$null; $meta=$null; $lookup=$null; $ps=$null; $cancel=$null; $async=$null; $stopAsync=$null
        $endTried=$false; $guard=$false; $allDisposed=$true
        function Refuse([string]$outcome) { $script:guard=$true; $r.outcome=$outcome; throw 'Fixed component-store qualification refused' }
        function Ordinary([string]$p,[bool]$directory) {
          if (-not [IO.Path]::IsPathRooted($p)) { return $false }
          $p=[IO.Path]::GetFullPath($p)
          if ($p.StartsWith('\\') -or $p.Length -gt 1024) { return $false }
          $chain=[Collections.Generic.Stack[string]]::new(); $n=0
          for ($q=$p; $null -ne $q; $q=[IO.Path]::GetDirectoryName($q)) {
            $n++; if ($n -gt 64) { return $false }; $chain.Push($q)
          }
          while ($chain.Count -gt 0) {
            $q=$chain.Pop(); $a=[IO.File]::GetAttributes($q)
            if (($a -band 0x445400) -ne 0) { return $false }
            if ($chain.Count -eq 0) {
              if ((($a -band [IO.FileAttributes]::Directory) -ne 0) -ne $directory) { return $false }
            } elseif (($a -band [IO.FileAttributes]::Directory) -eq 0) { return $false }
          }
          return $true
        }
        function InDism([string]$p,[string]$dir) {
          return (Ordinary $p $false) -and [IO.Path]::GetFullPath($p).StartsWith($dir+'\',[StringComparison]::OrdinalIgnoreCase)
        }
        function SaveException([object]$e) {
          if ($null -eq $r.managedExceptionHResult -and $null -ne $e -and $null -ne $e.Exception) { $r.managedExceptionHResult=[int]$e.Exception.HResult }
        }
        try {
          $r.stage='AdministratorCheck'
          $id=[Security.Principal.WindowsIdentity]::GetCurrent()
          try { $admin=([Security.Principal.WindowsPrincipal]::new($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
          finally { $id.Dispose() }
          if (-not $admin) { Refuse 'Unavailable' }
          $r.stage='HostIdentity'
          $system=[Environment]::GetFolderPath([System.Environment+SpecialFolder]::System)
          $fixedPsHome=[IO.Path]::Combine($system,'WindowsPowerShell','v1.0')
          if ($PSVersionTable.PSEdition -cne 'Desktop' -or $PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSVersion.Minor -ne 1 -or
              -not [string]::Equals([IO.Path]::GetFullPath($PSHOME),[IO.Path]::GetFullPath($fixedPsHome),[StringComparison]::OrdinalIgnoreCase) -or
              -not (Ordinary ([IO.Path]::Combine($fixedPsHome,'powershell.exe')) $false)) { Refuse 'Unavailable' }
          $r.hostQualified=$true
          $env:PSModulePath=[IO.Path]::Combine($fixedPsHome,'Modules')
          $cancel=[Threading.EventWaitHandle]::OpenExisting('__CANCEL_EVENT__',[Security.AccessControl.EventWaitHandleRights]::Synchronize)
          $r.cancelEventObserved=$cancel.WaitOne(0)
          if ($r.cancelEventObserved) { $r.outcome='NotStarted'; Refuse 'NotStarted' }
          $rs=[Management.Automation.Runspaces.RunspaceFactory]::CreateRunspace([Management.Automation.Runspaces.InitialSessionState]::CreateDefault())
          $rs.Open()
          $rs.SessionStateProxy.SetVariable('ErrorActionPreference',[Management.Automation.ActionPreference]::Stop)
          $rs.SessionStateProxy.SetVariable('ProgressPreference',[Management.Automation.ActionPreference]::SilentlyContinue)
          $rs.SessionStateProxy.SetVariable('WarningPreference',[Management.Automation.ActionPreference]::Continue)
          $rs.SessionStateProxy.SetVariable('VerbosePreference',[Management.Automation.ActionPreference]::SilentlyContinue)
          $rs.SessionStateProxy.SetVariable('DebugPreference',[Management.Automation.ActionPreference]::SilentlyContinue)
          $rs.SessionStateProxy.SetVariable('InformationPreference',[Management.Automation.ActionPreference]::SilentlyContinue)
          $rs.SessionStateProxy.SetVariable('PSModuleAutoLoadingPreference','All')
          $rs.SessionStateProxy.SetVariable('PSDefaultParameterValues',@{})
          $dir=[IO.Path]::Combine($fixedPsHome,'Modules','Dism'); $manifest=[IO.Path]::Combine($dir,'Dism.psd1')
          if (-not (Ordinary $manifest $false)) { Refuse 'Unavailable' }
          $r.stage='ModuleImport'
          $meta=[Management.Automation.PowerShell]::Create(); $meta.Runspace=$rs
          [void]$meta.AddCommand('Microsoft.PowerShell.Core\Import-Module').AddParameter('Name',$manifest).AddParameter('PassThru').AddParameter('ErrorAction','Stop')
          $modules=@($meta.Invoke())
          if ($meta.HadErrors -or $modules.Count -ne 1) { Refuse 'MetadataUnqualified' }
          $module=[Management.Automation.PSObject]::AsPSObject($modules[0]).BaseObject
          $r.moduleBound=($module.Name -ceq 'Dism') -and [string]::Equals([IO.Path]::GetFullPath($module.ModuleBase),$dir,[StringComparison]::OrdinalIgnoreCase) -and (InDism $module.Path $dir)
          if (-not $r.moduleBound) { Refuse 'MetadataUnqualified' }
          $r.stage='CmdletQualification'
          $lookup=[Management.Automation.PowerShell]::Create(); $lookup.Runspace=$rs
          [void]$lookup.AddCommand('Microsoft.PowerShell.Core\Get-Command').AddParameter('Name','Dism\Repair-WindowsImage').AddParameter('CommandType',[Management.Automation.CommandTypes]::Cmdlet).AddParameter('All').AddParameter('ListImported').AddParameter('ErrorAction','Stop')
          $commands=@($lookup.Invoke())
          if ($lookup.HadErrors -or $commands.Count -ne 1) { Refuse 'MetadataUnqualified' }
          $cmd=[Management.Automation.PSObject]::AsPSObject($commands[0]).BaseObject
          $r.cmdletQualified=($cmd -is [Management.Automation.CmdletInfo]) -and ($cmd.Name -ceq 'Repair-WindowsImage') -and [object]::ReferenceEquals($cmd.Module,$module) -and
              (-not $cmd.ImplementingType.Assembly.IsDynamic) -and (InDism $cmd.ImplementingType.Assembly.Location $dir)
          if (-not $r.cmdletQualified) { Refuse 'MetadataUnqualified' }
          $outTypes=@($cmd.OutputType); if ($outTypes.Length -lt 1 -or $outTypes.Length -gt 8) { Refuse 'MetadataUnqualified' }
          $types=@(); foreach ($entry in $outTypes) { if ($entry.Name -ceq 'Microsoft.Dism.Commands.ImageObjectWithState') { $types+=,$entry.Type } }
          if ($types.Length -ne 1 -or $null -eq $types[0]) { Refuse 'MetadataUnqualified' }
          $ot=$types[0]
          if (-not $ot.IsVisible -or $ot.FullName -cne 'Microsoft.Dism.Commands.ImageObjectWithState' -or $ot.Assembly.IsDynamic -or -not (InDism $ot.Assembly.Location $dir)) { Refuse 'MetadataUnqualified' }
          $props=@($ot.GetProperties([Reflection.BindingFlags]::Public -bor [Reflection.BindingFlags]::Instance))
          if ($props.Length -gt 64) { Refuse 'MetadataUnqualified' }
          $healthProps=@(); foreach ($p in $props) { if ($p.Name -ceq 'ImageHealthState') { $healthProps+=,$p } }
          if ($healthProps.Length -ne 1) { Refuse 'MetadataUnqualified' }
          $hp=$healthProps[0]; $getter=$hp.GetGetMethod($false); $enum=$hp.PropertyType
          if ($null -eq $getter -or $getter.IsStatic -or $hp.GetIndexParameters().Length -ne 0 -or -not $enum.IsEnum -or -not $enum.IsVisible -or
              $enum.Assembly.IsDynamic -or -not (InDism $enum.Assembly.Location $dir)) { Refuse 'MetadataUnqualified' }
          $members=[Enum]::GetNames($enum)
          if ($members.Length -ne 3 -or -not ($members -ccontains 'Healthy') -or -not ($members -ccontains 'Repairable') -or -not ($members -ccontains 'NonRepairable')) { Refuse 'MetadataUnqualified' }
          $sets=@($cmd.ParameterSets); if ($sets.Length -lt 1 -or $sets.Length -gt 16) { Refuse 'MetadataUnqualified' }
          $validSet=$false
          foreach ($set in $sets) {
            $params=@($set.Parameters); if ($params.Length -gt 64) { Refuse 'MetadataUnqualified' }
            $switches=0; $extra=$false
            foreach ($p in $params) {
              if ($p.Name -cin @('Online','CheckHealth','NoRestart')) { if ($p.ParameterType -eq [Management.Automation.SwitchParameter]) { $switches++ } }
              elseif ($p.IsMandatory) { $extra=$true }
            }
            if ($switches -eq 3 -and -not $extra) { $validSet=$true }
          }
          if (-not $validSet) { Refuse 'MetadataUnqualified' }
          $r.outputContractQualified=$true
          $r.cancelEventObserved=$cancel.WaitOne(0)
          if ($r.cancelEventObserved) { Refuse 'NotStarted' }
          $ps=[Management.Automation.PowerShell]::Create(); $ps.Runspace=$rs
          [void]$ps.AddCommand($cmd).AddParameter('Online').AddParameter('CheckHealth').AddParameter('NoRestart').AddParameter('ErrorAction','Stop')
          $r.stage='Invocation'; $async=$ps.BeginInvoke(); if ($null -eq $async) { Refuse 'InvocationFailed' }; $r.invocationStarted=$true; $r.beginStopCalled=$false
          while (-not $async.IsCompleted) {
            if ($cancel.WaitOne(0)) {
              $r.cancelEventObserved=$true
              if (-not $r.beginStopCalled) {
                $r.stage='StopRequested'; $r.beginStopCalled=$true
                try { $stopAsync=$ps.BeginStop($null,$null) } catch { SaveException $_ }
              }
            }
            [Threading.Thread]::Sleep(50)
          }
          if ($null -ne $stopAsync) {
            while (-not $stopAsync.IsCompleted) { [Threading.Thread]::Sleep(50) }
            try { $ps.EndStop($stopAsync); $r.endStopReturned=$true } catch { $r.endStopReturned=$false; SaveException $_ }
          }
          $endTried=$true
          try { $output=@($ps.EndInvoke($async)); $r.endInvokeReturned=$true }
          catch { $r.endInvokeReturned=$false; SaveException $_ }
          $state=$ps.InvocationStateInfo.State
          if ($state.ToString() -cin @('NotStarted','Running','Stopping','Stopped','Completed','Failed','Disconnected')) { $r.pipelineState=$state.ToString() }
          $ec=$ps.Streams.Error.Count; if ($ec -le 128) { $r.errorCount=$ec }
          if ($r.pipelineState -ceq 'Stopped') { $r.outcome='PipelineStopped' }
          elseif ($r.endInvokeReturned -ne $true -or $r.pipelineState -cne 'Completed' -or $ec -ne 0) { $r.outcome='InvocationFailed' }
          else {
            $r.stage='OutputQualification'
            if ($output.Count -le 16) { $r.outputCount=$output.Count }
            if ($output.Count -ne 1) { Refuse 'UnexpectedOutput' }
            $obj=[Management.Automation.PSObject]::AsPSObject($output[0]).BaseObject
            if ($null -eq $obj -or $obj.GetType() -ne $ot) { Refuse 'UnexpectedOutput' }
            $value=$hp.GetValue($obj,$null)
            if ($null -eq $value -or $value.GetType() -ne $enum) { Refuse 'UnexpectedOutput' }
            $name=[Enum]::GetName($enum,$value)
            if ($name -cnotin @('Healthy','Repairable','NonRepairable')) { Refuse 'UnexpectedOutput' }
            $r.healthState=$name; $r.outcome='Completed'
          }
          if ($cancel.WaitOne(0)) { $r.cancelEventObserved=$true }
        }
        catch {
          if (-not $guard) { SaveException $_; $r.outcome=if ($r.invocationStarted -eq $true) { 'InvocationFailed' } else { 'Unavailable' } }
        }
        finally {
          if ($null -ne $async -and -not $endTried) {
            while (-not $async.IsCompleted) { [Threading.Thread]::Sleep(50) }
            if ($null -ne $stopAsync) {
              while (-not $stopAsync.IsCompleted) { [Threading.Thread]::Sleep(50) }
              if ($null -eq $r.endStopReturned) { try { $ps.EndStop($stopAsync); $r.endStopReturned=$true } catch { $r.endStopReturned=$false; SaveException $_ } }
            }
            $endTried=$true
            try { [void]$ps.EndInvoke($async); $r.endInvokeReturned=$true } catch { $r.endInvokeReturned=$false; SaveException $_ }
            try { $state=$ps.InvocationStateInfo.State; if ($state.ToString() -cin @('NotStarted','Running','Stopping','Stopped','Completed','Failed','Disconnected')) { $r.pipelineState=$state.ToString() } } catch { SaveException $_ }
          }
          if ($null -ne $ps -and $null -eq $async) {
            $known=$false
            do {
              try { $orphanState=$ps.InvocationStateInfo.State; $known=$true } catch { SaveException $_; [Threading.Thread]::Sleep(50) }
            } while (-not $known)
            while ($orphanState -in @([Management.Automation.PSInvocationState]::Running,[Management.Automation.PSInvocationState]::Stopping)) {
              $r.invocationStarted=$true
              try {
                if ($cancel.WaitOne(0)) {
                  $r.cancelEventObserved=$true
                  if ($r.beginStopCalled -ne $true) {
                    $r.beginStopCalled=$true
                    try { $stopAsync=$ps.BeginStop($null,$null) } catch { SaveException $_ }
                  }
                }
              } catch { SaveException $_ }
              [Threading.Thread]::Sleep(50)
              try { $orphanState=$ps.InvocationStateInfo.State } catch { SaveException $_ }
            }
            if ($null -ne $stopAsync -and $null -eq $r.endStopReturned) {
              while (-not $stopAsync.IsCompleted) { [Threading.Thread]::Sleep(50) }
              try { $ps.EndStop($stopAsync); $r.endStopReturned=$true } catch { $r.endStopReturned=$false; SaveException $_ }
            }
          }
          $r.warningCount=$null
          $warningTotal=0; $warningKnown=$true; $warningObserved=$false
          foreach ($item in @($ps,$lookup,$meta)) {
            if ($null -ne $item) {
              $warningObserved=$true
              try {
                $wc=$item.Streams.Warning.Count
                if ($wc -gt 128-$warningTotal) { $warningKnown=$false } else { $warningTotal+=$wc }
              } catch { $warningKnown=$false; SaveException $_ }
            }
          }
          if ($warningObserved -and $warningKnown) { $r.warningCount=$warningTotal }
          foreach ($item in @($ps,$lookup,$meta)) {
            if ($null -ne $item) { try { $item.Dispose() } catch { $allDisposed=$false; SaveException $_ } }
          }
          if ($null -ne $ps -or $null -ne $lookup -or $null -ne $meta) { $r.powerShellDisposed=$allDisposed }
          if ($null -ne $rs) { try { $rs.Close(); $rs.Dispose(); $r.runspaceClosed=$true } catch { $r.runspaceClosed=$false; SaveException $_ } }
          if ($null -ne $cancel) { try { $cancel.Dispose(); $r.cancelHandleDisposed=$true } catch { $r.cancelHandleDisposed=$false; SaveException $_ } }
          if ($r.powerShellDisposed -eq $false -or $r.runspaceClosed -eq $false -or $r.cancelHandleDisposed -eq $false) { $r.outcome='CleanupUncertain'; $r.stage='Cleanup' }
          elseif ($r.outcome -ceq 'Completed') { $r.stage='Completed' }
        }
        [Console]::Out.Write(($r | Microsoft.PowerShell.Utility\ConvertTo-Json -Compress -Depth 3))
        """;
}
