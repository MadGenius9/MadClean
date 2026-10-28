using System.ComponentModel;
using System.IO;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.WindowsSmoke;

internal static partial class NativeFixtures
{
    private static void MsiRegistrations(CheckResult<InstalledApplication>? registrations, SmokeReport report, CancellationToken token)
    {
        const string check = "msi-registration-contract";
        if (registrations == null || registrations.State is not (CheckState.Available or CheckState.Partial) || registrations.Items.Count == 0)
        {
            report.Unknown(check, "The distinct actual ARP metadata check supplied no qualified nonempty in-memory snapshot. No MSI lookup started; absence, installed state and native metadata support remain unverified.");
            return;
        }
        var inventory = new WindowsMsiRegistrationInventory();
        var attempts = new List<string>();
        bool cancellationVerified = false;
        int selectedCount = 0;
        try
        {
            foreach (var app in registrations.Items)
            {
                token.ThrowIfCancellationRequested();
                if (selectedCount == 3) break;
                var evidence = app.RegistrationEvidence;
                if (evidence is not { WindowsInstaller: true, ProductCode: not null } ||
                    !app.Identity.EndsWith("\\" + evidence.ProductCode, StringComparison.OrdinalIgnoreCase)) continue;
                var selectedAt = DateTimeOffset.UtcNow;
                var qualified = MsiRegistrationReview.CreateScope(registrations, app.Identity, selectedAt, token);
                if (qualified.Items.Count == 0) continue;
                if (qualified.Items.Count != 1 || qualified.State != registrations.State)
                {
                    report.Fail(check, "The read-only MSI scope factory contradicted the exact selected source or snapshot coverage state. No metadata values exported.");
                    return;
                }
                var scope = qualified.Items[0];
                if (scope.Application != app || scope.ProductCode != evidence.ProductCode || scope.SnapshotState != registrations.State ||
                    scope.ExpectedContextMask is not (3 or 4) || scope.ValidatedUtc != selectedAt)
                {
                    report.Fail(check, "The selected MSI scope did not preserve exact input provenance, validation time or allowed context boundary; no native call started.");
                    return;
                }
                selectedCount++;
                if (inventory.HasPendingNativeRead)
                {
                    report.Unknown(check, "A native MSI worker remains pending; no overlapping query or forced cancellation was attempted. " + string.Join("; ", attempts));
                    return;
                }
                if (!cancellationVerified)
                {
                    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                    try
                    {
                        var stopped = inventory.VerifyAsync(scope, cancelled.Token).GetAwaiter().GetResult();
                        cancellationVerified = stopped.State == CheckState.Cancelled && stopped.Items.Count == 0;
                    }
                    catch (OperationCanceledException) { cancellationVerified = true; }
                    if (!cancellationVerified || inventory.HasPendingNativeRead)
                    {
                        report.Fail(check, "Pre-cancelled selected MSI lookup did not remain cancelled/empty with no pending worker; no actual MSI query started.");
                        return;
                    }
                }
                var before = DateTimeOffset.UtcNow;
                var result = Query(ct => inventory.VerifyAsync(scope, ct), token);
                var after = DateTimeOffset.UtcNow;
                if (inventory.HasPendingNativeRead)
                {
                    report.Unknown(check, $"Selected MSI state={result.State}; pendingWorker=true; actualBindingVerified=false. The caller wait ended without proof native work stopped; no additional query started.");
                    return;
                }
                attempts.Add($"attempt{selectedCount}State={result.State}/reports={result.Items.Count}");
                if (result.State is not (CheckState.Available or CheckState.Partial)) continue;
                if (result.Items.Count != 1)
                {
                    report.Fail(check, "A readable selected MSI result omitted its unique source-bound report.");
                    return;
                }
                var data = result.Items[0];
                if (data.RegistrationIdentity != app.Identity || data.ProductCode != scope.ProductCode ||
                    data.SnapshotState != registrations.State || data.NoRemove != evidence.NoRemove ||
                    data.CheckedUtc.Offset != TimeSpan.Zero || data.CheckedUtc < before || data.CheckedUtc > after ||
                    data.Coverage.Count == 0 || data.Instances.Count != 1)
                {
                    report.Fail(check, "Successful selected MSI metadata contradicted exact source/coverage/restriction/time or unique-context provenance; all actual values withheld.");
                    return;
                }
                var instance = data.Instances[0];
                bool valid = instance.ProductCode == scope.ProductCode && Enum.IsDefined(instance.Context) &&
                    ((int)instance.Context & scope.ExpectedContextMask) == (int)instance.Context &&
                    instance.ProductState == MsiObservedProductState.Installed &&
                    instance.ObservedUtc.Offset == TimeSpan.Zero && instance.ObservedUtc >= scope.ValidatedUtc && instance.ObservedUtc <= data.CheckedUtc &&
                    InstalledMetadataText(instance.Name, MsiRegistrationReview.NameCharacters) &&
                    InstalledMetadataText(instance.Version, MsiRegistrationReview.VersionCharacters) &&
                    InstalledMetadataText(instance.Publisher, MsiRegistrationReview.PublisherCharacters) &&
                    instance.Name != "Unknown" && instance.Version != "Unknown" && instance.Publisher != "Unknown" &&
                    instance.Name == app.Name && instance.Version == app.Version && instance.Publisher == app.Publisher;
                bool expectedPartial = registrations.State == CheckState.Partial || evidence.NoRemove == null;
                if (!valid || result.State != (expectedPartial ? CheckState.Partial : CheckState.Available))
                {
                    report.Fail(check, "Successful selected MSI metadata violated exact Installed-context/display binding or upgraded incomplete source/restriction coverage. No names, product codes, contexts or flags exported.");
                    return;
                }
                report.Pass(check, $"MSI state={result.State}; selectedQueryCount={selectedCount}; instanceCount=1; actualBindingVerified=true; fixedSourceProvenanceVerified=true; preCancellationObserved=true; pendingWorker=false. " +
                    "A real exact-selected-product Installed context and display metadata match the reused actual ARP observation. Partial source coverage and NoRemove restrictions remain; this grants no action eligibility. No names, IDs/product codes, SID data, paths, versions, times, flags or provider messages exported. No all-products/other-user inventory, package open, install, uninstall, repair, source-path query, elevation or fixture MSI registration performed.");
                return;
            }
            report.Unknown(check, $"Selected MSI actualBindingVerified=false; selectedQueryCount={Math.Min(selectedCount, 3)}; preCancellationObserved={cancellationVerified}; pendingWorker=false. " +
                string.Join("; ", attempts) + ". No eligible exact installed match was established; missing/advertised/ambiguous/mismatched/failed metadata is not absence or uninstall proof. No metadata values exported.");
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            report.Unknown(check, $"Selected MSI fixture did not complete (code 0x{ex.HResult:X8}); actualBindingVerified=false. Native ownership may remain pending; no force-abort or action occurred and no provider metadata was exported.");
        }
    }
}
