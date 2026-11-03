using System.Runtime.InteropServices;
using MadClean.Core;
using Microsoft.Management.Deployment;

namespace MadClean.Windows.WinGet;

/// <summary>Explicit, read-only SDK query candidate. Not wired into production inventory.</summary>
public sealed class StructuredWingetInventory
{
    private static readonly object Gate = new();
    private static QueryOwner? pending;
    private static readonly TimeSpan CallerDeadline = TimeSpan.FromSeconds(90);

    public bool HasPendingNativeRead { get { lock (Gate) return pending is not null; } }

    public async Task<StructuredWingetObservation> ReadObservedAsync(CancellationToken token)
    {
        if (token.IsCancellationRequested) return Empty(StructuredWingetOutcome.Cancelled, "WinGet check cancelled before activation.");
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            return Empty(StructuredWingetOutcome.Unsupported, "Structured WinGet checking requires Windows 11; this host is unverified.");
        QueryOwner owner;
        lock (Gate)
        {
            if (pending is not null) return Empty(StructuredWingetOutcome.Busy, "A previous WinGet query still owns its worker. Wait for actual completion.");
            owner = new QueryOwner();
            pending = owner; // Root before any scheduling or COM activation.
        }
        using var registration = token.Register(owner.RequestStop);
        try
        {
            var thread = new Thread(() => Run(owner)) { IsBackground = true, Name = "MadClean structured WinGet read" };
            thread.Start();
        }
        catch (Exception error)
        {
            lock (Gate) { if (ReferenceEquals(pending, owner)) pending = null; }
            owner.Finished.TrySetResult(owner.Failure(error));
        }
        try { return await owner.Finished.Task.WaitAsync(CallerDeadline, token).ConfigureAwait(false); }
        catch (OperationCanceledException) { owner.RequestStop(); return owner.Interrupted(StructuredWingetOutcome.Cancelled); }
        catch (TimeoutException) { owner.RequestStop(); return owner.Interrupted(StructuredWingetOutcome.TimedOut); }
    }

    private static void Run(QueryOwner owner)
    {
        StructuredWingetObservation result;
        bool initialized = false;
        try
        {
            owner.CheckStop();
            int hr = Native.CoInitializeEx(IntPtr.Zero, 0); // COINIT_MULTITHREADED on this same dedicated thread.
            Marshal.ThrowExceptionForHR(hr);
            initialized = true;
            result = owner.Query();
        }
        catch (OperationCanceledException) { result = owner.Interrupted(StructuredWingetOutcome.Cancelled); }
        catch (Exception error) { result = owner.Failure(error); }
        finally
        {
            // Cached WinRT wrappers use their normal runtime-managed lifetime. Do not force-release them.
            GC.KeepAlive(owner.Retained);
            if (initialized) Native.CoUninitialize();
        }
        result = result with { Diagnostic = result.Diagnostic with { CompletedUtc = DateTimeOffset.UtcNow } };
        lock (Gate) { if (ReferenceEquals(pending, owner)) pending = null; }
        owner.Finished.TrySetResult(result);
    }

    private static StructuredWingetObservation Empty(StructuredWingetOutcome outcome, string message) => new(
        CheckResult<AppUpdate>.Empty(State(outcome), message), new(StructuredWingetStage.NotStarted, outcome, null,
            false, null, null, 0, 0, null, null, null, null, null, null, null));

    private static CheckState State(StructuredWingetOutcome outcome) => outcome switch
    {
        StructuredWingetOutcome.Completed => CheckState.Available,
        StructuredWingetOutcome.Partial => CheckState.Partial,
        StructuredWingetOutcome.Unsupported => CheckState.Unsupported,
        StructuredWingetOutcome.Unavailable => CheckState.Unavailable,
        StructuredWingetOutcome.Cancelled => CheckState.Cancelled,
        StructuredWingetOutcome.Failed => CheckState.Failed,
        _ => CheckState.Unknown
    };

    private sealed class QueryOwner
    {
        internal readonly TaskCompletionSource<StructuredWingetObservation> Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly List<object> Retained = [];
        private readonly object sync = new();
        private int stop;
        private StructuredWingetDiagnostic diagnostic = new(StructuredWingetStage.NotStarted, StructuredWingetOutcome.NotStarted,
            null, false, null, null, 0, 0, null, null, null, null, null, null, null);

        internal void RequestStop() => Interlocked.Exchange(ref stop, 1);
        internal void CheckStop() { if (Volatile.Read(ref stop) != 0) throw new OperationCanceledException(); }
        private void Update(Func<StructuredWingetDiagnostic, StructuredWingetDiagnostic> change) { lock (sync) diagnostic = change(diagnostic); }
        private StructuredWingetDiagnostic Snapshot() { lock (sync) return diagnostic; }
        private T Keep<T>(T value) where T : class { Retained.Add(value); return value; }

        internal StructuredWingetObservation Interrupted(StructuredWingetOutcome outcome) => new(
            CheckResult<AppUpdate>.Empty(State(outcome), "Caller stopped waiting. Any pending WinGet operation remains owned until its actual completion; provider background work is not confirmed stopped."),
            Snapshot() with { Outcome = outcome });

        internal StructuredWingetObservation Failure(Exception error)
        {
            var snapshot = Snapshot();
            var outcome = error.HResult is unchecked((int)0x80040154) or unchecked((int)0x80070490)
                ? StructuredWingetOutcome.Unavailable
                : error is NotSupportedException || error.HResult == unchecked((int)0x80004002)
                    ? StructuredWingetOutcome.Unsupported : StructuredWingetOutcome.Failed;
            return new(CheckResult<AppUpdate>.Empty(State(outcome),
                "Structured WinGet checking could not complete. Check App Installer availability, the configured official source, policy and network access. No source agreements were accepted or repairs attempted."),
                snapshot with { Outcome = outcome, HResult = error.HResult });
        }

        internal StructuredWingetObservation Query()
        {
            CheckStop();
            Update(d => d with { Stage = StructuredWingetStage.Activation, Attempted = true, StartedUtc = DateTimeOffset.UtcNow });
            var manager = Keep(FixedFactory.Manager());
            CheckStop();
            Update(d => d with { Stage = StructuredWingetStage.SourceValidation });
            var source = Keep(manager.GetPackageCatalogByName(StructuredWingetPolicy.SourceName)
                ?? throw new NotSupportedException("The configured source is unavailable."));
            if (source.IsComposite) throw new NotSupportedException("A non-composite source is required.");
            // These setters/getters are capabilities of the actual returned server object. A missing interface fails closed.
            source.AcceptSourceAgreements = false;
            bool agreementsDisabled = !source.AcceptSourceAgreements;
            Update(d => d with { AgreementsDisabled = agreementsDisabled });
            var info = Keep(source.Info ?? throw new NotSupportedException("Source metadata is unavailable."));
            var facts = Facts(info, false, agreementsDisabled, null);
            bool sourceMatched = StructuredWingetPolicy.MatchesSource(facts);
            Update(d => d with { SourceMatched = sourceMatched });
            // AuthenticationInfo may fetch metadata for a REST source. Refuse altered sources before that getter.
            if (!sourceMatched || !agreementsDisabled)
                throw new NotSupportedException("The exact source and agreement controls were not established.");
            CheckStop();
            var auth = Keep(source.AuthenticationInfo ?? throw new NotSupportedException("Authentication metadata is unavailable."));
            bool authNone = auth.AuthenticationType == AuthenticationType.None;
            Update(d => d with { AuthenticationNone = authNone });
            facts = facts with { AuthenticationNone = authNone };
            if (!StructuredWingetPolicy.CanConnect(facts)) throw new NotSupportedException("Source requirements were not established.");
            source.PackageCatalogBackgroundUpdateInterval = TimeSpan.Zero;
            if (source.PackageCatalogBackgroundUpdateInterval != TimeSpan.Zero)
                throw new NotSupportedException("Background interval control was not established.");
            var authArgs = Keep(FixedFactory.Authentication());
            authArgs.AuthenticationMode = AuthenticationMode.Silent;
            if (authArgs.AuthenticationMode != AuthenticationMode.Silent)
                throw new NotSupportedException("Silent authentication control was not established.");
            source.AuthenticationArguments = authArgs;
            var appliedAuthArgs = Keep(source.AuthenticationArguments ?? throw new NotSupportedException("Authentication control is unavailable."));
            if (appliedAuthArgs.AuthenticationMode != AuthenticationMode.Silent || !string.IsNullOrEmpty(appliedAuthArgs.AuthenticationAccount))
                throw new NotSupportedException("Silent account-free authentication is required.");
            CheckStop();
            var compositeOptions = Keep(FixedFactory.Composite());
            compositeOptions.Catalogs.Add(source);
            compositeOptions.CompositeSearchBehavior = CompositeSearchBehavior.LocalCatalogs;
            var composite = Keep(manager.CreateCompositePackageCatalog(compositeOptions));
            if (!composite.IsComposite) throw new NotSupportedException("Installed-first composite was not established.");
            CheckStop();
            Update(d => d with { Stage = StructuredWingetStage.Connect });
            var connectOperation = Keep(composite.ConnectAsync());
            // Caller cancellation never disposes this operation or kills the shared server. This owner waits for real completion.
            var connected = Keep(connectOperation.AsTask().GetAwaiter().GetResult());
            bool connectOk = connected.Status == ConnectResultStatus.Ok;
            Update(d => d with { ConnectSucceeded = connectOk });
            if (!connectOk) return Unsuccessful("WinGet source connection did not succeed.");
            CheckStop();
            var catalog = Keep(connected.PackageCatalog ?? throw new NotSupportedException("Connected catalog is absent."));
            var findOptions = Keep(FixedFactory.Find());
            findOptions.ResultLimit = StructuredWingetPolicy.ResultLimit;
            Update(d => d with { Stage = StructuredWingetStage.Find });
            var findOperation = Keep(catalog.FindPackagesAsync(findOptions));
            var found = Keep(findOperation.AsTask().GetAwaiter().GetResult());
            bool findOk = found.Status == FindPackagesResultStatus.Ok;
            Update(d => d with { FindSucceeded = findOk });
            if (!findOk) return Unsuccessful("WinGet installed-first query did not succeed.");
            CheckStop();
            Update(d => d with { Stage = StructuredWingetStage.Projection });
            bool? nativeLimit = Read(() => found.WasLimitExceeded);
            var matches = Keep(found.Matches);
            int count = matches.Count;
            if (count < 0) throw new NotSupportedException("The result count cannot be represented safely.");
            bool projectionLimit = count > StructuredWingetPolicy.ResultLimit;
            var packages = new List<StructuredWingetPackageFacts>();
            for (int index = 0; index < Math.Min(count, StructuredWingetPolicy.ResultLimit); index++)
            {
                CheckStop();
                try
                {
                    var match = Keep(matches[index]);
                    var package = Keep(match.CatalogPackage);
                    string? name = Text(() => package.Name), id = Text(() => package.Id);
                    var installed = ReadObject(() => package.InstalledVersion);
                    var offered = ReadObject(() => package.DefaultInstallVersion);
                    string? installedVersion = installed is null ? null : Text(() => installed.Version);
                    string? offeredVersion = offered is null ? null : Text(() => offered.Version);
                    bool? offeredSource = null;
                    if (offered is not null)
                    {
                        var offeredCatalog = ReadObject(() => offered.PackageCatalog);
                        var offeredInfo = offeredCatalog is null ? null : ReadObject(() => offeredCatalog.Info);
                        if (offeredInfo is not null) offeredSource = StructuredWingetPolicy.MatchesSource(Facts(offeredInfo, null, null, null));
                    }
                    packages.Add(new(name, id, installedVersion, offeredVersion, Read(() => package.IsUpdateAvailable), offeredSource));
                }
                catch (Exception) { PropertyFailure(); }
            }
            CheckStop();
            Update(d => d with { NativeLimitExceeded = nativeLimit, ProjectionLimitExceeded = projectionLimit, ProjectedCount = packages.Count });
            var projected = StructuredWingetPolicy.Project(packages, nativeLimit, projectionLimit, Snapshot().PropertyFailureCount);
            var outcome = projected.State == CheckState.Partial ? StructuredWingetOutcome.Partial : StructuredWingetOutcome.Completed;
            Update(d => d with { Stage = StructuredWingetStage.Completed, Outcome = outcome });
            return new(projected, Snapshot());
        }

        private StructuredWingetObservation Unsuccessful(string message) => new(CheckResult<AppUpdate>.Empty(CheckState.Failed,
            message + " No update status was established; no agreements were accepted."), Snapshot() with { Outcome = StructuredWingetOutcome.Failed });
        private void PropertyFailure() => Update(d => d with { PropertyFailureCount = Math.Min(4096, d.PropertyFailureCount + 1) });
        private bool? Read(Func<bool> read) { try { return read(); } catch (Exception) { PropertyFailure(); return null; } }
        private string? Text(Func<string> read)
        {
            try { string value = read(); if (StructuredWingetPolicy.IsKnownText(value)) return value; }
            catch (Exception) { }
            PropertyFailure(); return null;
        }
        private T? ReadObject<T>(Func<T> read) where T : class
        { try { var value = read(); return value is null ? null : Keep(value); } catch (Exception) { PropertyFailure(); return null; } }
        private StructuredWingetCatalogFacts Facts(PackageCatalogInfo info, bool? composite, bool? agreements, bool? auth) =>
            new(Text(() => info.Name), Text(() => info.Type), Text(() => info.Argument), Text(() => info.Id), composite, agreements, auth);
    }

    private static class FixedFactory
    {
        // Microsoft winget-cli v1.10.340 src/Microsoft.Management.Deployment/Public/ComClsids.h, USE_PROD_CLSIDS.
        internal static PackageManager Manager() => Create<PackageManager>(new("C53A4F16-787E-42A4-B304-29EFFB4BF597"));
        internal static FindPackagesOptions Find() => Create<FindPackagesOptions>(new("572DED96-9C60-4526-8F92-EE7D91D38C1A"));
        internal static CreateCompositePackageCatalogOptions Composite() => Create<CreateCompositePackageCatalogOptions>(new("526534B8-7E46-47C8-8416-B1685C327D37"));
        internal static AuthenticationArguments Authentication() => Create<AuthenticationArguments>(new("BA580786-BDE3-4F6C-B8F3-44698AC8711A"));
        private static T Create<T>(Guid classId)
        {
            Guid inspectable = new("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90");
            IntPtr acquired = IntPtr.Zero;
            try
            {
                int hr = Native.CoCreateInstance(ref classId, IntPtr.Zero, 0x4 | 0x400, ref inspectable, out acquired);
                Marshal.ThrowExceptionForHR(hr);
                if (acquired == IntPtr.Zero) throw new COMException("Activation returned no interface.");
                return WinRT.MarshalInspectable<T>.FromAbi(acquired);
            }
            finally { if (acquired != IntPtr.Zero) WinRT.MarshalInspectable<T>.DisposeAbi(acquired); }
        }
    }

    private static class Native
    {
        [DllImport("ole32.dll", ExactSpelling = true)] internal static extern int CoInitializeEx(IntPtr reserved, uint coInit);
        [DllImport("ole32.dll", ExactSpelling = true)] internal static extern void CoUninitialize();
        [DllImport("ole32.dll", ExactSpelling = true)] internal static extern int CoCreateInstance(ref Guid classId, IntPtr outer,
            uint context, ref Guid iid, out IntPtr result);
    }
}
