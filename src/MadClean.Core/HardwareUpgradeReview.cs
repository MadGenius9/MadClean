using System.Globalization;

namespace MadClean.Core;

public sealed record HardwareUpgradeReviewInput(HardwareCompatibilityUpgradeArea? Area,
    string BudgetText, string Currency, string Region, string UseCase);

public sealed record HardwareUpgradeReviewRequirement(HardwareCompatibilityCheckKind Kind,
    string Title, string Requirement)
{
    public CheckState State => CheckState.Unknown;
}

public sealed record HardwareUpgradeReviewReport(string Summary, IReadOnlyList<string> Reasons,
    IReadOnlyList<HardwareUpgradeReviewRequirement> Requirements)
{
    public CheckState State => CheckState.Unknown;
}

/// <summary>In-memory planning inputs and evidence gaps only. Never retrieves or verifies evidence.</summary>
public static class HardwareUpgradeReview
{
    public static HardwareUpgradeReviewReport Build(HardwareUpgradeReviewInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        List<string> reasons = [];
        bool validArea = input.Area is { } selected && Enum.IsDefined(selected);
        if (!validArea) reasons.Add(input.Area == null
            ? "Choose an upgrade area before reviewing its evidence requirements."
            : "The selected upgrade area is unsupported; no evidence checklist was created.");
        decimal? budget = Budget(input.BudgetText);
        if (budget == null)
            reasons.Add("Enter a positive budget using digits and an optional decimal point, without currency symbols, grouping, signs or exponent notation (at most 32 characters and two decimal places).");
        var preferences = new HardwareCompatibilityPreferences(budget,
            AsciiCode(input.Currency, 3), AsciiCode(input.Region, 2),
            input.UseCase is { Length: <= 1024 } ? input.UseCase : "");
        var area = validArea ? input.Area!.Value : (HardwareCompatibilityUpgradeArea)(-1);
        // No trusted source or verified check enters this worksheet. User preferences,
        // detected inventory and family-level guidance cannot satisfy compatibility.
        var advice = HardwareCompatibilityPolicy.AssessAdvice(area,
            new(HardwareCompatibilityMatchState.Unknown, Array.Empty<string>()), preferences,
            Array.Empty<HardwareCompatibilityCheck>());
        reasons.AddRange(advice.Reasons);
        var requirements = validArea
            ? HardwareCompatibilityPolicy.RequiredChecks(area).Select(Describe).ToArray()
            : Array.Empty<HardwareUpgradeReviewRequirement>();
        return new("Compatibility remains Unknown. This worksheet lists missing evidence for manual review; it does not recommend products, prices, purchases, installations or firmware changes.",
            Array.AsReadOnly(reasons.ToArray()), Array.AsReadOnly(requirements));
    }

    private static decimal? Budget(string? value)
    {
        if (value == null || value.Length is 0 or > 32) return null;
        string text = value.Trim(' ');
        if (text.Length == 0 || text.Any(c => c != '.' && (c < '0' || c > '9'))) return null;
        int dot = text.IndexOf('.');
        if (dot == 0 || dot == text.Length - 1 || dot >= 0 &&
            (text.IndexOf('.', dot + 1) >= 0 || text.Length - dot - 1 > 2)) return null;
        return decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal parsed) && parsed > 0
            ? parsed : null;
    }

    private static string AsciiCode(string? value, int length)
    {
        if (value == null || value.Length > length + 2) return "";
        string text = value.Trim(' ');
        return text.Length == length && text.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
            ? text.ToUpperInvariant() : "";
    }

    private static HardwareUpgradeReviewRequirement Describe(HardwareCompatibilityCheckKind kind) => kind switch
    {
        HardwareCompatibilityCheckKind.ManufacturerSpecification => new(kind, "Exact manufacturer specification",
            "Retrieve applicable official specifications for the exact OEM model/SKU, or retail motherboard model and independently verified PCB revision. Family service notes and factory options are not upgrade limits."),
        HardwareCompatibilityCheckKind.MemoryTypeLimitsAndPopulation => new(kind, "Supported RAM and population rules",
            "Verify manufacturer-supported memory types, capacities, module combinations and population rules for the exact configuration. Detected capacity and memory codes do not establish limits or supported kits."),
        HardwareCompatibilityCheckKind.ReplaceabilityOrSolderedDesign => new(kind, "Replaceability and soldered design",
            "Verify the exact model's official service design and replacement procedure. A reported memory form factor or CPU socket name cannot establish that a component is replaceable."),
        HardwareCompatibilityCheckKind.PhysicalOccupancy => new(kind, "Physical occupancy",
            "Verify actual usable slot, socket or bay occupancy through exact documentation and a safe manual check where needed. Do not subtract detected devices from firmware counts to infer free physical slots."),
        HardwareCompatibilityCheckKind.SupportedCpuExactSku => new(kind, "Exact supported CPU",
            "Verify the exact processor SKU against the manufacturer's applicable system or board support list. A matching socket, chipset or family name is insufficient."),
        HardwareCompatibilityCheckKind.RequiredFirmware => new(kind, "Required BIOS or firmware",
            "Verify the exact vendor's CPU prerequisite against the installed firmware and applicable revision. OEM systems require OEM firmware; no generic version ordering or BIOS update is supplied."),
        HardwareCompatibilityCheckKind.PowerAndCooling => new(kind, "Power delivery and cooling",
            "Verify manufacturer limits, board power delivery and the actual cooling configuration. Detected CPU information does not establish electrical or thermal compatibility."),
        HardwareCompatibilityCheckKind.ExpansionLanes => new(kind, "Expansion interface and lanes",
            "Verify the exact supported expansion interface, slot wiring, lane allocation and configuration restrictions. Detected GPUs do not establish a usable expansion slot."),
        HardwareCompatibilityCheckKind.PsuCapacityAndConnectors => new(kind, "PSU capacity and connectors",
            "Verify the installed PSU's actual capacity, connectors and manufacturer requirements manually or from exact configuration documentation. Inventory does not infer these facts."),
        HardwareCompatibilityCheckKind.CaseAndGpuClearance => new(kind, "Case and GPU clearance",
            "Verify actual case dimensions, card clearance and surrounding obstructions for the intended component. Board or GPU names do not establish physical fit."),
        HardwareCompatibilityCheckKind.StorageProtocolAndSocket => new(kind, "Storage protocol and socket",
            "Verify the exact supported connector, protocol, form factor and device size. A controller-reported interface does not prove the physical socket or supported replacement."),
        HardwareCompatibilityCheckKind.LaneSharing => new(kind, "Lane and port sharing",
            "Verify manufacturer-documented lane and port sharing for the exact configuration. Existing drive enumeration does not establish unused ports or available PCIe lanes."),
        HardwareCompatibilityCheckKind.MountingAndCooling => new(kind, "Storage mounting and cooling",
            "Verify applicable mounting hardware, physical dimensions and cooling requirements from exact service documentation and manual inspection where needed. No spare bay, screw or heatsink is inferred."),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}
