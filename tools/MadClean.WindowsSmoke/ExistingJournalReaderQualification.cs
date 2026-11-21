using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.WindowsSmoke;

// An explicit, owned disposable-runner fixture. Never called by the passive harness or App.
internal static class ExistingJournalReaderQualification
{
    private static Owner? rootedOwner;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

    internal static int Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task<int> RunAsync()
    {
        var context = ExistingJournalReaderQualificationProfile.CaptureCurrent();
        if (context == null ||
            WindowsModernOwnedJournalSample.ExecutionValidated ||
            !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100) ||
            RuntimeInformation.ProcessArchitecture != Architecture.X64 ||
            RuntimeInformation.OSArchitecture != Architecture.X64 ||
            !int.TryParse(Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int attempt) || attempt <= 0 ||
            WindowsModernOwnedJournalSample.HasPendingQualificationWork ||
            WindowsModernOwnedJournalSample.RequiresQualificationRetention) return 2;
        var owner = new Owner(context, attempt);
        if (Interlocked.CompareExchange(ref rootedOwner, owner, null) != null) return 2;
        using var deadlineStop = new CancellationTokenSource();
        try
        {
            owner.StartedUtc = DateTimeOffset.UtcNow;
            owner.StartedStamp = TimeProvider.System.GetTimestamp();
            owner.ReserveOutput();
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // The actual task slot is rooted before its start gate can admit any fixture effects.
            owner.Actual = Task.Run(async () => { await start.Task.ConfigureAwait(false); owner.Execute(); });
            Task deadline = Task.Delay(Budget, deadlineStop.Token);
            start.SetResult();
            await Task.WhenAny(owner.Actual, deadline).ConfigureAwait(false);
            if (!owner.Actual.IsCompleted || owner.Elapsed())
            {
                owner.MarkTimeout();
                // No mutable worker collections are read while native/setup work may run.
                // Keep an initial conservative artifact even if the external step later ends us.
                owner.WriteReport(initialTimeout: true);
            }
            await owner.Actual.ConfigureAwait(false); // No timeout/caller departure abandons actual work.
            deadlineStop.Cancel();
            owner.WriteReport();
            return owner.ReportState == "passed" ? 0 : owner.ReportState == "failed" ? 1 : 2;
        }
        catch
        {
            owner.MarkObservationUncertain();
            try { owner.WriteReport(initialTimeout: owner.Actual is { IsCompleted: false }); }
            catch { /* Report failure never releases the native owner. */ }
            // Fixed message only; no paths, identities, source text or exception data are exported.
            Console.Error.WriteLine("Existing-reader qualification did not produce complete settled evidence.");
            return 2;
        }
        finally
        {
            if (owner.Actual is { } actual)
            {
                try { await actual.ConfigureAwait(false); }
                catch { /* Observe actual task completion, never retry or infer cleanup. */ }
            }
            deadlineStop.Cancel();
            // A completed managed task is not proof that an ambiguous native/frame prefix
            // released ownership. Hold the CLR until the external disposable step boundary.
            if (!owner.Settled) await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            // Retained/uncertain frames remain rooted. Process termination is not cleanup evidence.
            if (owner.Settled) Interlocked.CompareExchange(ref rootedOwner, null, owner);
        }
    }

    private sealed class Owner
    {
        internal Task? Actual;
        internal DateTimeOffset StartedUtc;
        internal long StartedStamp;
        private int timeout;
        private int observationUncertain;
        private readonly ExistingJournalReaderQualificationContext context;
        private readonly int runAttempt;
        private string? output;
        private bool outputReserved, outputReservationUncertain;
        private readonly List<Scope> scopes = [];
        private readonly List<ExistingReaderCaseEvidence> results = [];
        private bool failed;
        internal Owner(ExistingJournalReaderQualificationContext context, int runAttempt)
        { this.context = context; this.runAttempt = runAttempt; }
        internal bool Settled => !outputReservationUncertain && (Actual == null || Actual.IsCompleted) && scopes.All(scope => scope.TeardownConfirmed) &&
            !WindowsModernOwnedJournalSample.HasPendingQualificationWork &&
            !WindowsModernOwnedJournalSample.RequiresQualificationRetention;
        internal bool Elapsed() => TimeProvider.System.GetElapsedTime(StartedStamp) >= Budget;
        internal void MarkTimeout() => Interlocked.Exchange(ref timeout, 1);
        internal void MarkObservationUncertain() => Interlocked.Exchange(ref observationUncertain, 1);
        internal string ReportState => failed ? "failed" : Volatile.Read(ref timeout) == 0 && Volatile.Read(ref observationUncertain) == 0 && Settled &&
            results.Count == ExistingReaderQualificationCases.Labels.Length && results.All(item => item.State == "passed") ? "passed" : "inconclusive";

        internal void Execute()
        {
            foreach (string label in ExistingReaderQualificationCases.Labels)
            {
                if (Elapsed()) MarkTimeout();
                if (Volatile.Read(ref timeout) != 0 || scopes.Any(scope => !scope.TeardownConfirmed) ||
                    WindowsModernOwnedJournalSample.HasPendingQualificationWork || WindowsModernOwnedJournalSample.RequiresQualificationRetention) break;
                var scope = new Scope(label); // Owns no native resource until registered below.
                scopes.Add(scope);
                ExistingReaderCaseEvidence evidence;
                try { evidence = scope.Run(); }
                catch { scope.Retain(); evidence = scope.Snapshot("inconclusive"); }
                results.Add(evidence);
                failed |= evidence.State == "failed";
                if (Elapsed()) MarkTimeout();
                if (evidence.State != "passed" || !scope.TeardownConfirmed) break;
            }
        }

        internal void ReserveOutput()
        {
            string repository = Path.GetFullPath(Environment.CurrentDirectory);
            CheckPathAncestors(repository);
            string artifacts = Path.Combine(repository, "artifacts");
            if (!Directory.Exists(artifacts)) Directory.CreateDirectory(artifacts);
            CheckPathAncestors(artifacts);
            string candidate = Path.Combine(artifacts, "existing-journal-reader-qualification");
            if (Directory.Exists(candidate) || File.Exists(candidate)) throw Refused();
            // Unlike Directory.CreateDirectory, this does not adopt an existing target.
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>() };
            outputReservationUncertain = true;
            int made = Native.CreateDirectoryW(candidate, ref attributes);
            if (made == 0) throw Refused();
            output = candidate; outputReserved = true; outputReservationUncertain = false;
            CheckPathAncestors(output);
        }

        internal void WriteReport(bool initialTimeout = false)
        {
            if (!outputReserved || output == null || outputReservationUncertain) throw Refused();
            if (!initialTimeout && Actual is { IsCompleted: false }) throw Refused();
            var report = new ExistingReaderQualificationEvidence(1, "existing-journal-reader-qualification",
                context.SourceSha!, context.SourceRef!, context.RunId!, runAttempt,
                ExistingJournalReaderQualificationProfile.Job, StartedUtc, DateTimeOffset.UtcNow, initialTimeout ? "inconclusive" : ReportState,
                Volatile.Read(ref timeout) != 0, initialTimeout ? false : Settled, Environment.OSVersion.Version.Build,
                RuntimeInformation.ProcessArchitecture == Architecture.X64,
                initialTimeout ? Array.Empty<ExistingReaderCaseEvidence>() : results.AsReadOnly());
            if (initialTimeout) report = report with { State = "inconclusive" };
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(report, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
                Converters = { new JsonStringEnumConverter() }
            });
            if (bytes.Length > 32768) throw Refused();
            CheckPathAncestors(output);
            using var stream = new FileStream(Path.Combine(output, initialTimeout ? "report-timeout.json" : "report.json"), FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            stream.Write(bytes); stream.Flush(true);
            Console.WriteLine("Existing-reader qualification report: artifacts/existing-journal-reader-qualification/report.json");
            Console.WriteLine("Owned synthetic files only. Production recovery, servicing and MSI remain unqualified.");
        }
    }

    private sealed record Stamp(ulong Volume, ulong Low, ulong High, long Length, bool Directory, byte[] Security, byte[] Bytes);

    // A whole case's setup/read/post/teardown runs on one rooted worker. Raw slots, pointed-to
    // descriptors and current I/O buffers exist on this object before dependent native calls.
    private sealed class Scope
    {
        private readonly string label;
        private readonly Guid id = Guid.NewGuid();
        private readonly nint[] handles = new nint[64], descriptors = new nint[64];
        private readonly bool[] handleOwned = new bool[64], descriptorOwned = new bool[64];
        private int handleCount, descriptorCount;
        private bool nativeActive, retained;
        private nint ioStorage, pathStorage;
        private bool ioStorageOwned, pathStorageOwned;
        private StandardInfo standard;
        private IdentityInfo identity;
        private AttributeInfo attributes;
        private string? root, child, sid;
        private bool rootCreated, childCreated, setupConfirmed, knownFailure;
        private readonly List<string> files = [], extraDirectories = [];
        private readonly Dictionary<string, byte[]> authored = new(StringComparer.Ordinal);
        private Dictionary<string, Stamp>? baseline;
        private ModernOwnedJournalSampleAttempt? attempt;
        private bool? boundary, comparison;
        internal bool TeardownConfirmed { get; private set; }
        internal Scope(string label) { this.label = label; }
        internal void Retain() => retained = true;

        internal ExistingReaderCaseEvidence Run()
        {
            Setup();
            if (retained || nativeActive || !ReleaseNativePrefix()) throw Refused();
            setupConfirmed = true;
            // All baseline read/write handles are closed before the real exclusive reader opens.
            attempt = WindowsModernOwnedJournalSample.RunQualifiedExistingFixture(id);
            knownFailure |= label != "valid" && attempt.SampleVerified;
            if (!attempt.ResourcesReleased || attempt.Observation.Release != ModernJournalCallbackOutcome.Confirmed || attempt.HasPendingWork || attempt.RequiresRetention ||
                WindowsModernOwnedJournalSample.HasPendingQualificationWork || WindowsModernOwnedJournalSample.RequiresQualificationRetention)
            { retained = true; return Snapshot("inconclusive"); }
            boundary = ExistingReaderQualificationCases.Matches(label, attempt);
            var after = ObserveTree();
            comparison = SameTree(baseline!, after);
            knownFailure |= comparison == false;
            if (!ReleaseNativePrefix()) return Snapshot("inconclusive");
            if (comparison != true) { retained = true; return Snapshot("failed"); }
            // Nonrecursive, exact-owned-path teardown in a trusted disposable fixture, not
            // an atomic namespace defense or a production cleanup implementation.
            Teardown();
            return Snapshot(boundary == true && TeardownConfirmed ? "passed" : "inconclusive");
        }

        internal ExistingReaderCaseEvidence Snapshot(string state) => new(label, knownFailure ? "failed" : state, setupConfirmed,
            attempt?.Observation, attempt?.SampleVerified, attempt?.ResourcesReleased, attempt?.HasPendingWork,
            attempt == null ? retained : retained || attempt.RequiresRetention, boundary, comparison,
            TeardownConfirmed ? true : null);

        private void Setup()
        {
            if (id == Guid.Empty || !ExistingJournalReaderQualificationProfile.IsCurrentSourceAllowed() ||
                WindowsModernOwnedJournalSample.ExecutionValidated || SystemToolJournalStore.HasUncertainCleanup) throw Refused();
            string repository = Path.GetFullPath(Environment.CurrentDirectory);
            CheckPathAncestors(repository);
            if (!File.Exists(Path.Combine(repository, "MadClean.sln")) ||
                new DriveInfo(Path.GetPathRoot(repository)!).DriveType != DriveType.Fixed ||
                new DriveInfo(Path.GetPathRoot(repository)!).DriveFormat != "NTFS") throw Refused();
            string artifacts = Path.Combine(repository, "artifacts"); CheckPathAncestors(artifacts);
            root = Path.Combine(artifacts, "windows-validation-fixtures-" + id.ToString("N"));
            child = Path.Combine(root, id.ToString("N"));
            if (root.Length > 160 || Path.Combine(child, id.ToString("N") + ".jsonl").Length > 258) throw Refused();
            using (var current = WindowsIdentity.GetCurrent()) sid = current.User?.Value ?? throw Refused();
            nint directoryDescriptor = Descriptor(true), fileDescriptor = Descriptor(false);
            CreateDirectory(root, directoryDescriptor); rootCreated = true;
            CreateDirectory(child, directoryDescriptor); childCreated = true;
            WriteNew(Path.Combine(root, ".madclean-fixture-owner"), Encoding.ASCII.GetBytes(id.ToString("N")), fileDescriptor);
            if (label != "missing-lease") WriteNew(Path.Combine(child, label == "case-mismatch" ? "Lease.bin" : "lease.bin"), [], fileDescriptor);
            if (label != "missing-journal")
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                var entry = new SystemToolJournalEntry(id, SystemToolOperation.DismCheckHealth, 1, now,
                    SystemToolJournalPhase.DispatchIntent, null, null, null, null);
                if (!SystemToolJournal.TryEncode([entry], now, out byte[]? encoded) || encoded == null || encoded.Length + 1 > 4096) throw Refused();
                byte[] framed = new byte[encoded.Length + 1]; encoded.CopyTo(framed, 0); framed[^1] = (byte)'\n';
                if (!ModernJournalSyntheticSeed.IsExpected(SystemToolJournal.DecodeSnapshots(framed, DateTimeOffset.UtcNow), id, DateTimeOffset.UtcNow)) throw Refused();
                WriteNew(Path.Combine(child, id.ToString("N") + ".jsonl"), label == "missing-lf" ? encoded : framed, fileDescriptor);
            }
            if (label == "extra-file") WriteNew(Path.Combine(child, "extra.bin"), [17, 29], fileDescriptor);
            if (label == "extra-directory")
            {
                string extra = Path.Combine(child, "extra-directory"); CreateDirectory(extra, directoryDescriptor); extraDirectories.Add(extra);
            }
            baseline = ObserveTree();
            foreach (var file in authored)
                if (!baseline.TryGetValue(file.Key, out var observed) || !observed.Bytes.AsSpan().SequenceEqual(file.Value)) throw Refused();
        }

        private nint Descriptor(bool directory)
        {
            int slot = descriptorCount++;
            if (slot >= descriptors.Length) throw Refused();
            string inheritance = directory ? "OICI" : "";
            string sddl = $"O:{sid}D:P(A;{inheritance};FA;;;{sid})(A;{inheritance};FA;;;SY)";
            Begin();
            int ok = Native.ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out descriptors[slot], out _);
            End();
            if (ok == 0 || descriptors[slot] == 0) { retained = true; throw Refused(); } // Failure/null output never authorizes creation or freeing.
            descriptorOwned[slot] = true;
            return descriptors[slot];
        }

        private void CreateDirectory(string path, nint descriptor)
        {
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            Begin(); int ok = Native.CreateDirectoryW(path, ref security); End();
            if (ok == 0) { retained = true; throw Refused(); } // Collision is never adopted or repaired.
        }

        private nint Open(string path, uint access, uint share, uint disposition, bool directory, nint descriptor = 0)
        {
            int slot = handleCount++;
            if (slot >= handles.Length) throw Refused();
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            Begin();
            handles[slot] = descriptor == 0
                ? Native.CreateFilePlain(path, access, share, 0, disposition, 0x00200000u | (directory ? 0x02000000u : 0), 0)
                : Native.CreateFileSecured(path, access, share, ref security, disposition, 0x00200000u | 0x80000000u, 0);
            End();
            if (handles[slot] == -1) { handles[slot] = 0; retained = true; throw Refused(); }
            if (handles[slot] == 0) { retained = true; throw Refused(); }
            handleOwned[slot] = true;
            return handles[slot];
        }

        private void WriteNew(string path, byte[] bytes, nint descriptor)
        {
            nint handle = Open(path, 0xC0020080, 0, 1, false, descriptor); files.Add(path);
            authored.Add(path, bytes);
            EnsureIoStorage();
            if (bytes.Length > 4096) throw Refused();
            Marshal.Copy(bytes, 0, ioStorage, bytes.Length);
            if (bytes.Length != 0)
            {
                Begin(); int ok = Native.WriteFile(handle, ioStorage, (uint)bytes.Length, out uint written, 0); End();
                if (ok == 0 || written != bytes.Length) { retained = true; throw Refused(); }
            }
            Begin(); int flushed = Native.FlushFileBuffers(handle); End();
            if (flushed == 0) { retained = true; throw Refused(); }
            if (!Close(handle)) throw Refused();
        }

        private Dictionary<string, Stamp> ObserveTree()
        {
            if (!rootCreated || !childCreated || root == null || child == null || retained || nativeActive) throw Refused();
            CheckPathAncestors(root);
            CheckPathAncestors(child);
            foreach (string directory in extraDirectories) CheckPathAncestors(directory);
            CheckNames(root, [".madclean-fixture-owner", id.ToString("N")]);
            var expected = files.Where(file => Path.GetDirectoryName(file) == child).Select(Path.GetFileName)
                .Concat(extraDirectories.Select(Path.GetFileName)).Select(name => name!).ToArray();
            CheckNames(child, expected);
            var output = new Dictionary<string, Stamp>(StringComparer.Ordinal);
            output.Add(root, Observe(root, true)); output.Add(child, Observe(child, true));
            foreach (string directory in extraDirectories) { CheckNames(directory, []); output.Add(directory, Observe(directory, true)); }
            foreach (string file in files) output.Add(file, Observe(file, false));
            return output;
        }

        private static void CheckNames(string directory, string[] expected)
        {
            var actual = Directory.EnumerateFileSystemEntries(directory).Take(9).Select(Path.GetFileName).ToArray();
            if (actual.Length != expected.Length || !actual.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal)) throw Refused();
        }

        private Stamp Observe(string path, bool directory)
        {
            nint handle = Open(path, directory ? 0x20080u : 0x80020080u, 1, 3, directory);
            Begin(); int kind = Native.GetInfoAttributes(handle, 9, out attributes, 8); End();
            if (kind == 0 || (attributes.Attributes & 0x400) != 0 || ((attributes.Attributes & 0x10) != 0) != directory) throw Refused();
            Begin(); int standardRead = Native.GetInfoStandard(handle, 1, out standard, 24); End();
            if (standardRead == 0 || standard.DeletePending != 0 || standard.Directory != (directory ? 1 : 0) ||
                !directory && (standard.EndOfFile < 0 || standard.EndOfFile > 4096 || standard.Links != 1)) throw Refused();
            long length = directory ? 0 : standard.EndOfFile;
            Begin(); int identityRead = Native.GetInfoIdentity(handle, 18, out identity, 24); End();
            if (identityRead == 0 || identity.Low == 0 && identity.High == 0) throw Refused();
            var observedIdentity = identity;
            EnsureIoStorage();
            if (!pathStorageOwned)
            {
                pathStorage = Marshal.AllocHGlobal(1024);
                pathStorageOwned = true; // Actual pointer published before dependent native use.
            }
            for (int i = 0; i < 1024; i++) Marshal.WriteByte(pathStorage, i, 0);
            Begin(); uint finalCount = Native.GetFinalPathNameByHandleW(handle, pathStorage, 512, 0); End();
            if (finalCount == 0 || finalCount >= 512) throw Refused();
            string actual = Marshal.PtrToStringUni(pathStorage, checked((int)finalCount)) ?? throw Refused(); if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual[4..];
            if (!string.Equals(Path.TrimEndingDirectorySeparator(actual), Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase)) throw Refused();
            byte[] security = ReadSecurity(handle, directory);
            byte[] bytes = new byte[(int)length];
            if (length != 0)
            {
                Begin(); int read = Native.ReadFile(handle, ioStorage, (uint)length, out uint got, 0); End();
                if (read == 0 || got != length) throw Refused();
            }
            if (length != 0) Marshal.Copy(ioStorage, bytes, 0, bytes.Length);
            if (!Close(handle)) throw Refused();
            return new(observedIdentity.Volume, observedIdentity.Low, observedIdentity.High, length, directory, security, bytes);
        }

        private byte[] ReadSecurity(nint handle, bool directory)
        {
            int slot = descriptorCount++;
            if (slot >= descriptors.Length) throw Refused();
            Begin(); uint code = Native.GetSecurityInfo(handle, 1, 5, 0, 0, 0, 0, out descriptors[slot]); End();
            if (code != 0 || descriptors[slot] == 0) { retained = true; throw Refused(); } // Failure/null output confers no usable descriptor.
            descriptorOwned[slot] = true;
            Begin(); uint count = Native.GetSecurityDescriptorLength(descriptors[slot]); End();
            if (count is 0 or > 65536) throw Refused();
            byte[] bytes = new byte[count]; Marshal.Copy(descriptors[slot], bytes, 0, bytes.Length);
            var descriptor = new RawSecurityDescriptor(bytes, 0);
            if (descriptor.Owner?.Value != sid || (descriptor.ControlFlags & ControlFlags.DiscretionaryAclProtected) == 0 ||
                descriptor.DiscretionaryAcl is not { Count: 2 } acl) throw Refused();
            bool user = false, system = false;
            foreach (GenericAce generic in acl)
            {
                if (generic is not CommonAce ace || ace.IsCallback || ace.AceQualifier != AceQualifier.AccessAllowed || ace.AccessMask != 0x1F01FF ||
                    ace.AceFlags != (directory ? AceFlags.ObjectInherit | AceFlags.ContainerInherit : AceFlags.None)) throw Refused();
                if (ace.SecurityIdentifier.Value == sid && !user) user = true;
                else if (ace.SecurityIdentifier.Value == "S-1-5-18" && !system) system = true;
                else throw Refused();
            }
            if (!user || !system) throw Refused();
            // Compare normalized owner/DACL encoding, not undocumented native padding/layout.
            return Encoding.UTF8.GetBytes(descriptor.GetSddlForm(AccessControlSections.Owner | AccessControlSections.Access));
        }

        private static bool SameTree(Dictionary<string, Stamp> before, Dictionary<string, Stamp> after) => before.Count == after.Count && before.All(pair =>
            after.TryGetValue(pair.Key, out var value) && pair.Value.Volume == value.Volume && pair.Value.Low == value.Low &&
            pair.Value.High == value.High && pair.Value.Length == value.Length && pair.Value.Directory == value.Directory &&
            pair.Value.Security.AsSpan().SequenceEqual(value.Security) && pair.Value.Bytes.AsSpan().SequenceEqual(value.Bytes));

        private void Teardown()
        {
            if (retained || nativeActive || attempt is not { ResourcesReleased: true, RequiresRetention: false, HasPendingWork: false } ||
                attempt.Observation.Release != ModernJournalCallbackOutcome.Confirmed ||
                WindowsModernOwnedJournalSample.HasPendingQualificationWork || WindowsModernOwnedJournalSample.RequiresQualificationRetention) throw Refused();
            CheckPathAncestors(root!); CheckPathAncestors(child!);
            CheckNames(root!, [".madclean-fixture-owner", id.ToString("N")]);
            CheckNames(child!, files.Where(file => Path.GetDirectoryName(file) == child).Select(Path.GetFileName)
                .Concat(extraDirectories.Select(Path.GetFileName)).Select(name => name!).ToArray());
            foreach (string directory in extraDirectories) { CheckPathAncestors(directory); CheckNames(directory, []); }
            foreach (string file in files.AsEnumerable().Reverse())
            {
                if ((File.GetAttributes(file) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) throw Refused();
                File.Delete(file);
                if (File.Exists(file) || Directory.Exists(file)) throw Refused();
            }
            foreach (string directory in extraDirectories.AsEnumerable().Reverse()) Directory.Delete(directory, false);
            Directory.Delete(child!, false); Directory.Delete(root!, false);
            TeardownConfirmed = !Directory.Exists(root!) && !File.Exists(root!);
            if (!TeardownConfirmed) throw Refused();
        }

        private bool Close(nint handle)
        {
            for (int i = handleCount - 1; i >= 0; i--)
            {
                if (!handleOwned[i] || handles[i] != handle) continue;
                Begin(); int ok = Native.CloseHandle(handle); End();
                if (ok == 0) { retained = true; return false; }
                handleOwned[i] = false; handles[i] = 0;
                return true;
            }
            retained = true; return false;
        }
        private void EnsureIoStorage()
        {
            if (nativeActive || retained) throw Refused();
            if (!ioStorageOwned)
            {
                ioStorage = Marshal.AllocHGlobal(4096);
                ioStorageOwned = true; // No marshaler-owned buffer can disappear during uncertainty.
            }
        }

        private bool ReleaseNativePrefix()
        {
            if (retained || nativeActive) return false;
            for (int i = handleCount - 1; i >= 0; i--) if (handleOwned[i] && !Close(handles[i])) return false;
            for (int i = descriptorCount - 1; i >= 0; i--)
            {
                if (!descriptorOwned[i]) continue;
                Begin(); nint result = Native.LocalFree(descriptors[i]); End();
                if (result != 0) { retained = true; return false; }
                descriptorOwned[i] = false; descriptors[i] = 0;
            }
            if (pathStorageOwned)
            {
                Begin(); Marshal.FreeHGlobal(pathStorage); End();
                pathStorageOwned = false; pathStorage = 0;
            }
            if (ioStorageOwned)
            {
                Begin(); Marshal.FreeHGlobal(ioStorage); End();
                ioStorageOwned = false; ioStorage = 0;
            }
            return true;
        }
        private void Begin() { if (nativeActive || retained) throw Refused(); nativeActive = true; }
        private void End() { nativeActive = false; }
    }

    private static void CheckPathAncestors(string path)
    {
        for (var directory = new DirectoryInfo(path); directory != null; directory = directory.Parent)
            if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0) throw Refused();
    }
    private static InvalidOperationException Refused() => new("Existing-reader fixture evidence is unavailable.");
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { internal int Length; internal nint Descriptor; internal int Inherit; }
    [StructLayout(LayoutKind.Sequential)] private struct StandardInfo { internal long Allocation, EndOfFile; internal uint Links; internal byte DeletePending, Directory; }
    [StructLayout(LayoutKind.Sequential)] private struct IdentityInfo { internal ulong Volume, Low, High; }
    [StructLayout(LayoutKind.Sequential)] private struct AttributeInfo { internal uint Attributes, Tag; }

    private static class Native
    {
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int ConvertStringSecurityDescriptorToSecurityDescriptorW(string text, uint revision, out nint descriptor, out uint bytes);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int CreateDirectoryW(string path, ref SecurityAttributes security);
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern nint CreateFilePlain(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern nint CreateFileSecured(string path, uint access, uint share, ref SecurityAttributes security, uint disposition, uint flags, nint template);
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int WriteFile(nint handle, nint buffer, uint count, out uint written, nint overlapped);
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int ReadFile(nint handle, nint buffer, uint count, out uint read, nint overlapped);
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int FlushFileBuffers(nint handle);
        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int GetInfoAttributes(nint handle, int info, out AttributeInfo attributes, uint bytes);
        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int GetInfoStandard(nint handle, int info, out StandardInfo standard, uint bytes);
        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int GetInfoIdentity(nint handle, int info, out IdentityInfo identity, uint bytes);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern uint GetFinalPathNameByHandleW(nint handle, nint path, uint count, uint flags);
        [DllImport("advapi32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern uint GetSecurityInfo(nint handle, uint type, uint information, nint owner, nint group, nint dacl, nint sacl, out nint descriptor);
        [DllImport("advapi32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern uint GetSecurityDescriptorLength(nint descriptor);
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int CloseHandle(nint handle);
        [DllImport("kernel32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern nint LocalFree(nint pointer);
    }
}
