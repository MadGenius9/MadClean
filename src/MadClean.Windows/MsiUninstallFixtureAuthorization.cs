using System.Text;
using MadClean.Core;
using Microsoft.Win32.SafeHandles;

namespace MadClean.Windows;

/// <summary>
/// Development-only ownership proof for one fresh inert MSI qualification product.
/// This cannot enable the public production dispatcher or authorize another ProductCode.
/// </summary>
internal sealed class MsiUninstallFixtureAuthorization : IDisposable
{
    private readonly object sync = new();
    private readonly List<SafeFileHandle> directories;
    private readonly List<FileStream> files;
    private int ownership; // 0 caller; 1 worker; 2 released. Caller never releases a worker's proof.
    private readonly string productCode;

    private MsiUninstallFixtureAuthorization(Guid id, List<SafeFileHandle> directories, List<FileStream> files)
    { productCode = id.ToString("B").ToUpperInvariant(); this.directories = directories; this.files = files; }

    internal static MsiUninstallFixtureAuthorization Open(string repository, string scopeDirectory, Guid id)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess || id == Guid.Empty ||
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted" ||
            Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") != "MadGenius9/MadClean" ||
            Environment.GetEnvironmentVariable("MADCLEAN_MSI_REQUEST_VALIDATION") != "1") throw Invalid();
        string? sha = Environment.GetEnvironmentVariable("GITHUB_SHA"), run = Environment.GetEnvironmentVariable("GITHUB_RUN_ID");
        if (sha is not { Length: 40 } || !sha.All(Uri.IsHexDigit) || string.IsNullOrEmpty(run) || run.Length > 32 || !run.All(char.IsAsciiDigit)) throw Invalid();
        string root = Path.GetFullPath(repository), scope = Path.GetFullPath(scopeDirectory);
        string? workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        if (!Path.IsPathFullyQualified(repository) || !Path.IsPathFullyQualified(scopeDirectory) ||
            root.StartsWith(@"\\", StringComparison.Ordinal) || workspace == null ||
            !string.Equals(Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace)), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(scope, Path.Combine(root, "artifacts", "msi-request-validation-" + id.ToString("N")), StringComparison.OrdinalIgnoreCase)) throw Invalid();
        var directories = SystemToolTrustNative.LockDirectoryChain(Path.Combine(scope, "source"));
        List<FileStream> files = [];
        try
        {
            FileStream Hold(string path, long bound)
            {
                var handle = SystemToolTrustNative.OpenFile(path);
                try
                {
                    SystemToolTrustNative.ValidateAttributes(handle, directory: false);
                    SystemToolTrustNative.ValidateFinalPath(handle, path);
                    var stream = new FileStream(handle, FileAccess.Read, 4096, false);
                    files.Add(stream);
                    if (stream.Length is <= 0 || stream.Length > bound) throw Invalid();
                    return stream;
                }
                catch { if (!files.Any(stream => ReferenceEquals(stream.SafeFileHandle, handle))) handle.Dispose(); throw; }
            }
            var marker = Hold(Path.Combine(scope, ".madclean-msi-owner"), 32);
            if (marker.Length != 32) throw Invalid();
            byte[] bytes = new byte[33];
            try
            {
                marker.ReadExactly(bytes.AsSpan(0, 32));
                if (marker.ReadByte() != -1 || Encoding.ASCII.GetString(bytes, 0, 32) != id.ToString("N")) throw Invalid();
            }
            finally { Array.Clear(bytes); }
            _ = Hold(Path.Combine(scope, "source", "fixture.msi"), 2_097_152);
            _ = Hold(Path.Combine(scope, "source", "fixture.txt"), 4096);
            // Do not retain a target-directory lease: the real owned uninstall must be able
            // to remove its payload/directory. Source, scope, marker and ancestors stay held.
            return new(id, directories, files);
        }
        catch
        {
            foreach (var file in files.AsEnumerable().Reverse()) file.Dispose();
            foreach (var directory in directories.AsEnumerable().Reverse()) directory.Dispose();
            throw;
        }
    }

    internal bool TransferToWorker(MsiUninstallRequestIntent intent)
    {
        lock (sync)
        {
            if (ownership != 0 || intent.ProductCode != productCode || intent.Context != MsiRegistrationContext.UserUnmanaged ||
                intent.Name != "MadClean MSI qualification fixture" || intent.Publisher != "MadClean validation" || intent.Version != "1.0.0") return false;
            ownership = 1; return true;
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
        foreach (var file in files.AsEnumerable().Reverse()) file.Dispose();
        foreach (var directory in directories.AsEnumerable().Reverse()) directory.Dispose();
    }
    private static IOException Invalid() => new("The exact owned Windows Installer qualification scope was not established.");
}
