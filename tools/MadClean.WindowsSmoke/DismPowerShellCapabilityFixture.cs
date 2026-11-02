using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;
using MadClean.Core;

namespace MadClean.WindowsSmoke;

internal static partial class NativeFixtures
{
    private enum DismCapabilityTransportStage { Host, Formatting, CommandLineBound, Process, Exit, OutputBound, Parse, Projection, Completed }

    // Metadata discovery only. This runner's process-tree termination is NOT a servicing
    // cancellation mechanism and must never be reused to launch CheckHealth or a repair.
    private static void DismPowerShellCapability(SmokeReport report, CancellationToken token)
    {
        const string check = "dism-powershell-capability-contract";
        var transportStage = DismCapabilityTransportStage.Host;
        int? observedExit = null, observedCharacters = null;
        string TransportEvidence(Exception? exception = null)
        {
            string kind = exception switch
            {
                null => "None", Win32Exception => "Win32", JsonException => "Json", TimeoutException => "Timeout",
                OperationCanceledException => "Cancelled", UnauthorizedAccessException => "AccessDenied", IOException => "IO",
                System.Security.SecurityException => "Security", ArgumentException => "Argument", InvalidOperationException => "InvalidOperation", _ => "Unknown"
            };
            string code = exception == null ? "unobserved" : "0x" + unchecked((uint)exception.HResult).ToString("X8");
            string native = exception is Win32Exception win32 ? win32.NativeErrorCode.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unobserved";
            string exit = observedExit?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unobserved";
            string characters = observedCharacters?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unobserved";
            return $"transport={transportStage}; transportException={kind}/{code}; win32={native}; exit={exit}; outputChars={characters}. ";
        }
        try
        {
            token.ThrowIfCancellationRequested();
            string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            if (!OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(system))
            { report.Unknown(check, TransportEvidence() + "The fixed Windows PowerShell metadata host is unavailable. No health check or servicing command was invoked."); return; }
            string executable = Path.Combine(system, @"WindowsPowerShell\v1.0\powershell.exe");
            if (!DismCapabilitySafeFile(executable))
            { report.Unknown(check, TransportEvidence() + "The fixed metadata host path was missing or unsafe. Path qualification does not establish publisher trust; no alternative host was launched."); return; }
            transportStage = DismCapabilityTransportStage.Formatting;
            if (!DismCapabilityFormatFixedScript(out string formatted))
            { report.Unknown(check, TransportEvidence() + "The fixed source contains an unsupported formatting construct; no script was shortened, truncated or launched. No health check was invoked."); return; }
            string[] arguments = ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(formatted))];
            transportStage = DismCapabilityTransportStage.CommandLineBound;
            if (!DismCapabilityCommandLineFits(executable, arguments))
            { report.Unknown(check, TransportEvidence() + "The complete conservatively quoted fixed command line exceeds the 32,767 UTF-16-character limit including NUL. The encoded script was not truncated or launched; no health check was invoked."); return; }
            transportStage = DismCapabilityTransportStage.Process;
            var result = new ReadOnlyProcess().RunAsync(executable, arguments,
                TimeSpan.FromSeconds(20), token).GetAwaiter().GetResult();
            observedExit = result.ExitCode;
            observedCharacters = result.Output.Length;
            token.ThrowIfCancellationRequested();
            transportStage = DismCapabilityTransportStage.Exit;
            if (result.ExitCode != 0)
            { report.Unknown(check, TransportEvidence() + "The bounded metadata process did not exit successfully. No raw output or host/provider data is exported; no health check was invoked."); return; }
            transportStage = DismCapabilityTransportStage.OutputBound;
            if (result.Output.Length is < 1 or > 16_384)
            { report.Unknown(check, TransportEvidence() + "The metadata output was empty or outside its bound; it was not parsed or exported. No health check was invoked."); return; }
            transportStage = DismCapabilityTransportStage.Parse;
            using var document = JsonDocument.Parse(result.Output, new JsonDocumentOptions { MaxDepth = 3 });
            transportStage = DismCapabilityTransportStage.Projection;
            var root = document.RootElement;
            string[] flags = ["HostPathQualified", "ManifestPathQualified", "ImportedModuleBound", "CommandBound", "OutputTypeResolved",
                "OutputAssemblyPathQualified", "HealthPropertyReadable", "HealthPropertyEnum", "EnumShapeMatched", "SharedParameterSetMatched",
                "SwitchParametersMatched", "NonTargetMandatoryAbsent", "PublisherTrustVerified", "HealthCheckInvoked"];
            string[] counts = ["OutputTypeCount", "PublicPropertyCount", "EnumMemberCount", "ParameterSetCount", "SharedParameterSetCount"];
            string[] diagnostics = ["Stage", "OuterExceptionKind", "ExceptionKind", "ModuleError"];
            string[] diagnosticNumbers = ["ErrorCategory", "OuterExceptionHResult", "ExceptionHResult"];
            string[] lookupFlags = ["QualifiedImportMetadata", "UnqualifiedImportMetadata"];
            string[] stages = ["Host", "Manifest", "ModuleImport", "ModuleBinding", "CommandMetadata", "OutputMetadata", "PropertyMetadata", "EnumMetadata", "ParameterMetadata", "Completed", "Incomplete"];
            string[] kinds = ["None", "PowerShell", "PowerShellInvocation", "PowerShellStop", "CommandNotFound", "IO", "FileLoad", "FileNotFound", "TypeLoad", "BadImage", "Security", "AccessDenied", "Unknown", "Guard"];
            string[] moduleErrors = ["None", "ModuleNotFound", "InvalidManifest", "ModuleLoadFailure", "CommandNotFound", "AutoLoadFailed", "MatchingAutoLoadFailed", "Unknown"];
            bool NullableInteger(string name) => root.TryGetProperty(name, out var value) &&
                (value.ValueKind == JsonValueKind.Null || value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _));
            bool FixedText(string name, string[] allowed) => root.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.String && value.GetString() is { } text && allowed.Contains(text, StringComparer.Ordinal);
            var names = root.ValueKind == JsonValueKind.Object ? root.EnumerateObject().Select(property => property.Name).ToArray() : [];
            if (names.Length != flags.Length + counts.Length + diagnostics.Length + diagnosticNumbers.Length + lookupFlags.Length + 2 || names.Distinct(StringComparer.Ordinal).Count() != names.Length ||
                names.Any(name => name is not ("Schema" or "Status") && !flags.Contains(name, StringComparer.Ordinal) && !counts.Contains(name, StringComparer.Ordinal) &&
                    !diagnostics.Contains(name, StringComparer.Ordinal) && !diagnosticNumbers.Contains(name, StringComparer.Ordinal) && !lookupFlags.Contains(name, StringComparer.Ordinal)) ||
                !root.TryGetProperty("Schema", out var schema) || !schema.TryGetInt32(out int version) || version != 3 ||
                !root.TryGetProperty("Status", out var status) || status.ValueKind != JsonValueKind.String || status.GetString() is not ("Available" or "Unknown" or "Unavailable") ||
                flags.Any(name => !root.TryGetProperty(name, out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) ||
                counts.Any(name => !root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Null &&
                    (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int count) || count is < 0 or > 64)) ||
                diagnosticNumbers.Any(name => !NullableInteger(name)) || !FixedText("Stage", stages) || !FixedText("OuterExceptionKind", kinds) ||
                !FixedText("ExceptionKind", kinds) || !FixedText("ModuleError", moduleErrors) ||
                lookupFlags.Any(name => !root.TryGetProperty(name, out var value) || value.ValueKind is not (JsonValueKind.Null or JsonValueKind.True or JsonValueKind.False)))
            { report.Unknown(check, TransportEvidence() + "The metadata projection was unrecognized or ambiguous. No type/parameter capability, publisher trust or health result was inferred."); return; }
            bool Flag(string name) => root.GetProperty(name).GetBoolean();
            int? Count(string name) => root.GetProperty(name).ValueKind == JsonValueKind.Null ? null : root.GetProperty(name).GetInt32();
            string CountText(string name) => Count(name)?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unobserved";
            string Text(string name) => root.GetProperty(name).GetString()!;
            string HResultText(string name) => Count(name) is { } code ? "0x" + unchecked((uint)code).ToString("X8") : "unobserved";
            string LookupText(string name) => root.GetProperty(name).ValueKind == JsonValueKind.Null ? "unobserved" : root.GetProperty(name).GetBoolean().ToString();
            if (Flag("PublisherTrustVerified") || Flag("HealthCheckInvoked"))
            { report.Fail(check, TransportEvidence() + "The metadata-only projection contradicted its no-health-execution/no-publisher-verification invariant. No payload or host data is exported."); return; }
            bool qualified = flags.Where(name => name is not ("PublisherTrustVerified" or "HealthCheckInvoked")).All(Flag) &&
                Count("OutputTypeCount") is >= 1 and <= 8 && Count("PublicPropertyCount") is >= 1 and <= 64 && Count("EnumMemberCount") == 3 &&
                Count("ParameterSetCount") is >= 1 and <= 16 && Count("SharedParameterSetCount") is >= 1 and <= 16 &&
                Count("SharedParameterSetCount") <= Count("ParameterSetCount");
            string detail = $"metadata={status.GetString()}; stage={Text("Stage")}; host={Flag("HostPathQualified")}; manifest={Flag("ManifestPathQualified")}; module={Flag("ImportedModuleBound")}; command={Flag("CommandBound")}; outputType={Flag("OutputTypeResolved")}; assemblyPath={Flag("OutputAssemblyPathQualified")}; propertyReadable={Flag("HealthPropertyReadable")}; propertyEnum={Flag("HealthPropertyEnum")}; enumShape={Flag("EnumShapeMatched")}; sharedSet={Flag("SharedParameterSetMatched")}; switches={Flag("SwitchParametersMatched")}; noOtherMandatory={Flag("NonTargetMandatoryAbsent")}; counts(output/property/enum/sets/shared)={CountText("OutputTypeCount")}/{CountText("PublicPropertyCount")}/{CountText("EnumMemberCount")}/{CountText("ParameterSetCount")}/{CountText("SharedParameterSetCount")}; importMetadata(qualified/unqualified)={LookupText("QualifiedImportMetadata")}/{LookupText("UnqualifiedImportMetadata")}; errorCategory={CountText("ErrorCategory")}; outer={Text("OuterExceptionKind")}/{HResultText("OuterExceptionHResult")}; exception={Text("ExceptionKind")}/{HResultText("ExceptionHResult")}; moduleError={Text("ModuleError")}; publisherTrustVerified=false; healthCheckInvoked=false. ";
            bool complete = status.GetString() == "Available" && qualified;
            transportStage = DismCapabilityTransportStage.Completed;
            string message = TransportEvidence() + detail + (complete
                ? "Candidate metadata observed only. No output instance/getter or health/servicing command invoked. No stable result-schema, publisher, elevation, component-health or production-readiness proof."
                : "Metadata coverage remains Unknown. Diagnostics are observed exception metadata, not native error codes or a proven cause. No health/servicing command, output getter, publisher verification or gate enablement.");
            if (message.Length > 1000)
            { report.Unknown(check, TransportEvidence() + "The bounded metadata summary could not preserve its complete qualification. No health check was invoked and no publisher trust or production servicing readiness is established."); return; }
            if (complete) report.Pass(check, message);
            else report.Unknown(check, message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or
            ArgumentException or System.Security.SecurityException or JsonException or OperationCanceledException or TimeoutException)
        { report.Unknown(check, TransportEvidence(ex) + "The fixed-module metadata query did not complete. These are actual exception metadata, not a guessed native cause. No raw text/paths, health check, repair, UAC or ADK payload."); }
    }

    // Formatting of this fixed constant only, not a PowerShell minifier. Preserve statement
    // boundaries and directives, reject unsupported multiline constructs, and always join LF.
    private static bool DismCapabilityFormatFixedScript(out string formatted)
    {
        List<string> lines = [];
        foreach (string source in DismCapabilityScript.Split('\n'))
        {
            string line = source.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('#') && !line.StartsWith("#requires", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.Contains('`') || line.Contains("@'", StringComparison.Ordinal) || line.Contains("@\"", StringComparison.Ordinal))
            { formatted = ""; return false; }
            char quote = '\0';
            for (int index = 0; index < line.Length; index++)
            {
                char value = line[index];
                if (quote == '\0')
                {
                    if (value == '#') break;
                    if (value is '\'' or '"') quote = value;
                }
                else if (value == quote)
                {
                    if (index + 1 < line.Length && line[index + 1] == quote) index++;
                    else quote = '\0';
                }
            }
            if (quote != '\0') { formatted = ""; return false; }
            lines.Add(line);
        }
        formatted = string.Join('\n', lines);
        return formatted.Length != 0;
    }

    private static bool DismCapabilityCommandLineFits(string executable, IReadOnlyList<string> arguments)
    {
        // Quote EVERY token conservatively, including backslashes before quotes/end, as in
        // Windows argument quoting. Include executable, separators and the terminal WCHAR NUL.
        static long QuotedLength(string value)
        {
            if (value.Contains('\0')) return 32_768;
            long length = 2, backslashes = 0;
            foreach (char character in value)
            {
                if (character == '\\') { backslashes++; continue; }
                length += character == '"' ? backslashes * 2 + 2 : backslashes + 1;
                backslashes = 0;
            }
            return length + backslashes * 2;
        }
        long total = QuotedLength(executable) + 1;
        foreach (string argument in arguments) total += 1 + QuotedLength(argument);
        return total <= 32_767;
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
    // Fixed private locals use short readable identifiers to fit EncodedCommand: diag/diagnostics,
    // flg/flags, cnt/counts, ex/exception, rec/record, prm/parameters, dir/directory, psDir/expectedHome,
    // man/manifest, props/properties, imp/imported, outTypes/outputs, cmd/command, get/getter,
    // enm/members and ln/lk for diagnostic lookup names/keys. No automatic variable was renamed.
    // Additional private aliases: allow/allowGuard, cap/captured, hp/health, on/online,
    // nr/restart, mandatory/otherMandatory, valid/validSets, shared/sharedSets, cur/current,
    // p/path, mod/module, nx/next, prev/prior, dup/repeat, ix/index, js/json, en/entry,
    // cm/comma, tok/token, ot/type, ci/info, lookup/lookups and outs/output declarations.
    // Get-McKind/Get-McError/Save-McError/Test-McFile/Test-McPath are the same private helpers.
    // mcGuards stores the exact existing guard strings once; indexed throws preserve each FQID.
    private const string DismCapabilityScript = """
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        $WarningPreference = 'SilentlyContinue'
        $InformationPreference = 'SilentlyContinue'
        $VerbosePreference = 'SilentlyContinue'
        $DebugPreference = 'SilentlyContinue'
        $PSModuleAutoLoadingPreference = 'None'
        [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
        $flg = [ordered]@{
          HostPathQualified=$false; ManifestPathQualified=$false; ImportedModuleBound=$false; CommandBound=$false
          OutputTypeResolved=$false; OutputAssemblyPathQualified=$false; HealthPropertyReadable=$false; HealthPropertyEnum=$false
          EnumShapeMatched=$false; SharedParameterSetMatched=$false; SwitchParametersMatched=$false; NonTargetMandatoryAbsent=$false
          PublisherTrustVerified=$false; HealthCheckInvoked=$false
        }
        $cnt = [ordered]@{OutputTypeCount=$null; PublicPropertyCount=$null; EnumMemberCount=$null; ParameterSetCount=$null; SharedParameterSetCount=$null}
        $status = 'Unknown'
        $stage = 'Host'
        $diag = [ordered]@{Stage='Host'; OuterExceptionKind='None'; ExceptionKind='None'; ModuleError='None'; ErrorCategory=$null; OuterExceptionHResult=$null; ExceptionHResult=$null}
        $cap = @{First=$false}
        $lookup = [ordered]@{QualifiedImportMetadata=$null; UnqualifiedImportMetadata=$null}
        $mcGuards = @('FixedMetadataHostUnavailable','FixedMetadataModuleUnavailable','AmbiguousImportedModule','ImportedModuleScopeUnknown','AmbiguousCommandMetadata','CommandModuleScopeUnknown','OutputMetadataBound','OutputMetadataUnknown','OutputMetadataUnresolved','OutputAssemblyScopeUnknown','PropertyMetadataBound','CandidatePropertyUnknown','CandidatePropertyShapeUnknown','EnumAssemblyScopeUnknown','EnumMetadataBound','ParameterSetMetadataBound','ParameterMetadataBound')
        function Get-McKind([Exception]$ex) {
          switch -CaseSensitive ($ex.GetType().FullName) {
            'System.Management.Automation.RuntimeException' { return 'PowerShell' }
            'System.Management.Automation.CmdletInvocationException' { return 'PowerShellInvocation' }
            'System.Management.Automation.ActionPreferenceStopException' { return 'PowerShellStop' }
            'System.Management.Automation.CommandNotFoundException' { return 'CommandNotFound' }
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
        function Get-McError($rec) {
          $id=$rec.FullyQualifiedErrorId
          if ([string]::IsNullOrEmpty($id) -or $id.Length -gt 512) { return 'Unknown' }
          $cm=$id.IndexOf(','); $tok=$id
          if ($cm -ge 0) { $tok=$id.Substring(0,$cm) }
          switch -CaseSensitive ($tok) {
            'Modules_ModuleNotFound' { return 'ModuleNotFound' }
            'Modules_InvalidManifest' { return 'InvalidManifest' }
            'Modules_ManifestModuleLoadFailure' { return 'ModuleLoadFailure' }
            'CommandNotFoundException' { return 'CommandNotFound' }
            'CouldNotAutoLoadModule' { return 'AutoLoadFailed' }
            'CouldNotAutoloadMatchingModule' { return 'MatchingAutoLoadFailed' }
            default { return 'Unknown' }
          }
        }
        function Save-McError($rec,[bool]$allow) {
          if ($cap.First) { return }
          $cap.First = $true
          $diag.Stage = $stage
          $id = $rec.FullyQualifiedErrorId
          if ($allow -and $mcGuards -ccontains $id) {
            # Synthetic guards carry no reported provider category/HRESULT/native code.
            $diag.OuterExceptionKind='Guard'; $diag.ExceptionKind='Guard'
            return
          }
          $diag.ModuleError=Get-McError $rec
          if ($null -ne $rec.CategoryInfo) { $diag.ErrorCategory=[int]$rec.CategoryInfo.Category }
          $ex=$rec.Exception
          if ($null -eq $ex) { $diag.OuterExceptionKind='Unknown'; $diag.ExceptionKind='Unknown'; return }
          $diag.OuterExceptionKind=Get-McKind $ex
          $diag.OuterExceptionHResult=[int]$ex.HResult
          $diag.ExceptionKind=$diag.OuterExceptionKind
          $diag.ExceptionHResult=$diag.OuterExceptionHResult
          # Four reference-unique nodes maximum, including the actual Stop ErrorRecord edge.
          $seen=@()
          for ($depth=0; $null -ne $ex -and $depth -lt 4; $depth++) {
            $dup=$false
            foreach ($prev in $seen) { if ([object]::ReferenceEquals($prev,$ex)) { $dup=$true; break } }
            if ($dup) { break }
            $seen+=,$ex
            $kind=Get-McKind $ex
            if ($kind -cnotin @('PowerShell','PowerShellInvocation','PowerShellStop','Unknown')) {
              $diag.ExceptionKind=$kind; $diag.ExceptionHResult=[int]$ex.HResult; break
            }
            $nx=$ex.InnerException
            if ($kind -ceq 'PowerShellStop') {
              $nested=$ex.ErrorRecord
              if ($null -ne $nested) {
                if ($diag.ModuleError -ceq 'Unknown') { $diag.ModuleError=Get-McError $nested }
                if ($null -ne $nested.Exception) { $nx=$nested.Exception }
              }
            }
            $ex=$nx
          }
        }
        function Test-McFile([string]$p) {
          if ([string]::IsNullOrEmpty($p) -or $p.Length -gt 1024) { return $false }
          $full = [IO.Path]::GetFullPath($p)
          if ($full.Length -lt 3 -or $full[1] -ne ':' -or $full[2] -ne '\' -or -not [IO.File]::Exists($full)) { return $false }
          $depth = 0
          for ($cur=$full; -not [string]::IsNullOrEmpty($cur); $cur=[IO.Path]::GetDirectoryName($cur)) {
            $depth++
            if ($depth -gt 64 -or (([IO.File]::GetAttributes($cur) -band ([IO.FileAttributes]::ReparsePoint -bor [IO.FileAttributes]::Offline)) -ne 0)) { return $false }
          }
          return $true
        }
        function Test-McPath([string]$p,[string]$dir) {
          if (-not (Test-McFile $p)) { return $false }
          return [IO.Path]::GetFullPath($p).StartsWith($dir + '\',[StringComparison]::OrdinalIgnoreCase)
        }
        try {
          $system = [Environment]::GetFolderPath([System.Environment+SpecialFolder]::System)
          $psDir = [IO.Path]::Combine($system,'WindowsPowerShell','v1.0')
          if (-not [string]::Equals([IO.Path]::GetFullPath($PSHOME),[IO.Path]::GetFullPath($psDir),[StringComparison]::OrdinalIgnoreCase)) { throw $mcGuards[0] }
          $flg.HostPathQualified = Test-McFile ([IO.Path]::Combine($psDir,'powershell.exe'))
          if (-not $flg.HostPathQualified) { throw $mcGuards[0] }
          $env:PSModulePath = [IO.Path]::Combine($psDir,'Modules')
          $dir = [IO.Path]::Combine($psDir,'Modules','Dism')
          $man = [IO.Path]::Combine($dir,'Dism.psd1')
          $stage = 'Manifest'
          $flg.ManifestPathQualified = Test-McFile $man
          if (-not $flg.ManifestPathQualified) { $status='Unavailable'; throw $mcGuards[1] }
          $stage = 'ModuleImport'
          $ln=@('Microsoft.PowerShell.Core\Import-Module','Import-Module')
          $lk=@('QualifiedImportMetadata','UnqualifiedImportMetadata')
          for ($ix=0; $ix -lt 2; $ix++) {
            try {
              $ci=$ExecutionContext.InvokeCommand.GetCommand($ln[$ix],[System.Management.Automation.CommandTypes]::Cmdlet)
              $lookup[$lk[$ix]]=($null -ne $ci -and $ci.Name -ceq 'Import-Module')
            } catch { }
          }
          try { $imp = @(Microsoft.PowerShell.Core\Import-Module -Name $man -PassThru -ErrorAction Stop) }
          catch { Save-McError $_ $false; $status='Unavailable'; throw $mcGuards[1] }
          $stage = 'ModuleBinding'
          if ($imp.Count -ne 1) { throw $mcGuards[2] }
          $mod = $imp[0]
          $flg.ImportedModuleBound = ($mod.Name -ceq 'Dism') -and [string]::Equals([IO.Path]::GetFullPath($mod.ModuleBase),$dir,[StringComparison]::OrdinalIgnoreCase) -and (Test-McPath $mod.Path $dir)
          if (-not $flg.ImportedModuleBound) { throw $mcGuards[3] }
          $stage = 'CommandMetadata'
          $commands = @(Microsoft.PowerShell.Core\Get-Command -Name 'Dism\Repair-WindowsImage' -CommandType Cmdlet -All -ListImported -ErrorAction Stop)
          if ($commands.Count -ne 1) { throw $mcGuards[4] }
          $cmd = $commands[0]
          $flg.CommandBound = ($cmd.Name -ceq 'Repair-WindowsImage') -and [object]::ReferenceEquals($cmd.Module,$mod)
          if (-not $flg.CommandBound) { throw $mcGuards[5] }
          $stage = 'OutputMetadata'
          $outs = @($cmd.OutputType)
          if ($outs.Count -le 8) { $cnt.OutputTypeCount = $outs.Count }
          if ($outs.Count -lt 1 -or $outs.Count -gt 8) { throw $mcGuards[6] }
          $matches = @($outs | Microsoft.PowerShell.Core\Where-Object { $_.Name -ceq 'Microsoft.Dism.Commands.ImageObjectWithState' })
          if ($matches.Count -ne 1) { throw $mcGuards[7] }
          $ot = $matches[0].Type
          if ($null -eq $ot -or -not $ot.IsVisible -or $ot.FullName -cne 'Microsoft.Dism.Commands.ImageObjectWithState') { throw $mcGuards[8] }
          $flg.OutputTypeResolved = $true
          $flg.OutputAssemblyPathQualified = (-not $ot.Assembly.IsDynamic) -and (Test-McPath $ot.Assembly.Location $dir)
          if (-not $flg.OutputAssemblyPathQualified) { throw $mcGuards[9] }
          $stage = 'PropertyMetadata'
          $props = @($ot.GetProperties([Reflection.BindingFlags]::Public -bor [Reflection.BindingFlags]::Instance))
          if ($props.Count -le 64) { $cnt.PublicPropertyCount = $props.Count }
          if ($props.Count -lt 1 -or $props.Count -gt 64) { throw $mcGuards[10] }
          $hp = @($props | Microsoft.PowerShell.Core\Where-Object { $_.Name -ceq 'ImageHealthState' })
          if ($hp.Count -ne 1) { throw $mcGuards[11] }
          $get = $hp[0].GetGetMethod($false)
          $flg.HealthPropertyReadable = ($null -ne $get) -and (-not $get.IsStatic) -and ($hp[0].GetIndexParameters().Length -eq 0)
          $enum = $hp[0].PropertyType
          $flg.HealthPropertyEnum = $enum.IsEnum
          if (-not $flg.HealthPropertyReadable -or -not $flg.HealthPropertyEnum) { throw $mcGuards[12] }
          $stage = 'EnumMetadata'
          if ($enum.Assembly.IsDynamic -or -not (Test-McPath $enum.Assembly.Location $dir)) { throw $mcGuards[13] }
          $enm = [Enum]::GetNames($enum)
          if ($enm.Length -gt 16) { throw $mcGuards[14] }
          $cnt.EnumMemberCount = $enm.Length
          $flg.EnumShapeMatched = ($enm.Length -eq 3) -and ($enm -ccontains 'Healthy') -and ($enm -ccontains 'Repairable') -and ($enm -ccontains 'NonRepairable')
          $stage = 'ParameterMetadata'
          $sets = @($cmd.ParameterSets)
          if ($sets.Count -le 16) { $cnt.ParameterSetCount = $sets.Count }
          if ($sets.Count -lt 1 -or $sets.Count -gt 16) { throw $mcGuards[15] }
          $shared = 0
          $valid = 0
          foreach ($set in $sets) {
            $prm = @($set.Parameters)
            if ($prm.Count -gt 64) { throw $mcGuards[16] }
            $on = @($prm | Microsoft.PowerShell.Core\Where-Object { $_.Name -ceq 'Online' })
            $check = @($prm | Microsoft.PowerShell.Core\Where-Object { $_.Name -ceq 'CheckHealth' })
            $nr = @($prm | Microsoft.PowerShell.Core\Where-Object { $_.Name -ceq 'NoRestart' })
            if ($on.Count -eq 1 -and $check.Count -eq 1 -and $nr.Count -eq 1) {
              $shared++
              $switches = ($on[0].ParameterType -eq [System.Management.Automation.SwitchParameter]) -and ($check[0].ParameterType -eq [System.Management.Automation.SwitchParameter]) -and ($nr[0].ParameterType -eq [System.Management.Automation.SwitchParameter])
              $mandatory = @($prm | Microsoft.PowerShell.Core\Where-Object { $_.IsMandatory -and $_.Name -cnotin @('Online','CheckHealth','NoRestart') })
              if ($switches -and $mandatory.Count -eq 0) { $valid++ }
            }
          }
          $cnt.SharedParameterSetCount = $shared
          $flg.SharedParameterSetMatched = $cnt.SharedParameterSetCount -gt 0
          $flg.SwitchParametersMatched = $valid -gt 0
          $flg.NonTargetMandatoryAbsent = $valid -gt 0
          $complete = $true
          foreach ($en in $flg.GetEnumerator()) { if ($en.Key -cnotin @('PublisherTrustVerified','HealthCheckInvoked') -and -not $en.Value) { $complete=$false } }
          if ($complete) { $status='Available'; $stage='Completed' } else { $stage='Incomplete' }
        } catch { Save-McError $_ $true }
        if (-not $cap.First) { $diag.Stage=$stage }
        $js = [System.Text.StringBuilder]::new()
        [void]$js.Append('{"Schema":3,"Status":"').Append($status).Append('"')
        foreach ($en in $flg.GetEnumerator()) { [void]$js.Append(',"').Append($en.Key).Append('":').Append(([bool]$en.Value).ToString().ToLowerInvariant()) }
        foreach ($en in $cnt.GetEnumerator()) {
          [void]$js.Append(',"').Append($en.Key).Append('":')
          if ($null -eq $en.Value) { [void]$js.Append('null') }
          else { [void]$js.Append(([int]$en.Value).ToString([Globalization.CultureInfo]::InvariantCulture)) }
        }
        foreach ($key in @('Stage','OuterExceptionKind','ExceptionKind','ModuleError')) {
          [void]$js.Append(',"').Append($key).Append('":"').Append($diag[$key]).Append('"')
        }
        foreach ($key in @('ErrorCategory','OuterExceptionHResult','ExceptionHResult')) {
          [void]$js.Append(',"').Append($key).Append('":')
          if ($null -eq $diag[$key]) { [void]$js.Append('null') }
          else { [void]$js.Append(([int]$diag[$key]).ToString([Globalization.CultureInfo]::InvariantCulture)) }
        }
        foreach ($en in $lookup.GetEnumerator()) {
          [void]$js.Append(',"').Append($en.Key).Append('":')
          if ($null -eq $en.Value) { [void]$js.Append('null') }
          else { [void]$js.Append(([bool]$en.Value).ToString().ToLowerInvariant()) }
        }
        [void]$js.Append('}')
        [Console]::WriteLine($js.ToString())
        """;
}
