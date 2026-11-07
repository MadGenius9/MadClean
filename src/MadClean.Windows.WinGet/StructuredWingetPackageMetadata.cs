using System.Runtime.InteropServices;
using MadClean.Core;
using Microsoft.Management.Deployment;

namespace MadClean.Windows.WinGet;

public sealed partial class StructuredWingetInventory
{
    /// <summary>
    /// Explicit online manifest-metadata review for one exact offered version. Uses the same
    /// admission and retained MTA worker as discovery; it never downloads or installs a package.
    /// </summary>
    public async Task<CheckResult<AppPackageMetadataReport>> ReadPackageMetadataAsync(
        AppPackageMetadataRequest request, CancellationToken token)
    {
        if (token.IsCancellationRequested) return MetadataEmpty(CheckState.Cancelled, "Package metadata review was cancelled before activation.");
        if (!AppPackageMetadataPolicy.IsRequestConsistent(request, DateTimeOffset.UtcNow))
            return MetadataEmpty(CheckState.Unknown, "The selected-package request is missing, inconsistent or expired. Refresh updates and select the offered version again.");
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            return MetadataEmpty(CheckState.Unsupported, "Selected catalog metadata requires the supported Windows WinGet API; this host is unverified.");

        QueryOwner owner;
        lock (Gate)
        {
            if (pending is not null)
                return MetadataEmpty(CheckState.Unknown, "A WinGet operation still owns its worker. Wait for actual completion before reviewing metadata.");
            owner = new QueryOwner();
            pending = owner;
        }
        using var registration = token.Register(owner.RequestStop);
        try
        {
            var thread = new Thread(() => RunMetadata(owner, request))
                { IsBackground = true, Name = "MadClean selected WinGet catalog metadata" };
            thread.Start();
        }
        catch (Exception error)
        {
            lock (Gate) { if (ReferenceEquals(pending, owner)) pending = null; }
            owner.MetadataFinished.TrySetResult(owner.MetadataFailure(error));
        }
        try { return await owner.MetadataFinished.Task.WaitAsync(CallerDeadline, token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            owner.RequestStop();
            return MetadataEmpty(CheckState.Cancelled, "Caller stopped waiting for catalog metadata. Any pending WinGet call remains owned until its actual return; no server operation was killed.");
        }
        catch (TimeoutException)
        {
            owner.RequestStop();
            return MetadataEmpty(CheckState.Unknown, "The catalog-metadata wait limit elapsed. Any pending WinGet call remains owned until its actual return; retry is blocked while it is pending.");
        }
    }

    private static void RunMetadata(QueryOwner owner, AppPackageMetadataRequest request)
    {
        CheckResult<AppPackageMetadataReport> result;
        bool initialized = false;
        try
        {
            owner.CheckStop();
            Marshal.ThrowExceptionForHR(Native.CoInitializeEx(IntPtr.Zero, 0));
            initialized = true;
            result = owner.QueryPackageMetadata(request);
        }
        catch (OperationCanceledException)
        { result = MetadataEmpty(CheckState.Cancelled, "Catalog metadata observation was interrupted; no late result is accepted as current."); }
        catch (Exception error) { result = owner.MetadataFailure(error); }
        finally
        {
            // The synchronous manifest getter can outlive the caller's wait. All wrappers
            // stay rooted on this MTA owner until it returns; never force-release or cancel it.
            GC.KeepAlive(owner.Retained);
            if (initialized) Native.CoUninitialize();
        }
        lock (Gate) { if (ReferenceEquals(pending, owner)) pending = null; }
        owner.MetadataFinished.TrySetResult(result);
    }

    private static CheckResult<AppPackageMetadataReport> MetadataEmpty(CheckState state, string message) =>
        CheckResult<AppPackageMetadataReport>.Empty(state, message + " No package download, installation, source agreement acceptance or restart was requested.");

    private sealed partial class QueryOwner
    {
        internal readonly TaskCompletionSource<CheckResult<AppPackageMetadataReport>> MetadataFinished =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal CheckResult<AppPackageMetadataReport> MetadataFailure(Exception error) => MetadataEmpty(
            Failure(error).Result.State,
            $"Catalog metadata could not be established (code 0x{error.HResult:X8}). Check App Installer API support, source access and policy, then refresh updates before retrying.");

        internal CheckResult<AppPackageMetadataReport> QueryPackageMetadata(AppPackageMetadataRequest request)
        {
            CheckStop();
            // Request expiry is a dispatch check, not a fabricated native-getter timeout.
            if (!AppPackageMetadataPolicy.IsRequestConsistent(request, DateTimeOffset.UtcNow))
                return MetadataEmpty(CheckState.Unknown, "The selected-package request expired before activation. Refresh updates and select the offered version again.");
            var catalog = ConnectInstalledCatalog();
            if (catalog == null) return MetadataEmpty(CheckState.Failed, "The configured WinGet source did not connect.");
            CheckStop();
            var options = Keep(FixedFactory.Find());
            var filter = Keep(FixedFactory.MatchFilter());
            filter.Field = PackageMatchField.Id;
            filter.Option = PackageFieldMatchOption.Equals;
            filter.Value = request.PackageId;
            options.Selectors.Add(filter);
            options.ResultLimit = 2;
            // A single selector establishes an exact ID request; no empty-selector or
            // catalog-defined search semantics are assumed for this review.
            if (options.Selectors.Count != 1 || options.Filters.Count != 0 || options.ResultLimit != 2 ||
                filter.Field != PackageMatchField.Id || filter.Option != PackageFieldMatchOption.Equals ||
                !string.Equals(filter.Value, request.PackageId, StringComparison.Ordinal))
                return MetadataEmpty(CheckState.Unknown, "Exact package-query controls were not established.");
            CheckStop();
            var operation = Keep(catalog.FindPackagesAsync(options));
            var found = Keep(operation.AsTask().GetAwaiter().GetResult());
            CheckStop();
            if (found.Status != FindPackagesResultStatus.Ok)
                return MetadataEmpty(CheckState.Failed, "The exact package lookup did not succeed.");
            if (Read(() => found.WasLimitExceeded) != false)
                return MetadataEmpty(CheckState.Unknown, "Exact package lookup coverage is incomplete or unknown; no version was substituted.");
            var matches = Keep(found.Matches);
            if (matches.Count != 1)
                return MetadataEmpty(CheckState.Unknown, "The selected package is missing or ambiguous in the fresh lookup. Refresh updates and select it again.");
            var match = Keep(matches[0]);
            var package = Keep(match.CatalogPackage);
            var installed = ReadObject(() => package.InstalledVersion);
            var offered = ReadObject(() => package.DefaultInstallVersion);
            if (installed == null || offered == null || Read(() => package.IsUpdateAvailable) != true ||
                !string.Equals(Text(() => package.Id), request.PackageId, StringComparison.Ordinal) ||
                !string.Equals(Text(() => installed.Version), request.InstalledVersion, StringComparison.Ordinal) ||
                !string.Equals(Text(() => offered.Version), request.OfferedVersion, StringComparison.Ordinal))
                return MetadataEmpty(CheckState.Unknown, "The selected package or installed/offered version changed. Refresh updates; no different version's metadata was accepted.");
            var offeredCatalog = ReadObject(() => offered.PackageCatalog);
            var offeredInfo = offeredCatalog == null ? null : ReadObject(() => offeredCatalog.Info);
            if (offeredInfo == null || !StructuredWingetPolicy.MatchesSource(Facts(offeredInfo, null, null, null)))
                return MetadataEmpty(CheckState.Unknown, "The offered version's exact configured source was not established.");
            CheckStop();
            // Contract 6: this synchronous getter can fetch/cache manifest metadata.
            // Never use GetMetadata, whose installed fields include raw uninstall commands.
            var metadata = Keep(offered.GetCatalogPackageMetadata()
                ?? throw new NotSupportedException("Catalog metadata is unavailable."));
            CheckStop();
            string? publisher = CatalogText(() => metadata.Publisher);
            string? description = CatalogText(() => metadata.ShortDescription);
            if (string.IsNullOrWhiteSpace(description)) description = CatalogText(() => metadata.Description);
            string? license = CatalogText(() => metadata.License);
            string? notes = CatalogText(() => metadata.ReleaseNotes);
            CheckStop();
            return AppPackageMetadataPolicy.Project(request, publisher, description, license, notes, DateTimeOffset.UtcNow);
        }

        private string? CatalogText(Func<string> read)
        {
            CheckStop();
            try { return read(); }
            catch (Exception) { PropertyFailure(); return null; }
        }
    }
}
