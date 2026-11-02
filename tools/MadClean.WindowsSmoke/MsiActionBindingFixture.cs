using System.IO;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.WindowsSmoke;

internal static partial class NativeFixtures
{
    public static bool MsiActionBindingQuerySettled { get; private set; } = true;

    private static void MsiActionBindingFixture(CheckResult<InstalledApplication>? registrations, SmokeReport report, CancellationToken token)
    {
        const string check = "msi-action-binding-contract";
        var provider = new WindowsMsiActionBindingInventory();
        if (provider.HasPendingNativeRead)
        {
            MsiActionBindingQuerySettled = false;
            report.Unknown(check, "An earlier shared MSI worker remains pending; no selected source, cancellation proof or additional query was manufactured.");
            return;
        }
        if (registrations?.Items == null || registrations.State is not (CheckState.Available or CheckState.Partial) ||
            registrations.Items.Count is 0 or > 20000)
        { report.Unknown(check, "No actual bounded ARP snapshot is available for exact selection; no registration or MSI data was seeded."); return; }
        MsiActionBindingScope? scope = null;
        foreach (var app in registrations.Items)
        {
            token.ThrowIfCancellationRequested();
            if (app?.RegistrationEvidence is not { WindowsInstaller: true, ProductCode: not null }) continue;
            var qualified = MsiActionBindingPolicy.CreateScope(registrations, app, DateTimeOffset.UtcNow, token);
            if (qualified.Items.Count == 1) { scope = qualified.Items[0]; break; }
        }
        if (scope == null)
        { report.Unknown(check, "No genuine current exact-reference MSI selection qualified; cross-context runtime coverage remains unverified. No fixture registrations or guessed product identifiers were substituted."); return; }
        bool preCancelled = false;
        try
        {
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                try
                {
                    var stopped = provider.VerifyAsync(scope, cancellation.Token).GetAwaiter().GetResult();
                    preCancelled = stopped.State == CheckState.Cancelled && stopped.Items.Count == 0;
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { preCancelled = true; }
            }
            if (!preCancelled || provider.HasPendingNativeRead)
                throw new SmokeAssertionException("msi-action-binding-pre-cancel");
            var before = DateTimeOffset.UtcNow;
            MsiActionBindingQuerySettled = false;
            var result = Query(ct => provider.VerifyAsync(scope, ct), token);
            var after = DateTimeOffset.UtcNow;
            MsiActionBindingQuerySettled = !provider.HasPendingNativeRead;
            if (!MsiActionBindingQuerySettled)
            { report.Unknown(check, "The caller returned while the shared MSI worker is still pending; no next provider/fixture may start. No completion or force-abort is claimed."); return; }
            if (result.Items.Count != 1)
            { report.Unknown(check, $"sourceState={result.State}; reportCount={result.Items.Count}; preCancelled={preCancelled}; querySettled=true; actual cross-context coverage was not established. No source identifiers or raw provider text exported."); return; }
            var data = result.Items[0];
            if (!Enum.IsDefined(data.State) || !Enum.IsDefined(data.CurrentSnapshotState) || data.CanExecute ||
                data.RegistrationIdentity != scope.Application.Identity || data.ProductCode != scope.ProductCode ||
                data.SelectionSnapshotState != registrations.State || data.ExpectedContextMask != scope.ExpectedContextMask ||
                data.CheckedUtc.Offset != TimeSpan.Zero || data.CheckedUtc < before || data.CheckedUtc > after ||
                data.Instances == null || data.Instances.Count > 3 || data.Coverage == null || data.Coverage.Count == 0)
                throw new SmokeAssertionException("msi-action-binding-report-shape");
            var contexts = new HashSet<MsiRegistrationContext>();
            foreach (var instance in data.Instances)
            {
                if (instance == null || instance.ProductCode != scope.ProductCode || !Enum.IsDefined(instance.Context) ||
                    !contexts.Add(instance.Context) || !Enum.IsDefined(instance.ProductState) || instance.ObservedUtc.Offset != TimeSpan.Zero ||
                    instance.ObservedUtc < scope.ValidatedUtc || instance.ObservedUtc > data.CheckedUtc)
                    throw new SmokeAssertionException("msi-action-binding-context-shape");
            }
            if (!data.EnumerationComplete || data.Instances.Count == 0 || data.State == MsiActionBindingState.Unknown ||
                data.CurrentSnapshotState is not (CheckState.Available or CheckState.Partial))
            { report.Unknown(check, $"sourceState={result.State}; bindingState={data.State}; observedContextCount={data.Instances.Count}; enumerationComplete={data.EnumerationComplete}; preCancelled={preCancelled}; querySettled=true; no action permission. Actual current-user/machine metadata support remains inconclusive; values withheld."); return; }
            if (data.State == MsiActionBindingState.Bound)
            {
                var instance = data.Instances.Single();
                if (result.State is not (CheckState.Available or CheckState.Partial) || data.NoRemove != false ||
                    ((int)instance.Context & scope.ExpectedContextMask) == 0 || instance.ProductState != MsiObservedProductState.Installed ||
                    instance.Name != scope.Application.Name || instance.Version != scope.Application.Version || instance.Publisher != scope.Application.Publisher ||
                    (data.SelectionSnapshotState == CheckState.Partial || data.CurrentSnapshotState == CheckState.Partial) && result.State != CheckState.Partial)
                    throw new SmokeAssertionException("msi-action-binding-positive-invariant");
            }
            else if (data.State != MsiActionBindingState.Blocked || result.State != CheckState.Unavailable)
                throw new SmokeAssertionException("msi-action-binding-blocked-invariant");
            report.Pass(check, $"sourceState={result.State}; bindingState={data.State}; observedContextCount={data.Instances.Count}; enumerationComplete=true; preCancelled={preCancelled}; querySettled=true; canExecute=false. One genuine selected product was rechecked using fresh actual ARP and full current-user/machine metadata scope. Other users/global absence, package trust, atomic continuity and action permission remain unverified. No registry seeding, raw values, identifiers, commands, process launch, installer action or journal write occurred.");
        }
        catch (SmokeAssertionException) { throw; }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            MsiActionBindingQuerySettled = !provider.HasPendingNativeRead;
            report.Unknown(check, $"preCancelled={preCancelled}; querySettled={MsiActionBindingQuerySettled}; the actual selected read did not establish complete runtime evidence. No raw exception/provider content or identifiers exported.");
        }
    }
}
