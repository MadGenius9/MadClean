using System.Text;

namespace MadClean.Windows;

/// <summary>
/// Fixed host source only. No caller launches this candidate. A future parent must
/// revalidate and hold the exact folder, supply only its draft input, and own the actual client.
/// </summary>
internal static class DefenderScanRequestHost
{
    internal static string EncodedSource => Convert.ToBase64String(Encoding.Unicode.GetBytes(Source));

    private const string Source = """
        function Report([string]$state) {
          # The shared profile calls Report only when selection/metadata is refused.
          [Console]::WriteLine('MadClean.DefenderRequest/1:refused')
        }
        function Complete-DefenderProfile($command) {
          try {
            # Base64 for the confirmed, normalized UTF-16 path. The pipe is closed
            # after its one value; path text is never evaluated as PowerShell source.
            $buffer = [char[]]::new(1025)
            $count = 0
            while ($count -lt $buffer.Length) {
              $read = [Console]::In.Read($buffer, $count, $buffer.Length - $count)
              if ($read -eq 0) { break }
              $count += $read
            }
            if ($count -eq 0 -or $count -gt 1024) { Report 'input'; return }
            $inputValue = [string]::new($buffer, 0, $count)
            $bytes = [Convert]::FromBase64String($inputValue)
            if ([Convert]::ToBase64String($bytes) -cne $inputValue) { Report 'input'; return }
            $folder = [Text.UnicodeEncoding]::new($false, $false, $true).GetString($bytes)
            if ($folder.Length -lt 4 -or $folder.Length -gt 258 -or
                $folder -cnotmatch '^[A-Z]:\\[^\r\n]+$' -or
                -not [string]::Equals([IO.Path]::GetFullPath($folder), $folder, [StringComparison]::OrdinalIgnoreCase)) {
              Report 'input'; return
            }
          } catch { Report 'input'; return }

          # Enter the already-checked function with literal operation and separately
          # bound folder. Suppress all command streams, including provider findings.
          try {
            & $command -ScanType CustomScan -ScanPath $folder -ErrorAction Stop *> $null
            [Console]::WriteLine('MadClean.DefenderRequest/1:returned')
          } catch {
            [Console]::WriteLine('MadClean.DefenderRequest/1:unknown')
          }
        }
        """ + "\n" + WindowsDefenderCommandProbe.ProfileSelectionScript;
}
