using System.Globalization;
using System.Text;

namespace MadClean.Core;

/// <summary>
/// Current visible row selection only. This establishes neither removal permission, metadata
/// authentication nor full inventory validity/coverage, including for a Partial snapshot.
/// </summary>
public static class ApplicationSelectionPolicy
{
    public static bool IsCurrentDesktop(CheckResult<InstalledApplication>? snapshot,
        IReadOnlyList<InstalledApplication>? loaded, InstalledApplication? selected, InstalledApplicationSearch? search)
        => IsCurrent(snapshot, loaded, selected, search, 20_000, row => row.Identity,
            row => search!.Matches(row));

    public static bool IsCurrentPackage(CheckResult<PackagedApplication>? snapshot,
        IReadOnlyList<PackagedApplication>? loaded, PackagedApplication? selected, InstalledApplicationSearch? search)
        => IsCurrent(snapshot, loaded, selected, search, PackagedApplicationParser.MaximumRecords, row => row.FullName,
            row => search!.MatchesText(row.Name, row.Publisher));

    private static bool IsCurrent<T>(CheckResult<T>? snapshot, IReadOnlyList<T>? loaded, T? selected,
        InstalledApplicationSearch? search, int maximum, Func<T, string?> identity, Func<T, bool> visible) where T : class
    {
        if (snapshot?.State is not (CheckState.Available or CheckState.Partial) || snapshot.Items == null ||
            loaded == null || selected == null || search?.IsValid != true || snapshot.Items.Count is 0 ||
            snapshot.Items.Count > maximum || loaded.Count != snapshot.Items.Count)
            return false;

        return ContainsUniqueCurrentRow(snapshot.Items, selected, identity) &&
            ContainsUniqueCurrentRow(loaded, selected, identity) && visible(selected);
    }

    private static bool ContainsUniqueCurrentRow<T>(IReadOnlyList<T> rows, T selected, Func<T, string?> identity) where T : class
    {
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int selectedReferences = 0;
        foreach (var row in rows)
        {
            if (row == null) return false;
            var key = identity(row);
            if (!SafeIdentity(key) || !identities.Add(key!)) return false;
            if (ReferenceEquals(row, selected)) selectedReferences++;
        }
        return selectedReferences == 1;
    }

    private static bool SafeIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 1024) return false;
        for (int index = 0; index < value.Length;)
        {
            if (!Rune.TryGetRuneAt(value, index, out var rune) || Rune.GetUnicodeCategory(rune) is
                UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                return false;
            index += rune.Utf16SequenceLength;
        }
        return true;
    }
}
