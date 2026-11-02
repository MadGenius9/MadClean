using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;
using MadClean.Core;

namespace MadClean.WindowsSmoke;

internal static partial class NativeFixtures
{
    // Metadata discovery only. This runner's process-tree termination is NOT a servicing
    // cancellation mechanism and must never be reused to launch CheckHealth or a repair.
    private static void DismPowerShellCapability(SmokeReport report, CancellationToken token)
    {
        const string check = "dism-powershell-capability-contract";
        try
        {
            token.ThrowIfCancellationRequested();
            string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            if (!OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(system))
            { report.Unknown(check, "The fixed Windows PowerShell metadata host is unavailable. No health check or servicing command was invoked."); return; }
            string executable = Path.Combine(system, @"WindowsPowerShell\v1.0\powershell.exe");
            if (!DismCapabilitySafeFile(executable))
            { report.Unknown(check, "The fixed metadata host path was missing or unsafe. Path qualification does not establish publisher trust; no alternative host was launched."); return; }
            var result = new ReadOnlyProcess().RunAsync(executable,
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(DismCapabilityScript))],
                TimeSpan.FromSeconds(20), token).GetAwaiter().GetResult();
            token.ThrowIfCancellationRequested();
            if (result.ExitCode != 0 || result.Output.Length is < 1 or > 16_384)
            { report.Unknown(check, "The bounded metadata query did not return a supported result. No raw output, host details or provider errors are exported; no health check was invoked."); return; }
            using var document = JsonDocument.Parse(result.Output, new JsonDocumentOptions { MaxDepth = 3 });
            var root = document.RootElement;
            string[] flags = ["HostPathQualified", "ManifestPathQualified", "ImportedModuleBound", "CommandBound", "OutputTypeResolved",
                "OutputAssemblyPathQualified", "HealthPropertyReadable", "HealthPropertyEnum", "EnumShapeMatched", "SharedParameterSetMatched",
                "SwitchParametersMatched", "NonTargetMandatoryAbsent", "PublisherTrustVerified", "HealthCheckInvoked"];
            string[] counts = ["OutputTypeCount", "PublicPropertyCount", "EnumMemberCount", "ParameterSetCount", "SharedParameterSetCount"];
            string[] diagnostics = ["Stage", "OuterExceptionKind", "ExceptionKind", "ModuleError"];
            string[] diagnosticNumbers = ["ErrorCategory", "OuterExceptionHResult", "ExceptionHResult"];
            string[] stages = ["Host", "Manifest", "ModuleImport", "ModuleBinding", "CommandMetadata", "OutputMetadata", "PropertyMetadata", "EnumMetadata", "ParameterMetadata", "Completed", "Incomplete"];
            string[] kinds = ["None", "PowerShell", "PowerShellInvocation", "PowerShellStop", "IO", "FileLoad", "FileNotFound", "TypeLoad", "BadImage", "Security", "AccessDenied", "Unknown", "Guard"];
            string[] moduleErrors = ["None", "ModuleNotFound", "InvalidManifest", "ModuleLoadFailure", "Unknown"];
            bool NullableInteger(string name) => root.TryGetProperty(name, out var value) &&
                (value.ValueKind == JsonValueKind.Null || value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _));
            bool FixedText(string name, string[] allowed) => root.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.String && value.GetString() is { } text && allowed.Contains(text, StringComparer.Ordinal);
            var names = root.ValueKind == JsonValueKind.Object ? root.EnumerateObject().Select(property => property.Name).ToArray() : [];
            if (names.Length != flags.Length + counts.Length + diagnostics.Length + diagnosticNumbers.Length + 2 || names.Distinct(StringComparer.Ordinal).Count() != names.Length ||
                names.Any(name => name is not ("Schema" or "Status") && !flags.Contains(name, StringComparer.Ordinal) && !counts.Contains(name, StringComparer.Ordinal) &&
                    !diagnostics.Contains(name, StringComparer.Ordinal) && !diagnosticNumbers.Contains(name, StringComparer.Ordinal)) ||
                !root.TryGetProperty("Schema", out var schema) || !schema.TryGetInt32(out int version) || version != 2 ||
                !root.TryGetProperty("Status", out var status) || status.ValueKind != JsonValueKind.String || status.GetString() is not ("Available" or "Unknown" or "Unavailable") ||
                flags.Any(name => !root.TryGetProperty(name, out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) ||
                counts.Any(name => !root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Null &&
                    (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int count) || count is < 0 or > 64)) ||
                diagnosticNumbers.Any(name => !NullableInteger(name)) || !FixedText("Stage", stages) || !FixedText("OuterExceptionKind", kinds) ||
                !FixedText("ExceptionKind", kinds) || !FixedText("ModuleError", moduleErrors))
            { report.Unknown(check, "The metadata projection was unrecognized or ambiguous. No type/parameter capability, publisher trust or health result was inferred."); return; }
            bool Flag(string name) => root.GetProperty(name).GetBoolean();
            int? Count(string name) => root.GetProperty(name).ValueKind == JsonValueKind.Null ? null : root.GetProperty(name).GetInt32();
            string CountText(string name) => Count(name)?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unobserved";
            string Text(string name) => root.GetProperty(name).GetString()!;
            string HResultText(string name) => Count(name) is { } code ? "0x" + unchecked((uint)code).ToString("X8") : "unobserved";
            if (Flag("PublisherTrustVerified") || Flag("HealthCheckInvoked"))
            { report.Fail(check, "The metadata-only projection contradicted its no-health-execution/no-publisher-verification invariant. No payload or host data is exported."); return; }
            bool qualified = flags.Where(name => name is not ("PublisherTrustVerified" or "HealthCheckInvoked")).All(Flag) &&
                Count("OutputTypeCount") is >= 1 and <= 8 && Count("PublicPropertyCount") is >= 1 and <= 64 && Count("EnumMemberCount") == 3 &&
                Count("ParameterSetCount") is >= 1 and <= 16 && Count("SharedParameterSetCount") is >= 1 and <= 16 &&
                Count("SharedParameterSetCount") <= Count("ParameterSetCount");
            string detail = $"metadata={status.GetString()}; stage={Text("Stage")}; host={Flag("HostPathQualified")}; manifest={Flag("ManifestPathQualified")}; module={Flag("ImportedModuleBound")}; command={Flag("CommandBound")}; outputType={Flag("OutputTypeResolved")}; assemblyPath={Flag("OutputAssemblyPathQualified")}; propertyReadable={Flag("HealthPropertyReadable")}; propertyEnum={Flag("HealthPropertyEnum")}; enumShape={Flag("EnumShapeMatched")}; sharedSet={Flag("SharedParameterSetMatched")}; switches={Flag("SwitchParametersMatched")}; noOtherMandatory={Flag("NonTargetMandatoryAbsent")}; counts(output/property/enum/sets/shared)={CountText("OutputTypeCount")}/{CountText("PublicPropertyCount")}/{CountText("EnumMemberCount")}/{CountText("ParameterSetCount")}/{CountText("SharedParameterSetCount")}; errorCategory={CountText("ErrorCategory")}; outer={Text("OuterExceptionKind")}/{HResultText("OuterExceptionHResult")}; exception={Text("ExceptionKind")}/{HResultText("ExceptionHResult")}; moduleError={Text("ModuleError")}; publisherTrustVerified=false; healthCheckInvoked=false. ";
            bool complete = status.GetString() == "Available" && qualified;
            string message = detail + (complete
                ? "Candidate metadata observed only. No output instance/getter or health/servicing command invoked. No stable result-schema, publisher, elevation, component-health or production-readiness proof."
                : "Metadata coverage remains Unknown. Diagnostics are observed exception metadata, not native error codes or a proven cause. No health/servicing command, output getter, publisher verification or gate enablement.");
            if (message.Length > 1000)
            { report.Unknown(check, "The bounded metadata summary could not preserve its complete qualification. No health check was invoked and no publisher trust or production servicing readiness is established."); return; }
            if (complete) report.Pass(check, message);
            else report.Unknown(check, message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or
            ArgumentException or System.Security.SecurityException or JsonException or OperationCanceledException or TimeoutException)
        { report.Unknown(check, "The bounded fixed-module metadata query did not complete with recognized evidence. No raw provider/exception text, paths or host identity is exported; no health check, repair, UAC or ADK payload was requested."); }
    }

    private static bool DismCapabilitySafeFile(string path)
    {
        if (!File.Exists(path)) return false;
        int depth = 0;
        for (string? current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if (++depth > 64 || (File.GetAttributes(current) & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0) return false;
        return true;
    }

    // Only one explicit module import: the fixed inbox manifest. Autoloading is disabled.
    // Output uses a fixed JSON vocabulary, never provider-derived strings or an extra utility module.
    private const string DismCapabilityScript = """
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        $WarningPreference = 'SilentlyContinue'
        $InformationPreference = 'SilentlyContinue'
        $VerbosePreference = 'SilentlyContinue'
        $DebugPreference = 'SilentlyContinue'
        $PSModuleAutoLoadingPreference = 'None'
        [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
        $flags = [ordered]@{
          HostPathQualified=$false; ManifestPathQualified=$false; ImportedModuleBound=$false; CommandBound=$false
          OutputTypeResolved=$false; OutputAssemblyPathQualified=$false; HealthPropertyReadable=$false; HealthPropertyEnum=$false
          EnumShapeMatched=$false; SharedParameterSetMatched=$false; SwitchParametersMatched=$false; NonTargetMandatoryAbsent=$false
          PublisherTrustVerified=$false; HealthCheckInvoked=$false
        }
        $counts = [ordered]@{OutputTypeCount=$null; PublicPropertyCount=$null; EnumMemberCount=$null; ParameterSetCount=$null; SharedParameterSetCount=$null}
        $status = 'Unknown'
        $stage = 'Host'
        $diagnostics = [ordered]@{Stage='Host'; OuterExceptionKind='None'; ExceptionKind='None'; ModuleError='None'; ErrorCategory=$null; OuterExceptionHResult=$null; ExceptionHResult=$null}
        $captured = @{First=$false}
        function Get-FixedExceptionKind([Exception]$exception) {
          switch -CaseSensitive ($exception.GetType().FullName) {
            'System.Management.Automation.RuntimeException' { return 'PowerShell' }
            'System.Management.Automation.CmdletInvocationException' { return 'PowerShellInvocation' }
            'System.Management.Automation.ActionPreferenceStopException' { return 'PowerShellStop' }
            'System.Management.Automation.PSSecurityException' { return 'Security' }
            'System.IO.IOException' { return 'IO' }
            'System.IO.DirectoryNotFoundException' { return 'IO' }
            'System.IO.FileLoadException' { return 'FileLoad' }
            'System.IO.FileNotFoundException' { return 'FileNotFound' }
            'System.TypeLoadException' { return 'TypeLoad' }
            'System.Reflection.ReflectionTypeLoadException' { return 'TypeLoad' }
            'System.BadImageFormatException' { return 'BadImage' }
            'System.Security.SecurityException' { return 'Security' }
            'System.UnauthorizedAccessException' { return 'AccessDenied' }
            default { return 'Unknown' }
          }
        }
        function Save-FirstMetadataError($record,[bool]$allowGuard) {
          if ($captured.First) { return }
          $captured.First = $true
          $diagnostics.Stage = $stage
          $guards = @('FixedMetadataHostUnavailable','FixedMetadataModuleUnavailable','AmbiguousImportedModule','ImportedModuleScopeUnknown','AmbiguousCommandMetadata','CommandModuleScopeUnknown','OutputMetadataBound','OutputMetadataUnknown','OutputMetadataUnresolved','OutputAssemblyScopeUnknown','PropertyMetadataBound','CandidatePropertyUnknown','CandidatePropertyShapeUnknown','EnumAssemblyScopeUnknown','EnumMetadataBound','ParameterSetMetadataBound','ParameterMetadataBound')
          $id = $record.FullyQualifiedErrorId
          if ($allowGuard -and $guards -ccontains $id) {
            # Synthetic guards carry no reported provider category/HRESULT/native code.
            $diagnostics.OuterExceptionKind='Guard'; $diagnostics.ExceptionKind='Guard'
            return
          }
          $diagnostics.ModuleError='Unknown'
          if (-not [string]::IsNullOrEmpty($id) -and $id.Length -le 512) {
            $comma = $id.IndexOf(',')
            $token = $id
            if ($comma -ge 0) { $token=$id.Substring(0,$comma) }
            switch -CaseSensitive ($token) {
              'Modules_ModuleNotFound' { $diagnostics.ModuleError='ModuleNotFound' }
              'Modules_InvalidManifest' { $diagnostics.ModuleError='InvalidManifest' }
              'Modules_ManifestModuleLoadFailure' { $diagnostics.ModuleError='ModuleLoadFailure' }
            }
          }
          if ($null -ne $record.CategoryInfo) { $diagnostics.ErrorCategory=[int]$record.CategoryInfo.Category }
          $exception=$record.Exception
          if ($null -eq $exception) { $diagnostics.OuterExceptionKind='Unknown'; $diagnostics.ExceptionKind='Unknown'; return }
          $diagnostics.OuterExceptionKind=Get-FixedExceptionKind $exception
          $diagnostics.OuterExceptionHResult=[int]$exception.HResult
          $diagnostics.ExceptionKind=$diagnostics.OuterExceptionKind
          $diagnostics.ExceptionHResult=$diagnostics.OuterExceptionHResult
          # At most four exception objects; never serialize type names/messages, FQIDs or paths.
          for ($depth=0; $null -ne $exception -and $depth -lt 4; $depth++) {
            $kind=Get-FixedExceptionKind $exception
            if ($kind -cnotin @('PowerShell','PowerShellInvocation','PowerShellStop','Unknown')) {
              $diagnostics.ExceptionKind=$kind; $diagnostics.ExceptionHResult=[int]$exception.HResult; break
            }
            $exception=$exception.InnerException
          }
        }
        function Test-FixedLocalFile([string]$path) {
          if ([string]::IsNullOrEmpty($path) -or $path.Length -gt 1024) { return $false }
          $full = [IO.Path]::GetFullPath($path)
          if ($full.Length -lt 3 -or $full[1] -ne ':' -or $full[2] -ne '\' -or -not [IO.File]::Exists($full)) { return $false }
          $depth = 0
          for ($current=$full; -not [string]::IsNullOrEmpty($current); $current=[IO.Path]::GetDirectoryName($current)) {
            $depth++
            if ($depth -gt 64 -or (([IO.File]::GetAttributes($current) -band ([IO.FileAttributes]::ReparsePoint -bor [IO.FileAttributes]::Offline)) -ne 0)) { return $false }
          }
          return $true
        }
        function Test-WithinDismDirectory([string]$path,[string]$directory) {
          if (-not (Test-FixedLocalFile $path)) { return $false }
          return [IO.Path]::GetFullPath($path).StartsWith($directory + '\',[StringComparison]::OrdinalIgnoreCase)
        }
        try {
          $system = [Environment]::GetFolderPath([System.Environment+SpecialFolder]::System)
          $expectedHome = [IO.Path]::Combine($system,'WindowsPowerShell','v1.0')
          if (-not [string]::Equals([IO.Path]::GetFullPath($PSHOME),[IO.Path]::GetFullPath($expectedHome),[StringComparison]::OrdinalIgnoreCase)) { throw 'FixedMetadataHostUnavailable' }
          $flags.HostPathQualified = Test-FixedLocalFile ([IO.Path]::Combine($expectedHome,'powershell.exe'))
          if (-not $flags.HostPathQualified) { throw 'FixedMetadataHostUnavailable' }
          $env:PSModulePath = [IO.Path]::Combine($expectedHome,'Modules')
          $directory = [IO.Path]::Combine($expectedHome,'Modules','Dism')
          $manifest = [IO.Path]::Combine($directory,'Dism.psd1')
          $stage = 'Manifest'
          $flags.ManifestPathQualified = Test-FixedLocalFile $manifest
          if (-not $flags.ManifestPathQualified) { $status='Unavailable'; throw 'FixedMetadataModuleUnavailable' }
          $stage = 'ModuleImport'
          try { $imported = @(Microsoft.PowerShell.Core\Import-Module -Name $manifest -PassThru -ErrorAction Stop) }
          catch { Save-FirstMetadataError $_ $false; $status='Unavailable'; throw 'FixedMetadataModuleUnavailable' }
          $stage = 'ModuleBinding'
          if ($imported.Count -ne 1) { throw 'AmbiguousImportedModule' }
          $module = $imported[0]
          $flags.ImportedModuleBound = ($module.Name -ceq 'Dism') -and [string]::Equals([IO.Path]::GetFullPath($module.ModuleBase),$directory,[StringComparison]::OrdinalIgnoreCase) -and (Test-WithinDismDirectory $module.Path $directory)
          if (-not $flags.ImportedModuleBound) { throw 'ImportedModuleScopeUnknown' }
          $stage = 'CommandMetadata'
          $commands = @(Microsoft.PowerShell.Core\Get-Command -Name 'Dism\Repair-WindowsImage' -CommandType Cmdlet -All -ListImported -ErrorAction Stop)
          if ($commands.Count -ne 1) { throw 'AmbiguousCommandMetadata' }
          $command = $commands[0]
          $flags.CommandBound = ($command.Name -ceq 'Repair-WindowsImage') -and [object]::ReferenceEquals($command.Module,$module)
          if (-not $flags.CommandBound) { throw 'CommandModuleScopeUnknown' }
          $stage = 'OutputMetadata'
          $outputs = @($command.OutputType)
          if ($outputs.Count -le 8) { $counts.OutputTypeCount = $outputs.Count }
          if ($outputs.Count -lt 1 -or $outputs.Count -gt 8) { throw 'OutputMetadataBound' }
          $matches = @($outputs | Microsoft.PowerShell.Core\Where-Object { $_.Name -ceq 'Microsoft.Dism.Commands.ImageObjectWithState' })
          if ($matches.Count -ne 1) { throw 'OutputMetadataUnknown' }
          $type = $matches[0].Type
          if ($null -eq $type -or -not $type.IsVisible -or $type.FullName -cne 'Microsoft.Dism.Commands.ImageObjectWithState') { throw 'OutputMetadataUnresolved' }
          $flags.OutputTypeResolved = $true
          $flags.OutputAssemblyPathQualified = (-not $type.Assembly.IsDynamic) -and (Test-WithinDismDirectory $type.Assembly.Location $directory)
          if (-not $flags.OutputAssemblyPathQualified) { throw 'OutputAssemblyScopeUnknown' }
          $stage = 'PropertyMetadata'
          $properties = @($type.GetProperties([Reflection.BindingFlags]::Public -bor [Reflection.BindingFlags]::Instance))
          if ($properties.Count -le 64) { $counts.PublicPropertyCount = $properties.Count }
          if ($properties.Count -lt 1 -or $properties.Count -gt 64) { throw 'PropertyMetadataBound' }
          $health = @($properties | Microsoft.PowerShell.Core\Where-Object { $_.Name -ceq 'ImageHealthState' })
          if ($health.Count -ne 1) { throw 'CandidatePropertyUnknown' }
          $getter = $health[0].GetGetMethod($false)
          $flags.HealthPropertyReadable = ($null -ne $getter) -and (-not $getter.IsStatic) -and ($health[0].GetIndexParameters().Length -eq 0)
          $enum = $health[0].PropertyType
          $flags.HealthPropertyEnum = $enum.IsEnum
          if (-not $flags.HealthPropertyReadable -or -not $flags.HealthPropertyEnum) { throw 'CandidatePropertyShapeUnknown' }
          $stage = 'EnumMetadata'
          if ($enum.Assembly.IsDynamic -or -not (Test-WithinDismDirectory $enum.Assembly.Location $directory)) { throw 'EnumAssemblyScopeUnknown' }
          $members = [Enum]::GetNames($enum)
          if ($members.Length -gt 16) { throw 'EnumMetadataBound' }
          $counts.EnumMemberCount = $members.Length
          $flags.EnumShapeMatched = ($members.Length -eq 3) -and ($members -ccontains 'Healthy') -and ($members -ccontains 'Repairable') -and ($members -ccontains 'NonRepairable')
          $stage = 'ParameterMetadata'
          $sets = @($command.ParameterSets)
          if ($sets.Count -le 16) { $counts.ParameterSetCount = $sets.Count }
          if ($sets.Count -lt 1 -or $sets.Count -gt 16) { throw 'ParameterSetMetadataBound' }
          $sharedSets = 0
          $validSets = 0
          foreach ($set in $sets) {
            $parameters = @($set.Parameters)
            if ($parameters.Count -gt 64) { throw 'ParameterMetadataBound' }
            $online = @($parameters | Microsoft.PowerShell.Core\Where-Object { $_.Name -ceq 'Online' })
            $check = @($parameters | Microsoft.PowerShell.Core\Where-Object { $_.Name -ceq 'CheckHealth' })
            $restart = @($parameters | Microsoft.PowerShell.Core\Where-Object { $_.Name -ceq 'NoRestart' })
            if ($online.Count -eq 1 -and $check.Count -eq 1 -and $restart.Count -eq 1) {
              $sharedSets++
              $switches = ($online[0].ParameterType -eq [System.Management.Automation.SwitchParameter]) -and ($check[0].ParameterType -eq [System.Management.Automation.SwitchParameter]) -and ($restart[0].ParameterType -eq [System.Management.Automation.SwitchParameter])
              $otherMandatory = @($parameters | Microsoft.PowerShell.Core\Where-Object { $_.IsMandatory -and $_.Name -cnotin @('Online','CheckHealth','NoRestart') })
              if ($switches -and $otherMandatory.Count -eq 0) { $validSets++ }
            }
          }
          $counts.SharedParameterSetCount = $sharedSets
          $flags.SharedParameterSetMatched = $counts.SharedParameterSetCount -gt 0
          $flags.SwitchParametersMatched = $validSets -gt 0
          $flags.NonTargetMandatoryAbsent = $validSets -gt 0
          $complete = $true
          foreach ($entry in $flags.GetEnumerator()) { if ($entry.Key -cnotin @('PublisherTrustVerified','HealthCheckInvoked') -and -not $entry.Value) { $complete=$false } }
          if ($complete) { $status='Available'; $stage='Completed' } else { $stage='Incomplete' }
        } catch { Save-FirstMetadataError $_ $true }
        if (-not $captured.First) { $diagnostics.Stage=$stage }
        $json = [System.Text.StringBuilder]::new()
        [void]$json.Append('{"Schema":2,"Status":"').Append($status).Append('"')
        foreach ($entry in $flags.GetEnumerator()) { [void]$json.Append(',"').Append($entry.Key).Append('":').Append(([bool]$entry.Value).ToString().ToLowerInvariant()) }
        foreach ($entry in $counts.GetEnumerator()) {
          [void]$json.Append(',"').Append($entry.Key).Append('":')
          if ($null -eq $entry.Value) { [void]$json.Append('null') }
          else { [void]$json.Append(([int]$entry.Value).ToString([Globalization.CultureInfo]::InvariantCulture)) }
        }
        foreach ($key in @('Stage','OuterExceptionKind','ExceptionKind','ModuleError')) {
          [void]$json.Append(',"').Append($key).Append('":"').Append($diagnostics[$key]).Append('"')
        }
        foreach ($key in @('ErrorCategory','OuterExceptionHResult','ExceptionHResult')) {
          [void]$json.Append(',"').Append($key).Append('":')
          if ($null -eq $diagnostics[$key]) { [void]$json.Append('null') }
          else { [void]$json.Append(([int]$diagnostics[$key]).ToString([Globalization.CultureInfo]::InvariantCulture)) }
        }
        [void]$json.Append('}')
        [Console]::WriteLine($json.ToString())
        """;
}
