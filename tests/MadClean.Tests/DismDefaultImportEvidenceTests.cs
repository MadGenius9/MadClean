using System.Text.Json;
using MadClean.Validation;
using Xunit;

namespace MadClean.Tests;

public sealed class DismDefaultImportEvidenceTests
{
    [Fact]
    public void QualifiedImportRequiresActualBindingAndSettledOwnedObjects()
    {
        var input = Positive();
        Assert.True(Read(input, out var facts));
        Assert.True(facts!.IsImportBound);
        Assert.True(facts.ResourcesSettled);
        Assert.Null(facts.ExceptionHResult);
    }

    [Fact]
    public void ValidNoHostResponseIsNotSuccessfulImport()
    {
        var input = Positive();
        foreach (var key in new[] { "hostQualified", "manifestQualified", "runspaceCreated", "powerShellCreated", "importAttempted" }) input[key] = false;
        foreach (var key in new[] { "runspaceOpened", "importReturned", "importHadErrors", "returnedModuleCount", "moduleBound", "powerShellDisposed", "runspaceClosed", "runspaceDisposed" }) input[key] = null;
        Assert.True(Read(input, out var facts));
        Assert.True(facts!.ResourcesSettled); // No owned object was reported created.
        Assert.False(facts.IsImportBound);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReportedOrThrowingImportFailureCannotBecomeSuccess(bool returned)
    {
        var input = Positive();
        input["importReturned"] = returned;
        input["importHadErrors"] = returned ? true : null;
        input["returnedModuleCount"] = returned ? 0 : null;
        input["moduleBound"] = null;
        input["exceptionHResult"] = returned ? null : unchecked((int)0x80131501);
        Assert.True(Read(input, out var facts));
        Assert.True(facts!.ResourcesSettled);
        Assert.False(facts.IsImportBound);
        Assert.Equal(returned, facts.ImportReturned);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(32)]
    public void ReturnedCollectionDoesNotProveOneBoundModule(int count)
    {
        var input = Positive();
        input["returnedModuleCount"] = count;
        input["moduleBound"] = null;
        Assert.True(Read(input, out var facts));
        Assert.False(facts!.IsImportBound);
        Assert.Equal(count, facts.ReturnedModuleCount);
        input["moduleBound"] = true;
        Assert.False(Read(input, out _));
    }

    [Theory]
    [InlineData("powerShellDisposed")]
    [InlineData("runspaceClosed")]
    [InlineData("runspaceDisposed")]
    public void CleanupFailureKeepsPositiveImportFactsSeparateFromReadiness(string field)
    {
        var input = Positive(); input[field] = false;
        Assert.True(Read(input, out var facts));
        Assert.True(facts!.ModuleBound);
        Assert.False(facts.ResourcesSettled);
        Assert.False(facts.IsImportBound);
        input[field] = null;
        Assert.False(Read(input, out _)); // Created objects need actual cleanup observations.
    }

    [Theory]
    [InlineData(0)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void ActualExceptionCodeIncludingZeroIsRetainedAndCannotQualifyImport(int code)
    {
        var input = Positive(); input["exceptionHResult"] = code;
        Assert.True(Read(input, out var facts));
        Assert.Equal(code, facts!.ExceptionHResult);
        Assert.False(facts.IsImportBound);
    }

    [Theory]
    [InlineData("hostQualified", false)]
    [InlineData("manifestQualified", false)]
    [InlineData("runspaceCreated", false)]
    [InlineData("runspaceOpened", false)]
    [InlineData("powerShellCreated", false)]
    [InlineData("importAttempted", false)]
    [InlineData("importReturned", false)]
    [InlineData("importHadErrors", true)]
    public void ImpossiblePositiveSuccessorsCannotValidateARefusedPrerequisite(string field, bool value)
    {
        var input = Positive(); input[field] = value;
        Assert.False(Read(input, out var facts));
        Assert.Null(facts);
    }

    [Fact]
    public void UnobservedBindingRemainsUnknownEvenWithOneReturnedModule()
    {
        var input = Positive(); input["moduleBound"] = null;
        Assert.True(Read(input, out var facts));
        Assert.Null(facts!.ModuleBound);
        Assert.False(facts.IsImportBound);
        input["moduleBound"] = false;
        Assert.True(Read(input, out facts));
        Assert.False(facts!.IsImportBound);
    }

    [Theory]
    [InlineData("schema", 2)]
    [InlineData("schema", "1")]
    [InlineData("context", "other-context")]
    [InlineData("profile", "CreateDefault-AutoloadNone-InboxOnly")]
    [InlineData("profile", "CreateDefault-AutoloadAll-UserPaths")]
    [InlineData("hostQualified", "true")]
    [InlineData("returnedModuleCount", -1)]
    [InlineData("returnedModuleCount", 33)]
    [InlineData("exceptionHResult", "0x80131501")]
    public void ForeignProfilesAndUnsupportedScalarShapesDoNotProduceFacts(string field, object value)
    {
        var input = Positive(); input[field] = value;
        Assert.False(Read(input, out var facts));
        Assert.Null(facts);
    }

    [Fact]
    public void ExtraMissingDuplicateOrProviderTextFieldsAreRejectedWithoutEchoingValues()
    {
        var input = Positive(); input["providerMessage"] = "TEST-ONLY private payload";
        Assert.False(DismDefaultImportEvidence.TryRead(JsonSerializer.Serialize(input), out _, out string reason));
        Assert.DoesNotContain("payload", reason);
        input = Positive(); input.Remove("runspaceDisposed");
        Assert.False(Read(input, out _));
        string json = JsonSerializer.Serialize(Positive());
        foreach (string addition in new[] { "\"hostQualified\":true,", "\"ho\\u0073tQualified\":true," })
            Assert.False(DismDefaultImportEvidence.TryRead("{" + addition + json[1..], out _, out _));
    }

    [Fact]
    public void OversizedNestedMalformedOrNonObjectResponsesAreRefused()
    {
        string valid = JsonSerializer.Serialize(Positive());
        Assert.False(DismDefaultImportEvidence.TryRead(valid.PadRight(4097), out _, out _));
        Assert.True(DismDefaultImportEvidence.TryRead(valid.PadRight(4096), out _, out _));
        foreach (string? json in new[] { null, "", "[]", "null", "true", "{", "{\"unknown\":{\"nested\":{\"tooDeep\":true}}}" })
            Assert.False(DismDefaultImportEvidence.TryRead(json, out _, out _));
        var input = Positive(); input["returnedModuleCount"] = (long)int.MaxValue + 1;
        Assert.False(Read(input, out _));
    }

    private static bool Read(Dictionary<string, object?> input, out DismDefaultImportFacts? facts) =>
        DismDefaultImportEvidence.TryRead(JsonSerializer.Serialize(input), out facts, out _);

    private static Dictionary<string, object?> Positive() => new()
    {
        ["schema"] = 1, ["context"] = DismDefaultImportEvidence.Context, ["profile"] = DismDefaultImportEvidence.Profile,
        ["hostQualified"] = true, ["manifestQualified"] = true, ["runspaceCreated"] = true, ["runspaceOpened"] = true,
        ["powerShellCreated"] = true, ["importAttempted"] = true, ["importReturned"] = true, ["importHadErrors"] = false,
        ["returnedModuleCount"] = 1, ["moduleBound"] = true, ["powerShellDisposed"] = true,
        ["runspaceClosed"] = true, ["runspaceDisposed"] = true, ["exceptionHResult"] = null
    };
}
