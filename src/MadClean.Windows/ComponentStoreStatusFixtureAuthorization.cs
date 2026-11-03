using System.Security.Principal;
using System.Text;
using MadClean.Core;
using Microsoft.Win32.SafeHandles;

namespace MadClean.Windows;

/// <summary>Kind-bound ownership proof for one explicit disposable-runner component-store check.</summary>
internal sealed class ComponentStoreStatusFixtureAuthorization : IDisposable
{
    private readonly object sync = new();
    private readonly Guid requestId;
    private readonly ComponentStoreCheckKind kind;
    private readonly List<SafeFileHandle> directories;
    private readonly FileStream marker;
    private int ownership; // 0 caller; 1 worker; 2 released.
    private bool transferred;
    private ComponentStoreStatusFixtureAuthorization(Guid id, ComponentStoreCheckKind kind, List<SafeFileHandle> directories, FileStream marker)
    { requestId = id; this.kind = kind; this.directories = directories; this.marker = marker; }
    internal ComponentStoreCheckKind Kind => kind;
    internal bool WasTransferred { get { lock (sync) return transferred; } }
    internal bool IsReleased { get { lock (sync) return ownership == 2; } }

    internal static ComponentStoreStatusFixtureAuthorization Open(string repository, string scopeDirectory, Guid id) =>
        Open(repository, scopeDirectory, id, ComponentStoreCheckKind.RecordedFlags);

    internal static ComponentStoreStatusFixtureAuthorization Open(string repository, string scopeDirectory, Guid id,
        ComponentStoreCheckKind kind)
    {
        (string job, string flag, string prefix) = kind switch
        {
            ComponentStoreCheckKind.RecordedFlags => ("windows-component-store-status", "MADCLEAN_COMPONENT_STORE_STATUS_VALIDATION", "component-store-status-fixtures-"),
            ComponentStoreCheckKind.ScanHealth => ("windows-component-store-scan", "MADCLEAN_COMPONENT_STORE_SCAN_VALIDATION", "component-store-scan-fixtures-"),
            _ => throw Invalid()
        };
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess || id == Guid.Empty ||
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted" ||
            Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") != "MadGenius9/MadClean" ||
            Environment.GetEnvironmentVariable("GITHUB_REF") != "refs/heads/feat/phase-one-foundation" ||
            Environment.GetEnvironmentVariable("GITHUB_EVENT_NAME") is not ("push" or "workflow_dispatch") ||
            Environment.GetEnvironmentVariable("GITHUB_JOB") != job ||
            Environment.GetEnvironmentVariable(flag) != "1") throw Invalid();
        string? source = Environment.GetEnvironmentVariable("GITHUB_SHA"), run = Environment.GetEnvironmentVariable("GITHUB_RUN_ID");
        if (source is not { Length: 40 } || !source.All(Uri.IsHexDigit) || string.IsNullOrEmpty(run) || run.Length > 32 ||
            !run.All(char.IsAsciiDigit) || !int.TryParse(Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"), out int attempt) || attempt <= 0) throw Invalid();
        using (var identity = WindowsIdentity.GetCurrent())
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) throw Invalid();
        string? workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        if (!Path.IsPathFullyQualified(repository) || !Path.IsPathFullyQualified(scopeDirectory) ||
            string.IsNullOrWhiteSpace(workspace) || !Path.IsPathFullyQualified(workspace)) throw Invalid();
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repository)), scope = Path.GetFullPath(scopeDirectory);
        if (root.Length < 4 || !char.IsAsciiLetter(root[0]) || root[1] != ':' || root[2] != '\\' ||
            !string.Equals(root, Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace)), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(scope, Path.Combine(root, "artifacts", prefix + id.ToString("N")), StringComparison.OrdinalIgnoreCase)) throw Invalid();
        var directories = SystemToolTrustNative.LockDirectoryChain(scope);
        FileStream? marker = null;
        try
        {
            string path = Path.Combine(scope, ".madclean-component-store-owner");
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
                if (marker.ReadByte() != -1 || Encoding.ASCII.GetString(bytes) != id.ToString("N")) throw Invalid();
            }
            finally { Array.Clear(bytes); }
            return new(id, kind, directories, marker);
        }
        catch
        {
            marker?.Dispose();
            foreach (var directory in directories.AsEnumerable().Reverse()) directory.Dispose();
            throw;
        }
    }
    internal bool TransferToWorker(Guid id) => TransferToWorker(id, ComponentStoreCheckKind.RecordedFlags);

    internal bool TransferToWorker(Guid id, ComponentStoreCheckKind requestedKind)
    {
        lock (sync)
        {
            if (ownership != 0 || id != requestId || requestedKind != kind) return false;
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
    private static IOException Invalid() => new("The exact disposable component-store status scope was not established.");
}
