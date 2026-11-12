using System.ComponentModel;
using System.Globalization;
using System.Text;
using MadClean.Core;

namespace MadClean.Windows;

/// <summary>
/// Explicit local WUA event-history metadata. The dedicated child owns its COM wrappers until
/// exit; stopping that read-only client neither cancels a Windows Update operation nor proves
/// Windows Update service work stopped. No current applicability or installation is queried.
/// </summary>
public sealed class WindowsUpdateHistoryInventory : IWindowsUpdateHistoryInventory
{
    private readonly ReadOnlyProcess runner = new();

    public async Task<CheckResult<WindowsUpdateHistoryReport>> ReadAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return Empty(CheckState.Unsupported,
            "Windows Update event history requires Windows. This platform was not queried.");
        var attempted = DateTimeOffset.UtcNow;
        try
        {
            string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            if (!Path.IsPathFullyQualified(system)) return Empty(CheckState.Unavailable,
                "The Windows system directory is unavailable; history was not queried.");
            string executable = Path.Combine(system, @"WindowsPowerShell\v1.0\powershell.exe");
            if (!File.Exists(executable)) return Empty(CheckState.Unavailable,
                "System Windows PowerShell is unavailable or inaccessible. No history query ran; no provider was installed.");
            string script = Script.Replace("__LIMIT__", WindowsUpdateHistoryPolicy.MaximumEntries.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
            var result = await runner.RunAsync(executable,
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))],
                TimeSpan.FromSeconds(90), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (result.ExitCode != 0) return Empty(CheckState.Failed,
                $"Local Windows Update history could not be read (query code 0x{result.ExitCode:X8}). Check Windows Update service/read policy and retry. No update search or installation was requested.");
            var parsed = WindowsUpdateHistoryPolicy.Parse(result.Output);
            token.ThrowIfCancellationRequested();
            if (parsed.Items.Count != 0 && (!WindowsUpdateHistoryPolicy.IsConsistent(parsed, DateTimeOffset.UtcNow) ||
                parsed.Items[0].QueryStartedUtc < attempted)) return Empty(CheckState.Unknown,
                    "History query boundaries or response times could not be validated; no history rows were accepted.");
            return parsed;
        }
        catch (OperationCanceledException) { throw; }
        catch (TimeoutException)
        {
            token.ThrowIfCancellationRequested();
            return Empty(CheckState.Failed,
                "The local history read timed out. Coverage remains Unknown. Only the read-only query client was stopped; this is not cancellation of a Windows Update operation.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or System.Security.SecurityException)
        {
            token.ThrowIfCancellationRequested();
            return Empty(CheckState.Failed,
                $"Local Windows Update history could not be read (code 0x{ex.HResult:X8}). Check provider/read access; no elevation, update search or installation was requested.");
        }
    }

    private static CheckResult<WindowsUpdateHistoryReport> Empty(CheckState state, string message) =>
        CheckResult<WindowsUpdateHistoryReport>.Empty(state, message);

    private const string Script = """
        $env:PSModulePath = [System.IO.Path]::Combine($PSHOME, 'Modules')
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        $WarningPreference = 'SilentlyContinue'
        $VerbosePreference = 'SilentlyContinue'
        $DebugPreference = 'SilentlyContinue'
        [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
        function Unknown-Field { return @{State='Unknown'; Value=$null} }
        function Failed-Field { return @{State='Failed'; Value=$null} }
        function Read-Number($read, $kind) {
          try {
            $value = & $read
            if ($null -eq $value) { return (Unknown-Field) }
            if ($value -isnot [int]) { return (Failed-Field) }
            if (($kind -eq 'operation' -and $value -notin @(1,2)) -or
                ($kind -eq 'result' -and ($value -lt 0 -or $value -gt 5)) -or
                ($kind -eq 'revision' -and $value -lt 0)) { return (Unknown-Field) }
            return @{State='Known'; Value=$value}
          } catch { return (Failed-Field) }
        }
        function Read-Title($entry) {
          try {
            $value = $entry.Title
            if ($null -eq $value) { return (Unknown-Field) }
            if ($value -isnot [string] -or $value.Length -eq 0 -or $value.Length -gt 1024) { return (Failed-Field) }
            foreach ($character in $value.ToCharArray()) {
              if ([char]::IsControl($character) -or [char]::GetUnicodeCategory($character) -eq [System.Globalization.UnicodeCategory]::Format) { return (Failed-Field) }
            }
            return @{State='Known'; Value=$value}
          } catch { return (Failed-Field) }
        }
        function Read-Date($entry) {
          try {
            $value = $entry.Date
            if ($null -eq $value) { return (Unknown-Field) }
            if ($value -isnot [datetime]) { return (Failed-Field) }
            # WUA specifies UTC even when COM marshals DATE with unspecified Kind.
            # Preserve its clock fields; do not apply this machine's local timezone.
            $number = $value.ToOADate()
            if ([double]::IsNaN($number) -or [double]::IsInfinity($number)) { return (Failed-Field) }
            return @{State='Known'; Value=$number}
          } catch { return (Failed-Field) }
        }
        function Read-Identity($identity) {
          try {
            if ($null -eq $identity) { return (Unknown-Field) }
            $value = $identity.UpdateID
            if ($null -eq $value) { return (Unknown-Field) }
            if ($value -isnot [string]) { return (Failed-Field) }
            $id = [guid]::Empty
            if (-not [guid]::TryParseExact($value, 'D', [ref]$id) -or $id -eq [guid]::Empty) { return (Failed-Field) }
            return @{State='Known'; Value=$id.ToString('D')}
          } catch { return (Failed-Field) }
        }
        try {
          $started = [DateTimeOffset]::UtcNow.ToString('o', [System.Globalization.CultureInfo]::InvariantCulture)
          $session = Microsoft.PowerShell.Utility\New-Object -ComObject Microsoft.Update.Session
          $searcher = $session.CreateUpdateSearcher()
          $total = $searcher.GetTotalHistoryCount()
          if ($total -isnot [int] -or $total -lt 0) { exit 2 }
          $requested = [Math]::Min($total, __LIMIT__)
          $rows = [System.Collections.Generic.List[object]]::new()
          # Count and query are separate observations. Zero count is not a valid QueryHistory call.
          if ($requested -gt 0) {
            $history = $searcher.QueryHistory(0, $requested)
            if ($null -eq $history) { exit 2 }
            $returned = $history.Count
            if ($returned -isnot [int] -or $returned -lt 0 -or $returned -gt $requested) { exit 2 }
            for ($index = 0; $index -lt $returned; $index++) {
              $entry = $history.Item($index)
              if ($null -eq $entry) { exit 2 }
              $identity = $null
              $identityFailed = $false
              try { $identity = $entry.UpdateIdentity } catch { $identityFailed = $true }
              $id = if ($identityFailed) { Failed-Field } else { Read-Identity $identity }
              $revision = if ($identityFailed) { Failed-Field } elseif ($null -eq $identity) { Unknown-Field } else { Read-Number { $identity.RevisionNumber } 'revision' }
              $rows.Add([pscustomobject]@{
                Index=$index; Title=(Read-Title $entry); DateUtc=(Read-Date $entry)
                Operation=(Read-Number { $entry.Operation } 'operation')
                ResultCode=(Read-Number { $entry.ResultCode } 'result')
                HResult=(Read-Number { $entry.HResult } 'hresult')
                UpdateId=$id; Revision=$revision
              })
            }
          }
          $answer = [pscustomobject]@{
            FormatVersion=1; QueryStartedUtc=$started
            CheckedUtc=[DateTimeOffset]::UtcNow.ToString('o', [System.Globalization.CultureInfo]::InvariantCulture)
            TotalBefore=$total; RequestedCount=$requested; Entries=@($rows.ToArray())
          }
          $answer | Microsoft.PowerShell.Utility\ConvertTo-Json -Depth 6 -Compress
        } catch { exit 2 }
        """;
}
