using System.Globalization;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MadClean.Windows;

/// <summary>Held request-bound proof for one explicit disposable-runner SFC client observation.</summary>
internal sealed class SfcClientFixtureAuthorization : IDisposable
{
    private readonly object sync = new();
    private readonly Guid requestId;
    private readonly List<SafeFileHandle> directories;
    private readonly FileStream marker;
    private int ownership; // 0 caller; 1 worker; 2 released.
    private bool transferred;

    private SfcClientFixtureAuthorization(Guid id, List<SafeFileHandle> directories, FileStream marker)
    { requestId = id; this.directories = directories; this.marker = marker; }

    internal bool WasTransferred { get { lock (sync) return transferred; } }
    internal bool IsReleased { get { lock (sync) return ownership == 2; } }
    internal bool MatchesRequest(Guid id) => id != Guid.Empty && id == requestId;

    internal static SfcClientFixtureAuthorization Open(string repository, string scopeDirectory, Guid requestId)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess || requestId == Guid.Empty ||
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted" ||
            Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") != "MadGenius9/MadClean" ||
            Environment.GetEnvironmentVariable("GITHUB_REF") != "refs/heads/feat/phase-one-foundation" ||
            Environment.GetEnvironmentVariable("GITHUB_EVENT_NAME") is not ("push" or "workflow_dispatch") ||
            Environment.GetEnvironmentVariable("GITHUB_JOB") != "windows-sfc-client" ||
            Environment.GetEnvironmentVariable("MADCLEAN_WINDOWS_VALIDATION") != "1" ||
            Environment.GetEnvironmentVariable("MADCLEAN_SFC_CLIENT_VALIDATION") != "1") throw Invalid();
        string? source = Environment.GetEnvironmentVariable("GITHUB_SHA"), run = Environment.GetEnvironmentVariable("GITHUB_RUN_ID"),
            attemptText = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT");
        if (source is not { Length: 40 } || !source.All(Uri.IsHexDigit) || string.IsNullOrEmpty(run) || run.Length > 32 ||
            !run.All(char.IsAsciiDigit) || string.IsNullOrEmpty(attemptText) || attemptText.Length > 10 || !attemptText.All(char.IsAsciiDigit) ||
            !int.TryParse(attemptText, NumberStyles.None, CultureInfo.InvariantCulture, out int attempt) || attempt <= 0) throw Invalid();
        using (var impersonated = WindowsIdentity.GetCurrent(ifImpersonating: true))
            if (impersonated != null) throw Invalid();
        using (var identity = WindowsIdentity.GetCurrent())
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) throw Invalid();
        if (SystemToolTrustNative.HasCleanupUncertainty || SystemToolCatalogObservation.HasCleanupUncertainty) throw Invalid();
        string? workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        if (!Path.IsPathFullyQualified(repository) || !Path.IsPathFullyQualified(scopeDirectory) ||
            string.IsNullOrWhiteSpace(workspace) || !Path.IsPathFullyQualified(workspace)) throw Invalid();
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repository)), scope = Path.GetFullPath(scopeDirectory);
        if (root.Length < 4 || !char.IsAsciiLetter(root[0]) || root[1] != ':' || root[2] != '\\' ||
            !string.Equals(root, Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace)), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(scope, Path.Combine(root, "artifacts", "sfc-client-fixtures-" + requestId.ToString("N")), StringComparison.OrdinalIgnoreCase)) throw Invalid();
        var directories = SystemToolTrustNative.LockDirectoryChain(scope);
        FileStream? marker = null;
        try
        {
            string path = Path.Combine(scope, ".madclean-sfc-client-owner");
            var handle = SystemToolTrustNative.OpenFile(path);
            try
            {
                SystemToolTrustNative.ValidateAttributes(handle, directory: false);
                SystemToolTrustNative.ValidateFinalPath(handle, path);
                marker = new FileStream(handle, FileAccess.Read, 4096, false);
            }
            catch { handle.Dispose(); throw; }
            if (marker.Length != 32) throw Invalid();
            byte[] bytes = new byte[32];
            try
            {
                marker.ReadExactly(bytes);
                if (marker.ReadByte() != -1 || Encoding.ASCII.GetString(bytes) != requestId.ToString("N")) throw Invalid();
            }
            finally { Array.Clear(bytes); }
            return new(requestId, directories, marker);
        }
        catch
        {
            marker?.Dispose();
            foreach (var directory in directories.AsEnumerable().Reverse()) directory.Dispose();
            throw;
        }
    }

    internal bool TransferToWorker(Guid id)
    {
        lock (sync)
        {
            if (ownership != 0 || !MatchesRequest(id)) return false;
            ownership = 1; transferred = true; return true;
        }
    }

    internal void ReleaseFromWorker()
    {
        lock (sync)
        {
            if (ownership != 1) return;
            Release(); ownership = 2;
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (ownership != 0) return;
            Release(); ownership = 2;
        }
    }

    private void Release()
    {
        marker.Dispose();
        foreach (var directory in directories.AsEnumerable().Reverse()) directory.Dispose();
    }
    private static IOException Invalid() => new("The exact disposable SFC client scope was not established.");
}
