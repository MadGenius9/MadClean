using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class HardwareUpgradeContextTests
{
    private static readonly DateTimeOffset Attempt = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Received = Attempt.AddMinutes(1), Now = Attempt.AddMinutes(2);
    private static HardwareFact Memory(string value = "8589934592", bool complete = true) =>
        new("RAM", "Memory device 1", "Capacity (bytes)", value, "root/cimv2:Win32_PhysicalMemory.Capacity", complete);
    private static HardwareFact Pc(string property = "Model", string value = "Unknown", string component = "PC record 1") =>
        new("PC", component, property, value, "root/cimv2:Win32_ComputerSystem." + property, true);
    private static CheckResult<HardwareFact> Source(params HardwareFact[] facts) => new(CheckState.Partial, "Reported only", facts);
    private static CheckResult<HardwareUpgradeContextReport> Capture(HardwareFact[] facts,
        HardwareCompatibilityUpgradeArea? area = HardwareCompatibilityUpgradeArea.Memory, IReadOnlyList<HardwareFact>? loaded = null) =>
        HardwareUpgradeContextPolicy.Capture(Source(facts), loaded ?? facts, area, Attempt, Received, Now);
    private static void Refused(CheckResult<HardwareUpgradeContextReport> result)
    { Assert.Equal(CheckState.Unknown, result.State); Assert.Empty(result.Items); }

    [Fact]
    public void ActualParserProjectionRemainsReportedContextAcrossEveryAreaWithoutPromotingMeaning()
    {
        const string json = """
            {"Version":1,"Sources":[
              {"Class":"Win32_ComputerSystem","Status":"Available","Truncated":false,"Records":[{"Model":"Reported OEM model","SystemSKUNumber":"Unknown"}]},
              {"Class":"Win32_BaseBoard","Status":"Available","Truncated":false,"Records":[{"Version":"1.0"}]},
              {"Class":"Win32_Processor","Status":"Available","Truncated":false,"Records":[{"Name":"Reported CPU"}]},
              {"Class":"Win32_PhysicalMemory","Status":"Available","Truncated":false,"Records":[{"Capacity":8589934592,"SMBIOSMemoryType":34,"FormFactor":12}]},
              {"Class":"Win32_VideoController","Status":"Available","Truncated":false,"Records":[{"Name":"Reported GPU"}]},
              {"Class":"Win32_DiskDrive","Status":"Available","Truncated":false,"Records":[{"InterfaceType":"SCSI","FirmwareRevision":"7"}]}
            ]}
            """;
        var source = HardwareInventoryParser.Parse(json);
        foreach (var area in Enum.GetValues<HardwareCompatibilityUpgradeArea>())
        {
            var result = HardwareUpgradeContextPolicy.Capture(source, source.Items, area, Attempt, Received, Now);
            var report = Assert.Single(result.Items);
            Assert.Equal(CheckState.Partial, result.State); Assert.Equal(CheckState.Unknown, report.State);
            Assert.Equal(source.State, report.SourceState);
            Assert.False(report.CanRecommend); Assert.False(report.CanPurchase); Assert.False(report.CanInstall);
            Assert.All(report.Facts, fact => Assert.Contains(source.Items, actual => ReferenceEquals(fact, actual)));
        }
        var memory = Assert.Single(HardwareUpgradeContextPolicy.Capture(source, source.Items,
            HardwareCompatibilityUpgradeArea.Memory, Attempt, Received, Now).Items);
        Assert.Contains(memory.Facts, fact => fact.Value == "Unknown meaning (reported raw SMBIOS code 34)");
        Assert.Contains(memory.Facts, fact => fact.Value.Contains("not replaceability or slot availability", StringComparison.Ordinal));
        Assert.Contains(memory.Facts, fact => fact.Property == "Version (reported; not verified PCB revision)");
    }

    [Fact]
    public void PartialOrAmbiguousPcIdentityDoesNotBlockHonestComponentContextOrBecomeAnExactMatch()
    {
        HardwareFact[] facts = [Pc(), Pc(value: "Different reported model", component: "PC record 2"), Memory(complete: false)];
        var result = Capture(facts); var report = Assert.Single(result.Items);
        Assert.Equal(CheckState.Partial, result.State); Assert.Equal(CheckState.Partial, report.SourceState);
        Assert.Equal(3, report.Facts.Count); Assert.False(report.Facts.Single(fact => fact.Category == "RAM").SourceEnumerationComplete);
        Assert.Contains("Missing, ambiguous and incomplete identities remain unverified", result.Message);
        Assert.Equal(CheckState.Unknown, report.State); Assert.False(report.CanRecommend);
    }

    [Fact]
    public void OnlyPcBoardAndChosenAreaAreCopiedWithoutTotalsOrCrossAreaGuesses()
    {
        var memory = Memory(); var pc = Pc();
        var board = new HardwareFact("Baseboard", "Baseboard record 1", "Product", "Reported board", "root/cimv2:Win32_BaseBoard.Product");
        var storage = new HardwareFact("Storage", "Disk 1", "Size (bytes)", "1000", "root/cimv2:Win32_DiskDrive.Size");
        var report = Assert.Single(Capture([pc, memory, board, storage]).Items);
        Assert.Equal(3, report.Facts.Count); Assert.Same(pc, report.Facts[0]); Assert.Same(memory, report.Facts[1]); Assert.Same(board, report.Facts[2]);
        Assert.DoesNotContain(report.Facts, fact => fact.Category == "Storage");
        Assert.Equal("8589934592", report.Facts[1].Value);
    }

    [Fact]
    public void MissingChosenAreaCannotBeReplacedByPcIdentity()
    {
        Refused(Capture([Pc()]));
        var onlyMemory = Assert.Single(Capture([Memory()]).Items);
        Assert.Single(onlyMemory.Facts); Assert.Equal(CheckState.Unknown, onlyMemory.State);
    }

    [Fact]
    public void ValueEqualClonesIncompleteExtraAndRepeatedPublicationsAreRefused()
    {
        var memory = Memory(); var pc = Pc(); HardwareFact[] source = [memory, pc];
        Refused(Capture(source, loaded: [memory with { }, pc]));
        Refused(Capture(source, loaded: [memory]));
        Refused(Capture(source, loaded: [memory, memory]));
        Refused(Capture(source, loaded: [memory, pc, Memory("1")]));
        Refused(Capture([memory, memory]));
        Refused(Capture([memory, memory with { Value = "1" }]));
        // Display reordering does not change source membership or the captured source order.
        Assert.Equal(2, Assert.Single(Capture(source, loaded: [pc, memory]).Items).Facts.Count);
    }

    [Theory]
    [InlineData(CheckState.Unknown)] [InlineData(CheckState.Unavailable)] [InlineData(CheckState.Unsupported)]
    [InlineData(CheckState.Failed)] [InlineData(CheckState.Cancelled)] [InlineData((CheckState)999)]
    public void NegativeOrUnknownSourcesCannotYieldAContextEvenWhenTheyContainRows(CheckState state)
    {
        HardwareFact[] facts = [Memory()];
        Refused(HardwareUpgradeContextPolicy.Capture(new(state, "Untrusted source", facts), facts,
            HardwareCompatibilityUpgradeArea.Memory, Attempt, Received, Now));
    }

    [Theory]
    [InlineData("root/cimv2:Win32_PhysicalMemory.SerialNumber", "SerialNumber")]
    [InlineData("root/cimv2:Win32_PhysicalMemory.Capacity", "Supported maximum RAM")]
    [InlineData("root/cimv2:Win32_BIOS.SMBIOSBIOSVersion", "SMBIOSBIOSVersion")]
    [InlineData("root/cimv2:Win32_PhysicalMemory.capacity", "Capacity (bytes)")]
    public void UnexpectedSensitiveOrRelabeledSourceFieldsAreNotAdmitted(string source, string property)
    {
        Refused(Capture([Memory() with { Source = source, Property = property }]));
    }

    [Fact]
    public void WrongCategoryComponentAndInconsistentCoverageAreRefusedAcrossAllClassRecords()
    {
        Refused(Capture([Memory() with { Category = "Storage" }]));
        Refused(Capture([Memory() with { Component = "Memory device 01" }]));
        Refused(Capture([Memory() with { Component = "Memory device 129" }]));
        Refused(Capture([Memory() with { Component = "PC record 1" }]));
        Refused(Capture([Memory(), Memory(complete: false) with { Component = "Memory device 2" }]));
    }

    [Theory]
    [InlineData("\u202eunsafe")] [InlineData("control\ntext")] [InlineData("\0")]
    [InlineData("")][InlineData(" ")]
    public void UnsafeProviderValuesDoNotReachContext(string value) => Refused(Capture([Memory(value)]));

    [Fact]
    public void MalformedUnicodeAndOversizedFactsAreRefusedWithoutTruncation()
    {
        Refused(Capture([Memory(new string((char)0xd800, 1))]));
        Refused(Capture([Memory(new string('x', 513))]));
        Refused(Capture([Memory() with { Category = new string('x', 33) }]));
        Refused(Capture([Memory() with { Component = new string('x', 129) }]));
        Refused(Capture([Memory() with { Property = new string('x', 129) }]));
        Refused(Capture([Memory() with { Source = new string('x', 161) }]));
        Assert.Single(Capture([Memory(new string('x', 512))]).Items);
    }

    [Fact]
    public void StaleAttemptFutureOrNonUtcTimesCannotCreateAFreshContext()
    {
        HardwareFact[] facts = [Memory()];
        CheckResult<HardwareUpgradeContextReport> At(DateTimeOffset attempt, DateTimeOffset received, DateTimeOffset now) =>
            HardwareUpgradeContextPolicy.Capture(Source(facts), facts, HardwareCompatibilityUpgradeArea.Memory, attempt, received, now);
        Refused(At(Attempt, Received, Attempt.AddMinutes(30).AddTicks(1)));
        Refused(At(Received, Attempt, Now)); Refused(At(Attempt, Now.AddTicks(1), Now));
        Refused(At(default, Received, Now)); Refused(At(Attempt.ToOffset(TimeSpan.FromHours(1)), Received, Now));
        Assert.Single(At(Attempt, Received, Attempt.AddMinutes(30)).Items);
    }

    [Fact]
    public void InvalidAreaNullInputsAndOversizedSourcesAreRefused()
    {
        HardwareFact[] facts = [Memory()];
        Refused(Capture(facts, area: null)); Refused(Capture(facts, area: (HardwareCompatibilityUpgradeArea)999));
        Refused(HardwareUpgradeContextPolicy.Capture(null, facts, HardwareCompatibilityUpgradeArea.Memory, Attempt, Received, Now));
        Refused(HardwareUpgradeContextPolicy.Capture(Source(facts), null, HardwareCompatibilityUpgradeArea.Memory, Attempt, Received, Now));
        Refused(Capture([])); Refused(Capture(Enumerable.Repeat(Memory(), HardwareUpgradeContextPolicy.MaximumFacts + 1).ToArray()));
        Refused(Capture([null!]));
    }

    [Fact]
    public void CapturedContainerCannotBeRewrittenThroughOriginalListOrPublicCollection()
    {
        var original = Memory(); HardwareFact[] facts = [original];
        var report = Assert.Single(Capture(facts).Items);
        facts[0] = Memory("1"); Assert.Same(original, Assert.Single(report.Facts));
        var collection = Assert.IsAssignableFrom<IList<HardwareFact>>(report.Facts);
        Assert.Throws<NotSupportedException>(() => collection.Clear());
        Assert.Equal(Attempt, report.AttemptedUtc); Assert.Equal(Received, report.ReceivedUtc); Assert.Equal(Now, report.ReviewedUtc);
    }
}
