using System.Text;

namespace MadClean.Core;

/// <summary>Transport values for a fixed request host. No value establishes an engine result.</summary>
public static class DefenderScanHostProtocol
{
    public static string EncodeFolderInput(DefenderCustomScanDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return Convert.ToBase64String(Encoding.Unicode.GetBytes(draft.Folder));
    }

    public static DefenderScanSubmission Interpret(ProcessResult client)
    {
        if (client.ExitCode != 0 || client.Output.Length > 64)
            return DefenderScanSubmission.Unknown;

        var marker = client.Output.EndsWith("\r\n", StringComparison.Ordinal) ? client.Output[..^2]
            : client.Output.EndsWith('\n') ? client.Output[..^1] : client.Output;
        return marker switch
        {
            "MadClean.DefenderRequest/1:refused" => DefenderScanSubmission.Rejected,
            "MadClean.DefenderRequest/1:returned" => DefenderScanSubmission.RequestReturnedWithoutCommandError,
            _ => DefenderScanSubmission.Unknown
        };
    }
}
