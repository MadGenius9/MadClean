using System.Buffers;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.WindowsSmoke;

internal static partial class NativeFixtures
{
    private static void PackagedApplications(SmokeReport report, CancellationToken token)
    {
        const string check = "packaged-application-contract";
        var inventory = new WindowsPackagedApplicationInventory();
        try
        {
            bool cancellationObserved;
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                try
                {
                    var stopped = inventory.ReadAsync(cancelled.Token).GetAwaiter().GetResult();
                    cancellationObserved = stopped.State == CheckState.Cancelled && stopped.Items.Count == 0;
                }
                catch (OperationCanceledException) { cancellationObserved = true; }
            }
            if (!cancellationObserved)
            {
                report.Fail(check, "Pre-cancelled package metadata did not remain cancelled/empty; no actual provider query started.");
                return;
            }
            // Existing fixed current-user Main/Bundle getter only. No deployment, fixture
            // package installation, other-user query, manifest or installation-path access.
            var result = Query(ct => inventory.ReadAsync(ct), token);
            if (result.State is not (CheckState.Available or CheckState.Partial) || result.Items.Count == 0)
            {
                report.Unknown(check, $"Package state={result.State}; itemCount={result.Items.Count}; preCancellationObserved=true; actualMetadataVerified=false. Empty completed inventory is legitimate, but does not verify a positive-row native projection or prove software absence. No package action or metadata export.");
                return;
            }
            var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int unknownRows = 0;
            if (result.Items.Count > PackagedApplicationParser.MaximumRecords)
            {
                report.Fail(check, "Actual current-user package projection exceeded its fixed record bound; no package identifiers or provider output exported.");
                return;
            }
            foreach (var app in result.Items)
            {
                if (app == null || !PackageText(app.Name, 256, allowUnknown: false) || !PackageText(app.FullName, 256, allowUnknown: false) ||
                    !PackageText(app.FamilyName, 128, allowUnknown: false) || !PackageText(app.Publisher, 1024, allowUnknown: true) ||
                    !identities.Add(app.FullName) || app.IsFramework == true || app.IsResourcePackage == true ||
                    app.PackageType is not ("Main" or "Bundle" or "Unknown") ||
                    app.Architecture is not ("X86" or "Arm" or "X64" or "Neutral" or "Arm64" or "X86OnArm64" or "Unknown") ||
                    app.SignatureKind is not ("None" or "Developer" or "Enterprise" or "Store" or "System" or "Unknown") ||
                    app.Version != "Unknown" && !PackageVersion(app.Version))
                {
                    report.Fail(check, "Actual package rows violated safe unique identity/schema, version/enumeration metadata or known framework/resource exclusions. All offending values, package identifiers and provider text withheld.");
                    return;
                }
                bool unknown = app.Version == "Unknown" || app.Publisher == "Unknown" || app.Architecture == "Unknown" ||
                    app.PackageType == "Unknown" || app.SignatureKind == "Unknown" || !app.NonRemovable.HasValue ||
                    !app.IsFramework.HasValue || !app.IsResourcePackage.HasValue;
                if (unknown) unknownRows++;
            }
            if (unknownRows > 0 && result.State != CheckState.Partial)
            {
                report.Fail(check, "Unknown package metadata or nullable provider flags were promoted to complete coverage. No actual flag values or package data exported.");
                return;
            }
            report.Pass(check, $"Package state={result.State}; itemCount={result.Items.Count}; rowsWithUnknownMetadata={unknownRows}; preCancellationObserved=true; projectedSchemaValid=true; fixedCurrentUserProviderRequested=true; actualMetadataVerified=true. " +
                "A real nonempty bounded Main/Bundle projection was returned; nullable flags remain unknown and Partial coverage remains incomplete. Bundle/main records are not unique-app counts or a combined ARP baseline. No names, identities, publishers, versions, SIDs, paths, flag values or provider text exported. No update, removal, repair, fixture package installation or other deployment action performed; metadata does not establish trust, health or action eligibility.");
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            report.Unknown(check, $"The bounded current-user package query did not complete (code 0x{ex.HResult:X8}); actualMetadataVerified=false. No positive-row verification, software absence or deployment outcome inferred; no provider data exported.");
        }
    }

    private static bool PackageVersion(string? version)
    {
        if (version == null) return false;
        string[] parts = version.Split('.');
        return parts.Length == 4 && parts.All(part => ushort.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out ushort number) &&
            number.ToString(CultureInfo.InvariantCulture) == part);
    }

    private static bool PackageText(string? text, int maximum, bool allowUnknown)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximum || text != text.Trim() || !allowUnknown && text == "Unknown") return false;
        var remaining = text.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out var rune, out int consumed) != OperationStatus.Done ||
                Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) return false;
            remaining = remaining[consumed..];
        }
        return true;
    }
}
