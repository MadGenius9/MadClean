using System.Text.Json;

namespace MadClean.Core;

public sealed record CleanupExclusion(string Path, bool IncludeDescendants);
public sealed record CleanupExclusionSettings(IReadOnlyList<CleanupExclusion> Items, string? Error);

// This file contains only user-selected exclusion paths and scope flags, never file contents or credentials.
public sealed class CleanupExclusionStore(string path)
{
    private const int MaximumBytes = 262_144;
    private readonly object gate = new();
    private const string ReadError = "Cleanup exclusion settings are unreadable, invalid, or not a regular file on a fixed local drive. Use a local profile settings folder, review permissions, and restore or remove invalid settings manually; the file was not changed.";
    private const string SaveError = "Cleanup exclusions could not be saved safely. Use a settings folder on a fixed local drive, check permissions, and restore any invalid settings file before retrying; existing settings were not intentionally overwritten.";

    public CleanupExclusionSettings Load()
    {
        lock (gate)
        {
            try
            {
                var fullPath = SettingsPath();
                var contents = ReadExisting(fullPath);
                return contents == null ? new([], null) : new(Parse(contents), null);
            }
            catch (Exception ex) when (IsSettingsError(ex)) { return new([], ReadError); }
        }
    }

    public string? Save(IReadOnlyList<CleanupExclusion> items)
    {
        if (!CleanupExclusionRules.TryNormalize(items, out var normalized))
            return "Cleanup exclusions must contain at most 256 fully qualified paths without control characters. Review the selected paths before saving.";
        lock (gate)
        {
            string? temporaryPath = null;
            try
            {
                var fullPath = SettingsPath();
                var previous = ReadExisting(fullPath);
                if (previous != null) _ = Parse(previous); // Never replace malformed/unreadable settings blindly.
                var contents = JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, Items = normalized });
                if (contents.Length > MaximumBytes) return "Cleanup exclusion settings exceed the 256 KiB limit. Remove some exclusions before saving.";
                var directory = Path.GetDirectoryName(fullPath)!;
                Directory.CreateDirectory(directory);
                if (!EnsureNoReparseDestination(fullPath)) throw new IOException();
                temporaryPath = Path.Combine(directory, ".madclean-exclusions-" + Guid.NewGuid().ToString("N") + ".tmp");
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    stream.Write(contents);
                    stream.Flush(flushToDisk: true);
                }
                // Detect ordinary concurrent edits before the atomic replacement. Portable path checks cannot
                // eliminate an adversarial directory-replacement race; this is never a deletion authorization.
                var current = ReadExisting(fullPath);
                if ((previous == null) != (current == null) || (previous != null && !previous.AsSpan().SequenceEqual(current)))
                    return "Cleanup exclusion settings changed while saving. Reload settings before retrying; the newer file was preserved.";
                if (!EnsureNoReparseDestination(fullPath)) throw new IOException();
                if (previous == null) File.Move(temporaryPath, fullPath); // Does not overwrite a concurrently created file.
                else File.Replace(temporaryPath, fullPath, destinationBackupFileName: null);
                temporaryPath = null;
                return null;
            }
            catch (Exception ex) when (IsSettingsError(ex)) { return SaveError; }
            finally
            {
                if (temporaryPath != null)
                {
                    // Only remove this operation's private temporary settings file, never an inventory candidate.
                    try { File.Delete(temporaryPath); }
                    catch (Exception ex) when (IsSettingsError(ex)) { }
                }
            }
        }
    }

    private string SettingsPath()
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new InvalidDataException();
        var fullPath = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows())
        {
            if (fullPath.StartsWith(@"\\", StringComparison.Ordinal)) throw new InvalidDataException();
            var driveRoot = Path.GetPathRoot(fullPath);
            // Reject mapped network/removable/unknown drives before probing any ancestor or settings file.
            if (string.IsNullOrEmpty(driveRoot) || new DriveInfo(driveRoot).DriveType != DriveType.Fixed) throw new InvalidDataException();
        }
        if (string.IsNullOrEmpty(Path.GetFileName(fullPath))) throw new InvalidDataException();
        EnsureNoReparseDestination(fullPath);
        return fullPath;
    }

    private static byte[]? ReadExisting(string fullPath)
    {
        if (!EnsureNoReparseDestination(fullPath)) return null;
        if (!TryGetAttributes(fullPath, out _)) return null;
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!EnsureNoReparseDestination(fullPath)) throw new IOException();
        if (stream.Length > MaximumBytes) throw new InvalidDataException();
        using var contents = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            if (contents.Length + read > MaximumBytes) throw new InvalidDataException();
            contents.Write(buffer, 0, read);
        }
        return contents.ToArray();
    }

    private static CleanupExclusion[] Parse(byte[] contents)
    {
        using var document = JsonDocument.Parse(contents, new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        RequireFields(root, "Version", "Items");
        if (root.GetProperty("Version").GetInt32() != 1) throw new InvalidDataException();
        var list = root.GetProperty("Items");
        if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > CleanupExclusionRules.MaximumItems) throw new InvalidDataException();
        List<CleanupExclusion> items = [];
        foreach (var item in list.EnumerateArray())
        {
            RequireFields(item, "Path", "IncludeDescendants");
            var selectedPath = item.GetProperty("Path");
            if (selectedPath.ValueKind != JsonValueKind.String) throw new InvalidDataException();
            items.Add(new(selectedPath.GetString()!, item.GetProperty("IncludeDescendants").GetBoolean()));
        }
        if (!CleanupExclusionRules.TryNormalize(items, out var normalized)) throw new InvalidDataException();
        return normalized;
    }

    private static void RequireFields(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        var properties = value.EnumerateObject().Select(property => property.Name).ToArray();
        if (properties.Length != names.Length || properties.Distinct(StringComparer.Ordinal).Count() != names.Length ||
            properties.Any(property => !names.Contains(property, StringComparer.Ordinal))) throw new InvalidDataException();
    }

    private static bool EnsureNoReparseDestination(string fullPath)
    {
        // Construct the complete lexical chain without metadata access, then inspect root-to-leaf.
        // A missing ancestor means no descendant can exist at this snapshot; do not probe through it.
        var ancestors = new Stack<string>();
        for (var directory = Path.GetDirectoryName(fullPath); !string.IsNullOrEmpty(directory); directory = Path.GetDirectoryName(directory))
            ancestors.Push(directory);
        while (ancestors.Count > 0)
        {
            if (!TryGetAttributes(ancestors.Pop(), out var attributes)) return false;
            if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0) throw new IOException();
        }
        if (TryGetAttributes(fullPath, out var fileAttributes) &&
            (fileAttributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) throw new IOException();
        return true;
    }

    private static bool TryGetAttributes(string selectedPath, out FileAttributes attributes)
    {
        try { attributes = File.GetAttributes(selectedPath); return true; }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { attributes = default; return false; }
    }
    private static bool IsSettingsError(Exception ex) => ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Security.SecurityException
        or JsonException or ArgumentException or NotSupportedException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException;
}

internal sealed class CleanupExclusionRules(CleanupExclusion[] items)
{
    internal const int MaximumItems = 256;
    private const int MaximumPathCharacters = 32_767;

    internal static bool TryNormalize(IReadOnlyList<CleanupExclusion>? items, out CleanupExclusion[] normalized)
    {
        normalized = [];
        if (items == null || items.Count > MaximumItems) return false;
        List<CleanupExclusion> result = [];
        foreach (var item in items)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Path) || item.Path.Length > MaximumPathCharacters ||
                item.Path.Any(char.IsControl) || !Path.IsPathFullyQualified(item.Path)) return false;
            try { result.Add(new(Path.TrimEndingDirectorySeparator(Path.GetFullPath(item.Path)), item.IncludeDescendants)); }
            catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException or System.Security.SecurityException) { return false; }
        }
        normalized = result.ToArray();
        return true;
    }

    internal bool ExcludesDirectory(string path) => items.Any(item => item.IncludeDescendants && IsSameOrDescendant(path, item.Path));
    internal bool ExcludesFile(string path) => items.Any(item =>
        string.Equals(path, item.Path, StringComparison.OrdinalIgnoreCase) || (item.IncludeDescendants && IsSameOrDescendant(path, item.Path)));

    private static bool IsSameOrDescendant(string path, string folder)
    {
        if (string.Equals(path, folder, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
