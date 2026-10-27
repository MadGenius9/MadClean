using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;

namespace MadClean.WindowsSmoke;

internal sealed record SmokeCheck(string Name, string State, string Detail);
internal sealed record CapturedImage(string File, int Width, int Height, string Sha256);

internal sealed class SmokeReport(string outputDirectory)
{
    private readonly ConcurrentQueue<SmokeCheck> checks = new();
    private readonly ConcurrentQueue<CapturedImage> images = new();
    private readonly object writeGate = new();
    private readonly DateTimeOffset started = DateTimeOffset.UtcNow;
    public bool Failed => checks.Any(check => check.State == "failed");
    public bool Inconclusive => checks.Any(check => check.State == "inconclusive");
    public int ExitCode => Failed ? 1 : Inconclusive ? 2 : 0;
    public void Pass(string name, string detail) => Add(name, "passed", detail);
    public void Fail(string name, string detail) => Add(name, "failed", detail);
    public void Unknown(string name, string detail) => Add(name, "inconclusive", detail);
    private void Add(string name, string state, string detail) => checks.Enqueue(new(name, state, detail.Length <= 1000 ? detail : detail[..1000]));
    public void Image(CapturedImage image) => images.Enqueue(image);
    public void Save()
    {
        lock (writeGate)
        {
            string? supplied = Environment.GetEnvironmentVariable("GITHUB_SHA");
            string sourceCommit = supplied is { Length: 40 } && supplied.All(Uri.IsHexDigit) ? supplied : "Unknown";
            string? suppliedRun = Environment.GetEnvironmentVariable("GITHUB_RUN_ID");
            string runId = suppliedRun is { Length: > 0 and <= 32 } && suppliedRun.All(character => character is >= '0' and <= '9') ? suppliedRun : "Unknown";
            string? suppliedAttempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT");
            int runAttempt = int.TryParse(suppliedAttempt, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int attempt) && attempt > 0 ? attempt : 0;
            var snapshot = new
            {
                formatVersion = 1,
                sourceCommit,
                runId,
                runAttempt,
                startedUtc = started,
                completedUtc = DateTimeOffset.UtcNow,
                passed = !Failed && !Inconclusive,
                state = Failed ? "failed" : Inconclusive ? "inconclusive" : "passed",
                checks = checks.ToArray(),
                screenshots = images.ToArray(),
                limits = "Development-only disposable Windows evidence. Client-content WPF renders at 96 DPI, not DWM/title-bar/real OS-DPI or exhaustive keyboard/provider validation. No healthy/up-to-date/repair verdict."
            };
            string temporary = Path.Combine(outputDirectory, "report.json.new");
            File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            File.Move(temporary, Path.Combine(outputDirectory, "report.json"), overwrite: true);
        }
    }
}
