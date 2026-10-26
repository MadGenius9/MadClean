using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class UpdateContractReviewTests
{
    [Theory]
    [InlineData("null", "\"test-id\"")]
    [InlineData("false", "\"test-id\"")]
    [InlineData("42", "\"test-id\"")]
    [InlineData("\"Example offer\"", "null")]
    [InlineData("\"Example offer\"", "false")]
    [InlineData("\"Example offer\"", "42")]
    [InlineData("\" \"", "\"test-id\"")]
    [InlineData("\"Example offer\"", "\"\"")]
    public void NonStringOrEmptyOfferMetadataCannotBecomeAGenuineAvailableOffer(string title, string id)
    {
        var result = WindowsResponseParser.SystemUpdates(
            $"{{\"ResultCode\":2,\"Updates\":[{{\"Title\":{title},\"Id\":{id},\"Type\":1,\"RebootRequired\":false}}]}}");
        Assert.Equal(CheckState.Unknown, result.State);
        Assert.Empty(result.Items);
    }

    [Fact]
    public void AnAbortedProviderSearchIsReportedAsCancelled()
    {
        var result = WindowsResponseParser.SystemUpdates("{\"ResultCode\":5,\"Updates\":[]}");
        Assert.Equal(CheckState.Cancelled, result.State);
        Assert.Empty(result.Items);
    }

    [Fact]
    public void UnrecognizedUpdateTypeDoesNotClaimACompleteClassification()
    {
        var result = WindowsResponseParser.SystemUpdates(
            "{\"ResultCode\":2,\"Updates\":[{\"Title\":\"Example offer\",\"Id\":\"test-id\",\"Type\":99,\"RebootRequired\":false}]}");
        Assert.Equal(CheckState.Partial, result.State);
        Assert.Equal("Unknown", Assert.Single(result.Items).Kind);
    }
}
