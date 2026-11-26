using System.Text;
using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class DefenderScanHostProtocolTests
{
    [Fact]
    public void FolderWithPowerShellSyntaxCharactersRoundTripsAsData()
    {
        const string intendedFolder = @"C:\Review\résumé 'test'; `$(echo)";
        var draft = Assert.IsType<DefenderCustomScanDraft>(DefenderCustomScanDraft.Prepare(intendedFolder));
        var input = DefenderScanHostProtocol.EncodeFolderInput(draft);

        Assert.DoesNotContain(intendedFolder, input);
        Assert.True(input.Length <= 1024);
        Assert.Equal(intendedFolder, Encoding.Unicode.GetString(Convert.FromBase64String(input)));
    }

    [Theory]
    [InlineData("MadClean.DefenderRequest/1:refused\r\n", 0, DefenderScanSubmission.Rejected)]
    [InlineData("MadClean.DefenderRequest/1:returned\n", 0, DefenderScanSubmission.RequestReturnedWithoutCommandError)]
    [InlineData("MadClean.DefenderRequest/1:returned", 1, DefenderScanSubmission.Unknown)]
    [InlineData("MadClean.DefenderRequest/1:unknown", 0, DefenderScanSubmission.Unknown)]
    [InlineData("MadClean.DefenderProfile/1:observed", 0, DefenderScanSubmission.Unknown)]
    [InlineData("MadClean.DefenderRequest/1:returned\nMadClean.DefenderRequest/1:refused", 0, DefenderScanSubmission.Unknown)]
    public void OnlyOneTypedMarkerAndACompletedClientCanEstablishARequestOutcome(
        string output, int exitCode, DefenderScanSubmission expected) =>
        Assert.Equal(expected, DefenderScanHostProtocol.Interpret(new(exitCode, output)));
}
