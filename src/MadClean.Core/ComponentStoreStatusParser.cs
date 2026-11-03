using System.Text.Json;

namespace MadClean.Core;

public sealed record ComponentStoreStatusParseResult(bool Accepted, ComponentStoreStatusEvidence? Evidence, string Message);

/// <summary>Parses only a fixed, nonce-correlated primitive host envelope. No provider text is returned.</summary>
public static class ComponentStoreStatusParser
{
    public const int MaximumCharacters = 8192;
    public const int MaximumOutputCount = 16;
    public const int MaximumErrorCount = 128;
    private static readonly string[] Fields =
    [
        "version", "requestId", "stage", "outcome", "hostQualified", "moduleBound", "cmdletQualified",
        "outputContractQualified", "invocationStarted", "pipelineState", "endInvokeReturned", "cancelEventObserved",
        "beginStopCalled", "endStopReturned", "outputCount", "errorCount", "warningCount", "healthState", "powerShellDisposed",
        "runspaceClosed", "cancelHandleDisposed", "managedExceptionHResult"
    ];

    public static ComponentStoreStatusParseResult Parse(string json, Guid expectedRequestId, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumCharacters || expectedRequestId == Guid.Empty) return Rejected();
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 2 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Rejected();
            var names = root.EnumerateObject().Select(value => value.Name).ToArray();
            if (names.Length != Fields.Length || names.Distinct(StringComparer.Ordinal).Count() != names.Length ||
                Fields.Any(field => !names.Contains(field, StringComparer.Ordinal))) return Rejected();
            if (Integer(root.GetProperty("version")) != 1) return Rejected();
            string? id = Text(root.GetProperty("requestId"));
            if (!Guid.TryParseExact(id, "N", out var requestId) || requestId != expectedRequestId || requestId.ToString("N") != id) return Rejected();
            var evidence = new ComponentStoreStatusEvidence
            {
                RequestId = requestId,
                Stage = EnumValue<ComponentStoreStatusStage>(root.GetProperty("stage")),
                Outcome = EnumValue<ComponentStoreStatusOutcome>(root.GetProperty("outcome")),
                HostQualified = Boolean(root.GetProperty("hostQualified")),
                ModuleBound = Boolean(root.GetProperty("moduleBound")),
                CmdletQualified = Boolean(root.GetProperty("cmdletQualified")),
                OutputContractQualified = Boolean(root.GetProperty("outputContractQualified")),
                InvocationStarted = Boolean(root.GetProperty("invocationStarted")),
                PipelineState = root.GetProperty("pipelineState").ValueKind == JsonValueKind.Null
                    ? null : EnumValue<ComponentStorePipelineState>(root.GetProperty("pipelineState")),
                EndInvokeReturned = Boolean(root.GetProperty("endInvokeReturned")),
                CancelEventObserved = Boolean(root.GetProperty("cancelEventObserved")),
                BeginStopCalled = Boolean(root.GetProperty("beginStopCalled")),
                EndStopReturned = Boolean(root.GetProperty("endStopReturned")),
                OutputCount = Count(root.GetProperty("outputCount"), MaximumOutputCount),
                ErrorCount = Count(root.GetProperty("errorCount"), MaximumErrorCount),
                WarningCount = Count(root.GetProperty("warningCount"), MaximumErrorCount),
                HealthState = Text(root.GetProperty("healthState")),
                PowerShellDisposed = Boolean(root.GetProperty("powerShellDisposed")),
                RunspaceClosed = Boolean(root.GetProperty("runspaceClosed")),
                CancelHandleDisposed = Boolean(root.GetProperty("cancelHandleDisposed")),
                ManagedExceptionHResult = root.GetProperty("managedExceptionHResult").ValueKind == JsonValueKind.Null
                    ? null : Integer(root.GetProperty("managedExceptionHResult"))
            };
            token.ThrowIfCancellationRequested();
            return IsConsistent(evidence, expectedRequestId)
                ? new(true, evidence, "The fixed host envelope was accepted. This is source compatibility, not a Windows health verdict.")
                : Rejected();
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException or OverflowException)
        { return Rejected(); }
    }

    public static bool IsConsistent(ComponentStoreStatusEvidence evidence, Guid expectedRequestId)
    {
        if (evidence == null || expectedRequestId == Guid.Empty || evidence.RequestId != expectedRequestId ||
            !Enum.IsDefined(evidence.Stage) || !Enum.IsDefined(evidence.Outcome) ||
            evidence.PipelineState is { } pipeline && !Enum.IsDefined(pipeline) ||
            evidence.OutputCount is < 0 or > MaximumOutputCount || evidence.ErrorCount is < 0 or > MaximumErrorCount ||
            evidence.WarningCount is < 0 or > MaximumErrorCount ||
            evidence.HealthState is not (null or "Healthy" or "Repairable" or "NonRepairable")) return false;
        if (evidence.ModuleBound == true && evidence.HostQualified != true ||
            evidence.CmdletQualified == true && evidence.ModuleBound != true ||
            evidence.OutputContractQualified == true && evidence.CmdletQualified != true) return false;
        if (evidence.InvocationStarted == true && (evidence.HostQualified != true || evidence.ModuleBound != true ||
            evidence.CmdletQualified != true || evidence.OutputContractQualified != true)) return false;
        if (evidence.EndInvokeReturned.HasValue && evidence.InvocationStarted != true) return false;
        if (evidence.PipelineState is not (null or ComponentStorePipelineState.NotStarted) && evidence.InvocationStarted != true) return false;
        if (evidence.BeginStopCalled == true && (evidence.InvocationStarted != true || evidence.CancelEventObserved != true)) return false;
        if (evidence.EndStopReturned.HasValue && evidence.BeginStopCalled != true) return false;
        if (evidence.HealthState != null && (evidence.OutputContractQualified != true || evidence.InvocationStarted != true ||
            evidence.OutputCount is not >= 1)) return false;
        if (evidence.Outcome == ComponentStoreStatusOutcome.MetadataUnqualified && evidence.InvocationStarted == true) return false;
        if (evidence.Outcome == ComponentStoreStatusOutcome.PipelineStopped && evidence.PipelineState != ComponentStorePipelineState.Stopped) return false;
        if (evidence.Outcome == ComponentStoreStatusOutcome.Completed &&
            (evidence.PipelineState != ComponentStorePipelineState.Completed || evidence.EndInvokeReturned != true ||
             evidence.OutputCount != 1 || evidence.ErrorCount != 0 || evidence.HealthState == null)) return false;
        return true;
    }

    private static int Integer(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || value.GetRawText().IndexOfAny(['.', 'e', 'E']) >= 0 || !value.TryGetInt32(out int result))
            throw new FormatException();
        return result;
    }
    private static int? Count(JsonElement value, int maximum)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        int count = Integer(value);
        if (count < 0 || count > maximum) throw new FormatException();
        return count;
    }
    private static bool? Boolean(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null, JsonValueKind.True => true, JsonValueKind.False => false, _ => throw new FormatException()
    };
    private static string? Text(JsonElement value) => value.ValueKind switch
    { JsonValueKind.Null => null, JsonValueKind.String => value.GetString(), _ => throw new FormatException() };
    private static T EnumValue<T>(JsonElement value) where T : struct, Enum
    {
        string? text = Text(value);
        if (text == null || !Enum.TryParse<T>(text, false, out var result) || !Enum.IsDefined(result) || result.ToString() != text) throw new FormatException();
        return result;
    }
    private static ComponentStoreStatusParseResult Rejected() =>
        new(false, null, "The fixed host response was absent, malformed, inconsistent or mismatched. No recorded flag was accepted.");
}
