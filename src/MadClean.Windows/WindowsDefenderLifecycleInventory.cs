using System.ComponentModel;
using System.Globalization;
using System.Text;
using MadClean.Core;

namespace MadClean.Windows;

/// <summary>Explicit, bounded retained Defender lifecycle event metadata; never requests a scan.</summary>
public sealed class WindowsDefenderLifecycleInventory : IDefenderLifecycleInventory
{
    private readonly ReadOnlyProcess runner = new();

    public async Task<CheckResult<DefenderLifecycleReport>> ReadAsync(int days, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (days is not (1 or 7 or 30)) throw new ArgumentOutOfRangeException(nameof(days), "Choose 1, 7 or 30 days.");
        if (!OperatingSystem.IsWindows()) return CheckResult<DefenderLifecycleReport>.Empty(CheckState.Unsupported,
            "Defender lifecycle event metadata requires Windows Event Log; this platform was not queried.");

        var end = DateTimeOffset.UtcNow;
        var start = end.AddDays(-days);
        try
        {
            string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            if (!Path.IsPathFullyQualified(system)) return CheckResult<DefenderLifecycleReport>.Empty(CheckState.Unavailable,
                "The Windows system directory is unavailable; Defender event history was not queried.");
            string executable = Path.Combine(system, @"WindowsPowerShell\v1.0\powershell.exe");
            if (!File.Exists(executable)) return CheckResult<DefenderLifecycleReport>.Empty(CheckState.Unavailable,
                "System Windows PowerShell is unavailable/inaccessible; Defender event history was not queried.");
            // Only locally generated UTC times are substituted into this fixed script.
            string script = Script.Replace("__START_UTC__", start.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("__END_UTC__", end.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture), StringComparison.Ordinal);
            var result = await runner.RunAsync(executable,
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))],
                TimeSpan.FromSeconds(90), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (result.ExitCode == 3) return CheckResult<DefenderLifecycleReport>.Empty(CheckState.Unavailable,
                "The inbox Event Log module is unavailable or blocked. Check system module policy; no elevation was requested.");
            if (result.ExitCode != 0) return CheckResult<DefenderLifecycleReport>.Empty(CheckState.Failed,
                $"Defender event metadata query failed (code 0x{result.ExitCode:X8}). Check Event Log service/read access and retry; no scan was requested.");
            var parsed = DefenderLifecycleParser.Parse(result.Output, token);
            token.ThrowIfCancellationRequested();
            var received = DateTimeOffset.UtcNow;
            if (parsed.Items.Any(report => report.StartUtc != start || report.EndUtc != end ||
                report.ObservedUtc < end || report.ObservedUtc > received))
                return CheckResult<DefenderLifecycleReport>.Empty(CheckState.Unknown,
                    "Returned Defender event boundaries/times differed from the requested fixed UTC window; no rows were trusted.");
            return parsed;
        }
        catch (OperationCanceledException) { throw; }
        catch (TimeoutException)
        {
            token.ThrowIfCancellationRequested();
            return CheckResult<DefenderLifecycleReport>.Empty(CheckState.Failed,
                "Defender event metadata query timed out. History coverage remains unknown; check service/read access or choose a shorter window. No scan was requested.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or System.Security.SecurityException)
        {
            token.ThrowIfCancellationRequested();
            return CheckResult<DefenderLifecycleReport>.Empty(CheckState.Failed,
                $"Defender event metadata could not be read (code 0x{ex.HResult:X8}). Check system provider/read access; no elevation or scan was requested.");
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
        $channel = 'Microsoft-Windows-Windows Defender/Operational'
        $provider = 'Microsoft-Windows-Windows Defender'
        $startText = '__START_UTC__'
        $endText = '__END_UTC__'
        $answer = [ordered]@{Version=1; StartUtc=$startText; EndUtc=$endText; ObservedUtc=$null; Channel=$channel; Provider=$provider; Status='Available'; Truncated=$false; Records=@()}
        function Read-SafeFailure($record) {
          $exception = $record.Exception
          for ($i=0; $i -lt 4 -and $null -ne $exception; $i++) {
            if ($exception -is [System.UnauthorizedAccessException] -or $exception -is [System.Security.SecurityException] -or (($exception.HResult -band 65535) -eq 5)) { return 'Denied' }
            if ($exception -is [System.Diagnostics.Eventing.Reader.EventLogNotFoundException] -or (($exception.HResult -band 65535) -eq 15007)) { return 'Unavailable' }
            $exception = $exception.InnerException
          }
          return 'Failed'
        }
        $configuration = $null
        try {
          $configuration = Microsoft.PowerShell.Diagnostics\Get-WinEvent -ListLog $channel -ErrorAction Stop
          if ($configuration.LogName -cne $channel) { $answer.Status='Failed' }
          elseif ($configuration.IsEnabled -eq $false) { $answer.Status='Disabled' }
          elseif ($configuration.IsEnabled -ne $true) { $answer.Status='Failed' }
        } catch { $answer.Status=Read-SafeFailure $_ }
        finally {
          if ($null -ne $configuration) {
            try { $configuration.Dispose() } catch { $answer.Status='Failed' }
          }
        }
        if ($answer.Status -eq 'Available') {
          $events = [System.Collections.Generic.List[object]]::new()
          try {
            $filter = "*[System[Provider[@Name='$provider'] and (EventID=1000 or EventID=1001 or EventID=1002) and TimeCreated[@SystemTime >= '$startText' and @SystemTime < '$endText']]]"
            # Retain each returned object before a later pipeline error can discard it.
            # Every object, including the truncation sentinel, is disposed below.
            Microsoft.PowerShell.Diagnostics\Get-WinEvent -LogName $channel -FilterXPath $filter -MaxEvents 257 -ErrorAction Stop |
              Microsoft.PowerShell.Core\ForEach-Object { $events.Add($_) }
            $answer.Truncated=$events.Count -gt 256
            $rows = [System.Collections.Generic.List[object]]::new()
            for ($i=0; $i -lt [Math]::Min($events.Count, 256); $i++) {
              $event=$events[$i]
              $time=$null
              if ($null -ne $event.TimeCreated) { $time=$event.TimeCreated.ToUniversalTime().ToString('o', [System.Globalization.CultureInfo]::InvariantCulture) }
              $rows.Add([pscustomobject]@{EventId=$event.Id; RecordId=$event.RecordId; TimeUtc=$time; Channel=$event.LogName; Provider=$event.ProviderName})
            }
            $answer.Records=@($rows.ToArray())
          } catch {
            # Only the cmdlet's explicit no-matching-events outcome qualifies an empty read.
            if ($_.FullyQualifiedErrorId -notlike 'NoMatchingEventsFound*' -or $events.Count -ne 0) { $answer.Status=Read-SafeFailure $_ }
            $answer.Records=@()
            $answer.Truncated=$false
          } finally {
            foreach ($event in $events) {
              if ($null -ne $event) {
                try { $event.Dispose() } catch { $answer.Status='Failed' }
              }
            }
          }
        }
        if ($answer.Status -ne 'Available') { $answer.Records=@(); $answer.Truncated=$false }
        $answer.ObservedUtc=[DateTime]::UtcNow.ToString('o', [System.Globalization.CultureInfo]::InvariantCulture)
        [pscustomobject]$answer | Microsoft.PowerShell.Utility\ConvertTo-Json -Depth 5 -Compress
        """;
}
