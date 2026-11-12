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

    public async Task<CheckResult<AppUpdate>> AppsAsync(CancellationToken token) =>
        (await AppsObservedAsync(token).ConfigureAwait(false)).Result;

    internal async Task<WingetQueryObservation> AppsObservedAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        WingetQueryObservation Unreturned(WingetQueryOutcome outcome, CheckState state, string message) =>
            new(CheckResult<AppUpdate>.Empty(state, message), new(outcome, null, null, null));
        try
        {
            // App execution alias: never resolve winget from a writable working-directory/PATH entry.
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!Path.IsPathFullyQualified(profile))
                return Unreturned(WingetQueryOutcome.ProfileUnavailable, CheckState.Unavailable,
                    "The Windows local application-data folder could not be resolved. Check the user profile; WinGet was not executed.");
            var winget = Path.Combine(profile, @"Microsoft\WindowsApps\winget.exe");
            if (!File.Exists(winget)) return Unreturned(WingetQueryOutcome.AliasUnavailable, CheckState.Unavailable,
                "WinGet is unavailable for this user. Install or repair Microsoft's App Installer, then retry.");
            var result = await runner.RunAsync(winget, ["list", "--source", "winget", "--disable-interactivity"], TimeSpan.FromSeconds(90), token).ConfigureAwait(false);
            bool content = !string.IsNullOrWhiteSpace(result.Output);
            if (result.ExitCode != 0) return new(Failure<AppUpdate>("WinGet", result.ExitCode,
                "Open WinGet manually to review source agreements, network access or version compatibility; MadClean never accepts agreements silently."),
                new(WingetQueryOutcome.NonzeroExit, result.ExitCode, content, null));
            var parsed = WingetParser.ParseObserved(result.Output);
            return new(parsed.Result, new(WingetQueryOutcome.ParsedOutput, result.ExitCode, content, parsed.Issue));
        }
        catch (OperationCanceledException) { throw; }
        catch (TimeoutException) { return Unreturned(WingetQueryOutcome.TimedOut, CheckState.Failed, "WinGet timed out. Check the provider service/network and retry."); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { return Unreturned(WingetQueryOutcome.UnexpectedResponse, CheckState.Unknown, "WinGet returned an unexpected response. Update the provider or report a compatibility issue; no successful check is claimed."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or System.Security.SecurityException)
        { return Unreturned(WingetQueryOutcome.QueryFailed, CheckState.Failed, $"WinGet could not be queried (code 0x{ex.HResult:X8}). Check provider availability and access; no elevation was requested."); }
    }

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
        function Offer-Field([string]$state,$value) { return [pscustomobject]@{State=$state;Value=$value} }
        function Read-OfferField($object,[string]$name,[string]$kind) {
          try {
            $value=$object.$name
            if ($null -eq $value) { return (Offer-Field 'Unknown' $null) }
            if ($kind -ceq 'String') {
              if ($value -isnot [string] -or $value.Length -gt 512 -or [string]::IsNullOrWhiteSpace($value)) { return (Offer-Field 'Unknown' $null) }
              for ($n=0;$n -lt $value.Length;$n++) {
                $c=$value[$n]
                if ([char]::IsHighSurrogate($c)) {
                  if ($n+1 -ge $value.Length -or -not [char]::IsLowSurrogate($value[$n+1])) { return (Offer-Field 'Unknown' $null) }
                  $category=[Globalization.CharUnicodeInfo]::GetUnicodeCategory($value,$n);$n++
                } elseif ([char]::IsLowSurrogate($c)) { return (Offer-Field 'Unknown' $null) }
                else { $category=[char]::GetUnicodeCategory($c) }
                if ($category -eq [Globalization.UnicodeCategory]::Control -or $category -eq [Globalization.UnicodeCategory]::Format -or
                    $category -eq [Globalization.UnicodeCategory]::LineSeparator -or $category -eq [Globalization.UnicodeCategory]::ParagraphSeparator) { return (Offer-Field 'Unknown' $null) }
              }
              return (Offer-Field 'Available' $value)
            }
            if ($kind -ceq 'Bool') {
              if ($value -is [bool]) { return (Offer-Field 'Available' $value) }
              return (Offer-Field 'Unknown' $null)
            }
            if ($kind -ceq 'Revision') {
              $integer=$value -is [sbyte] -or $value -is [byte] -or $value -is [int16] -or $value -is [uint16] -or
                  $value -is [int32] -or $value -is [uint32] -or $value -is [int64] -or $value -is [uint64]
              if ($integer -and $value -ge 0 -and $value -le [int32]::MaxValue) { return (Offer-Field 'Available' ([int]$value)) }
              return (Offer-Field 'Unknown' $null)
            }
            if ($kind -ceq 'Enum') {
              $integer=$value -is [sbyte] -or $value -is [byte] -or $value -is [int16] -or $value -is [uint16] -or
                  $value -is [int32] -or $value -is [uint32] -or $value -is [int64] -or $value -is [uint64]
              if ($integer -and $value -ge 0 -and $value -le 2) { return (Offer-Field 'Available' ([int]$value)) }
            }
            return (Offer-Field 'Unknown' $null)
          } catch { return (Offer-Field 'Failed' $null) }
        }
        try {
          $session = New-Object -ComObject Microsoft.Update.Session
          $session.ClientApplicationID = 'MadClean read-only inventory'
          $searcher = $session.CreateUpdateSearcher()
          $result = $searcher.Search("IsInstalled=0 and IsHidden=0")
          $updates = @()
          $count=$result.Updates.Count
          if ($count -isnot [int] -or $count -lt 0) { throw [InvalidOperationException]::new('Windows Update offer count unavailable.') }
          $truncated=$count -gt 256
          for ($i=0; $i -lt $count -and $i -lt 256; $i++) {
            $u=$result.Updates.Item($i)
            $type=[int]$u.Type
            $identity=$u.Identity
            $id=$identity.UpdateID
            $identityMetadata=[pscustomobject]@{
              Version=1
              RevisionNumber=(Read-OfferField $identity 'RevisionNumber' 'Revision')
              DriverHardwareID=(Offer-Field 'Unknown' $null)
            }
            if ($type -eq 1) {
              $identityMetadata.DriverHardwareID=Offer-Field 'Unsupported' $null
            } elseif ($type -eq 2) {
              $hardwareID=Read-OfferField $u 'DriverHardwareID' 'String'
              if ($hardwareID.State -ceq 'Available' -and
                  ($hardwareID.Value.Trim().Equals('Unknown',[StringComparison]::OrdinalIgnoreCase) -or
                   $hardwareID.Value.Trim().Equals('N/A',[StringComparison]::OrdinalIgnoreCase))) {
                $hardwareID=Offer-Field 'Unknown' $null
              }
              $identityMetadata.DriverHardwareID=$hardwareID
            }
            $class = $null
            $date = $null
            if ($type -eq 2) {
              try { $class = [string]$u.DriverClass } catch { }
              try { $date = $u.DriverVerDate.ToString('yyyy-MM-dd', [System.Globalization.CultureInfo]::InvariantCulture) } catch { }
            }
            $metadata=[pscustomobject]@{
              Version=1
              Manufacturer=(Offer-Field 'Unknown' $null);Provider=(Offer-Field 'Unknown' $null);Model=(Offer-Field 'Unknown' $null)
              CanRequestUserInput=(Offer-Field 'Unknown' $null);RequiresNetworkConnectivity=(Offer-Field 'Unknown' $null)
              Impact=(Offer-Field 'Unknown' $null);RebootBehavior=(Offer-Field 'Unknown' $null)
            }
            if ($type -eq 1) {
              $metadata.Manufacturer=Offer-Field 'Unsupported' $null
              $metadata.Provider=Offer-Field 'Unsupported' $null
              $metadata.Model=Offer-Field 'Unsupported' $null
            } elseif ($type -eq 2) {
              $metadata.Manufacturer=Read-OfferField $u 'DriverManufacturer' 'String'
              $metadata.Provider=Read-OfferField $u 'DriverProvider' 'String'
              $metadata.Model=Read-OfferField $u 'DriverModel' 'String'
            }
            $behavior=$null;$behaviorFailed=$false
            try { $behavior=$u.InstallationBehavior } catch { $behaviorFailed=$true }
            if ($behaviorFailed) {
              $metadata.CanRequestUserInput=Offer-Field 'Failed' $null
              $metadata.RequiresNetworkConnectivity=Offer-Field 'Failed' $null
              $metadata.Impact=Offer-Field 'Failed' $null
              $metadata.RebootBehavior=Offer-Field 'Failed' $null
            } elseif ($null -ne $behavior) {
              $metadata.CanRequestUserInput=Read-OfferField $behavior 'CanRequestUserInput' 'Bool'
              $metadata.RequiresNetworkConnectivity=Read-OfferField $behavior 'RequiresNetworkConnectivity' 'Bool'
              $metadata.Impact=Read-OfferField $behavior 'Impact' 'Enum'
              $metadata.RebootBehavior=Read-OfferField $behavior 'RebootBehavior' 'Enum'
            }
            $reboot=$u.RebootRequired
            if ($reboot -isnot [bool]) { throw [InvalidOperationException]::new('Windows Update required reboot metadata unavailable.') }
            $description = [string]$u.Description
            if ($description.Length -gt 2048) { $description = $description.Substring(0, 2048) + ' [truncated]' }
            $updates += [pscustomobject]@{Title=$u.Title; Type=$type; Id=$id; RebootRequired=$reboot; DriverClass=$class; DriverVerDate=$date; Description=$description; OfferMetadata=$metadata; IdentityMetadata=$identityMetadata}
          }
          [pscustomobject]@{ResultCode=[int]$result.ResultCode; Truncated=$truncated; Updates=@($updates)} | ConvertTo-Json -Depth 8 -Compress
        } catch { exit 1 }
        """;
}
