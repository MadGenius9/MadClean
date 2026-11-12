using System.Globalization;

namespace MadClean.Core;

/// <summary>Actual reported inventory context only; never verified specifications or upgrade advice.</summary>
public sealed class HardwareUpgradeContextReport
{
    internal HardwareUpgradeContextReport(HardwareCompatibilityUpgradeArea area, CheckState sourceState,
        DateTimeOffset attemptedUtc, DateTimeOffset receivedUtc, DateTimeOffset reviewedUtc, IEnumerable<HardwareFact> facts)
    {
        Area = area; SourceState = sourceState; AttemptedUtc = attemptedUtc;
        ReceivedUtc = receivedUtc; ReviewedUtc = reviewedUtc;
        Facts = Array.AsReadOnly(facts.ToArray());
    }
    public HardwareCompatibilityUpgradeArea Area { get; }
    public CheckState SourceState { get; }
    public DateTimeOffset AttemptedUtc { get; }
    public DateTimeOffset ReceivedUtc { get; }
    public DateTimeOffset ReviewedUtc { get; }
    public IReadOnlyList<HardwareFact> Facts { get; }
    public CheckState State => CheckState.Unknown;
    public bool CanRecommend => false;
    public bool CanPurchase => false;
    public bool CanInstall => false;
}

public static class HardwareUpgradeContextPolicy
{
    public const int MaximumFacts = 4096;
    public static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(30);
    public const string Limits = "Captured detected/provider-reported inventory context only, not a new query, exact-product match or manufacturer-verified specification. " +
        "Missing, ambiguous and incomplete identities remain unverified. Reported board Version is not a verified PCB revision. " +
        "No values establish upgrade limits, supported CPUs/BIOS, physical free slots, replaceability, soldered-memory design, PSU/connectors or case clearance. " +
        "Compatibility remains Unknown; no recommendation, purchase, installation or firmware action is supplied.";

    private sealed record Field(string Category, string ComponentPrefix, string Property);
    private static readonly IReadOnlyDictionary<string, Field> Fields = BuildFields();

    public static CheckResult<HardwareUpgradeContextReport> Capture(CheckResult<HardwareFact>? source,
        IReadOnlyList<HardwareFact>? loaded, HardwareCompatibilityUpgradeArea? area,
        DateTimeOffset attemptedUtc, DateTimeOffset receivedUtc, DateTimeOffset nowUtc)
    {
        if (area is not { } selected || !Enum.IsDefined(selected) || source is null ||
            source.State is not (CheckState.Available or CheckState.Partial) || source.Items is not { Count: > 0 and <= MaximumFacts } ||
            loaded is null || loaded.Count != source.Items.Count || !Utc(attemptedUtc) || !Utc(receivedUtc) || !Utc(nowUtc) ||
            attemptedUtc > receivedUtc || receivedUtc > nowUtc || nowUtc - attemptedUtc > MaximumAge || nowUtc - receivedUtc > MaximumAge)
            return Reject("The selected area, inventory source, publication or query times are unavailable, stale or inconsistent.");

        var references = new HashSet<HardwareFact>(ReferenceEqualityComparer.Instance);
        var identities = new HashSet<(string Source, string Component)>();
        var completeness = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var fact in source.Items)
        {
            if (fact is null || !references.Add(fact) || !Text(fact.Category, 32) || !Text(fact.Component, 128) ||
                !Text(fact.Property, 128) || !Text(fact.Value, 512) || !Text(fact.Source, 160) ||
                !Fields.TryGetValue(fact.Source, out var expected) || fact.Category != expected.Category || fact.Property != expected.Property ||
                !Component(fact.Component, expected.ComponentPrefix) || !identities.Add((fact.Source, fact.Component)))
                return Reject("The inventory contains duplicate references/fields, unsupported source properties or unsafe observations.");
            string sourceClass = fact.Source[..fact.Source.LastIndexOf('.')];
            if (completeness.TryGetValue(sourceClass, out bool complete) && complete != fact.SourceEnumerationComplete)
                return Reject("The inventory contains conflicting source-enumeration coverage flags.");
            completeness[sourceClass] = fact.SourceEnumerationComplete;
        }
        var loadedReferences = new HashSet<HardwareFact>(ReferenceEqualityComparer.Instance);
        foreach (var fact in loaded)
            if (fact is null || !references.Contains(fact) || !loadedReferences.Add(fact))
                return Reject("The displayed inventory is not the complete actual-reference source publication.");

        string category = selected switch
        {
            HardwareCompatibilityUpgradeArea.Memory => "RAM",
            HardwareCompatibilityUpgradeArea.Cpu => "CPU",
            HardwareCompatibilityUpgradeArea.Gpu => "GPU",
            HardwareCompatibilityUpgradeArea.Storage => "Storage",
            _ => throw new InvalidOperationException("Unsupported upgrade area.")
        };
        if (!source.Items.Any(fact => fact.Category == category))
            return Reject("No actual observations for the selected upgrade area were returned; PC identity alone cannot supply component context.");
        var facts = source.Items.Where(fact => fact.Category == category || fact.Category is "PC" or "Baseboard");
        return new(CheckState.Partial, "Captured the selected area's reported inventory context; source coverage remains " + source.State + ". " + Limits,
            [new(selected, source.State, attemptedUtc, receivedUtc, nowUtc, facts)]);
    }

    private static bool Utc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;
    private static bool Text(string? value, int limit) => value is { Length: > 0 } && value.Length <= limit &&
        !string.IsNullOrWhiteSpace(value) && ShortcutPathPolicy.HasSafeUnicode(value);
    private static bool Component(string value, string prefix)
    {
        if (!value.StartsWith(prefix + " ", StringComparison.Ordinal)) return false;
        string number = value[(prefix.Length + 1)..];
        return int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) && parsed is >= 1 and <= 128 &&
            number == parsed.ToString(CultureInfo.InvariantCulture);
    }
    private static CheckResult<HardwareUpgradeContextReport> Reject(string reason) =>
        CheckResult<HardwareUpgradeContextReport>.Empty(CheckState.Unknown, reason + " " + Limits);

    // Mirrors only the existing projected read-only schema. No additional CIM field is requested.
    private static IReadOnlyDictionary<string, Field> BuildFields()
    {
        var fields = new Dictionary<string, Field>(StringComparer.Ordinal);
        void Add(string sourceClass, string category, string component, params (string Name, string Label)[] properties)
        {
            foreach (var property in properties)
                fields.Add($"root/cimv2:{sourceClass}.{property.Name}", new(category, component, property.Label));
        }
        Add("Win32_ComputerSystem", "PC", "PC record", ("Manufacturer", "Manufacturer"), ("Model", "Model"),
            ("SystemFamily", "SystemFamily"), ("SystemSKUNumber", "SystemSKUNumber"), ("SystemType", "SystemType"));
        Add("Win32_BaseBoard", "Baseboard", "Baseboard record", ("Manufacturer", "Manufacturer"), ("Product", "Product"),
            ("Version", "Version (reported; not verified PCB revision)"));
        Add("Win32_Processor", "CPU", "Processor", ("Manufacturer", "Manufacturer"), ("Name", "Name"),
            ("NumberOfCores", "NumberOfCores"), ("NumberOfLogicalProcessors", "NumberOfLogicalProcessors"),
            ("MaxClockSpeed", "MaxClockSpeed (reported MHz)"), ("SocketDesignation", "SocketDesignation"));
        Add("Win32_PhysicalMemory", "RAM", "Memory device", ("Manufacturer", "Manufacturer"), ("PartNumber", "PartNumber"),
            ("Capacity", "Capacity (bytes)"), ("SMBIOSMemoryType", "SMBIOSMemoryType (raw code; not decoded)"),
            ("FormFactor", "FormFactor (raw code; not replaceability)"),
            ("ConfiguredClockSpeed", "ConfiguredClockSpeed (reported MHz; not marketing transfer rate)"));
        Add("Win32_VideoController", "GPU", "Video controller", ("Name", "Name"), ("VideoProcessor", "VideoProcessor"), ("DriverVersion", "DriverVersion"));
        Add("Win32_DiskDrive", "Storage", "Disk", ("Manufacturer", "Manufacturer"), ("Model", "Model"), ("MediaType", "MediaType"),
            ("InterfaceType", "InterfaceType"), ("Size", "Size (bytes)"), ("FirmwareRevision", "FirmwareRevision (drive firmware; not BIOS)"));
        return fields;
    }
}
