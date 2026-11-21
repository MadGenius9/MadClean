using MadClean.Windows;
using MadClean.WindowsSmoke;
using Xunit;

namespace MadClean.Tests;

public sealed class ExistingReaderQualificationCaseTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("missing-lease")]
    [InlineData("missing-journal")]
    [InlineData("extra-file")]
    [InlineData("extra-directory")]
    [InlineData("case-mismatch")]
    [InlineData("missing-lf")]
    public void ActualControlledOwnerMustReachTheCasesExactBoundary(string label)
    {
        var domain = new ModernOwnedJournalSampleDomain();
        var frame = new Frame(domain, label);
        var attempt = domain.Run(frame);
        Assert.True(ExistingReaderQualificationCases.Matches(label, attempt));
        Assert.True(attempt.ResourcesReleased);
        Assert.False(ExistingReaderQualificationCases.Matches("unrecognized", attempt));
        if (label != "valid") Assert.False(ExistingReaderQualificationCases.Matches("valid", attempt));
    }

    [Fact]
    public void EarlyRefusalCannotQualifyALaterNamespaceNegative()
    {
        var domain = new ModernOwnedJournalSampleDomain();
        var attempt = domain.Run(new Frame(domain, "missing-lease"));
        Assert.False(ExistingReaderQualificationCases.Matches("extra-file", attempt));
        Assert.False(ExistingReaderQualificationCases.Matches("missing-journal", attempt));
    }

    [Fact]
    public void NamesCompletionAndLaterSampleRefusalIsOnlyTheCodecNegative()
    {
        var domain = new ModernOwnedJournalSampleDomain();
        var attempt = domain.Run(new Frame(domain, "missing-lf"));
        Assert.False(ExistingReaderQualificationCases.Matches("extra-directory", attempt));
        Assert.True(ExistingReaderQualificationCases.Matches("missing-lf", attempt));
    }

    [Fact]
    public void VerifiedSampleWithUncertainReleaseNeverQualifies()
    {
        var domain = new ModernOwnedJournalSampleDomain();
        var attempt = domain.Run(new Frame(domain, "valid") { ReleaseConfirmed = false });
        Assert.True(attempt.SampleVerified);
        Assert.True(attempt.RequiresRetention);
        Assert.False(ExistingReaderQualificationCases.Matches("valid", attempt));
    }

    private sealed class Frame(ModernOwnedJournalSampleDomain domain, string label) : ModernOwnedJournalSampleFrame
    {
        internal bool ReleaseConfirmed = true;
        internal override bool Prepare(CancellationToken token) => true;
        internal override ModernJournalAcquisition AcquireDirectory() => ModernJournalAcquisition.Acquired;
        internal override ModernJournalAcquisition AcquireLease() => label == "missing-lease" ? ModernJournalAcquisition.NotAcquired : ModernJournalAcquisition.Acquired;
        internal override ModernJournalAcquisition AcquireJournal() => label == "missing-journal" ? ModernJournalAcquisition.NotAcquired : ModernJournalAcquisition.Acquired;
        internal override bool ObserveSyntheticIntent()
        {
            if (!domain.TryRecordNames(this, ModernJournalNamesObservation.InProgress)) throw new InvalidOperationException();
            var names = label is "valid" or "missing-lf" ? ModernJournalNamesObservation.Complete : ModernJournalNamesObservation.Refused;
            if (!domain.TryRecordNames(this, names)) throw new InvalidOperationException();
            return label == "valid";
        }
        internal override bool ReleaseKnownOwnership() => ReleaseConfirmed;
    }
}
