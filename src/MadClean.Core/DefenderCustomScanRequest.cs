namespace MadClean.Core;

public sealed class DefenderCustomScanDraft
{
    private int used;

    private DefenderCustomScanDraft(string folder) => Folder = folder;

    public string Folder { get; }
    public const string Effects = "Defender applies your Windows security policy, which may remediate or quarantine files and use cloud protection. The request result cannot establish a completed scan, a clean result, or which detections came from this scan.";

    public static DefenderCustomScanDraft? Prepare(string? selectedFolder)
    {
        var normalized = ShortcutPathPolicy.NormalizeLocalPath(selectedFolder, allowDriveRoot: false);
        // Use the exact ordinary drive path selected in the picker. Do not rewrite aliases.
        return normalized != null && string.Equals(selectedFolder, normalized, StringComparison.OrdinalIgnoreCase)
            ? new(normalized) : null;
    }

    internal bool TryClaim() => Interlocked.Exchange(ref used, 1) == 0;
}

public enum DefenderScanSubmission
{
    Unavailable,
    NotSubmitted,
    Busy,
    Rejected,
    RequestReturnedWithoutCommandError,
    Unknown
}

public interface IDefenderCustomScanClient
{
    // An implementation must revalidate the selected directory and authenticate the inbox
    // command before submitting. Rejected requires proof of refusal before submission.
    Task<DefenderScanSubmission> SubmitAsync(DefenderCustomScanDraft draft);
}

/// <summary>Owns one request client until it actually returns. No production client is wired.</summary>
public sealed class DefenderCustomScanRequest(IDefenderCustomScanClient? client)
{
    private int pending;

    public async Task<DefenderScanSubmission> SubmitAsync(DefenderCustomScanDraft? current,
        DefenderCustomScanDraft? confirmed, CancellationToken token = default)
    {
        if (current == null || !ReferenceEquals(confirmed, current) || token.IsCancellationRequested)
            return DefenderScanSubmission.NotSubmitted;
        if (client == null)
            return DefenderScanSubmission.Unavailable;
        if (Interlocked.CompareExchange(ref pending, 1, 0) != 0)
            return DefenderScanSubmission.Busy;

        try
        {
            if (token.IsCancellationRequested || !current.TryClaim())
                return DefenderScanSubmission.NotSubmitted;

            // Admission commits this one request. Later caller cancellation provides no
            // evidence that the client or Defender engine stopped. Keep awaiting the client.
            var result = await client.SubmitAsync(current).ConfigureAwait(false);
            return result is DefenderScanSubmission.Rejected or DefenderScanSubmission.RequestReturnedWithoutCommandError
                ? result : DefenderScanSubmission.Unknown;
        }
        catch (Exception)
        {
            // Once we enter the client, even a thrown cancellation can be ambiguous.
            return DefenderScanSubmission.Unknown;
        }
        finally
        {
            Volatile.Write(ref pending, 0);
        }
    }
}
