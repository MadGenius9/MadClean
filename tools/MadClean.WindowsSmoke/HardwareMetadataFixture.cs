using System.ComponentModel;
using System.Globalization;
using System.IO;
using MadClean.Core;

namespace MadClean.WindowsSmoke;

internal static partial class NativeFixtures
{
    private sealed record HardwareMetadataDefinition(string Category, string ComponentPrefix, IReadOnlyDictionary<string, string> Fields);
    private static readonly IReadOnlyDictionary<string, HardwareMetadataDefinition> HardwareMetadataSchema =
        new Dictionary<string, HardwareMetadataDefinition>(StringComparer.Ordinal)
        {
            ["Win32_ComputerSystem"] = new("PC", "PC record", HardwareFields("Manufacturer", "Model", "SystemFamily", "SystemSKUNumber", "SystemType")),
            ["Win32_BaseBoard"] = new("Baseboard", "Baseboard record", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Manufacturer"] = "Manufacturer", ["Product"] = "Product", ["Version"] = "Version (reported; not verified PCB revision)"
            }),
            ["Win32_Processor"] = new("CPU", "Processor", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Manufacturer"] = "Manufacturer", ["Name"] = "Name", ["NumberOfCores"] = "NumberOfCores", ["NumberOfLogicalProcessors"] = "NumberOfLogicalProcessors",
                ["MaxClockSpeed"] = "MaxClockSpeed (reported MHz)", ["SocketDesignation"] = "SocketDesignation"
            }),
            ["Win32_PhysicalMemory"] = new("RAM", "Memory device", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Manufacturer"] = "Manufacturer", ["PartNumber"] = "PartNumber", ["Capacity"] = "Capacity (bytes)",
                ["SMBIOSMemoryType"] = "SMBIOSMemoryType (raw code; not decoded)", ["FormFactor"] = "FormFactor (raw code; not replaceability)",
                ["ConfiguredClockSpeed"] = "ConfiguredClockSpeed (reported MHz; not marketing transfer rate)"
            }),
            ["Win32_VideoController"] = new("GPU", "Video controller", HardwareFields("Name", "VideoProcessor", "DriverVersion")),
            ["Win32_DiskDrive"] = new("Storage", "Disk", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Manufacturer"] = "Manufacturer", ["Model"] = "Model", ["MediaType"] = "MediaType", ["InterfaceType"] = "InterfaceType",
                ["Size"] = "Size (bytes)", ["FirmwareRevision"] = "FirmwareRevision (drive firmware; not BIOS)"
            })
        };

    internal static int HardwareMetadataFactLimit => HardwareMetadataSchema.Values.Sum(definition => definition.Fields.Count) * 128;

    private static IReadOnlyDictionary<string, string> HardwareFields(params string[] names) => names.ToDictionary(name => name, name => name, StringComparer.Ordinal);

    private static void HardwareMetadata(SmokeReport report, CancellationToken token)
    {
        const string check = "hardware-metadata-contract";
        try
        {
            // Explicit disposable-host H1 metadata read only. Do not perform manufacturer matching,
            // source HTTP calls, purchases, firmware operations or extra provider/property queries.
            var result = Query(ct => new MadClean.Windows.HardwareInventory().ReadAsync(ct), token);
            if (result.State is not (CheckState.Available or CheckState.Partial))
            {
                report.Unknown(check, $"Native H1 state={result.State}; projectedFacts={result.Items.Count}; pcSourceComplete=false. No successful hardware metadata or source-completeness proof is claimed.");
                return;
            }
            if (result.Items.Count < 1 || result.Items.Count > HardwareMetadataSchema.Values.Sum(definition => definition.Fields.Count) * 128)
            {
                report.Fail(check, "Readable H1 output violated the projected-fact count bound. No returned values are exported.");
                return;
            }
            var records = new Dictionary<(string Class, string Component), HashSet<string>>();
            var completion = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var fact in result.Items)
            {
                if (!TryHardwareMetadata(fact, out string sourceClass, out string sourceProperty))
                {
                    report.Fail(check, "Returned H1 source/category/property/component/value-shape metadata violated the fixed projection schema; offending metadata and values are withheld.");
                    return;
                }
                if (completion.TryGetValue(sourceClass, out bool complete) && complete != fact.SourceEnumerationComplete)
                {
                    report.Fail(check, "One H1 source returned inconsistent enumeration-complete flags. Actual identity and hardware values are withheld.");
                    return;
                }
                completion[sourceClass] = fact.SourceEnumerationComplete;
                var key = (sourceClass, fact.Component);
                if (!records.TryGetValue(key, out var fields)) records.Add(key, fields = new(StringComparer.Ordinal));
                if (!fields.Add(sourceProperty))
                {
                    report.Fail(check, "A projected source record repeated a property. Actual hardware values are withheld.");
                    return;
                }
            }
            foreach (var record in records)
            {
                if (record.Value.Count != HardwareMetadataSchema[record.Key.Class].Fields.Count)
                {
                    report.Fail(check, "A projected H1 record omitted required property metadata; no value accuracy or completeness is inferred.");
                    return;
                }
            }
            foreach (var source in completion.Where(source => source.Value))
            {
                var components = records.Keys.Where(key => key.Class == source.Key).Select(key => key.Component).ToHashSet(StringComparer.Ordinal);
                string componentPrefix = HardwareMetadataSchema[source.Key].ComponentPrefix;
                if (Enumerable.Range(1, components.Count).Any(ordinal => !components.Contains(componentPrefix + " " + ordinal.ToString(CultureInfo.InvariantCulture))))
                {
                    report.Fail(check, "An attested complete H1 source has inconsistent record ordinals. Returned identity and hardware values are withheld.");
                    return;
                }
            }
            int pcRecords = records.Keys.Count(key => key.Class == "Win32_ComputerSystem");
            bool pcComplete = pcRecords > 0 && completion.GetValueOrDefault("Win32_ComputerSystem");
            if (!pcComplete)
            {
                report.Unknown(check, $"Native H1 state={result.State}; projectedFacts={result.Items.Count}; sourceClasses={completion.Count}; pcRecords={pcRecords}; schemaValid=true; pcSourceComplete=false. PC-source completion remains unverified.");
                return;
            }
            report.Pass(check, $"Native H1 state={result.State}; projectedFacts={result.Items.Count}; sourceClasses={completion.Count}; pcRecords={pcRecords}; schemaValid=true; pcSourceComplete=true. This attests the bounded PC source response only, not value accuracy, single-machine identity, supported hardware, manufacturer matching or compatibility. All returned hardware values are withheld.");
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            report.Unknown(check, $"Native H1 metadata read did not complete (code 0x{ex.HResult:X8}); schemaValid=false; pcSourceComplete=false. No provider payload or actual hardware values are exported.");
        }
    }

    internal static bool TryHardwareMetadata(HardwareFact fact, out string sourceClass, out string sourceProperty)
    {
        sourceClass = sourceProperty = "";
        const string prefix = "root/cimv2:";
        if (fact == null || fact.Source == null || !fact.Source.StartsWith(prefix, StringComparison.Ordinal) ||
            fact.Value == null || fact.Value.Length is < 1 or > 512 || fact.Value.Any(character => char.IsControl(character) || char.GetUnicodeCategory(character) == UnicodeCategory.Format)) return false;
        int delimiter = fact.Source.LastIndexOf('.');
        if (delimiter <= prefix.Length || delimiter == fact.Source.Length - 1) return false;
        sourceClass = fact.Source[prefix.Length..delimiter]; sourceProperty = fact.Source[(delimiter + 1)..];
        if (!HardwareMetadataSchema.TryGetValue(sourceClass, out var definition) ||
            !definition.Fields.TryGetValue(sourceProperty, out string? label) || fact.Property != label || fact.Category != definition.Category ||
            fact.Component == null || !fact.Component.StartsWith(definition.ComponentPrefix + " ", StringComparison.Ordinal)) return false;
        string ordinal = fact.Component[(definition.ComponentPrefix.Length + 1)..];
        return int.TryParse(ordinal, NumberStyles.None, CultureInfo.InvariantCulture, out int record) && record is >= 1 and <= 128 &&
            ordinal == record.ToString(CultureInfo.InvariantCulture);
    }
}
