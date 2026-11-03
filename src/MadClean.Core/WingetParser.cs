using System.Text.RegularExpressions;

namespace MadClean.Core;

public enum WingetParseIssue
{
    Accepted, EmptyOutput, HeaderNotRecognized, SeparatorNotRecognized, UnsupportedDisplayCharacters, ShortRow, IncompleteFields, NoPackageRows
}

public sealed record WingetParseObservation(CheckResult<AppUpdate> Result, WingetParseIssue Issue);

public static partial class WingetParser
{
    // WinGet's table is localized and not a stable JSON API. Reject unfamiliar/truncated data.
    // Even a successfully listed package without a usable Available value remains Unknown.
    public static CheckResult<AppUpdate> Parse(string output) => ParseObserved(output).Result;

    public static WingetParseObservation ParseObserved(string output)
    {
        bool recognizedHeader = false;
        var lines = output.Replace("\r", "").Split('\n');
        for (int i = 0; i + 1 < lines.Length; i++)
        {
            var header = lines[i];
            if (header.Any(c => char.IsControl(c))) continue;
            var columns = Columns().Matches(header).Cast<Match>().ToArray();
            var names = columns.Select(m => m.Value).ToArray();
            // Official WinGet TableOutput uses one space when a column is exactly as wide
            // as its header. Headers are single words; do not assume two-space separators.
            if (!KnownLayout(names)) continue;
            recognizedHeader = true;
            if (!Separator().IsMatch(lines[i + 1].Trim())) continue;
            List<AppUpdate> apps = [];
            for (int row = i + 2; row < lines.Length; row++)
            {
                var line = lines[row];
                if (string.IsNullOrWhiteSpace(line)) continue;
                // WinGet aligns by terminal display width rather than UTF-16 offsets. ASCII
                // and its one-cell ellipsis are unambiguous here; unfamiliar Unicode/control
                // output must not shift identifiers/versions into plausible update results.
                if (line.Any(c => c < ' ' || (c > '~' && c != '…')))
                    return Unknown(WingetParseIssue.UnsupportedDisplayCharacters, "WinGet returned characters with unsupported display width; results were not trusted.");
                if (line.Length < columns[2].Index + 1)
                    return Unknown(WingetParseIssue.ShortRow, "WinGet returned an unrecognized row; results were not trusted.");
                string Cell(int index)
                {
                    int start = columns[index].Index;
                    if (start >= line.Length) return "";
                    int end = index + 1 < columns.Length ? Math.Min(columns[index + 1].Index, line.Length) : line.Length;
                    return line[start..end].Trim();
                }
                string name = Cell(0), id = Cell(1), version = Cell(2);
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id) || id.Any(char.IsWhiteSpace) ||
                    IsTruncated(id) || string.IsNullOrWhiteSpace(version))
                    return Unknown(WingetParseIssue.IncompleteFields, "WinGet returned incomplete identifiers; results were not trusted.");
                int availableIndex = Array.IndexOf(names, "Available"), sourceIndex = Array.IndexOf(names, "Source");
                string available = availableIndex < 0 ? "" : Cell(availableIndex);
                var status = IsUsableVersion(version) && IsUsableVersion(available) ? CheckState.Available : CheckState.Unknown;
                apps.Add(new(name, id, version, available.Length == 0 ? "Unknown" : available,
                    sourceIndex < 0 ? "winget" : Cell(sourceIndex), status));
            }
            if (apps.Count == 0)
                return Unknown(WingetParseIssue.NoPackageRows, "WinGet returned a table without package rows; no inventory determination was made.");
            return new(new(CheckState.Available, "WinGet inventory returned. Unknown means no verified update determination; coverage is limited to matched packages.", apps), WingetParseIssue.Accepted);
        }
        return Unknown(string.IsNullOrWhiteSpace(output) ? WingetParseIssue.EmptyOutput
            : recognizedHeader ? WingetParseIssue.SeparatorNotRecognized : WingetParseIssue.HeaderNotRecognized,
            "WinGet output was empty, localized, or unrecognized. No up-to-date claim can be made.");
    }

    private static bool KnownLayout(string[] names) =>
        names.SequenceEqual(["Name", "Id", "Version"]) ||
        names.SequenceEqual(["Name", "Id", "Version", "Source"]) ||
        names.SequenceEqual(["Name", "Id", "Version", "Available"]) ||
        names.SequenceEqual(["Name", "Id", "Version", "Available", "Source"]);

    private static bool IsTruncated(string value) => value.Contains('…') || value.Contains("...", StringComparison.Ordinal);
    private static bool IsUsableVersion(string value) => !string.IsNullOrWhiteSpace(value) &&
        !value.Equals("Unknown", StringComparison.OrdinalIgnoreCase) && !IsTruncated(value);
    private static WingetParseObservation Unknown(WingetParseIssue issue, string message) =>
        new(CheckResult<AppUpdate>.Empty(CheckState.Unknown, message), issue);

    [GeneratedRegex(@"[^ ]+")]
    private static partial Regex Columns();
    [GeneratedRegex(@"^-{3,}$")]
    private static partial Regex Separator();
}
