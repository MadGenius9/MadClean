using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class SystemToolIdentityObservationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private const string SystemDirectory = @"C:\Windows\System32";
    private static SystemToolActionPreview Preview(SystemToolOperation operation = SystemToolOperation.DismCheckHealth)
    {
        var definition = SystemToolActionsCatalog.Find(operation)!;
        return new(Guid.NewGuid(), definition, SystemDirectory, SystemDirectory + "\\" + definition.FileName, "10.0.1.2",
            new string('a', 64), SystemToolTrustState.MicrosoftSignatureVerified, "Provider-reported Microsoft signature identity.",
            Now.AddSeconds(-10), Now.AddSeconds(110), false, "Production execution disabled.");
    }
    private static SystemToolIdentityObservation Evaluate(SystemToolActionPreview preview, CheckState state = CheckState.Available) =>
        SystemToolIdentityObservationPolicy.Evaluate(preview.Definition.Operation, new(state, "PRIVATE native output", [preview]), SystemDirectory, Now);

    [Theory]
    [InlineData(SystemToolOperation.DismCheckHealth)][InlineData(SystemToolOperation.DismScanHealth)]
    [InlineData(SystemToolOperation.DismRestoreHealth)][InlineData(SystemToolOperation.SfcVerifyOnly)][InlineData(SystemToolOperation.SfcScanNow)]
    public void AcceptsExactReadOnlyProjectionForSelectedOperationWithoutLaunchPermission(SystemToolOperation operation)
    {
        var preview = Preview(operation); var result = Evaluate(preview);
        Assert.Equal(CheckState.Available, result.State); Assert.True(result.IdentityObserved);
        Assert.Equal(preview.CheckedUtc, result.CheckedUtc); Assert.Equal(preview.ExpiresUtc, result.ExpiresUtc);
        Assert.Contains("does not perform Authenticode", result.Explanation);
        Assert.Contains("Windows integrity is not checked", result.Explanation);
        Assert.DoesNotContain("PRIVATE", result.Explanation); Assert.False(preview.CanLaunch);
    }
    [Fact]
    public void PartialUnknownVersionRetainsSignatureObservationButCannotBecomeAvailable()
    {
        var preview = Preview() with { ToolVersion = "Unknown" };
        var partial = Evaluate(preview, CheckState.Partial);
        Assert.Equal(CheckState.Partial, partial.State); Assert.True(partial.IdentityObserved);
        Assert.Contains("version remains unknown", partial.Explanation);
        AssertUnknown(Evaluate(preview));
        Assert.Equal(CheckState.Partial, Evaluate(Preview(), CheckState.Partial).State);
    }
    [Theory]
    [InlineData(CheckState.Unsupported)][InlineData(CheckState.Unavailable)][InlineData(CheckState.Unknown)]
    [InlineData(CheckState.Failed)][InlineData(CheckState.Cancelled)]
    public void NonSuccessRemainsDistinctBeforeOSDirectoryValidation(CheckState state)
    {
        var result = SystemToolIdentityObservationPolicy.Evaluate(SystemToolOperation.DismCheckHealth,
            new(state, "PRIVATE native error", []), "", default);
        Assert.Equal(state, result.State); Assert.Null(result.IdentityObserved); Assert.DoesNotContain("PRIVATE", result.Explanation);
    }
    [Fact]
    public void RejectsForgedDescriptorsSelectedOperationAndArguments()
    {
        var preview = Preview(); var definition = preview.Definition;
        foreach (var forged in new[]
        {
            definition with { Arguments = ["/Online", "/Cleanup-Image", "/RestoreHealth", "/NoRestart"] },
            definition with { Effect = "No effects" }, definition with { Title = "Other" },
            definition with { Elevation = "No administrator needed" }, definition with { Kind = SystemToolActionKind.Repair },
            definition with { WindowsLogReference = "Other" }, definition with { Arguments = null! }
        }) AssertUnknown(Evaluate(preview with { Definition = forged }));
        AssertUnknown(SystemToolIdentityObservationPolicy.Evaluate(SystemToolOperation.SfcVerifyOnly,
            new(CheckState.Available, "", [preview]), SystemDirectory, Now));
    }
    [Fact]
    public void RejectsUntrustedEnabledMissingOrUnsafeIdentityMetadata()
    {
        foreach (var preview in new[]
        {
            Preview() with { CanLaunch = true }, Preview() with { PreviewId = Guid.Empty },
            Preview() with { TrustState = SystemToolTrustState.Unknown }, Preview() with { Sha256 = new string('A', 64) },
            Preview() with { Blocker = "" }, Preview() with { TrustDetail = "" }, Preview() with { ToolVersion = new string('x', 129) },
            Preview() with { ToolVersion = "bad\u202e" }, Preview() with { ToolVersion = "bad" + new string((char)0xD800, 1) },
            Preview() with { ToolPath = @"C:\Other\Dism.exe" }, Preview() with { SystemDirectory = @"C:\Other" }
        }) AssertUnknown(Evaluate(preview));
    }
    [Fact]
    public void RejectsStaleFutureNonUtcAndOverlongLifetimes()
    {
        foreach (var preview in new[]
        {
            Preview() with { CheckedUtc = default }, Preview() with { CheckedUtc = Now.AddSeconds(1) },
            Preview() with { ExpiresUtc = Now }, Preview() with { ExpiresUtc = Now.AddMinutes(3) },
            Preview() with { CheckedUtc = Now.AddSeconds(-10).ToOffset(TimeSpan.FromHours(1)) },
            Preview() with { ExpiresUtc = Now.AddSeconds(110).ToOffset(TimeSpan.FromHours(1)) }
        }) AssertUnknown(Evaluate(preview));
    }
    [Fact]
    public void RejectsMissingAmbiguousEnvelopesAndUnsafeIndependentDirectorySyntax()
    {
        foreach (var input in new CheckResult<SystemToolActionPreview>?[]
        { null, new(CheckState.Available, "", []), new(CheckState.Partial, "", [Preview(), Preview()]), new(CheckState.Available, "", [null!]) })
            AssertUnknown(SystemToolIdentityObservationPolicy.Evaluate(SystemToolOperation.DismCheckHealth, input, SystemDirectory, Now));
        foreach (string directory in new[] { "", @"\\server\share", @"C:\Windows\..\System32", @"C:\Windows\\System32", "C:\\Windows\\bad" + new string((char)0xDFFF, 1) })
            AssertUnknown(SystemToolIdentityObservationPolicy.Evaluate(SystemToolOperation.DismCheckHealth,
                new(CheckState.Available, "", [Preview()]), directory, Now));
    }
    [Fact]
    public void CancellationCannotPublishUnknownOrSuccessfulObservation()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => SystemToolIdentityObservationPolicy.Evaluate(SystemToolOperation.DismCheckHealth,
            new(CheckState.Available, "", [Preview()]), SystemDirectory, Now, stop.Token));
        Assert.Throws<OperationCanceledException>(() => SystemToolIdentityObservationPolicy.Evaluate(SystemToolOperation.DismCheckHealth,
            null, "", default, stop.Token));
    }
    private static void AssertUnknown(SystemToolIdentityObservation result)
    {
        Assert.Equal(CheckState.Unknown, result.State); Assert.Null(result.IdentityObserved);
        Assert.Null(result.CheckedUtc); Assert.Null(result.ExpiresUtc);
    }
}
