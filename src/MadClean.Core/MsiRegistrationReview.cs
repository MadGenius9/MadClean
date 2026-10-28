using System.Buffers;
using System.Globalization;
using System.Text;

namespace MadClean.Core;

/// <summary>Source-bound metadata comparisons; successful observation does not authorize MSI execution.</summary>
public static class MsiRegistrationReview
{
    public const int MaxInstances = 3;
    public const int MaximumIssues = 32;
    public const int ProductCodeBufferCharacters = 39;
    public const int NameCharacters = 1_024;
    public const int VersionCharacters = 256;
    public const int PublisherCharacters = 512;
    public const int StateCharacters = 1;
    public static readonly TimeSpan ScopeLifetime = TimeSpan.FromMinutes(2);
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\";
    private static readonly (string Prefix, string Source, int ContextMask)[] Sources =
    [
        ("HKCU/shared/", "HKCU / shared / Add or Remove Programs", 3),
        ("HKLM/64/", "HKLM / 64-bit / Add or Remove Programs", 4),
        ("HKLM/32/", "HKLM / 32-bit / Add or Remove Programs", 4)
    ];

    public static CheckResult<MsiRegistrationScope> CreateScope(CheckResult<InstalledApplication> snapshot,
        string selectedIdentity, DateTimeOffset now, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (snapshot == null || snapshot.State is not (CheckState.Available or CheckState.Partial) || snapshot.Items == null ||
            snapshot.Items.Count > 20_000 || !SafeText(selectedIdentity, 1_024) || string.IsNullOrWhiteSpace(selectedIdentity) || !Utc(now))
            return ScopeUnknown("A bounded Available or Partial registration snapshot and an exact selected identity are required.");
        InstalledApplication? selected = null;
        foreach (var app in snapshot.Items)
        {
            token.ThrowIfCancellationRequested();
            if (app == null || !SafeText(app.Identity, 1_024) || string.IsNullOrWhiteSpace(app.Identity))
                return ScopeUnknown("Snapshot contains invalid registration identities; refresh the source.");
            if (!app.Identity.Equals(selectedIdentity, StringComparison.OrdinalIgnoreCase)) continue;
            if (selected != null) return ScopeUnknown("The selected identity is duplicated among observed registrations; no source was guessed.");
            selected = app;
        }
        if (selected == null) return ScopeUnknown("The selected registration was not observed in this snapshot. Missing coverage does not prove absence or uninstallation.");
        var evidence = selected.RegistrationEvidence;
        if (evidence == null || evidence.WindowsInstaller != true || !selected.Identity.Equals(evidence.Identity, StringComparison.Ordinal) ||
            !string.Equals(selected.Source, evidence.Source, StringComparison.Ordinal) || !Utc(evidence.ObservedUtc) || evidence.ObservedUtc > now ||
            now - evidence.ObservedUtc > ScopeLifetime || !SafeText(selected.Name, NameCharacters) ||
            !SafeText(selected.Version, VersionCharacters) || !SafeText(selected.Publisher, PublisherCharacters) ||
            !TrySource(selected, out string code, out int mask) || evidence.ProductCode != code)
            return ScopeUnknown("Selected Windows Installer evidence is missing, stale, malformed or not bound to the exact source/product-code registration. Refresh inventory.");
        token.ThrowIfCancellationRequested();
        var scope = new MsiRegistrationScope(selected, code, mask, snapshot.State, now);
        return new(snapshot.State, "Read-only selected MSI lookup scope prepared. NoRemove remains visible, including restricted/unknown values; no action eligibility or uninstall is authorized.", [scope]);
    }

    public static CheckResult<MsiRegistrationReport> Assess(MsiRegistrationScope scope,
        IReadOnlyList<MsiRegistrationObservation> instances, bool enumerationComplete, IReadOnlyList<string> issues,
        DateTimeOffset checkedUtc, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (scope == null || instances == null || issues == null || instances.Count > MaxInstances || issues.Count > MaximumIssues ||
            !Utc(checkedUtc) || !Utc(scope.ValidatedUtc) || scope.ValidatedUtc > checkedUtc || checkedUtc - scope.ValidatedUtc > ScopeLifetime)
            return Unknown("The bounded current MSI observation set or lookup scope could not be validated.");
        // Recheck the immutable source facts at assessment time, including ARP evidence age. A
        // successful native call never upgrades stale or altered registration evidence.
        var sourceCheck = CreateScope(new(scope.SnapshotState, "Selected source recheck", [scope.Application]),
            scope.Application.Identity, checkedUtc, token);
        var checkedScope = sourceCheck.Items.SingleOrDefault();
        if (checkedScope == null || checkedScope.ProductCode != scope.ProductCode || checkedScope.ExpectedContextMask != scope.ExpectedContextMask)
            return Unknown("Selected source evidence expired or no longer qualifies for this read-only comparison; refresh inventory.");
        foreach (string issue in issues)
        {
            token.ThrowIfCancellationRequested();
            if (!SafeText(issue, 256)) return Unknown("Native coverage issues were malformed or exceeded the bounded metadata contract.");
        }
        var seen = new HashSet<MsiRegistrationContext>();
        foreach (var instance in instances)
        {
            token.ThrowIfCancellationRequested();
            if (instance == null || instance.ProductCode != scope.ProductCode || !Enum.IsDefined(instance.Context) ||
                ((int)instance.Context & scope.ExpectedContextMask) == 0 || !Enum.IsDefined(instance.ProductState) ||
                !seen.Add(instance.Context) || !Utc(instance.ObservedUtc) || instance.ObservedUtc < scope.ValidatedUtc || instance.ObservedUtc > checkedUtc ||
                !SafeText(instance.Name, NameCharacters) || !SafeText(instance.Version, VersionCharacters) || !SafeText(instance.Publisher, PublisherCharacters))
                return Unknown("MSI product/context identity, observed time, display metadata or uniqueness could not be validated. No partial identity binding was inferred.");
        }
        bool matched = enumerationComplete && issues.Count == 0 && instances.Count == 1 &&
            instances[0].ProductState == MsiObservedProductState.Installed &&
            Known(scope.Application.Name) && Known(scope.Application.Version) && Known(scope.Application.Publisher) &&
            Known(instances[0].Name) && Known(instances[0].Version) && Known(instances[0].Publisher) &&
            string.Equals(scope.Application.Name, instances[0].Name, StringComparison.Ordinal) &&
            string.Equals(scope.Application.Version, instances[0].Version, StringComparison.Ordinal) &&
            string.Equals(scope.Application.Publisher, instances[0].Publisher, StringComparison.Ordinal);
        bool? noRemove = scope.Application.RegistrationEvidence!.NoRemove;
        var coverage = new List<string>
        {
            "Source: MsiEnumProductsExW filtered to the exact selected product code, then MsiGetProductInfoExW in the returned source-matching context. Input SID is NULL; output SID and SID-length pointers are NULL. No SID, other-user registration, raw uninstall command or package/source path was requested.",
            scope.ExpectedContextMask == 3 ? "Lookup coverage: current-user managed and unmanaged contexts only, corresponding to the observed HKCU registration. Managed and unmanaged are not interchangeable; multiple instances remain ambiguous."
                : "Lookup coverage: machine context only, corresponding to the observed HKLM registration. Registry 32/64-bit view does not establish MSI architecture or package bitness.",
            "Enumeration and property reads are sequential, non-atomic observations. A registration can change between calls; matching metadata does not establish a stable lock, installer integrity or a safe action target.",
            "ProductState is the MSI query observation: 1 means advertised, 5 means installed in the queried context. It does not assess running state, health, package integrity, removal support, reversibility or action eligibility. Advertised metadata may have only ProductName; installed-only version/publisher remain Unknown.",
            "Metadata comparison is exact Ordinal name/version/publisher equality against the selected observed ARP registration. A match is not independent publisher authentication or proof of removal eligibility; mismatched or missing display metadata remains Unknown.",
            "ARP snapshot state: " + scope.SnapshotState + ". Partial source coverage remains Partial even when this selected metadata matches; no absence, missing-app or completed-uninstall inference is permitted.",
            noRemove == true ? "NoRemove: restricted by the selected ARP registration. Read-only MSI metadata cannot override this restriction or authorize removal."
                : noRemove == false ? "NoRemove: no restriction was declared in the observed registration. This is not a permission or removal-support guarantee."
                : "NoRemove: Unknown. This uncertainty is preserved; read-only MSI metadata cannot authorize or infer removal eligibility.",
            enumerationComplete ? "The bounded exact-product/source-context enumeration finished. This is not an all-products or all-users inventory."
                : "Exact-product/source-context enumeration did not finish; the displayed observations are incomplete and cannot establish a binding.",
            "Properties are limited to State, InstalledProductName/advertised ProductName, VersionString and Publisher. No MsiOpenProduct, maintenance/self-repair, install/configure/msiexec, elevation, downloads, registry changes or restart is performed. Native calls are synchronous; cancellation ending the caller wait does not prove a pending call stopped."
        };
        foreach (string issue in issues) coverage.Add("Bounded MSI query issue: " + issue);
        token.ThrowIfCancellationRequested();
        var report = new MsiRegistrationReport(scope.Application.Identity, scope.ProductCode, scope.SnapshotState,
            noRemove, Array.AsReadOnly(instances.ToArray()), checkedUtc, Array.AsReadOnly(coverage.ToArray()));
        CheckState state = !matched ? CheckState.Unknown : scope.SnapshotState == CheckState.Partial || noRemove == null ? CheckState.Partial : CheckState.Available;
        return new(state, matched
            ? "Selected source-context MSI installed-state/display metadata matched the observed ARP registration. Coverage and NoRemove restrictions remain; no action eligibility or uninstall is claimed."
            : "MSI observations were obtained, but exact unique installed-state/display binding remains Unknown. No absence, uninstall completion or action eligibility is inferred.", [report]);
    }

    private static bool TrySource(InstalledApplication app, out string code, out int mask)
    {
        code = ""; mask = 0;
        foreach (var source in Sources)
        {
            string prefix = source.Prefix + KeyPath;
            if (app.Source != source.Source || !app.Identity.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            string leaf = app.Identity[prefix.Length..];
            // TryParseExact accepts surrounding whitespace; require exactly the 38-character B form
            // so a different registry subkey can never be normalized into a valid product identity.
            if (leaf.Length != 38 || leaf[0] != '{' || leaf[^1] != '}' || !Guid.TryParseExact(leaf, "B", out var guid) || guid == Guid.Empty) return false;
            code = guid.ToString("B").ToUpperInvariant(); mask = source.ContextMask; return true;
        }
        return false;
    }
    private static bool Known(string text) => !string.IsNullOrWhiteSpace(text) && !text.Trim().Equals("Unknown", StringComparison.OrdinalIgnoreCase);
    private static bool Utc(DateTimeOffset time) => time != default && time.Offset == TimeSpan.Zero;
    private static bool SafeText(string? text, int maximum)
    {
        if (text == null || text.Length > maximum) return false;
        var remaining = text.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out var rune, out int consumed) != OperationStatus.Done ||
                Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format) return false;
            remaining = remaining[consumed..];
        }
        return true;
    }
    private static CheckResult<MsiRegistrationScope> ScopeUnknown(string reason) =>
        CheckResult<MsiRegistrationScope>.Empty(CheckState.Unknown, reason + " No MSI query or action was performed by this portable policy.");
    private static CheckResult<MsiRegistrationReport> Unknown(string reason) =>
        CheckResult<MsiRegistrationReport>.Empty(CheckState.Unknown, reason + " No uninstall, repair or action eligibility is inferred.");
}
