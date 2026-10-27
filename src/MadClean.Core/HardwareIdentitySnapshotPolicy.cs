namespace MadClean.Core;

/// <summary>Requires a separately complete, unique PC identity source. Unknown RAM meaning or
/// unrelated provider failures remain Partial and do not manufacture identity ambiguity.</summary>
public static class HardwareIdentitySnapshotPolicy
{
    public static HardwareCompatibilityIdentityReference? FindCompleteKnownPc(IEnumerable<HardwareFact> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        const string source = "root/cimv2:Win32_ComputerSystem.";
        var computer = facts.Where(fact => fact.Source.StartsWith(source, StringComparison.Ordinal)).ToArray();
        if (computer.Length == 0 || computer.Any(fact => !fact.SourceEnumerationComplete) ||
            computer.Select(fact => fact.Component).Distinct(StringComparer.Ordinal).Count() != 1) return null;
        string? Value(string property)
        {
            var fields = computer.Where(fact => fact.Source == source + property).Take(2).ToArray();
            return fields.Length == 1 ? fields[0].Value : null;
        }
        return HardwareCompatibilityIdentityCatalog.Find(Value("Manufacturer"), Value("Model"), Value("SystemSKUNumber"));
    }
}
