using System.Text.Json;

namespace MadClean.Validation;

// Fixed read-only diagnostic facts, not publisher trust, command eligibility or a
// servicing capability. A valid negative response is not a successful import.
internal sealed record DismDefaultImportFacts(
    bool HostQualified, bool ManifestQualified, bool RunspaceCreated, bool? RunspaceOpened,
    bool PowerShellCreated, bool ImportAttempted, bool? ImportReturned, bool? ImportHadErrors,
    int? ReturnedModuleCount, bool? ModuleBound, bool? PowerShellDisposed,
    bool? RunspaceClosed, bool? RunspaceDisposed, int? ExceptionHResult)
{
    public bool ResourcesSettled =>
        (!PowerShellCreated || PowerShellDisposed == true) &&
        (!RunspaceCreated || RunspaceClosed == true && RunspaceDisposed == true);

    public bool IsImportBound => HostQualified && ManifestQualified && RunspaceCreated &&
        RunspaceOpened == true && PowerShellCreated && ImportAttempted && ImportReturned == true &&
        ImportHadErrors == false && ReturnedModuleCount == 1 && ModuleBound == true &&
        ResourcesSettled && ExceptionHResult == null;
}

/// <summary>Bounded scalar-only parser. It starts no process, query, import or operation.</summary>
internal static class DismDefaultImportEvidence
{
    internal const string Context = "dism-default-runspace-import";
    internal const string Profile = "CreateDefault-AutoloadAll-InboxOnly";
    internal const int MaximumCharacters = 4096;

    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
    {
        "schema", "context", "profile", "hostQualified", "manifestQualified", "runspaceCreated",
        "runspaceOpened", "powerShellCreated", "importAttempted", "importReturned", "importHadErrors",
        "returnedModuleCount", "moduleBound", "powerShellDisposed", "runspaceClosed", "runspaceDisposed",
        "exceptionHResult"
    };

    internal static bool TryRead(string? json, out DismDefaultImportFacts? facts, out string reason)
    {
        facts = null;
        reason = "size";
        if (json is not { Length: > 0 and <= MaximumCharacters }) return false;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 3 });
            var root = document.RootElement;
            reason = "schema";
            if (root.ValueKind != JsonValueKind.Object) return false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!Fields.Contains(property.Name) || !seen.Add(property.Name)) return false;
            if (seen.Count != Fields.Count || !root.GetProperty("schema").TryGetInt32(out int schema) || schema != 1 ||
                root.GetProperty("context").ValueKind != JsonValueKind.String || root.GetProperty("context").GetString() != Context ||
                root.GetProperty("profile").ValueKind != JsonValueKind.String || root.GetProperty("profile").GetString() != Profile)
                return false;

            if (!Boolean(root, "hostQualified", out bool host) || !Boolean(root, "manifestQualified", out bool manifest) ||
                !Boolean(root, "runspaceCreated", out bool runspace) || !NullableBoolean(root, "runspaceOpened", out bool? opened) ||
                !Boolean(root, "powerShellCreated", out bool powerShell) || !Boolean(root, "importAttempted", out bool attempted) ||
                !NullableBoolean(root, "importReturned", out bool? returned) || !NullableBoolean(root, "importHadErrors", out bool? errors) ||
                !NullableInteger(root, "returnedModuleCount", out int? count) || !NullableBoolean(root, "moduleBound", out bool? bound) ||
                !NullableBoolean(root, "powerShellDisposed", out bool? powerShellDisposed) ||
                !NullableBoolean(root, "runspaceClosed", out bool? closed) || !NullableBoolean(root, "runspaceDisposed", out bool? disposed) ||
                !NullableInteger(root, "exceptionHResult", out int? hresult) || count is < 0 or > 32)
                return false;

            reason = "facts";
            if (manifest && !host || runspace && !manifest || opened.HasValue != runspace ||
                powerShell && opened != true || attempted && !powerShell || returned.HasValue != attempted ||
                errors.HasValue != (returned == true) || count.HasValue != (returned == true) ||
                bound.HasValue && !(returned == true && errors == false && count == 1) ||
                powerShellDisposed.HasValue != powerShell || closed.HasValue != runspace || disposed.HasValue != runspace)
                return false;

            facts = new(host, manifest, runspace, opened, powerShell, attempted, returned, errors,
                count, bound, powerShellDisposed, closed, disposed, hresult);
            reason = "accepted";
            return true;
        }
        catch (JsonException) { facts = null; reason = "json"; return false; }
        catch (InvalidOperationException) { facts = null; reason = "schema"; return false; }
    }

    private static bool Boolean(JsonElement root, string name, out bool value)
    {
        var field = root.GetProperty(name);
        value = field.ValueKind == JsonValueKind.True;
        return field.ValueKind is JsonValueKind.True or JsonValueKind.False;
    }

    private static bool NullableBoolean(JsonElement root, string name, out bool? value)
    {
        var field = root.GetProperty(name);
        value = field.ValueKind == JsonValueKind.True ? true : field.ValueKind == JsonValueKind.False ? false : null;
        return field.ValueKind is JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null;
    }

    private static bool NullableInteger(JsonElement root, string name, out int? value)
    {
        var field = root.GetProperty(name);
        value = null;
        if (field.ValueKind == JsonValueKind.Null) return true;
        if (field.ValueKind != JsonValueKind.Number || !field.TryGetInt32(out int number)) return false;
        value = number;
        return true;
    }
}
