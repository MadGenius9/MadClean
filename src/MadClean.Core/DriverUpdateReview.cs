using System.Globalization;

namespace MadClean.Core;

public sealed record DriverUpdateReviewRequirement(string Title, CheckState State, string Evidence, string Limit);

/// <summary>An in-memory review of one selected offer, never an installation capability.</summary>
public sealed class DriverUpdateReviewReport
{
    internal DriverUpdateReviewReport(SystemUpdate selectedOffer, DateTimeOffset reviewedUtc,
        DateTimeOffset attemptedUtc, DateTimeOffset receivedUtc, CheckState sourceState,
        IEnumerable<DriverUpdateReviewRequirement> requirements)
    {
        SelectedOffer = selectedOffer; ReviewedUtc = reviewedUtc;
        AttemptedUtc = attemptedUtc; ReceivedUtc = receivedUtc; SourceState = sourceState;
        Requirements = Array.AsReadOnly(requirements.ToArray());
    }
    public SystemUpdate SelectedOffer { get; }
    public DateTimeOffset ReviewedUtc { get; }
    public DateTimeOffset AttemptedUtc { get; }
    public DateTimeOffset ReceivedUtc { get; }
    public CheckState SourceState { get; }
    public IReadOnlyList<DriverUpdateReviewRequirement> Requirements { get; }
    public CheckState State => CheckState.Unknown;
    public bool CanInstall => false;
}

public static class DriverUpdateReviewPolicy
{
    public static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(30);
    public const string Limits = "Selected Windows Update offer snapshot review only, not a fresh applicability check or action token. " +
        "Provider declarations do not verify an exact device/OEM match, complete prerequisites or installation readiness. " +
        "No download, installation, bulk driver replacement, elevation, restart or firmware action is authorized.";

    public static CheckResult<DriverUpdateReviewReport> Review(CheckResult<SystemUpdate>? source,
        IReadOnlyList<SystemUpdate>? loaded, SystemUpdate? selection, DateTimeOffset attemptedUtc,
        DateTimeOffset receivedUtc, DateTimeOffset nowUtc)
    {
        if (source is null || source.State is not (CheckState.Available or CheckState.Partial) ||
            source.Items is not { Count: > 0 and <= 256 } || loaded is null || loaded.Count > 256 || selection is null ||
            !Utc(attemptedUtc) || !Utc(receivedUtc) || !Utc(nowUtc) || attemptedUtc > receivedUtc || receivedUtc > nowUtc ||
            nowUtc - receivedUtc > MaximumAge || nowUtc - attemptedUtc > MaximumAge)
            return Reject("The selected source or query times are unavailable, stale or inconsistent.");

        var identities = new HashSet<Guid>();
        var expectedDrivers = new List<SystemUpdate>();
        int selectedSourceReferences = 0;
        foreach (var item in source.Items)
        {
            if (item is null || !Identity(item.Identity, out var id) || !identities.Add(id))
                return Reject("The source contains missing, invalid or ambiguous update identities.");
            if (ReferenceEquals(item, selection)) selectedSourceReferences++;
            if (item.Kind == "Driver") expectedDrivers.Add(item);
        }
        if (selectedSourceReferences != 1 || expectedDrivers.Count != loaded.Count ||
            loaded.Any(item => item is null || expectedDrivers.Count(expected => ReferenceEquals(expected, item)) != 1) ||
            expectedDrivers.Any(item => loaded.Count(actual => ReferenceEquals(actual, item)) != 1) ||
            loaded.Count(item => ReferenceEquals(item, selection)) != 1)
            return Reject("The selection is not an exact current row in the fully published driver source.");

        if (selection.Kind != "Driver" || !Text(selection.Title, 2048) || !Text(selection.DriverClass, 128) ||
            selection.DriverClass.Trim().Equals("Firmware", StringComparison.OrdinalIgnoreCase) ||
            !Date(selection.PackageDate) || !Description(selection.Description) ||
            selection.OfferMetadata is { } supplied && !WindowsUpdateOfferMetadataParser.IsConsistent(supplied, 2))
            return Reject("The selected offer is firmware, malformed or has inconsistent optional metadata.");

        var metadata = selection.OfferMetadata ?? WindowsUpdateOfferMetadataParser.Unknown();
        var identity = selection.IdentityMetadata;
        bool identityConsistent = identity != null && WindowsUpdateIdentityMetadataParser.IsConsistent(identity, 2);
        var revision = identityConsistent ? identity!.RevisionNumber : new WindowsUpdateRevisionField(CheckState.Unknown, null);
        var hardware = identityConsistent ? identity!.DriverHardwareId : new WindowsUpdateTextField(CheckState.Unknown, null);
        var rows = new List<DriverUpdateReviewRequirement>
        {
            new("Selected offer and source", CheckState.Partial,
                $"{selection.Title}; update ID {selection.Identity}; configured Windows Update service; source state {source.State}.",
                "Exact row membership is established only in this accepted snapshot. Identity declarations are shown separately; publisher trust and current eligibility are not established."),
            new("Query and review times", CheckState.Partial,
                $"Attempt UTC {attemptedUtc:O}; response received UTC {receivedUtc:O}; review UTC {nowUtc:O}.",
                "The 30-minute review bound is MadClean policy, not provider expiry, an atomic snapshot or a fresh query."),
            new("Provider update revision", revision.State,
                revision.Value?.ToString(CultureInfo.InvariantCulture) ?? "Unknown",
                "Provider metadata revision only, independent of update ID. This is not an installed/offered driver version or BIOS version, and does not establish their ordering or current applicability."),
            new("Declared hardware or compatible ID", hardware.State, hardware.Value ?? "Unknown",
                "Provider required-match declaration only. This is not a machine-instance identifier or evidence of a match to the current device; full OEM compatibility remains Unknown."),
            TextRow("Declared manufacturer", metadata.Manufacturer),
            TextRow("Declared provider", metadata.Provider),
            TextRow("Declared intended model", metadata.Model),
            BooleanRow("Declared user-input possibility", metadata.CanRequestUserInput),
            BooleanRow("Declared network requirement", metadata.RequiresNetworkConnectivity),
            new("Declared installation impact", metadata.Impact.State,
                metadata.Impact.Value?.ToString() ?? "Unknown", "Provider declaration only; this does not determine actual exclusive access or dependency changes."),
            new("Declared reboot behavior", metadata.RebootBehavior.State,
                metadata.RebootBehavior.Value?.ToString() ?? "Unknown", "Provider declaration only; even NeverReboots does not establish MadClean's installation or restart-control contract."),
            new("Observed RebootRequired flag", CheckState.Available, selection.RebootRequired ? "Yes" : "No",
                "Observed provider flag only; false is not a promise that an installation cannot require a restart."),
            Missing("Exact device/OEM match", "Verify the exact device, PC model/configuration and OEM applicability. Names, intended model text and dates are insufficient."),
            Missing("Installed/offered driver versions", "Read and verify exact installed/offered driver versions. A provider update metadata revision shown above does not supply a driver or package version."),
            Missing("Official prerequisites and release notes", "Verify exact-model official instructions, release notes and vendor-specific power/recovery-key precautions where applicable. Do not supply recovery keys to MadClean."),
            Missing("Required elevation and consent", "Required privileges and explicit consent are not established. No silent elevation is permitted."),
            Missing("Dependencies and rollback", "Establish actual dependency changes, recovery options and limitations; a restore point cannot recover deleted files."),
            Missing("Actual restart controls", "Establish the supported operation's restart semantics independently of manifest declarations. No forced restart is permitted."),
            Missing("Actual result verification", "No operation has run. Completion, installed state and actual per-item results require independent verification.")
        };
        return new(CheckState.Partial, "Selected driver prerequisite review created; installation readiness remains Unknown. " + Limits,
            [new(selection, nowUtc, attemptedUtc, receivedUtc, source.State, rows)]);
    }

    private static DriverUpdateReviewRequirement TextRow(string title, WindowsUpdateTextField field) =>
        new(title, field.State, field.Value ?? "Unknown", "Provider-reported plain text; not authenticated manufacturer evidence or an exact-device match.");
    private static DriverUpdateReviewRequirement BooleanRow(string title, WindowsUpdateBooleanField field) =>
        new(title, field.State, field.Value.HasValue ? field.Value.Value ? "Yes" : "No" : "Unknown", "Provider declaration only, not complete execution behavior or prerequisites.");
    private static DriverUpdateReviewRequirement Missing(string title, string limit) => new(title, CheckState.Unknown, "Unknown", limit);
    private static bool Utc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;
    private static bool Identity(string? value, out Guid id) => Guid.TryParseExact(value, "D", out id) && id != Guid.Empty && value!.Length == 36;
    private static bool Text(string? value, int maximum) => value is { Length: > 0 } && value.Length <= maximum &&
        !string.IsNullOrWhiteSpace(value) && ShortcutPathPolicy.HasSafeUnicode(value);
    private static bool Description(string? value) => value is { Length: <= 2300 } &&
        ShortcutPathPolicy.HasSafeUnicode(value.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal).Replace("\t", "", StringComparison.Ordinal));
    private static bool Date(string? value) => value == "Unknown" || value is { Length: 10 } &&
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    private static CheckResult<DriverUpdateReviewReport> Reject(string reason) => CheckResult<DriverUpdateReviewReport>.Empty(CheckState.Unknown, reason + " " + Limits);
}
