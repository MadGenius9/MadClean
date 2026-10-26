using System.Text.RegularExpressions;

namespace MadClean.Core;

public static partial class WingetParser
{
    // WinGet's table is localized and not a stable JSON API. Reject unfamiliar/truncated data.
    // Even a successfully listed package without an Available value remains Unknown.
    public static CheckResult<AppUpdate> Parse(string output)
    {
        var lines = output.Replace("\r", "").Split('\n');
        for (int i = 0; i + 1 < lines.Length; i++)
        {
            var header = lines[i];
            var columns = Columns().Matches(header).Cast<Match>().ToArray();
            var names = columns.Select(m => m.Value.Trim()).ToArray();
            if (names.Length < 3 || names[0] != "Name" || names[1] != "Id" || names[2] != "Version") continue;
            if (names.Skip(3).Any(n => n is not ("Available" or "Source"))) continue;
            if (!Separator().IsMatch(lines[i + 1].Trim())) continue;
            List<AppUpdate> apps = [];
            for (int row = i + 2; row < lines.Length; row++)
            {
                var line = lines[row];
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.Length < columns[2].Index + 1)
                    return CheckResult<AppUpdate>.Empty(CheckState.Unknown, "WinGet returned an unrecognized row; results were not trusted.");
                string Cell(int index)
                {
                    int start = columns[index].Index;
                    if (start >= line.Length) return "";
                    int end = index + 1 < columns.Length ? Math.Min(columns[index + 1].Index, line.Length) : line.Length;
                    return line[start..end].Trim();
                }
                string name = Cell(0), id = Cell(1), version = Cell(2);
                if (string.IsNullOrWhiteSpace(id) || id.Contains(' ') || id.Contains('…') || id.Contains("...") || string.IsNullOrWhiteSpace(version))
                    return CheckResult<AppUpdate>.Empty(CheckState.Unknown, "WinGet returned incomplete identifiers; results were not trusted.");
                int availableIndex = Array.IndexOf(names, "Available"), sourceIndex = Array.IndexOf(names, "Source");
                string available = availableIndex < 0 ? "" : Cell(availableIndex);
                var status = available.Length == 0 || available.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ? CheckState.Unknown : CheckState.Available;
                apps.Add(new(name, id, version, available.Length == 0 ? "Unknown" : available, sourceIndex < 0 ? "winget" : Cell(sourceIndex), status));
            }
            return new(CheckState.Available, "WinGet inventory returned. Unknown means no verified update determination; coverage is limited to matched packages.", apps);
        }
        return CheckResult<AppUpdate>.Empty(CheckState.Unknown, "WinGet output was empty, localized, or unrecognized. No up-to-date claim can be made.");
    }
    [GeneratedRegex(@"\S(?:.*?\S)?(?= {2,}|$)")]
    private static partial Regex Columns();
    [GeneratedRegex(@"^-{3,}$")]
    private static partial Regex Separator();
}
