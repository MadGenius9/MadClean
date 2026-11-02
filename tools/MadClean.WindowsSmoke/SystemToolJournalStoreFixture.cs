using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using MadClean.Windows;
using Microsoft.Win32.SafeHandles;

namespace MadClean.WindowsSmoke;

internal static partial class NativeFixtures
{
    private static bool SystemToolJournalStoreCleanupConfirmed { get; set; } = true;
    private static readonly List<nint> JournalStoreRetainedDescriptors = [];

    private static void SystemToolJournalStoreFixture(string fixture, string marker, SmokeReport report, CancellationToken token)
    {
        const string check = "system-tool-journal-store-fixture";
        SystemToolJournalStore? store = null;
        bool security = false, fixedBytes = false, contention = false, wrongAcl = false, reparse = false, oversized = false, reopened = false;
        string stage = "initializing";
        bool descriptorsReleased = true;
        bool previousUnconfirmed = !SystemToolJournalStoreCleanupConfirmed || JournalStoreRetainedDescriptors.Count != 0 || SystemToolJournalStore.HasUncertainCleanup;
        string? createdLink = null, ownedTarget = null;
        try
        {
            token.ThrowIfCancellationRequested();
            if (previousUnconfirmed)
                throw new IOException();
            SystemToolJournalStoreCleanupConfirmed = false;
            stage = "primary-open";
            store = SystemToolJournalStore.OpenOwnedFixture(fixture, marker, token: token);
            if (!store.CreatedIntentFile) throw new SmokeAssertionException("journal-primary-not-new");
            using var identity = WindowsIdentity.GetCurrent();
            string sid = identity.User?.Value ?? throw new IOException();
            stage = "independent-security-audit";
            JournalStoreAuditSecurity(store.DirectoryHandle, sid, true, ref descriptorsReleased);
            JournalStoreAuditSecurity(store.IntentHandle, sid, false, ref descriptorsReleased);
            security = true;
            stage = "fixed-intent-flush-readback";
            fixedBytes = store.WriteAndReadFixedIntent(token);
            if (!fixedBytes) throw new SmokeAssertionException("journal-fixed-intent-not-matched");
            stage = "same-process-contention";
            try
            {
                using var unexpected = SystemToolJournalStore.OpenOwnedFixture(fixture, marker, token: token);
                throw new SmokeAssertionException("journal-exclusive-open-was-shared");
            }
            catch (SystemToolJournalStoreFailure failure) when (failure.Reason == SystemToolJournalStoreReason.StoreBusy && failure.NativeCode == 32)
            { contention = true; }
            store.Dispose(); store = null;
            stage = "existing-primary-reopen";
            using (var existing = SystemToolJournalStore.OpenOwnedFixture(fixture, marker, token: token))
            {
                if (existing.CreatedIntentFile || !existing.WriteAndReadFixedIntent(token))
                    throw new SmokeAssertionException("journal-existing-record-overwritten-or-mismatch");
                JournalStoreAuditSecurity(existing.IntentHandle, sid, false, ref descriptorsReleased);
                reopened = true;
            }
            if (SystemToolJournalStore.HasUncertainCleanup) throw new IOException();

            stage = "wrong-acl-owned-leaf";
            string bad = Path.Combine(fixture, SystemToolJournalStore.FixtureLeafName(SystemToolJournalFixtureCase.WrongAcl));
            // Deliberately insecure NEW owned directory; no existing ACL is modified.
            JournalStoreCreateDirectory(bad, $"O:{sid}D:P(A;OICI;FA;;;{sid})(A;OICI;FA;;;SY)(A;OICI;FR;;;WD)", ref descriptorsReleased);
            wrongAcl = JournalStoreRejects(fixture, marker, SystemToolJournalFixtureCase.WrongAcl, SystemToolJournalStoreReason.SecurityUnsupported, token);
            if (File.Exists(Path.Combine(bad, "intent.bin"))) throw new SmokeAssertionException("journal-wrong-acl-created-content");

            stage = "oversize-owned-leaf";
            string large = Path.Combine(fixture, SystemToolJournalStore.FixtureLeafName(SystemToolJournalFixtureCase.Oversize));
            JournalStoreCreateDirectory(large, JournalStoreSddl(sid, true), ref descriptorsReleased);
            JournalStoreCreateOversize(Path.Combine(large, "intent.bin"), JournalStoreSddl(sid, false), ref descriptorsReleased);
            oversized = JournalStoreRejects(fixture, marker, SystemToolJournalFixtureCase.Oversize, SystemToolJournalStoreReason.Oversize, token);
            if (new FileInfo(Path.Combine(large, "intent.bin")).Length != SystemToolJournalStore.MaximumIntentBytes + 1)
                throw new SmokeAssertionException("journal-oversize-file-changed");

            stage = "reparse-owned-leaf";
            string target = Path.Combine(fixture, "system-tool-journal-reparse-target");
            JournalStoreCreateDirectory(target, JournalStoreSddl(sid, true), ref descriptorsReleased);
            string link = Path.Combine(fixture, SystemToolJournalStore.FixtureLeafName(SystemToolJournalFixtureCase.Reparse));
            Directory.CreateSymbolicLink(link, target);
            createdLink = link; ownedTarget = target;
            reparse = JournalStoreRejects(fixture, marker, SystemToolJournalFixtureCase.Reparse, SystemToolJournalStoreReason.UnsafeObject, token);
            if (Directory.EnumerateFileSystemEntries(target).Any()) throw new SmokeAssertionException("journal-reparse-target-was-written");
            stage = "owned-store-facts-observed";
            if (!security || !fixedBytes || !contention || !wrongAcl || !reparse || !oversized || !reopened)
                throw new SmokeAssertionException("journal-store-incomplete-proof");
        }
        catch (SmokeAssertionException failure)
        {
            report.Fail(check, "Owned journal-store invariant " + failure.Code + " failed. No production store, launch or recovery operation ran.");
            return;
        }
        catch (OperationCanceledException)
        { report.Unknown(check, "Owned journal-store observation was cancelled; no production store or servicing operation ran."); return; }
        catch (SystemToolJournalStoreFailure failure)
        {
            report.Unknown(check, $"stage={stage}; reason={failure.Reason}; nativeCode={(failure.NativeCode is int code ? $"0x{code:X8}" : "Unknown")}; explicitSecurityObserved={security}; fixedBytesObserved={fixedBytes}; contentionObserved={contention}; wrongAclRefused={wrongAcl}; oversizeRefused={oversized}; reparseRefused={reparse}. Missing prerequisites are not a private-store or recovery pass.");
            return;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or
            NotSupportedException or ArgumentException or InvalidOperationException or System.Security.SecurityException)
        {
            report.Unknown(check, $"stage={stage}; explicitSecurityObserved={security}; fixedBytesObserved={fixedBytes}; contentionObserved={contention}; wrongAclRefused={wrongAcl}; oversizeRefused={oversized}; reparseRefused={reparse}. An owned prerequisite or symbolic-link permission was unavailable; no private-store/recovery success is inferred.");
            return;
        }
        finally
        {
            try { store?.Dispose(); }
            catch (Exception) { descriptorsReleased = false; }
            bool linkCleanupConfirmed = true;
            if (createdLink != null && ownedTarget != null)
            {
                try { JournalStoreRemoveOwnedLink(fixture, marker, createdLink, ownedTarget); }
                catch (Exception) { linkCleanupConfirmed = false; }
            }
            SystemToolJournalStoreCleanupConfirmed = !previousUnconfirmed && linkCleanupConfirmed && descriptorsReleased &&
                JournalStoreRetainedDescriptors.Count == 0 && !SystemToolJournalStore.HasUncertainCleanup;
        }
        if (!SystemToolJournalStoreCleanupConfirmed)
        {
            report.Unknown(check, "Owned journal-store cleanup was not confirmed. Retain the fixture tree; no private-store readiness is claimed.");
            return;
        }
        report.Pass(check, "explicitOwnerAndProtectedDaclMatched=true; newFixedIntentFlushedAndReadBack=true; sameProcessExclusiveOpenRefused=true; existingIntentReadWithoutReplacement=true; wrongAclRefused=true; reparseRefused=true; oversizedLengthUnchanged=true; cleanupConfirmed=true. These are facts about one owned development store only, not a real operation journal, authorship, crash/power-loss recovery, cross-instance/user coordination or production readiness.");
    }

    private static void JournalStoreRemoveOwnedLink(string fixture, string marker, string link, string target)
    {
        if (Path.GetDirectoryName(link) != fixture || Path.GetDirectoryName(target) != fixture ||
            Path.GetFileName(link) != SystemToolJournalStore.FixtureLeafName(SystemToolJournalFixtureCase.Reparse) ||
            Path.GetFileName(target) != "system-tool-journal-reparse-target") throw new IOException();
        // Revalidate marker/root and hold ordinary owned-store ancestry. This opens
        // the primary fixed record without rewriting it; no linked path is opened.
        using var ownership = SystemToolJournalStore.OpenOwnedFixture(fixture, marker);
        if (!WindowsShortcutNative.TryOpenAncestry(target, true, CancellationToken.None, out var before, out _)) throw new IOException();
        using (before)
        {
            var targetInfo = new DirectoryInfo(target);
            var linkInfo = new DirectoryInfo(link);
            if (targetInfo.LinkTarget != null || (targetInfo.Attributes & FileAttributes.ReparsePoint) != 0 ||
                (targetInfo.Attributes & FileAttributes.Directory) == 0 || Directory.EnumerateFileSystemEntries(target).Any() ||
                (linkInfo.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != (FileAttributes.ReparsePoint | FileAttributes.Directory) ||
                !string.Equals(linkInfo.LinkTarget, target, StringComparison.OrdinalIgnoreCase)) throw new IOException();
            var identity = before!.LeafInformation;
            // Nonrecursive deletion removes only this exact created link object.
            // Never recurse, enumerate the link, or delete its target directory.
            Directory.Delete(link, recursive: false);
            if (Directory.Exists(link) || File.Exists(link) || !Directory.Exists(target) || Directory.EnumerateFileSystemEntries(target).Any() ||
                !WindowsShortcutNative.TryOpenAncestry(target, true, CancellationToken.None, out var after, out _)) throw new IOException();
            using (after)
            {
                var current = after!.LeafInformation;
                if (identity.VolumeSerialNumber != current.VolumeSerialNumber || identity.FileIndexHigh != current.FileIndexHigh ||
                    identity.FileIndexLow != current.FileIndexLow || identity.FileAttributes != current.FileAttributes)
                    throw new IOException();
            }
        }
    }

    private static bool JournalStoreRejects(string fixture, string marker, SystemToolJournalFixtureCase fixtureCase,
        SystemToolJournalStoreReason expected, CancellationToken token)
    {
        try
        {
            using var unexpected = SystemToolJournalStore.OpenOwnedFixture(fixture, marker, fixtureCase, token);
            throw new SmokeAssertionException("journal-unsupported-case-was-accepted");
        }
        catch (SystemToolJournalStoreFailure failure) when (failure.Reason == expected) { return true; }
    }
    private static string JournalStoreSddl(string sid, bool directory) =>
        $"O:{sid}D:P(A;{(directory ? "OICI" : "")};FA;;;{sid})(A;{(directory ? "OICI" : "")};FA;;;SY)";

    // Independent audit: inspect the actual descriptors through the returned held
    // handles. No SID, ACE bytes, native pointer or file contents leave this fixture.
    private static void JournalStoreAuditSecurity(SafeFileHandle handle, string sid, bool directory, ref bool released)
    {
        uint code = JournalStoreGetSecurityInfo(handle, 1, 5, 0, 0, 0, 0, out nint descriptor);
        try
        {
            if (code != 0 || descriptor == 0 || !JournalStoreIsValidSecurityDescriptor(descriptor)) throw new IOException();
            uint size = JournalStoreGetSecurityDescriptorLength(descriptor);
            if (size is < 20 or > 65536) throw new IOException();
            byte[] bytes = new byte[size];
            try
            {
                Marshal.Copy(descriptor, bytes, 0, bytes.Length);
                var actual = new RawSecurityDescriptor(bytes, 0);
                if (actual.Owner?.Value != sid || (actual.ControlFlags & ControlFlags.DiscretionaryAclProtected) == 0 ||
                    actual.DiscretionaryAcl is not { Count: 2 } acl) throw new SmokeAssertionException("journal-audit-owner-or-dacl");
                var expected = new HashSet<string>(StringComparer.Ordinal) { sid, "S-1-5-18" };
                foreach (var entry in acl)
                {
                    if (entry is not CommonAce ace || ace.IsCallback || ace.AceQualifier != AceQualifier.AccessAllowed ||
                        ace.AccessMask != 0x1F01FF || ace.AceFlags != (directory ? AceFlags.ObjectInherit | AceFlags.ContainerInherit : AceFlags.None) ||
                        !expected.Remove(ace.SecurityIdentifier.Value)) throw new SmokeAssertionException("journal-audit-ace-policy");
                }
                if (expected.Count != 0) throw new SmokeAssertionException("journal-audit-required-principal");
            }
            finally { Array.Clear(bytes); }
        }
        finally { JournalStoreReleaseDescriptor(descriptor, ref released); }
        if (!released) throw new IOException();
    }
    private static void JournalStoreReleaseDescriptor(nint descriptor, ref bool released)
    {
        if (descriptor == 0 || JournalStoreLocalFree(descriptor) == 0) return;
        // Retain the one failed native allocation and stop this fixture immediately;
        // no later call retries the free or accumulates additional native queries.
        JournalStoreRetainedDescriptors.Add(descriptor); released = false;
    }

    private static void JournalStoreCreateDirectory(string path, string sddl, ref bool released)
    {
        if (!JournalStoreConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out nint descriptor, out _)) throw new IOException();
        try
        {
            var attributes = new JournalStoreSecurityAttributes { Length = 24, Descriptor = descriptor };
            if (!JournalStoreCreateDirectoryW(path, ref attributes)) throw new IOException();
        }
        finally { JournalStoreReleaseDescriptor(descriptor, ref released); }
        if (!released) throw new IOException();
    }
    private static void JournalStoreCreateOversize(string path, string sddl, ref bool released)
    {
        if (!JournalStoreConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out nint descriptor, out _)) throw new IOException();
        try
        {
            var attributes = new JournalStoreSecurityAttributes { Length = 24, Descriptor = descriptor };
            using var handle = JournalStoreCreateFileW(path, 0xC0000000, 0, ref attributes, 1, 0x00300000, 0);
            if (handle.IsInvalid) throw new IOException();
            using var stream = new FileStream(handle, FileAccess.ReadWrite);
            stream.SetLength(SystemToolJournalStore.MaximumIntentBytes + 1);
            stream.Flush(true);
        }
        finally { JournalStoreReleaseDescriptor(descriptor, ref released); }
        if (!released) throw new IOException();
    }

    [StructLayout(LayoutKind.Sequential)] private struct JournalStoreSecurityAttributes
    { internal uint Length; internal nint Descriptor; internal int Inherit; }
    [DllImport("advapi32.dll", EntryPoint = "GetSecurityInfo", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint JournalStoreGetSecurityInfo(SafeFileHandle handle, uint type, uint information, nint owner, nint group, nint dacl, nint sacl, out nint descriptor);
    [DllImport("advapi32.dll", EntryPoint = "IsValidSecurityDescriptor", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool JournalStoreIsValidSecurityDescriptor(nint descriptor);
    [DllImport("advapi32.dll", EntryPoint = "GetSecurityDescriptorLength", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint JournalStoreGetSecurityDescriptorLength(nint descriptor);
    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool JournalStoreConvertStringSecurityDescriptorToSecurityDescriptorW(string text, uint revision, out nint descriptor, out uint size);
    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool JournalStoreCreateDirectoryW(string path, ref JournalStoreSecurityAttributes attributes);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern SafeFileHandle JournalStoreCreateFileW(string path, uint access, uint share, ref JournalStoreSecurityAttributes attributes, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", EntryPoint = "LocalFree", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern nint JournalStoreLocalFree(nint memory);
}
