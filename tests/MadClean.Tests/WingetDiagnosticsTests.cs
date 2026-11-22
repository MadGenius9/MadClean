using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class WingetDiagnosticsTests
{
    private const string Header = "Name                 Id                       Version       Available     Source\n--------------------------------------------------------------------------------------\n";
    private static string Row(string name = "Example") => $"{name,-21}{"Example.App",-25}{"1.0",-14}{"2.0",-14}winget";

    [Fact]
    public void ARejectedTrailingLineDiscardsEarlierPlausibleRowsAndIdentifiesTheStructuralBoundary()
    {
        var observed = WingetParser.ParseObserved(Header + Row() + "\n1 upgrade available");
        Assert.Equal(CheckState.Unknown, observed.Result.State);
        Assert.Empty(observed.Result.Items);
        Assert.Equal(WingetParseIssue.ShortRow, observed.Issue);
        Assert.Equal(WingetParser.Parse(Header + Row() + "\n1 upgrade available").Message, observed.Result.Message);
    }

    [Fact]
    public void WindowsLineEndingsPreserveTheInstalledAndOfferedVersions()
    {
        var result = WingetParser.Parse((Header + Row() + "\n").Replace("\n", "\r\n"));
        var package = Assert.Single(result.Items);
        Assert.Equal(CheckState.Available, package.Status);
        Assert.Equal("Example.App", package.Id);
        Assert.Equal("1.0", package.InstalledVersion);
        Assert.Equal("2.0", package.AvailableVersion);
    }

    [Fact]
    public void CarriageReturnWithinATableRowCannotJoinSeparateOutputFragments()
    {
        var observed = WingetParser.ParseObserved(Header + Row().Replace("Example.App", "Example.\rApp"));
        Assert.Equal(CheckState.Unknown, observed.Result.State);
        Assert.Empty(observed.Result.Items);
        Assert.Equal(WingetParseIssue.UnsupportedDisplayCharacters, observed.Issue);
    }

    [Fact]
    public void AChangedSeparatorCannotBecomeAnEmptySuccessfulInventory()
    {
        var observed = WingetParser.ParseObserved(Header.Replace("---", "===") + Row());
        Assert.Equal(WingetParseIssue.SeparatorNotRecognized, observed.Issue);
        Assert.Equal(CheckState.Unknown, observed.Result.State);
        Assert.Empty(observed.Result.Items);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \r\n\t")]
    public void WhitespaceOnlyStdoutHasNoContentButDoesNotEstablishNoInstalledPackages(string output)
    {
        var observed = WingetParser.ParseObserved(output);
        var diagnostic = new WingetQueryDiagnostics(WingetQueryOutcome.ParsedOutput, 0, !string.IsNullOrWhiteSpace(output), observed.Issue);
        Assert.True(diagnostic.IsConsistent(observed.Result.State, observed.Result.Items.Count));
        Assert.False(diagnostic.IsConsistent(CheckState.Available, 0));
        Assert.Empty(observed.Result.Items);
    }

    [Fact]
    public void UnicodeOrUnrecognizedLayoutCannotBecomeAFailureCauseGuessOrAvailableRows()
    {
        var unicode = WingetParser.ParseObserved(Header + Row("示例"));
        Assert.Equal(WingetParseIssue.UnsupportedDisplayCharacters, unicode.Issue);
        Assert.Empty(unicode.Result.Items);
        var localizedOrUnknown = WingetParser.ParseObserved("Name Kennung Version\n--------------------\nExample Example.App 1.0");
        Assert.Equal(WingetParseIssue.HeaderNotRecognized, localizedOrUnknown.Issue);
        Assert.Empty(localizedOrUnknown.Result.Items);
    }

    [Fact]
    public void DiagnosticCompatibilityDoesNotPromoteUncheckedPackageVersions()
    {
        const string output = "Name Id          Version\n------------------------\nGit  Git.Git     Unknown";
        var observed = WingetParser.ParseObserved(output);
        Assert.Equal(CheckState.Unknown, Assert.Single(observed.Result.Items).Status);
        Assert.True(new WingetQueryDiagnostics(WingetQueryOutcome.ParsedOutput, 0, true, observed.Issue)
            .IsConsistent(observed.Result.State, observed.Result.Items.Count));
        Assert.Equal(WingetParseIssue.Accepted, observed.Issue);
    }

    [Fact]
    public void MissingTransportObservationsCannotBeFilledWithInventedExitOrParserSuccess()
    {
        var unavailable = new WingetQueryDiagnostics(WingetQueryOutcome.AliasUnavailable, null, null, null);
        Assert.True(unavailable.IsConsistent(CheckState.Unavailable, 0));
        Assert.False((unavailable with { ExitCode = 0 }).IsConsistent(CheckState.Unavailable, 0));
        Assert.False((unavailable with { ParseIssue = WingetParseIssue.Accepted }).IsConsistent(CheckState.Unavailable, 0));
        Assert.False((unavailable with { OutputHasContent = false }).IsConsistent(CheckState.Unavailable, 0));
        Assert.False(unavailable.IsConsistent(CheckState.Unknown, 0));
        Assert.False(new WingetQueryDiagnostics(WingetQueryOutcome.TimedOut, null, null, null).IsConsistent(CheckState.Available, 1));
    }

    [Fact]
    public void ReturnedExitAndParserStatesMustAgreeWithObservedSourceAndRows()
    {
        var rejected = new WingetQueryDiagnostics(WingetQueryOutcome.ParsedOutput, 0, true, WingetParseIssue.IncompleteFields);
        Assert.True(rejected.IsConsistent(CheckState.Unknown, 0));
        Assert.False(rejected.IsConsistent(CheckState.Available, 1));
        Assert.False((rejected with { OutputHasContent = null }).IsConsistent(CheckState.Unknown, 0));
        Assert.False((rejected with { ExitCode = 1 }).IsConsistent(CheckState.Unknown, 0));
        var failedExit = new WingetQueryDiagnostics(WingetQueryOutcome.NonzeroExit, unchecked((int)0x8A15000F), false, null);
        Assert.True(failedExit.IsConsistent(CheckState.Failed, 0));
        Assert.False((failedExit with { ExitCode = 0 }).IsConsistent(CheckState.Failed, 0));
        Assert.False((failedExit with { ParseIssue = WingetParseIssue.EmptyOutput }).IsConsistent(CheckState.Failed, 0));
        Assert.False(failedExit.IsConsistent(CheckState.Unknown, 0));
        Assert.False(new WingetQueryDiagnostics((WingetQueryOutcome)999, null, null, null).IsConsistent(CheckState.Unknown, 0));
        Assert.False((rejected with { ParseIssue = (WingetParseIssue)999 }).IsConsistent(CheckState.Unknown, 0));
    }
}
