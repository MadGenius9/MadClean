using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class StartupPreviewEvidenceTests
{
    private static readonly DateTimeOffset Attempt = new(2025, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private static readonly DateTimeOffset Receipt = Attempt.AddSeconds(2);
    private static readonly StartupRegistrationReference Target = new(StartupRegistrationSource.CurrentUserRun, "Chosen registration");
    private static StartupActionPreview Preview() => new("0123456789abcdef0123456789abcdef", Target, Attempt.AddSeconds(1),
        "REG_EXPAND_SZ", 42, "Read-only future effect preview.", "Current-user metadata only.", "No recovery guarantee or execution permission.");
    private static CheckResult<StartupActionPreview> Response(StartupActionPreview? preview = null) =>
        new(CheckState.Available, "Provider message is not an acceptance signal.", [preview ?? Preview()]);
    private static bool Accept(CheckResult<StartupActionPreview>? result, StartupRegistrationReference? target = null) =>
        StartupPreviewEvidence.IsConsistent(result, target ?? Target, Attempt, Receipt);

    [Fact]
    public void HistoricalPreviewConsistencyDoesNotGrantExecutionOrInventExpiry()
    {
        var preview = Preview();
        var response = Response(preview);
        Assert.True(Accept(response));
        Assert.Same(preview, Assert.Single(response.Items));
        Assert.False(StartupActionPolicy.ProductionMutationValidated);
        Assert.Equal("REG_EXPAND_SZ", preview.ValueKind); // No expansion or command materialization.
    }

    [Theory]
    [InlineData(StartupRegistrationSource.CurrentUserRunOnce, "Chosen registration")]
    [InlineData(StartupRegistrationSource.CurrentUserRun, "Other registration")]
    [InlineData(StartupRegistrationSource.CurrentUserRun, "chosen registration")]
    [InlineData((StartupRegistrationSource)100, "Chosen registration")]
    public void AnotherSourceOrDifferentlyRecordedNameCannotSupplySelectedEvidence(StartupRegistrationSource source, string name)
    {
        var foreign = Preview() with { Reference = new(source, name) };
        Assert.False(Accept(Response(foreign)));
    }

    [Theory]
    [InlineData(CheckState.Unknown)]
    [InlineData(CheckState.Partial)]
    [InlineData(CheckState.Cancelled)]
    [InlineData(CheckState.Failed)]
    [InlineData(CheckState.Unavailable)]
    [InlineData(CheckState.Unsupported)]
    [InlineData((CheckState)100)]
    public void RetainedPlausiblePayloadCannotPromoteAnUnqualifiedResponse(CheckState state) =>
        Assert.False(Accept(Response() with { State = state }));

    [Fact]
    public void MissingOrAmbiguousResponsesAndInvalidSelectedTargetsRemainRejected()
    {
        Assert.False(Accept(null));
        Assert.False(Accept(Response() with { Items = [] }));
        Assert.False(Accept(Response() with { Items = [Preview(), Preview()] }));
        Assert.False(Accept(Response() with { Items = [null!] }));
        Assert.False(Accept(Response() with { Items = null! }));
        Assert.False(StartupPreviewEvidence.IsConsistent(Response(), null, Attempt, Receipt));
        Assert.False(Accept(Response(), Target with { ValueName = "\u200b" }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(523)]
    [InlineData(-2)]
    public void UnsupportedByteCountsDoNotBecomeQualifiedPrivateValueMetadata(int bytes) =>
        Assert.False(Accept(Response(Preview() with { ByteCount = bytes })));

    [Theory]
    [InlineData("REG_SZ", 2)]
    [InlineData("REG_EXPAND_SZ", 522)]
    public void SupportedKindsAndBoundaryLengthsRemainMetadataOnly(string kind, int bytes) =>
        Assert.True(Accept(Response(Preview() with { ValueKind = kind, ByteCount = bytes })));

    [Theory]
    [InlineData("REG_MULTI_SZ")]
    [InlineData("reg_sz")]
    [InlineData("REG_SZ\u200b")]
    [InlineData("")]
    public void UnsupportedOrAmbiguousKindCannotBecomeACommandPreview(string kind) =>
        Assert.False(Accept(Response(Preview() with { ValueKind = kind })));

    [Theory]
    [InlineData("")]
    [InlineData("00000000000000000000000000000000")]
    [InlineData("01234567-89ab-cdef-0123-456789abcdef")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("private-token-sentinel")]
    public void MalformedOrEmptyOpaqueTokensAreNotConsistentMetadata(string token) =>
        Assert.False(Accept(Response(Preview() with { Token = token })));

    [Fact]
    public void TimeEvidenceMustBelongToTheActualUtcRequestInterval()
    {
        Assert.False(Accept(Response(Preview() with { ObservedUtc = Attempt.AddTicks(-1) })));
        Assert.False(Accept(Response(Preview() with { ObservedUtc = Receipt.AddTicks(1) })));
        Assert.False(Accept(Response(Preview() with { ObservedUtc = default })));
        Assert.False(Accept(Response(Preview() with { ObservedUtc = Attempt.AddSeconds(1).ToOffset(TimeSpan.FromHours(1)) })));
        Assert.False(StartupPreviewEvidence.IsConsistent(Response(), Target, default, Receipt));
        Assert.False(StartupPreviewEvidence.IsConsistent(Response(), Target, Receipt, Attempt));
        Assert.False(StartupPreviewEvidence.IsConsistent(Response(), Target, Attempt.ToOffset(TimeSpan.FromHours(1)), Receipt));
        var future = DateTimeOffset.UtcNow.AddDays(1);
        Assert.False(StartupPreviewEvidence.IsConsistent(Response(Preview() with { ObservedUtc = future }), Target, future, future.AddSeconds(1)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("private\nprovider-output")]
    [InlineData("misleading\u202etext")]
    public void UnreadableDescriptionsCannotEnterAnAcceptedPreview(string description)
    {
        var original = Preview();
        Assert.False(Accept(Response(original with { ExactEffect = description })));
        Assert.False(Accept(Response(original with { Permissions = description })));
        Assert.False(Accept(Response(original with { RecoveryLimits = description })));
    }

    [Fact]
    public void OversizedDescriptionsAndRuntimeNullsFailWithoutMutatingProviderObjects()
    {
        var preview = Preview() with { ExactEffect = new string('x', 4097) };
        var response = Response(preview);
        Assert.False(Accept(response));
        Assert.Same(preview, response.Items[0]);
        Assert.False(Accept(Response(Preview() with { Reference = null! })));
        Assert.False(Accept(Response(Preview() with { Token = null! })));
        Assert.False(Accept(Response(Preview() with { Permissions = null! })));
        Assert.False(StartupActionPolicy.ProductionMutationValidated);
    }
}
