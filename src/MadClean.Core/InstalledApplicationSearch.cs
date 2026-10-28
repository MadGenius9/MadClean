using System.Buffers;
using System.Globalization;
using System.Text;

namespace MadClean.Core;

/// <summary>Bounded literal Name/Publisher filtering only; never an inventory or action decision.</summary>
public sealed class InstalledApplicationSearch
{
    public const int MaximumQueryCharacters = 256;
    private readonly string query;
    private InstalledApplicationSearch(bool isValid, string query)
    { IsValid = isValid; this.query = query; }

    public bool IsValid { get; }
    // Invalid input must not masquerade as an empty query that displays every registration.
    public bool IsEmpty => IsValid && query.Length == 0;

    public static InstalledApplicationSearch Create(string? input)
    {
        if (input == null) return new(true, "");
        if (input.Length > MaximumQueryCharacters) return new(false, "");
        var remaining = input.AsSpan();
        // Validate before Trim: a query consisting only of a newline, bidi control or malformed
        // UTF-16 is invalid input, not an innocent empty filter. No normalization or expansion.
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out var rune, out int consumed) != OperationStatus.Done ||
                Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format or
                    UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                return new(false, "");
            remaining = remaining[consumed..];
        }
        return new(true, input.Trim());
    }

    public bool Matches(InstalledApplication? app)
        => app != null && MatchesText(app.Name, app.Publisher);

    public bool MatchesText(string? name, string? publisher)
    {
        if (!IsValid) return false;
        if (IsEmpty) return true;
        return name?.Contains(query, StringComparison.OrdinalIgnoreCase) == true ||
            publisher?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
    }
}
