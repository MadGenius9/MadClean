using System.Globalization;

namespace MadClean.Core;

/// <summary>Read-only registration evidence; neither command data nor an execution authorization.</summary>
public sealed record InstalledAppRegistrationEvidence(string Identity, string Source, bool? NoRemove,
    bool? WindowsInstaller, bool? HasRegisteredUninstaller, string? ProductCode, DateTimeOffset ObservedUtc);
public enum InstalledAppPlanMechanism { WindowsInstalledAppsReview, WindowsInstallerProductReview }
public sealed record InstalledAppActionPlan(InstalledApplication Application, InstalledAppRegistrationEvidence Evidence,
    InstalledAppPlanMechanism Mechanism, GuidedWindowsTask Handoff, string ExactEffect, string Permissions, string Limitations);

public static class InstalledAppActionPlans
{
    public static readonly TimeSpan EvidenceLifetime = TimeSpan.FromMinutes(2);
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\";
    private static readonly (string Prefix, string Source)[] Sources =
    [
        ("HKCU/shared/", "HKCU / shared / Add or Remove Programs"),
        ("HKLM/64/", "HKLM / 64-bit / Add or Remove Programs"),
        ("HKLM/32/", "HKLM / 32-bit / Add or Remove Programs")
    ];

    /// <summary>Builds metadata for a later user-reviewed Windows Settings handoff; performs no I/O.</summary>
    public static CheckResult<InstalledAppActionPlan> Preview(CheckResult<InstalledApplication> currentSnapshot,
        string selectedIdentity, DateTimeOffset now, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (currentSnapshot == null || currentSnapshot.State != CheckState.Available || currentSnapshot.Items == null ||
            currentSnapshot.Items.Count > 20_000 || !SafeText(selectedIdentity, 1_024))
            return Block("A complete bounded current installed-app snapshot and an exact selected identity are required.");
        var matches = new List<InstalledApplication>(2);
        foreach (var item in currentSnapshot.Items)
        {
            token.ThrowIfCancellationRequested();
            if (item == null || !SafeText(item.Identity, 1_024)) return Block("Snapshot contains an invalid registration identity; refresh inventory.");
            if (item.Identity.Equals(selectedIdentity, StringComparison.OrdinalIgnoreCase)) matches.Add(item);
            if (matches.Count > 1) return Block("Selected registration identity is ambiguous; no plan was created.");
        }
        if (matches.Count != 1) return Block("Selected registration is missing from the current snapshot; refresh inventory.");
        var app = matches[0]; var evidence = app.RegistrationEvidence;
        if (!SafeText(app.Name, 1_024) || !SafeText(app.Version, 256) || !SafeText(app.Publisher, 512) ||
            !TryRegistration(app.Identity, app.Source, out string subkey) || evidence == null ||
            !app.Identity.Equals(evidence.Identity, StringComparison.Ordinal) || !app.Source.Equals(evidence.Source, StringComparison.Ordinal) ||
            evidence.ObservedUtc == default || evidence.ObservedUtc.Offset != TimeSpan.Zero || evidence.ObservedUtc > now ||
            now - evidence.ObservedUtc > EvidenceLifetime)
            return Block("Exact source-bound registration evidence is missing, invalid or stale; refresh inventory. Display hints are insufficient.");
        if (evidence.NoRemove != false)
            return Block(evidence.NoRemove == true ? "Removal is restricted by this registration; no selected removal plan is offered."
                : "The registration removal restriction is unknown; no removal plan is offered.", CheckState.Unavailable);
        if (!evidence.WindowsInstaller.HasValue || !evidence.HasRegisteredUninstaller.HasValue)
            return Block("Supported mechanism metadata is unknown; no command or mechanism was inferred.");
        InstalledAppPlanMechanism mechanism;
        string mechanismLimit;
        if (evidence.WindowsInstaller == true)
        {
            string? expectedCode = ProductCodeFromSubkey(subkey);
            if (expectedCode == null || evidence.ProductCode != expectedCode)
                return Block("Windows Installer registration lacks an exact source-bound product-code GUID; no MSI operation was inferred.");
            mechanism = InstalledAppPlanMechanism.WindowsInstallerProductReview;
            mechanismLimit = "WindowsInstaller flag and product-code GUID format are registration evidence only. Installer database, installation context, package integrity, eligibility and actual removal availability were not queried.";
        }
        else
        {
            if (evidence.ProductCode != null || evidence.HasRegisteredUninstaller != true)
                return Block("No supported registered-uninstaller presence hint was established; no command was read or inferred.");
            mechanism = InstalledAppPlanMechanism.WindowsInstalledAppsReview;
            mechanismLimit = "Uninstaller value-name presence does not establish a valid, trusted, working or supported executable. No raw UninstallString/QuietUninstallString value was read.";
        }
        token.ThrowIfCancellationRequested();
        var plan = new InstalledAppActionPlan(app, evidence, mechanism, GuidedWindowsTask.InstalledApps,
            "Preview only. A separately selected handoff may open the general Windows Installed apps / Apps & Features page. No application identifier, product code, command or argument is passed; find and review the application yourself in Windows.",
            "This plan performs no I/O, launch or elevation. Windows may require separate administrator consent or enforce organizational policy for any action later selected there.",
            mechanismLimit + " Registration data can be stale or modified; this plan is not authorization for execution and must not be converted to an uninstall command. Windows-page opening does not execute, monitor or prove uninstall completion; status remains Unknown. Shared files, user data, saves, settings and leftovers must not be inferred safe to remove. Refresh evidence before another review.");
        return new(CheckState.Available, "Read-only selected registration review plan prepared. No uninstall or Windows handoff was executed; completion remains Unknown.", [plan]);
    }

    public static string? ProductCodeFromSubkey(string? subkey) => subkey != null && Guid.TryParseExact(subkey, "B", out var code) && code != Guid.Empty
        ? code.ToString("B").ToUpperInvariant() : null;

    private static bool TryRegistration(string identity, string source, out string subkey)
    {
        subkey = "";
        foreach (var entry in Sources)
        {
            string prefix = entry.Prefix + RegistryPath;
            if (source != entry.Source || !identity.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            subkey = identity[prefix.Length..];
            return SafeText(subkey, 256) && subkey.IndexOfAny(['\\', '/']) < 0 && subkey is not ("." or "..");
        }
        return false;
    }
    private static bool SafeText(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum &&
        !value.Any(character => char.IsControl(character) || char.GetUnicodeCategory(character) == UnicodeCategory.Format);
    private static CheckResult<InstalledAppActionPlan> Block(string reason, CheckState state = CheckState.Unknown) =>
        CheckResult<InstalledAppActionPlan>.Empty(state, reason + " No launch, uninstall or removal occurred.");
}
