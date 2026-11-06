using System.Text.Json;
using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class HardwareUpgradeReviewTests
{
    private static HardwareUpgradeReviewInput Valid(HardwareCompatibilityUpgradeArea? area = HardwareCompatibilityUpgradeArea.Memory) =>
        new(area, "1500.50", "usd", "us", "Rendering and gaming");
    private static bool MissingPreferences(HardwareUpgradeReviewReport report) => report.Reasons.Any(reason => reason.StartsWith("A positive user-supplied budget", StringComparison.Ordinal));

    [Fact]
    public void CompletePreferencesDoNotBecomeManufacturerEvidenceOrCompatibility()
    {
        var report = HardwareUpgradeReview.Build(Valid());
        Assert.False(MissingPreferences(report));
        Assert.Equal(CheckState.Unknown, report.State);
        Assert.All(report.Requirements, requirement => Assert.Equal(CheckState.Unknown, requirement.State));
        Assert.Contains(report.Reasons, reason => reason.Contains("Exact-model scope remains unknown", StringComparison.Ordinal));
        Assert.Contains(report.Reasons, reason => reason.Contains("identity-only", StringComparison.Ordinal));
        Assert.Contains("does not recommend", report.Summary);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("0.00")]
    [InlineData("-25")]
    [InlineData("+25")]
    [InlineData("$25")]
    [InlineData("1,500")]
    [InlineData("1500,50")]
    [InlineData("1e3")]
    [InlineData("1.2.3")]
    [InlineData("1.001")]
    [InlineData(".50")]
    [InlineData("50.")]
    [InlineData("１２３")]
    [InlineData("٢٥")]
    [InlineData("1\u200b00")]
    [InlineData("\t100")]
    [InlineData("100\n")]
    [InlineData("79228162514264337593543950336")]
    [InlineData("000000000000000000000000000000001")]
    public void AmbiguousOrUnrepresentableBudgetCannotBecomeAValidPreference(string budget)
    {
        var report = HardwareUpgradeReview.Build(Valid() with { BudgetText = budget });
        Assert.True(MissingPreferences(report));
        Assert.Contains(report.Reasons, reason => reason.StartsWith("Enter a positive budget", StringComparison.Ordinal));
        Assert.Equal(CheckState.Unknown, report.State);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("0.01")]
    [InlineData(" 1500.50 ")]
    [InlineData("79228162514264337593543950335")]
    public void PlainPositiveBudgetPreservesUnknownEvidence(string budget)
    {
        var report = HardwareUpgradeReview.Build(Valid() with { BudgetText = budget });
        Assert.False(MissingPreferences(report));
        Assert.Equal(CheckState.Unknown, report.State);
        Assert.All(report.Requirements, requirement => Assert.Equal(CheckState.Unknown, requirement.State));
    }

    [Theory]
    [InlineData(null)]
    [InlineData((HardwareCompatibilityUpgradeArea)(-1))]
    [InlineData((HardwareCompatibilityUpgradeArea)1000)]
    public void MissingOrUnsupportedAreaHasNoVacuouslyCompleteChecklist(HardwareCompatibilityUpgradeArea? area)
    {
        var report = HardwareUpgradeReview.Build(Valid(area));
        Assert.Empty(report.Requirements);
        Assert.Equal(CheckState.Unknown, report.State);
        Assert.Contains(report.Reasons, reason => reason.Contains("upgrade area", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(HardwareCompatibilityUpgradeArea.Memory, HardwareCompatibilityCheckKind.MemoryTypeLimitsAndPopulation, HardwareCompatibilityCheckKind.ReplaceabilityOrSolderedDesign)]
    [InlineData(HardwareCompatibilityUpgradeArea.Cpu, HardwareCompatibilityCheckKind.SupportedCpuExactSku, HardwareCompatibilityCheckKind.RequiredFirmware)]
    [InlineData(HardwareCompatibilityUpgradeArea.Gpu, HardwareCompatibilityCheckKind.PsuCapacityAndConnectors, HardwareCompatibilityCheckKind.CaseAndGpuClearance)]
    [InlineData(HardwareCompatibilityUpgradeArea.Storage, HardwareCompatibilityCheckKind.StorageProtocolAndSocket, HardwareCompatibilityCheckKind.LaneSharing)]
    public void AreaRequiresSpecificManufacturerAndPhysicalEvidence(HardwareCompatibilityUpgradeArea area,
        HardwareCompatibilityCheckKind first, HardwareCompatibilityCheckKind second)
    {
        var report = HardwareUpgradeReview.Build(Valid(area));
        Assert.Contains(report.Requirements, requirement => requirement.Kind == HardwareCompatibilityCheckKind.ManufacturerSpecification);
        Assert.Contains(report.Requirements, requirement => requirement.Kind == first);
        Assert.Contains(report.Requirements, requirement => requirement.Kind == second);
        Assert.Equal(report.Requirements.Count, report.Requirements.Select(requirement => requirement.Kind).Distinct().Count());
        Assert.All(report.Requirements, requirement => Assert.False(string.IsNullOrWhiteSpace(requirement.Requirement)));
    }

    [Fact]
    public void PreferencesAreRequiredRatherThanInferredFromMachineOrLocale()
    {
        var report = HardwareUpgradeReview.Build(new(HardwareCompatibilityUpgradeArea.Storage, "250", "", "", ""));
        Assert.True(MissingPreferences(report));
        Assert.Equal(CheckState.Unknown, report.State);
    }

    [Theory]
    [InlineData("BTC", "US", "Gaming")]
    [InlineData("USD", "ZZ", "Gaming")]
    [InlineData("USD", "US", "unknown")]
    [InlineData("ＵＳＤ", "US", "Gaming")]
    [InlineData("USD", "U\u200bS", "Gaming")]
    [InlineData("USD", "US", "Gaming\nprivate")]
    public void UnsupportedOrMalformedPreferencesRemainUnknown(string currency, string region, string useCase)
    {
        var report = HardwareUpgradeReview.Build(Valid() with { Currency = currency, Region = region, UseCase = useCase });
        Assert.True(MissingPreferences(report));
        Assert.Equal(CheckState.Unknown, report.State);
    }

    [Fact]
    public void ReportDoesNotRetainUserTextOrAcceptUnboundedPreferences()
    {
        const string secret = "private-user-sentinel";
        var report = HardwareUpgradeReview.Build(new(HardwareCompatibilityUpgradeArea.Gpu, secret,
            secret, secret, secret + new string('x', 1024)));
        string serialized = JsonSerializer.Serialize(report);
        Assert.DoesNotContain(secret, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 100), serialized, StringComparison.Ordinal);
        Assert.True(MissingPreferences(report));
        Assert.All(report.Requirements, requirement => Assert.Equal(CheckState.Unknown, requirement.State));
    }

    [Fact]
    public void NullRuntimeFieldsAreRejectedWithoutLeaksOrExceptions()
    {
        var report = HardwareUpgradeReview.Build(new(HardwareCompatibilityUpgradeArea.Cpu, null!, null!, null!, null!));
        Assert.True(MissingPreferences(report));
        Assert.Equal(CheckState.Unknown, report.State);
    }
}
