using System.ComponentModel;
using System.IO;
using System.Text;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.WindowsSmoke;

internal static partial class NativeFixtures
{
    private static void LeftoverContent(string fixture, SmokeReport report, CancellationToken token)
    {
        const string check = "leftover-content-fixture";
        string root = Path.Combine(fixture, "former-application");
        string bin = Path.Combine(root, "bin"), saves = Path.Combine(root, "Saves"), settings = Path.Combine(root, "Settings");
        string outside = Path.Combine(fixture, "leftover-outside-scope");
        foreach (string directory in new[] { root, bin, saves, settings, outside }) Directory.CreateDirectory(directory);
        var fixtureBytes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            [Path.Combine(root, "remaining.bin")] = Encoding.ASCII.GetBytes("Owned former-app fixture metadata A"),
            [Path.Combine(bin, "component.dll")] = Encoding.ASCII.GetBytes("Inert owned bytes, never loaded or executed"),
            [Path.Combine(saves, "owned-save.sav")] = Encoding.ASCII.GetBytes("Protected save fixture, not junk"),
            [Path.Combine(settings, "owned-settings.ini")] = Encoding.ASCII.GetBytes("Protected settings fixture, not junk"),
            [Path.Combine(outside, "outside.bin")] = Encoding.ASCII.GetBytes("Owned outside-scope sentinel, must not appear")
        };
        foreach (var file in fixtureBytes) File.WriteAllBytes(file.Key, file.Value);
        var links = new List<string>();
        try
        {
            TryOwnedLeftoverLink(Path.Combine(root, "escape-link"), outside, links);
            TryOwnedLeftoverLink(Path.Combine(root, "cycle-link"), root, links);
            // Synthetic developer inputs exercise the typed scope factory only. They do not
            // represent a real uninstall or real ARP collection and are never put into the UI.
            const string identity = "development-leftover-content-fixture";
            const string evidence = "DEVELOPMENT FIXTURE: synthetic prior registration absent from synthetic after snapshot; no actual uninstall or ARP write.";
            const string caution = "Candidates only; not proven junk. Shared ownership, active use and removal safety remain Unknown.";
            var candidate = new LeftoverCandidate(identity, root, evidence, caution);
            var before = new CheckResult<InstalledApplication>(CheckState.Available, "Development-only synthetic complete before fixture",
                [new(identity, "Owned inert fixture", "1", "Development fixture", "Synthetic fixture, not actual ARP", root, "Unknown")]);
            var after = new CheckResult<InstalledApplication>(CheckState.Available, "Development-only synthetic complete after fixture", []);
            var validation = LeftoverContentReview.ValidateScope(candidate, before, after, DateTimeOffset.UtcNow, token);
            if (validation.State != CheckState.Available || validation.Items.Count != 1)
            {
                report.Unknown(check, $"Development scope state={validation.State}; no native preview was started. The synthetic registration fixture did not qualify; no actual uninstall is inferred.");
                return;
            }
            var scope = validation.Items[0];
            if (scope.SourceEvidence != evidence || scope.SourceCaution != caution || !PathEquals(scope.RootPath, root))
            {
                report.Fail(check, "The typed development scope lost its exact selection or source uncertainty; no native preview was started.");
                return;
            }
            var result = Query(ct => new WindowsLeftoverContentInventory().PreviewAsync(scope, ct), token);
            if (result.State is not (CheckState.Available or CheckState.Partial))
            {
                report.Unknown(check, $"Native leftover preview state={result.State}; owned metadata, exclusions and reparse boundaries remain unverified. No real uninstall or application mutation was performed.");
                return;
            }
            if (result.Items.Count != 1)
            {
                report.Fail(check, "A readable leftover preview did not contain exactly one bounded report.");
                return;
            }
            var data = result.Items[0];
            string[] expectedFiles = [Path.Combine(root, "remaining.bin"), Path.Combine(bin, "component.dll")];
            if (!PathEquals(data.RootPath, root) || data.Entries.Any(entry =>
                entry.ActiveUse != "Unknown" || string.IsNullOrWhiteSpace(entry.Identity) ||
                (entry.IsDirectory ? !PathEquals(entry.Path, bin) || entry.Bytes != null :
                    !expectedFiles.Any(path => PathEquals(entry.Path, path)) || entry.Bytes != fixtureBytes.GetValueOrDefault(entry.Path)?.LongLength)) ||
                data.Entries.Select(entry => Path.GetFullPath(entry.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != data.Entries.Count)
            {
                report.Fail(check, "Leftover metadata contradicted exact fixture paths/types/sizes/identity or Unknown active use, exposed protected/out-of-scope names, or repeated a path. Values and paths are withheld.");
                return;
            }
            bool ordinaryComplete = data.Entries.Count == 3 && data.Entries.Count(entry => !entry.IsDirectory) == 2;
            bool protectedExcluded = new[] { saves, settings }.All(path => data.Issues.Any(issue => PathEquals(issue.Path, path)));
            bool linksExcluded = links.All(path => data.Issues.Any(issue => PathEquals(issue.Path, path)));
            bool sourceRetained = data.Coverage.Contains(evidence, StringComparer.Ordinal) && data.Coverage.Contains(caution, StringComparer.Ordinal) &&
                data.Coverage.Contains(scope.OwnershipCoverage, StringComparer.Ordinal);
            if (!ordinaryComplete || !protectedExcluded || !linksExcluded || !sourceRetained || data.Truncated ||
                data.VisitedFiles != 2 || data.VisitedDirectories != 2)
            {
                report.Unknown(check, $"Owned leftover coverage incomplete: ordinaryMetadataComplete={ordinaryComplete}; protectedExclusionsObserved={protectedExcluded}; createdReparseExclusionsObserved={linksExcluded}; sourceUncertaintyRetained={sourceRetained}; truncated={data.Truncated}. No complete preview-boundary proof.");
                return;
            }
            if (result.State != CheckState.Partial)
            {
                report.Fail(check, "Known protected/reparse exclusions were not reflected as incomplete leftover coverage.");
                return;
            }
            // Developer-owned byte audit only. The production adapter is metadata-only.
            if (fixtureBytes.Any(file => !File.ReadAllBytes(file.Key).SequenceEqual(file.Value)) ||
                !Directory.Exists(saves) || !Directory.Exists(settings) || links.Any(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0))
            {
                report.Fail(check, "Owned fixture files/directories/reparse entries changed unexpectedly during read-only preview.");
                return;
            }
            report.Pass(check, $"Native leftover state={result.State}; ordinaryFileCount=2; ordinaryDirectoryCount=1; protectedExclusionsVerified=true; createdReparseCount={links.Count}; createdReparseExclusionsVerified={linksExcluded}; fixtureBytesUnchanged=true; activeUse=Unknown. Synthetic development registrations only, no actual uninstall/ARP write. " +
                (links.Count == 2 ? "Owned escape/cycle links were both excluded. " : "Unavailable link setup leaves uncreated reparse cases unverified. ") +
                "No fixture paths, identities, contents or inventory exported; metadata does not establish ownership, inactivity, junk or deletion safety.");
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            report.Unknown(check, $"Owned leftover preview did not complete (code 0x{ex.HResult:X8}); no native preview/exclusion proof is claimed.");
        }
        finally
        {
            // Remove only the exact symlink objects created above, nonrecursively, before global
            // fixture teardown (which intentionally refuses all remaining reparse entries).
            foreach (string link in links)
            {
                try
                {
                    if (Path.GetDirectoryName(link) != root || (File.GetAttributes(link) & FileAttributes.ReparsePoint) == 0)
                        throw new IOException();
                    Directory.Delete(link, recursive: false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    report.Unknown("leftover-content-link-teardown", $"An exact owned link could not be removed nonrecursively (code 0x{ex.HResult:X8}); no traversal/deletion of its target was attempted.");
                }
            }
        }
    }

    private static void TryOwnedLeftoverLink(string path, string target, List<string> links)
    {
        try { Directory.CreateSymbolicLink(path, target); links.Add(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or Win32Exception)
        { /* Optional OS privilege/capability: an uncreated link never counts as reparse proof. */ }
    }
}
