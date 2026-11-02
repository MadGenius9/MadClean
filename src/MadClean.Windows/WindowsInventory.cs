using System.ComponentModel;
using System.Text;
using System.Text.Json;
using MadClean.Core;
using Microsoft.Win32;

namespace MadClean.Windows;

public sealed class WindowsInventory : IComputerInventory
{
    private readonly ReadOnlyProcess runner = new();
    private static readonly string Powershell = SystemPowerShellPath();
    private static string SystemPowerShellPath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.System);
        return Path.IsPathFullyQualified(root) ? Path.Combine(root, @"WindowsPowerShell\v1.0\powershell.exe") : string.Empty;
    }
    public IReadOnlyList<CleanupLocation> CleanupLocations =>
    [
        TemporaryLocation("User temporary files", Environment.SpecialFolder.LocalApplicationData),
        TemporaryLocation("Windows temporary files", Environment.SpecialFolder.Windows)
    ];

    public Task<CheckResult<DiskInfo>> DisksAsync(CancellationToken token) => Task.Run(() =>
    {
        List<DiskInfo> disks = []; int failures = 0;
        foreach (var drive in DriveInfo.GetDrives())
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (drive.DriveType != DriveType.Fixed) continue;
                if (!drive.IsReady) { failures++; continue; }
                disks.Add(new(drive.Name, drive.TotalSize, drive.AvailableFreeSpace));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failures++; }
        }
        return new CheckResult<DiskInfo>(failures > 0 ? CheckState.Partial : CheckState.Available,
            failures > 0 ? "Some local disks could not be read. Check drive access." : "Available space for the current user; fixed local disks only.", disks);
    }, token);

    public Task<CheckResult<StartupEntry>> StartupAsync(CancellationToken token) => Task.Run(() =>
    {
        List<StartupEntry> entries = []; int failures = 0;
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            // HKCU\Software is shared; HKLM\Software has distinct 32/64-bit views.
            foreach (var view in hive == RegistryHive.CurrentUser
                ? new[] { RegistryView.Default }
                : new[] { RegistryView.Registry64, RegistryView.Registry32 })
                foreach (var name in new[] { "Run", "RunOnce" })
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        using var key = RegistryKey.OpenBaseKey(hive, view);
                        using var startup = key.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\" + name, false);
                        if (startup == null) continue;
                        foreach (var value in startup.GetValueNames())
                        {
                            token.ThrowIfCancellationRequested();
                            // Deliberately do not collect command lines: they can contain tokens/passwords.
                            entries.Add(new(value, $"{hive} / {view} / {name}", "Unknown", "Registered entry; enabled state and target not verified."));
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { failures++; }
                }
        foreach (var folder in new[] { Environment.SpecialFolder.Startup, Environment.SpecialFolder.CommonStartup })
        {
            token.ThrowIfCancellationRequested();
            var path = Environment.GetFolderPath(folder);
            try
            {
                if (!Path.IsPathFullyQualified(path)) { failures++; continue; }
                // Directory.Exists hides ACL errors by returning false; inspect under the explicit catch instead.
                if ((File.GetAttributes(path) & FileAttributes.Directory) == 0) { failures++; continue; }
                if (HasReparseAncestor(path)) { failures++; continue; }
                foreach (var file in Directory.EnumerateFiles(path))
                {
                    token.ThrowIfCancellationRequested();
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
                    entries.Add(new(Path.GetFileName(file), folder.ToString(), "Unknown", "Startup folder item; shortcut target and enabled state not verified."));
                }
            }
            catch (DirectoryNotFoundException) { /* A genuine absent Startup folder has no entries. */ }
            catch (FileNotFoundException) { /* Folder vanished before enumeration. */ }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { failures++; }
        }
        return new CheckResult<StartupEntry>(failures > 0 ? CheckState.Partial : CheckState.Available,
            $"Run/RunOnce (shared HKCU and both HKLM views) and Startup folders. Publishers unknown without verified targets. Scheduled tasks, services and packaged startup tasks are outside this inventory. {failures} sources inaccessible.", entries);
    }, token);

    public Task<CheckResult<DefenderInfo>> DefenderAsync(CancellationToken token) => QueryAsync(async () =>
    {
        var result = await PowerShellAsync(DefenderScript, TimeSpan.FromSeconds(45), token);
        if (result.ExitCode == 3) return CheckResult<DefenderInfo>.Empty(CheckState.Unavailable, "Defender cmdlets are unavailable. Check Windows Security and installed protection.");
        if (result.ExitCode != 0) return Failure<DefenderInfo>("Defender", result.ExitCode, "Open Windows Security to check provider access and service status.");
        return WindowsResponseParser.Defender(result.Output);
    }, "Defender", token);

    public Task<CheckResult<AppUpdate>> AppsAsync(CancellationToken token) => QueryAsync(async () =>
    {
        // App execution alias: never resolve winget from a writable working-directory/PATH entry.
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!Path.IsPathFullyQualified(profile))
            return CheckResult<AppUpdate>.Empty(CheckState.Unavailable, "The Windows local application-data folder could not be resolved. Check the user profile; WinGet was not executed.");
        var winget = Path.Combine(profile, @"Microsoft\WindowsApps\winget.exe");
        if (!File.Exists(winget)) return CheckResult<AppUpdate>.Empty(CheckState.Unavailable, "WinGet is unavailable for this user. Install or repair Microsoft's App Installer, then retry.");
        var result = await runner.RunAsync(winget, ["list", "--source", "winget", "--disable-interactivity"], TimeSpan.FromSeconds(90), token);
        if (result.ExitCode != 0) return Failure<AppUpdate>("WinGet", result.ExitCode,
            "Open WinGet manually to review source agreements, network access or version compatibility; MadClean never accepts agreements silently.");
        return WingetParser.Parse(result.Output);
    }, "WinGet", token);

    public Task<CheckResult<SystemUpdate>> SystemUpdatesAsync(CancellationToken token) => QueryAsync(async () =>
    {
        var result = await PowerShellAsync(UpdateScript, TimeSpan.FromMinutes(3), token);
        if (result.ExitCode != 0) return Failure<SystemUpdate>("Windows Update", result.ExitCode,
            "Check Windows Update service, network and organizational policy in Windows Settings, then retry.");
        return WindowsResponseParser.SystemUpdates(result.Output);
    }, "Windows Update", token);

    private Task<ProcessResult> PowerShellAsync(string script, TimeSpan timeout, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(Powershell)) throw new FileNotFoundException("System Windows PowerShell location is unavailable.");
        return runner.RunAsync(Powershell, ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))], timeout, token);
    }

    private static async Task<CheckResult<T>> QueryAsync<T>(Func<Task<CheckResult<T>>> query, string name, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { return await query(); }
        catch (OperationCanceledException) { throw; }
        catch (TimeoutException) { return CheckResult<T>.Empty(CheckState.Failed, $"{name} timed out. Check the provider service/network and retry."); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { return CheckResult<T>.Empty(CheckState.Unknown, $"{name} returned an unexpected response. Update the provider or report a compatibility issue; no successful check is claimed."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or System.Security.SecurityException)
        { return CheckResult<T>.Empty(CheckState.Failed, $"{name} could not be queried (code 0x{ex.HResult:X8}). Check provider availability and access; no elevation was requested."); }
    }
    private static CheckResult<T> Failure<T>(string name, int code, string action) =>
        CheckResult<T>.Empty(CheckState.Failed, $"{name} query failed (code 0x{code:X8}). {action}");
    private static CleanupLocation TemporaryLocation(string category, Environment.SpecialFolder folder)
    {
        var root = Environment.GetFolderPath(folder);
        // The scanner records the invalid scope as an issue; never fall back to CWD/Temp.
        return new(category, Path.IsPathFullyQualified(root) ? Path.Combine(root, "Temp") : string.Empty);
    }
    private static bool HasReparseAncestor(string path)
    {
        for (DirectoryInfo? directory = new(path); directory != null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) return true;
        return false;
    }

    private const string DefenderScript = """
        $env:PSModulePath = [System.IO.Path]::Combine($PSHOME, 'Modules')
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
        $modulePath = [System.IO.Path]::Combine($PSHOME, 'Modules', 'Defender', 'Defender.psd1')
        if (-not [System.IO.File]::Exists($modulePath)) { exit 3 }
        try {
          Microsoft.PowerShell.Core\Import-Module -Name $modulePath -ErrorAction Stop
          $s = Defender\Get-MpComputerStatus
          function Read-ScanTimestamp([string]$name) {
            try {
              $property = $s.PSObject.Properties[$name]
              if ($null -eq $property -or $null -eq $property.Value) { return $null }
              $value = $property.Value
              if ($value -is [datetimeoffset]) {
                if ($value.DateTime -eq [datetime]::MinValue -or $value.DateTime -eq [datetime]::MaxValue -or
                    $value.DateTime -eq [datetime]::FromFileTimeUtc(0)) { return $null }
                $utc = $value.ToUniversalTime()
              } elseif ($value -is [datetime] -and ($value.Kind -eq [DateTimeKind]::Utc -or $value.Kind -eq [DateTimeKind]::Local)) {
                if ($value -eq [datetime]::MaxValue -or $value -eq [datetime]::MinValue -or
                    $value.Ticks -eq ([datetime]::FromFileTimeUtc(0)).Ticks) { return $null }
                $utc = [datetimeoffset]$value.ToUniversalTime()
              } else { return $null }
              if ($utc -eq [datetimeoffset]::MinValue -or $utc -eq [datetimeoffset]::MaxValue -or
                  $utc.UtcDateTime -eq [datetime]::FromFileTimeUtc(0)) { return $null }
              return $utc.ToString('o', [System.Globalization.CultureInfo]::InvariantCulture)
            } catch { return $null }
          }
          $findingsState = 'Available'
          $f = @()
          try { $f = @(Defender\Get-MpThreatDetection | Select-Object ThreatID,ActionSuccess,@{n='LastThreatStatusChangeTime';e={ if ($_.LastThreatStatusChangeTime) {$_.LastThreatStatusChangeTime.ToUniversalTime().ToString('o')} else {$null} }}) }
          catch { $findingsState = 'Failed' }
          [pscustomobject]@{
            AntivirusEnabled=$s.AntivirusEnabled; RealTimeProtectionEnabled=$s.RealTimeProtectionEnabled
            AMServiceEnabled=$s.AMServiceEnabled; AMRunningMode=$s.AMRunningMode
            AntivirusSignatureVersion=$s.AntivirusSignatureVersion
            SignatureUpdated=$(if ($s.AntivirusSignatureLastUpdated) {$s.AntivirusSignatureLastUpdated.ToUniversalTime().ToString('o')} else {$null})
            FindingsState=$findingsState; Findings=@($f)
            QuickScanStartTime=$(Read-ScanTimestamp 'QuickScanStartTime')
            QuickScanEndTime=$(Read-ScanTimestamp 'QuickScanEndTime')
            FullScanStartTime=$(Read-ScanTimestamp 'FullScanStartTime')
            FullScanEndTime=$(Read-ScanTimestamp 'FullScanEndTime')
          } | ConvertTo-Json -Depth 5 -Compress
        } catch { exit 1 }
        """;

    private const string UpdateScript = """
        $env:PSModulePath = [System.IO.Path]::Combine($PSHOME, 'Modules')
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
        try {
          $session = New-Object -ComObject Microsoft.Update.Session
          $session.ClientApplicationID = 'MadClean read-only inventory'
          $searcher = $session.CreateUpdateSearcher()
          $result = $searcher.Search("IsInstalled=0 and IsHidden=0")
          $updates = @()
          for ($i=0; $i -lt $result.Updates.Count; $i++) {
            $u=$result.Updates.Item($i)
            $class = $null
            $date = $null
            if ([int]$u.Type -eq 2) {
              try { $class = [string]$u.DriverClass } catch { }
              try { $date = $u.DriverVerDate.ToString('yyyy-MM-dd', [System.Globalization.CultureInfo]::InvariantCulture) } catch { }
            }
            $description = [string]$u.Description
            if ($description.Length -gt 2048) { $description = $description.Substring(0, 2048) + ' [truncated]' }
            $updates += [pscustomobject]@{Title=$u.Title; Type=[int]$u.Type; Id=$u.Identity.UpdateID; RebootRequired=[bool]$u.RebootRequired; DriverClass=$class; DriverVerDate=$date; Description=$description}
          }
          [pscustomobject]@{ResultCode=[int]$result.ResultCode; Updates=@($updates)} | ConvertTo-Json -Depth 5 -Compress
        } catch { exit 1 }
        """;
}
