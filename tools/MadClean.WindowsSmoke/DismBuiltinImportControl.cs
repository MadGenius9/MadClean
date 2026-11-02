using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;
using MadClean.Core;

namespace MadClean.WindowsSmoke;

internal static partial class NativeFixtures
{
    // Always an independent fresh-process observation. Never a retry/fallback for the
    // original capability check, and never permission to run an image-health operation.
    private static void DismBuiltinImportControl(SmokeReport report, CancellationToken token)
    {
        const string check = "dism-builtin-import-control-contract";
        string transport = "Host";
        int? exit = null, characters = null;
        string Transport(Exception? exception = null) => $"transport={transport}; exit={exit?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unobserved"}; outputChars={characters?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unobserved"}; exception={(exception == null ? "None" : exception is Win32Exception ? "Win32" : exception is JsonException ? "Json" : exception is OperationCanceledException ? "Cancelled" : exception is TimeoutException ? "Timeout" : exception is IOException ? "IO" : "Other")}; HRESULT={(exception == null ? "unobserved" : "0x" + unchecked((uint)exception.HResult).ToString("X8"))}; Win32={(exception is Win32Exception win32 ? win32.NativeErrorCode.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unobserved")}. ";
        try
        {
            token.ThrowIfCancellationRequested();
            string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            if (!OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(system))
            { report.Unknown(check, Transport() + "The fixed Windows metadata host is unavailable; no alternative or health command was invoked."); return; }
            string executable = Path.Combine(system, @"WindowsPowerShell\v1.0\powershell.exe");
            if (!DismCapabilitySafeFile(executable))
            { report.Unknown(check, Transport() + "The fixed metadata host path was unavailable or unsafe. No publisher trust is inferred."); return; }
            string[] arguments = ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(DismBuiltinControlScript))];
            transport = "CommandBound";
            if (!DismCapabilityCommandLineFits(executable, arguments))
            { report.Unknown(check, Transport() + "The complete quoted command line including NUL exceeds its Windows bound. Nothing was truncated or launched."); return; }
            transport = "Process";
            var result = new ReadOnlyProcess().RunAsync(executable, arguments, TimeSpan.FromSeconds(20), token).GetAwaiter().GetResult();
            exit = result.ExitCode; characters = result.Output.Length;
            token.ThrowIfCancellationRequested();
            if (result.ExitCode != 0 || result.Output.Length is < 1 or > 12_288)
            { report.Unknown(check, Transport() + "The independent metadata process did not return bounded recognized evidence. No raw output is exported and no health operation ran."); return; }
            transport = "Parse";
            using var document = JsonDocument.Parse(result.Output, new JsonDocumentOptions { MaxDepth = 3 });
            var root = document.RootElement;
            string[] flags = ["HostQualified", "ManifestQualified", "BuiltinCoreBound", "CoreAssemblyBound", "ImportAttempted", "ImportReturned", "ModuleBound", "PublisherTrustVerified", "HealthCheckInvoked", "FreshErrorsCleared", "FreshRecordsLimited"];
            string[] numeric = ["ErrorCategory", "OuterHResult", "HResult", "FreshRecordsSampled", "FreshMissingRecordIndex"];
            string[] texts = ["Status", "Stage", "OuterKind", "Kind", "MissingCommand", "InvocationContext", "ContextSource", "ErrorTargetContext", "ErrorTargetSource", "FreshMissingCommand"];
            string[] kinds = ["None", "PowerShell", "PowerShellStop", "PowerShellInvocation", "CommandNotFound", "FileLoad", "FileNotFound", "TypeLoad", "Security", "IO", "Unknown"];
            string[] commandNames = ["Unobserved", "Unknown", "ImportModule", "GetCommand", "JoinPath", "SplitPath", "TestPath", "GetItem", "GetChildItem", "NewObject", "WhereObject", "OutNull", "SelectObject", "UpdateFormatData", "AddType", "ExportModuleMember", "ImportLocalizedData", "WriteVerbose", "ConvertFromStringData", "UpdateTypeData", "SetStrictMode"];
            bool Fixed(string name, params string[] values) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
                value.GetString() is { } text && values.Contains(text, StringComparer.Ordinal);
            var names = root.ValueKind == JsonValueKind.Object ? root.EnumerateObject().Select(property => property.Name).ToArray() : [];
            transport = "Projection";
            if (names.Length != 1 + flags.Length + numeric.Length + texts.Length || names.Distinct(StringComparer.Ordinal).Count() != names.Length ||
                names.Any(name => name != "Schema" && !flags.Contains(name, StringComparer.Ordinal) && !numeric.Contains(name, StringComparer.Ordinal) && !texts.Contains(name, StringComparer.Ordinal)) ||
                !root.TryGetProperty("Schema", out var schema) || !schema.TryGetInt32(out int version) || version != 3 ||
                flags.Any(name => !root.TryGetProperty(name, out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null) ||
                    value.ValueKind == JsonValueKind.Null && name is not ("ImportReturned" or "ModuleBound" or "FreshRecordsLimited")) ||
                numeric.Any(name => !root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Null && (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out _))) ||
                !Fixed("Status", "Available", "Unknown", "Unavailable") || !Fixed("Stage", "Host", "Manifest", "BuiltinMetadata", "Import", "ModuleBinding", "Completed") ||
                !Fixed("OuterKind", kinds) || !Fixed("Kind", kinds) || !Fixed("MissingCommand", commandNames) || !Fixed("InvocationContext", commandNames) ||
                !Fixed("ContextSource", "None", "OuterInvocationName", "OuterMyCommand", "NestedInvocationName", "NestedMyCommand") ||
                !Fixed("ErrorTargetContext", commandNames) || !Fixed("ErrorTargetSource", "None", "OuterTargetObject", "OuterCategoryTarget", "NestedTargetObject", "NestedCategoryTarget") ||
                !Fixed("FreshMissingCommand", commandNames))
            { report.Unknown(check, Transport() + "The independent control projection was unrecognized; no route, cause or health result was inferred."); return; }
            string Text(string name) => root.GetProperty(name).GetString()!;
            bool? Flag(string name) => root.GetProperty(name).ValueKind == JsonValueKind.Null ? null : root.GetProperty(name).GetBoolean();
            string FlagText(string name) => Flag(name)?.ToString() ?? "unobserved";
            string Number(string name, bool hex = false) => root.GetProperty(name).ValueKind == JsonValueKind.Null ? "unobserved" :
                hex ? "0x" + unchecked((uint)root.GetProperty(name).GetInt32()).ToString("X8") : root.GetProperty(name).GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture);
            int? FreshNumber(string name) => root.GetProperty(name).ValueKind == JsonValueKind.Null ? null : root.GetProperty(name).GetInt32();
            int? sampled = FreshNumber("FreshRecordsSampled"), freshIndex = FreshNumber("FreshMissingRecordIndex");
            bool freshValid = sampled is null
                ? Flag("FreshRecordsLimited") == null && freshIndex == null && Text("FreshMissingCommand") == "Unobserved"
                : sampled is >= 0 and <= 4 && Flag("FreshErrorsCleared") == true && Flag("ImportAttempted") == true &&
                  Flag("ImportReturned") == false && Flag("FreshRecordsLimited").HasValue &&
                  (Flag("FreshRecordsLimited") != true || sampled == 4) &&
                  (freshIndex.HasValue ? freshIndex.Value >= 0 && freshIndex.Value < sampled.Value && Text("FreshMissingCommand") != "Unobserved"
                      : Text("FreshMissingCommand") == "Unobserved");
            if (!freshValid)
            { report.Unknown(check, Transport() + "The bounded fresh-error sample projection was inconsistent; no cause or capability promotion is inferred."); return; }
            if (Flag("PublisherTrustVerified") != false || Flag("HealthCheckInvoked") != false)
            { report.Fail(check, Transport() + "The independent control contradicted its metadata-only/no-publisher-verification invariant; no payload or host data is exported."); return; }
            bool complete = Text("Status") == "Available" && Text("Stage") == "Completed" && flags.Take(7).All(name => Flag(name) == true);
            transport = "Completed";
            string detail = Transport() + $"control={Text("Status")}/{Text("Stage")}; host={FlagText("HostQualified")}; manifest={FlagText("ManifestQualified")}; builtinCore={FlagText("BuiltinCoreBound")}; coreAssembly={FlagText("CoreAssemblyBound")}; attempted={FlagText("ImportAttempted")}; returned={FlagText("ImportReturned")}; moduleBound={FlagText("ModuleBound")}; category={Number("ErrorCategory")}; outer={Text("OuterKind")}/{Number("OuterHResult", true)}; exception={Text("Kind")}/{Number("HResult", true)}; typedMissing={Text("MissingCommand")}; invocation={Text("InvocationContext")}/{Text("ContextSource")}; targetContext={Text("ErrorTargetContext")}/{Text("ErrorTargetSource")}; freshCleared={FlagText("FreshErrorsCleared")}; freshSampled={Number("FreshRecordsSampled")}; freshLimited={FlagText("FreshRecordsLimited")}; freshTypedMissing={Text("FreshMissingCommand")}; freshRecordIndex={Number("FreshMissingRecordIndex")}. ";
            string message = detail + (complete
                ? "Independent captured-builtin import observed only; original capability result unchanged. No health/output-getter invocation, publisher trust, stable health schema, elevation suitability or production gate established."
                : "Independent route remains Unknown. Invocation/target contexts are not proof of a missing command or cause. Original result unchanged; no fallback, health command, publisher trust or production gate.");
            if (message.Length > 1000)
            { report.Unknown(check, Transport() + "The bounded independent summary could not retain its full caveats. No health command, publisher verification or production gate."); return; }
            if (complete) report.Pass(check, message); else report.Unknown(check, message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or ArgumentException or
            System.Security.SecurityException or JsonException or OperationCanceledException or TimeoutException)
        { report.Unknown(check, Transport(ex) + "The independent metadata control did not complete. No raw exception text, fallback import, health operation or publisher trust claim."); }
    }

    private const string DismBuiltinControlScript = """
        $ErrorActionPreference='Stop'
        $ProgressPreference='SilentlyContinue'; $WarningPreference='SilentlyContinue'; $InformationPreference='SilentlyContinue'
        $VerbosePreference='SilentlyContinue'; $DebugPreference='SilentlyContinue'; $PSModuleAutoLoadingPreference='None'
        [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
        $f=[ordered]@{HostQualified=$false;ManifestQualified=$false;BuiltinCoreBound=$false;CoreAssemblyBound=$false;ImportAttempted=$false;ImportReturned=$null;ModuleBound=$null;PublisherTrustVerified=$false;HealthCheckInvoked=$false;FreshErrorsCleared=$false;FreshRecordsLimited=$null}
        $d=[ordered]@{Status='Unknown';Stage='Host';OuterKind='None';Kind='None';MissingCommand='Unobserved';InvocationContext='Unobserved';ContextSource='None';ErrorTargetContext='Unobserved';ErrorTargetSource='None';ErrorCategory=$null;OuterHResult=$null;HResult=$null;FreshRecordsSampled=$null;FreshMissingRecordIndex=$null;FreshMissingCommand='Unobserved'}
        function Test-ControlFile([string]$p) {
          if ([string]::IsNullOrEmpty($p) -or $p.Length -gt 1024) { return $false }
          $v=[IO.Path]::GetFullPath($p)
          if ($v.Length -lt 3 -or $v[1] -ne ':' -or $v[2] -ne '\' -or -not [IO.File]::Exists($v)) { return $false }
          $n=0
          for ($cur=$v; -not [string]::IsNullOrEmpty($cur); $cur=[IO.Path]::GetDirectoryName($cur)) {
            $n++; if ($n -gt 64 -or (([IO.File]::GetAttributes($cur) -band ([IO.FileAttributes]::ReparsePoint -bor [IO.FileAttributes]::Offline)) -ne 0)) { return $false }
          }
          return $true
        }
        function Get-ControlCommand([string]$v) {
          if ([string]::IsNullOrEmpty($v) -or $v.Length -gt 256) { return 'Unknown' }
          switch -CaseSensitive ($v) {
            'Import-Module' { return 'ImportModule' }; 'Microsoft.PowerShell.Core\Import-Module' { return 'ImportModule' }
            'Get-Command' { return 'GetCommand' }; 'Microsoft.PowerShell.Core\Get-Command' { return 'GetCommand' }
            'Join-Path' { return 'JoinPath' }; 'Microsoft.PowerShell.Management\Join-Path' { return 'JoinPath' }
            'Split-Path' { return 'SplitPath' }; 'Test-Path' { return 'TestPath' }; 'Get-Item' { return 'GetItem' }
            'Get-ChildItem' { return 'GetChildItem' }; 'New-Object' { return 'NewObject' }; 'Where-Object' { return 'WhereObject' }
            'Out-Null' { return 'OutNull' }; 'Select-Object' { return 'SelectObject' }; 'Update-FormatData' { return 'UpdateFormatData' }
            'Add-Type' { return 'AddType' }; 'Export-ModuleMember' { return 'ExportModuleMember' }
            'Import-LocalizedData' { return 'ImportLocalizedData' }; 'Microsoft.PowerShell.Utility\Import-LocalizedData' { return 'ImportLocalizedData' }
            'Write-Verbose' { return 'WriteVerbose' }; 'Microsoft.PowerShell.Utility\Write-Verbose' { return 'WriteVerbose' }
            'ConvertFrom-StringData' { return 'ConvertFromStringData' }; 'Microsoft.PowerShell.Utility\ConvertFrom-StringData' { return 'ConvertFromStringData' }
            'Update-TypeData' { return 'UpdateTypeData' }; 'Microsoft.PowerShell.Utility\Update-TypeData' { return 'UpdateTypeData' }
            'Microsoft.PowerShell.Utility\Update-FormatData' { return 'UpdateFormatData' }
            'Microsoft.PowerShell.Utility\Add-Type' { return 'AddType' }
            'Set-StrictMode' { return 'SetStrictMode' }; 'Microsoft.PowerShell.Core\Set-StrictMode' { return 'SetStrictMode' }
            default { return 'Unknown' }
          }
        }
        function Save-ControlTarget($r,[bool]$nested) {
          # Preserve the actual fields as objects; never enumerate/stringify a TargetObject collection.
          $targets=@($null,$null);$targets[0]=$r.TargetObject
          if ($null -ne $r.CategoryInfo) { $targets[1]=$r.CategoryInfo.TargetName }
          for ($i=0;$i -lt 2;$i++) {
            $v=$targets[$i]
            if ($v -isnot [string]) { continue }
            $context=Get-ControlCommand $v
            if ($d.ErrorTargetSource -ceq 'None' -or $context -cne 'Unknown') {
              $d.ErrorTargetContext=$context
              if ($nested) { if ($i -eq 0) { $d.ErrorTargetSource='NestedTargetObject' } else { $d.ErrorTargetSource='NestedCategoryTarget' } }
              else { if ($i -eq 0) { $d.ErrorTargetSource='OuterTargetObject' } else { $d.ErrorTargetSource='OuterCategoryTarget' } }
            }
            if ($context -cne 'Unknown') { return }
          }
        }
        function Get-ControlKind([Exception]$x) {
          switch -CaseSensitive ($x.GetType().FullName) {
            'System.Management.Automation.RuntimeException' { return 'PowerShell' }
            'System.Management.Automation.ActionPreferenceStopException' { return 'PowerShellStop' }
            'System.Management.Automation.CmdletInvocationException' { return 'PowerShellInvocation' }
            'System.Management.Automation.CommandNotFoundException' { return 'CommandNotFound' }
            'System.IO.FileLoadException' { return 'FileLoad' }; 'System.IO.FileNotFoundException' { return 'FileNotFound' }
            'System.TypeLoadException' { return 'TypeLoad' }; 'System.Reflection.ReflectionTypeLoadException' { return 'TypeLoad' }
            'System.Management.Automation.PSSecurityException' { return 'Security' }; 'System.Security.SecurityException' { return 'Security' }
            'System.IO.IOException' { return 'IO' }; default { return 'Unknown' }
          }
        }
        function Save-ControlContext($r,[bool]$nested) {
          if ($null -eq $r.InvocationInfo) { return }
          $v=Get-ControlCommand $r.InvocationInfo.InvocationName
          if ($v -cne 'Unknown') { $d.InvocationContext=$v; if ($nested) { $d.ContextSource='NestedInvocationName' } else { $d.ContextSource='OuterInvocationName' }; return }
          if ($null -ne $r.InvocationInfo.MyCommand) {
            $v=Get-ControlCommand $r.InvocationInfo.MyCommand.Name
            if ($v -cne 'Unknown') { $d.InvocationContext=$v; if ($nested) { $d.ContextSource='NestedMyCommand' } else { $d.ContextSource='OuterMyCommand' } }
          }
        }
        function Save-ControlError($r) {
          if ($d.OuterKind -cne 'None') { return }
          if ($null -ne $r.CategoryInfo) { $d.ErrorCategory=[int]$r.CategoryInfo.Category }
          Save-ControlContext $r $false
          Save-ControlTarget $r $false
          $x=$r.Exception
          if ($null -eq $x) { $d.OuterKind='Unknown';$d.Kind='Unknown';return }
          $d.OuterKind=Get-ControlKind $x; $d.Kind=$d.OuterKind; $d.OuterHResult=[int]$x.HResult;$d.HResult=$d.OuterHResult
          $seen=@()
          for ($n=0; $null -ne $x -and $n -lt 4; $n++) {
            $repeat=$false
            foreach ($prior in $seen) { if ([object]::ReferenceEquals($prior,$x)) { $repeat=$true;break } }
            if ($repeat) { break }; $seen+=,$x
            $kind=Get-ControlKind $x
            if ($kind -ceq 'CommandNotFound' -and $x -is [System.Management.Automation.CommandNotFoundException]) { $d.MissingCommand=Get-ControlCommand $x.CommandName }
            if ($kind -cnotin @('PowerShell','PowerShellStop','PowerShellInvocation','Unknown')) { $d.Kind=$kind;$d.HResult=[int]$x.HResult;break }
            $next=$x.InnerException
            if ($kind -ceq 'PowerShellStop') {
              $r2=$x.ErrorRecord
              if ($null -ne $r2) { Save-ControlContext $r2 $true; Save-ControlTarget $r2 $true; if ($null -ne $r2.Exception) { $next=$r2.Exception } }
            }
            $x=$next
          }
        }
        function Save-ControlFresh([object[]]$records) {
          for ($i=0;$i -lt $records.Count -and $i -lt 4;$i++) {
            $r=$records[$i];if ($r -isnot [System.Management.Automation.ErrorRecord]) { continue }
            $x=$r.Exception;$seen=@()
            for ($n=0;$null -ne $x -and $n -lt 4;$n++) {
              $repeat=$false;foreach ($prior in $seen) { if ([object]::ReferenceEquals($prior,$x)) { $repeat=$true;break } }
              if ($repeat) { break };$seen+=,$x
              if ($x -is [System.Management.Automation.CommandNotFoundException]) {
                $d.FreshMissingCommand=Get-ControlCommand $x.CommandName;$d.FreshMissingRecordIndex=$i;return
              }
              $next=$x.InnerException
              if ($x -is [System.Management.Automation.ActionPreferenceStopException] -and $null -ne $x.ErrorRecord -and $null -ne $x.ErrorRecord.Exception) { $next=$x.ErrorRecord.Exception }
              $x=$next
            }
          }
        }
        try {
          do {
            $sys=[Environment]::GetFolderPath([System.Environment+SpecialFolder]::System)
            $homeDir=[IO.Path]::Combine($sys,'WindowsPowerShell','v1.0')
            $f.HostQualified=($PSVersionTable.PSEdition -ceq 'Desktop') -and ($PSVersionTable.PSVersion.Major -eq 5) -and ($PSVersionTable.PSVersion.Minor -eq 1) -and [string]::Equals([IO.Path]::GetFullPath($PSHOME),$homeDir,[StringComparison]::OrdinalIgnoreCase) -and (Test-ControlFile ([IO.Path]::Combine($homeDir,'powershell.exe')))
            if (-not $f.HostQualified) { break }
            $env:PSModulePath=[IO.Path]::Combine($homeDir,'Modules')
            $dir=[IO.Path]::Combine($homeDir,'Modules','Dism');$manifest=[IO.Path]::Combine($dir,'Dism.psd1')
            $d.Stage='Manifest';$f.ManifestQualified=Test-ControlFile $manifest
            if (-not $f.ManifestQualified) { $d.Status='Unavailable';break }
            $d.Stage='BuiltinMetadata'
            $builtin=$ExecutionContext.InvokeCommand.GetCommand('Import-Module',[System.Management.Automation.CommandTypes]::Cmdlet)
            $f.BuiltinCoreBound=($builtin -is [System.Management.Automation.CmdletInfo]) -and ($builtin.Name -ceq 'Import-Module') -and ($null -ne $builtin.PSSnapIn) -and ($builtin.PSSnapIn.Name -ceq 'Microsoft.PowerShell.Core')
            if (-not $f.BuiltinCoreBound) { break }
            $f.CoreAssemblyBound=($null -ne $builtin.ImplementingType) -and [object]::ReferenceEquals($builtin.ImplementingType.Assembly,[System.Management.Automation.PSObject].Assembly)
            if (-not $f.CoreAssemblyBound) { break }
            $d.Stage='Import'
            $Error.Clear();$f.FreshErrorsCleared=($Error.Count -eq 0);$f.ImportAttempted=$true
            try { $imported=@(& $builtin -Name $manifest -PassThru -ErrorAction Stop);$f.ImportReturned=$true }
            catch {
              $original=$_;$f.ImportReturned=$false;$d.Status='Unavailable';$fresh=@()
              if ($f.FreshErrorsCleared -and $f.ImportAttempted) {
                try {
                  $count=$Error.Count
                  for ($i=0;$i -lt $count -and $i -lt 4;$i++) { $fresh+=,$Error[$i] }
                  $d.FreshRecordsSampled=$fresh.Count;$f.FreshRecordsLimited=($count -gt 4)
                } catch { $fresh=@();$d.FreshRecordsSampled=$null;$f.FreshRecordsLimited=$null }
              }
              Save-ControlError $original
              if ($null -ne $d.FreshRecordsSampled) {
                try { Save-ControlFresh $fresh } catch { $d.FreshMissingCommand='Unobserved';$d.FreshMissingRecordIndex=$null }
              }
              break
            }
            $d.Stage='ModuleBinding';$f.ModuleBound=$false
            if ($imported.Count -ne 1) { break }
            $mod=$imported[0]
            $f.ModuleBound=($mod.Name -ceq 'Dism') -and [string]::Equals([IO.Path]::GetFullPath($mod.ModuleBase),$dir,[StringComparison]::OrdinalIgnoreCase) -and (Test-ControlFile $mod.Path) -and ([IO.Path]::GetFullPath($mod.Path).StartsWith($dir+'\',[StringComparison]::OrdinalIgnoreCase))
            if (-not $f.ModuleBound) { break }
            $d.Status='Available';$d.Stage='Completed'
          } while ($false)
        } catch { Save-ControlError $_ }
        $json=[Text.StringBuilder]::new();[void]$json.Append('{"Schema":3')
        foreach ($e in $f.GetEnumerator()) {
          [void]$json.Append(',"').Append($e.Key).Append('":')
          if ($null -eq $e.Value) { [void]$json.Append('null') } else { [void]$json.Append(([bool]$e.Value).ToString().ToLowerInvariant()) }
        }
        foreach ($key in @('Status','Stage','OuterKind','Kind','MissingCommand','InvocationContext','ContextSource','ErrorTargetContext','ErrorTargetSource','FreshMissingCommand')) { [void]$json.Append(',"').Append($key).Append('":"').Append($d[$key]).Append('"') }
        foreach ($key in @('ErrorCategory','OuterHResult','HResult','FreshRecordsSampled','FreshMissingRecordIndex')) {
          [void]$json.Append(',"').Append($key).Append('":')
          if ($null -eq $d[$key]) { [void]$json.Append('null') } else { [void]$json.Append(([int]$d[$key]).ToString([Globalization.CultureInfo]::InvariantCulture)) }
        }
        [void]$json.Append('}');[Console]::WriteLine($json.ToString())
        """;
}
