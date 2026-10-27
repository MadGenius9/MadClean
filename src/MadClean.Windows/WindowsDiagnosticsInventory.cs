using System.ComponentModel;
using System.Globalization;
using System.Text;
using MadClean.Core;

namespace MadClean.Windows;

/// <summary>Explicit passive local Application/System Event Log metadata; no diagnostic or repair commands.</summary>
public sealed class WindowsDiagnosticsInventory : IDiagnosticsInventory
{
    private readonly ReadOnlyProcess runner = new();

    public async Task<CheckResult<DiagnosticReport>> ReadAsync(int days, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (days is not (1 or 7 or 30)) throw new ArgumentOutOfRangeException(nameof(days), "Choose 1, 7 or 30 days.");
        if (!OperatingSystem.IsWindows()) return CheckResult<DiagnosticReport>.Empty(CheckState.Unsupported,
            "Passive diagnostics requires Windows local Event Log providers; this platform was not queried.");
        var end = DateTimeOffset.UtcNow;
        var start = end.AddDays(-days);
        try
        {
            string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            if (!Path.IsPathFullyQualified(system)) return CheckResult<DiagnosticReport>.Empty(CheckState.Unavailable,
                "The Windows system directory is unavailable; no Event Log command was executed.");
            string executable = Path.Combine(system, @"WindowsPowerShell\v1.0\powershell.exe");
            if (!File.Exists(executable)) return CheckResult<DiagnosticReport>.Empty(CheckState.Unavailable,
                "System Windows PowerShell is unavailable/inaccessible; Event Logs were not queried.");
            // The substitutions are generated UTC timestamps only, never user-provided code or paths.
            string script = Script.Replace("__START_UTC__", start.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("__END_UTC__", end.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture), StringComparison.Ordinal);
            var result = await runner.RunAsync(executable,
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))],
                TimeSpan.FromSeconds(90), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (result.ExitCode == 3) return CheckResult<DiagnosticReport>.Empty(CheckState.Unavailable,
                "The trusted system Event Log module is unavailable or blocked. Check Windows module policy; no elevation was requested.");
            if (result.ExitCode != 0) return CheckResult<DiagnosticReport>.Empty(CheckState.Failed,
                $"Passive Event Log query failed (code 0x{result.ExitCode:X8}); check Windows Event Log service and read access, then retry. No repair was run.");
            var parsed = DiagnosticEvidenceParser.Parse(result.Output, token);
            token.ThrowIfCancellationRequested();
            if (parsed.Items.Any(report => report.StartUtc != start || report.EndUtc != end))
                return CheckResult<DiagnosticReport>.Empty(CheckState.Unknown, "Returned Event Log boundaries differed from the selected fixed UTC window; no findings were trusted.");
            return parsed;
        }
        catch (OperationCanceledException) { throw; }
        catch (TimeoutException)
        {
            token.ThrowIfCancellationRequested();
            return CheckResult<DiagnosticReport>.Empty(CheckState.Failed, "Passive Event Log query timed out; selected-channel coverage is unknown. Check service/read access or try a shorter window. No integrity/repair operation ran.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or System.Security.SecurityException)
        {
            token.ThrowIfCancellationRequested();
            return CheckResult<DiagnosticReport>.Empty(CheckState.Failed,
                $"Passive Event Log query could not complete (code 0x{ex.HResult:X8}); check system provider/read access. No elevation was requested.");
        }
    }

    private const string Script = """
        $env:PSModulePath = [System.IO.Path]::Combine($PSHOME, 'Modules')
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
        $module = [System.IO.Path]::Combine($PSHOME, 'Modules', 'Microsoft.PowerShell.Diagnostics', 'Microsoft.PowerShell.Diagnostics.psd1')
        if (-not [System.IO.File]::Exists($module)) { exit 3 }
        try { Microsoft.PowerShell.Core\Import-Module -Name $module -ErrorAction Stop } catch { exit 3 }
        $startText = '__START_UTC__'
        $endText = '__END_UTC__'
        function Read-SafeFailure($record) {
          $exception = $record.Exception
          for ($i=0; $i -lt 4 -and $null -ne $exception; $i++) {
            if ($exception -is [System.UnauthorizedAccessException] -or $exception -is [System.Security.SecurityException] -or (($exception.HResult -band 65535) -eq 5)) { return 'Denied' }
            if ($exception -is [System.Diagnostics.Eventing.Reader.EventLogNotFoundException] -or (($exception.HResult -band 65535) -eq 15007)) { return 'Unavailable' }
            $exception = $exception.InnerException
          }
          return 'Failed'
        }
        function Read-EdgeTime([string]$name, [bool]$oldest) {
          $event = $null
          try {
            if ($oldest) { $event = Microsoft.PowerShell.Diagnostics\Get-WinEvent -LogName $name -Oldest -MaxEvents 1 -ErrorAction Stop }
            else { $event = Microsoft.PowerShell.Diagnostics\Get-WinEvent -LogName $name -MaxEvents 1 -ErrorAction Stop }
            if ($null -eq $event.TimeCreated) { return [pscustomobject]@{Status='Unknown'; Time=$null} }
            return [pscustomobject]@{Status='Available'; Time=$event.TimeCreated.ToUniversalTime().ToString('o', [System.Globalization.CultureInfo]::InvariantCulture)}
          } catch {
            if ($_.FullyQualifiedErrorId -like 'NoMatchingEventsFound*') { return [pscustomobject]@{Status='Empty'; Time=$null} }
            return [pscustomobject]@{Status='Unknown'; Time=$null}
          } finally { if ($null -ne $event) { $event.Dispose() } }
        }
        function Read-Channel([string]$name) {
          $answer = [ordered]@{Name=$name; Status='Available'; Truncated=$false; RecordCount=$null; OldestRecordId=$null; OldestUtc=$null; NewestUtc=$null; BoundsStatus='Unknown'; Records=@()}
          $configuration = $null
          try {
            $configuration = Microsoft.PowerShell.Diagnostics\Get-WinEvent -ListLog $name -ErrorAction Stop
            if ($configuration.IsEnabled -eq $false) { $answer.Status='Disabled'; return [pscustomobject]$answer }
            $answer.RecordCount=$configuration.RecordCount
            $answer.OldestRecordId=$configuration.OldestRecordNumber
          } catch { $answer.Status=Read-SafeFailure $_; return [pscustomobject]$answer }
          finally { if ($null -ne $configuration) { $configuration.Dispose() } }
          $old = Read-EdgeTime $name $true
          $new = Read-EdgeTime $name $false
          $answer.OldestUtc=$old.Time
          $answer.NewestUtc=$new.Time
          if ($old.Status -eq 'Available' -and $new.Status -eq 'Available') { $answer.BoundsStatus='Available' }
          elseif ($old.Status -eq 'Empty' -and $new.Status -eq 'Empty') { $answer.BoundsStatus='Empty' }
          $special = ''
          if ($name -eq 'Application') { $special = " or (Provider[@Name='Windows Error Reporting'] and EventID=1001)" }
          $filter = "*[System[((Level=1 or Level=2 or Level=3)$special) and TimeCreated[@SystemTime >= '$startText' and @SystemTime < '$endText']]]"
          $events = @()
          try {
            $events = @(Microsoft.PowerShell.Diagnostics\Get-WinEvent -LogName $name -FilterXPath $filter -MaxEvents 1001 -ErrorAction Stop)
            $answer.Truncated=$events.Count -gt 1000
            $rows = [System.Collections.Generic.List[object]]::new()
            for ($i=0; $i -lt $events.Count; $i++) {
              $event=$events[$i]
              try {
                if ($i -lt 1000) {
                  $time=$null
                  if ($null -ne $event.TimeCreated) { $time=$event.TimeCreated.ToUniversalTime().ToString('o', [System.Globalization.CultureInfo]::InvariantCulture) }
                  $rows.Add([pscustomobject]@{Channel=$event.LogName; Provider=$event.ProviderName; EventId=$event.Id; Version=$event.Version; Level=$event.Level; RecordId=$event.RecordId; TimeUtc=$time})
                }
              } finally { if ($null -ne $event) { $event.Dispose() } }
            }
            $answer.Records=@($rows.ToArray())
          } catch {
            if ($_.FullyQualifiedErrorId -notlike 'NoMatchingEventsFound*') { $answer.Status=Read-SafeFailure $_ }
            $answer.Records=@()
          }
          return [pscustomobject]$answer
        }
        $channels = @(Read-Channel 'Application'; Read-Channel 'System')
        [pscustomobject]@{Version=1; StartUtc=$startText; EndUtc=$endText; Channels=@($channels)} |
          Microsoft.PowerShell.Utility\ConvertTo-Json -Depth 7 -Compress
        """;
}
