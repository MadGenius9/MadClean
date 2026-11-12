using System.Runtime.InteropServices;
using MadClean.Core;
using Microsoft.Management.Deployment;

namespace MadClean.Windows.WinGet;

public sealed partial class StructuredWingetInventory
{
    /// <summary>Manifest-only requirements review under an explicit User/Interactive profile; never an installation request.</summary>
    public async Task<CheckResult<AppInstallerReviewReport>> ReviewInstallerAsync(
        AppPackageMetadataRequest request, CancellationToken token)
    {
        if (token.IsCancellationRequested) return InstallerReviewEmpty(CheckState.Cancelled, "Installer requirements review was cancelled before activation.");
        if (!AppPackageMetadataPolicy.IsRequestConsistent(request, DateTimeOffset.UtcNow))
            return InstallerReviewEmpty(CheckState.Unknown, "The selected-package request is inconsistent or expired. Refresh updates and select the offered version again.");
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            return InstallerReviewEmpty(CheckState.Unsupported, "Selected installer requirements require the supported Windows WinGet API; this host is unverified.");
        QueryOwner owner;
        lock (Gate)
        {
            if (pending is not null)
                return InstallerReviewEmpty(CheckState.Unknown, "A WinGet operation still owns its worker. Wait for actual completion before reviewing requirements.");
            owner = new QueryOwner();
            pending = owner;
        }
        using var registration = token.Register(owner.RequestStop);
        try
        {
            var thread = new Thread(() => RunInstallerReview(owner, request))
                { IsBackground = true, Name = "MadClean selected WinGet installer requirements" };
            thread.Start();
        }
        catch (Exception error)
        {
            lock (Gate) { if (ReferenceEquals(pending, owner)) pending = null; }
            owner.InstallerReviewFinished.TrySetResult(owner.InstallerReviewFailure(error));
        }
        try { return await owner.InstallerReviewFinished.Task.WaitAsync(CallerDeadline, token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            owner.RequestStop();
            return InstallerReviewEmpty(CheckState.Cancelled, "Caller stopped waiting for installer requirements. Any pending WinGet call remains owned until its actual return; no server operation was killed.");
        }
        catch (TimeoutException)
        {
            owner.RequestStop();
            return InstallerReviewEmpty(CheckState.Unknown, "The installer-requirements wait limit elapsed. Any pending WinGet call remains owned until its actual return; retry is blocked while it is pending.");
        }
    }

    private static void RunInstallerReview(QueryOwner owner, AppPackageMetadataRequest request)
    {
        CheckResult<AppInstallerReviewReport> result;
        bool initialized = false;
        try
        {
            owner.CheckStop();
            Marshal.ThrowExceptionForHR(Native.CoInitializeEx(IntPtr.Zero, 0));
            initialized = true;
            result = owner.QueryInstallerReview(request);
        }
        catch (OperationCanceledException)
        { result = InstallerReviewEmpty(CheckState.Cancelled, "Installer requirements observation was interrupted; no late result is accepted as current."); }
        catch (Exception error) { result = owner.InstallerReviewFailure(error); }
        finally
        {
            // The manifest comparator may outlive the caller's wait. Keep every wrapper on
            // the same MTA owner through actual return, using normal runtime-managed lifetime.
            GC.KeepAlive(owner.Retained);
            if (initialized) Native.CoUninitialize();
        }
        lock (Gate) { if (ReferenceEquals(pending, owner)) pending = null; }
        owner.InstallerReviewFinished.TrySetResult(result);
    }

    private static CheckResult<AppInstallerReviewReport> InstallerReviewEmpty(CheckState state, string message) =>
        CheckResult<AppInstallerReviewReport>.Empty(state, message +
            " This is a manifest-only User/Interactive requirements review. Installed scope is unknown; no installer binary, package download, installation, agreement acceptance or restart was requested.");

    private sealed partial class QueryOwner
    {
        internal readonly TaskCompletionSource<CheckResult<AppInstallerReviewReport>> InstallerReviewFinished =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CheckResult<AppInstallerReviewReport> InstallerReviewFailure(Exception error) => InstallerReviewEmpty(
            Failure(error).Result.State,
            $"Installer requirements could not be established (code 0x{error.HResult:X8}). Check App Installer API support, source access and policy, then refresh updates before retrying.");

        internal CheckResult<AppInstallerReviewReport> QueryInstallerReview(AppPackageMetadataRequest request)
        {
            var lookup = FindSelectedOffered(request);
            if (lookup.Offered is not { } offered) return InstallerReviewEmpty(lookup.State, lookup.Message);
            CheckStop();
            var options = Keep(FixedFactory.InstallerReviewOptions());
            options.PackageInstallScope = PackageInstallScope.User;
            options.PackageInstallMode = PackageInstallMode.Interactive;
            options.AcceptPackageAgreements = false;
            options.AllowHashMismatch = false;
            options.Force = false;
            options.AllowUpgradeToUnknownVersion = false;
            options.BypassIsStoreClientBlockedPolicyCheck = false;
            options.SkipDependencies = false;
            options.AdditionalInstallerArguments = "";
            options.ReplacementInstallerArguments = "";
            options.LogOutputPath = "";
            options.PreferredInstallLocation = "";
            options.AdditionalPackageCatalogArguments = "";
            options.CorrelationData = "";
            CheckStop();
            if (options.PackageInstallScope != PackageInstallScope.User || options.PackageInstallMode != PackageInstallMode.Interactive ||
                options.AcceptPackageAgreements || options.AllowHashMismatch || options.Force || options.AllowUpgradeToUnknownVersion ||
                options.BypassIsStoreClientBlockedPolicyCheck || options.SkipDependencies || options.PackageVersionId != null ||
                options.AuthenticationArguments != null || options.InstallerType != PackageInstallerType.Unknown ||
                !string.IsNullOrEmpty(options.AdditionalInstallerArguments) || !string.IsNullOrEmpty(options.ReplacementInstallerArguments) ||
                !string.IsNullOrEmpty(options.LogOutputPath) || !string.IsNullOrEmpty(options.PreferredInstallLocation) ||
                !string.IsNullOrEmpty(options.AdditionalPackageCatalogArguments) || !string.IsNullOrEmpty(options.CorrelationData))
                return InstallerReviewEmpty(CheckState.Unknown, "The fixed read-only applicability profile was not established.");
            CheckStop();
            // Supported synchronous manifest selection only: this may fetch/cache manifest
            // metadata. No installation/download operation or installer payload is requested.
            var installer = offered.GetApplicableInstaller(options);
            if (installer != null) Keep(installer);
            CheckStop();
            if (installer == null)
                return AppInstallerReviewPolicy.Project(request, false, null, null, null, null, null, null, null, DateTimeOffset.UtcNow);
            string? type = InstallerField(() => installer.InstallerType.ToString());
            string? nested = InstallerField(() => installer.NestedInstallerType.ToString());
            string? architecture = InstallerField(() => installer.Architecture.ToString());
            string? scope = InstallerField(() => installer.Scope.ToString());
            string? locale = InstallerField(() => installer.Locale);
            string? elevation = InstallerField(() => installer.ElevationRequirement.ToString());
            CheckStop();
            var authentication = ReadObject(() => installer.AuthenticationInfo);
            CheckStop();
            string? authenticationType = authentication == null ? null : InstallerField(() => authentication.AuthenticationType.ToString());
            CheckStop();
            return AppInstallerReviewPolicy.Project(request, true, type, nested, architecture, scope, locale,
                elevation, authenticationType, DateTimeOffset.UtcNow);
        }

        private string? InstallerField(Func<string> read)
        {
            CheckStop();
            string? value;
            try { value = read(); }
            catch (Exception) { PropertyFailure(); value = null; }
            CheckStop();
            return value;
        }
    }
}
