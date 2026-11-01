using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MadClean.Core;

namespace MadClean.Windows;

public sealed partial class WindowsShortcutInventory : IShortcutActionPreview
{
    public static bool HasPendingMetadataRead => WorkerGate.CurrentCount == 0;

    public async Task<CheckResult<ShortcutActionPreview>> InspectAsync(ShortcutRecheckScope scope, CancellationToken token)
    {
        if (token.IsCancellationRequested) return CheckResult<ShortcutActionPreview>.Empty(CheckState.Cancelled,
            "Selected shortcut preview cancelled before any read.");
        if (!OperatingSystem.IsWindows()) return CheckResult<ShortcutActionPreview>.Empty(CheckState.Unsupported,
            "Selected shortcut file evidence requires Windows; no identity or content evidence was simulated.");
        if (!ValidSelectedScope(scope)) return CheckResult<ShortcutActionPreview>.Empty(CheckState.Unsupported,
            "Select one supported .lnk observation beneath its explicit local folder before requesting file evidence.");
        if (DateTimeOffset.UtcNow - scope.ValidatedUtc > ShortcutActionPreviewPolicy.EvidenceLifetime)
            return ShortcutActionPreviewPolicy.Create(scope, null, null, ShortcutProtectionState.Unknown, token);
        return await RunMetadataWorkerAsync(
            stop => InspectSelectedFile(scope, stop), CheckResult<ShortcutActionPreview>.Empty,
            "Selected shortcut file evidence", CheckState.Unknown, token).ConfigureAwait(false);
    }

    private static CheckResult<ShortcutActionPreview> InspectSelectedFile(ShortcutRecheckScope scope, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var protection = PreviewProtection(scope, token);
        if (protection != ShortcutProtectionState.OutsideKnownProtectedFolders)
            return ShortcutActionPreviewPolicy.Create(scope, null, null, protection, token);
        // Protection is resolved before opening any selected-file contents or loading Shell metadata.
        try
        {
            if (!WindowsShortcutNative.TryOpenAncestry(scope.RootPath, true, token, out var root, out _))
                return PreviewReadFailure(scope, protection, "The selected root is currently inaccessible or outside the supported guarded local scope.", CheckState.Unavailable, token);
            using (root)
            {
                if (!WindowsShortcutNative.TryOpenAncestry(scope.ShortcutPath, false, token, out var entry, out _))
                    return PreviewReadFailure(scope, protection, "The selected shortcut is absent, inaccessible or has unsupported ancestry.", CheckState.Unavailable, token);
                using (entry)
                using (var handle = WindowsShortcutNative.OpenReadOnlyLink(scope.ShortcutPath))
                {
                    if (handle.IsInvalid) return PreviewReadFailure(scope, protection, "Read-only shared access to the selected shortcut was unavailable.",
                        CheckState.Unavailable, token, System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                    // Buffer size1 avoids retaining a second buffered copy of private link bytes.
                    using var stream = new FileStream(handle, FileAccess.Read, 1, false);
                    var started = DateTimeOffset.UtcNow;
                    var before = WindowsShortcutPreviewNative.Observe(handle, scope.ShortcutPath);
                    byte[]? first = null, second = null;
                    try
                    {
                        first = WindowsShortcutPreviewNative.Hash(stream, before.Bytes, token);
                        var afterFirst = WindowsShortcutPreviewNative.Observe(handle, scope.ShortcutPath);
                        if (before != afterFirst) throw new WindowsShortcutPreviewFailure(ShortcutPreviewReadFailure.ReadChanged);
                        var inspection = InspectShortcut(scope.ShortcutPath, (ulong)before.Bytes, token);
                        var afterMetadata = WindowsShortcutPreviewNative.Observe(handle, scope.ShortcutPath);
                        second = WindowsShortcutPreviewNative.Hash(stream, before.Bytes, token);
                        var after = WindowsShortcutPreviewNative.Observe(handle, scope.ShortcutPath);
                        token.ThrowIfCancellationRequested();
                        if (before != afterMetadata || before != after || !CryptographicOperations.FixedTimeEquals(first, second))
                            throw new WindowsShortcutPreviewFailure(ShortcutPreviewReadFailure.ReadChanged);
                        var completed = DateTimeOffset.UtcNow;
                        var observation = inspection.Observation;
                        var currentState = inspection.Issue != null || inspection.Skipped ||
                            observation.State is not (ShortcutTargetState.TargetExists or ShortcutTargetState.LocalTargetMissing)
                            ? CheckState.Partial : CheckState.Available;
                        var current = new CheckResult<ShortcutObservation>(currentState,
                            "Fresh guarded selected-link metadata; no activation or target contents.", [observation]);
                        var evidence = new ShortcutFileEvidence(scope.ShortcutPath, before.Volume, before.FileId, before.Bytes,
                            before.Links, Convert.ToHexString(first).ToLowerInvariant(), started, completed, ShortcutReadConsistency.ObservedConsistent);
                        return ShortcutActionPreviewPolicy.Create(scope, current, evidence, protection, token);
                    }
                    finally
                    {
                        if (first != null) CryptographicOperations.ZeroMemory(first);
                        if (second != null) CryptographicOperations.ZeroMemory(second);
                    }
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (WindowsShortcutPreviewFailure failure)
        {
            string reason = failure.Reason switch
            {
                ShortcutPreviewReadFailure.UnsupportedMetadata => "Required native metadata layouts are unsupported.",
                ShortcutPreviewReadFailure.IdentityUnavailable => "A full current file and volume identity was not established.",
                ShortcutPreviewReadFailure.LinkCountUnsupported => "The selected file does not have exactly one reported hard link; this preview does not support aliases.",
                ShortcutPreviewReadFailure.SizeUnsupported => "The selected shortcut is empty or exceeds the 1 MiB content-read bound.",
                ShortcutPreviewReadFailure.UnsafeMetadata => "The selected object has unsupported directory, pending-delete, system, hidden, cloud, reparse or other unsafe metadata.",
                ShortcutPreviewReadFailure.PathUnverified => "The open object's bounded final path did not establish the exact selected local path.",
                _ => "The read bookends did not establish equal bytes and metadata. Own access-time changes can also make this result unknown."
            };
            return PreviewReadFailure(scope, protection, reason, CheckState.Unknown, token);
        }
        catch (System.ComponentModel.Win32Exception exception)
        { return PreviewReadFailure(scope, protection, "A native read-only file metadata query failed.", CheckState.Unavailable, token, exception.NativeErrorCode); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or
            ArgumentException or NotSupportedException or InvalidOperationException or
            CryptographicException or DllNotFoundException or EntryPointNotFoundException)
        { return PreviewReadFailure(scope, protection, "The bounded read-only evidence operation was unavailable; no file identity or missing-target state was reused.", CheckState.Unavailable, token); }
    }

    private static CheckResult<ShortcutActionPreview> PreviewReadFailure(ShortcutRecheckScope scope, ShortcutProtectionState protection,
        string fixedReason, CheckState state, CancellationToken token, int? nativeError = null)
    {
        token.ThrowIfCancellationRequested();
        var result = ShortcutActionPreviewPolicy.Create(scope, CheckResult<ShortcutObservation>.Empty(state, "No accepted current observation."), null, protection, token);
        return result with { Message = result.Message + " " + fixedReason +
            (nativeError.HasValue ? $" Native code 0x{nativeError.Value:X8}." : "") +
            " Refresh the selection or choose another supported file; no rejected file evidence was retained." };
    }

    private static ShortcutProtectionState PreviewProtection(ShortcutRecheckScope scope, CancellationToken token)
    {
        try
        {
            var folders = new List<string>();
            foreach (var folder in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolder.CommonProgramFiles, Environment.SpecialFolder.CommonApplicationData,
                Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolder.Startup, Environment.SpecialFolder.CommonStartup, Environment.SpecialFolder.MyDocuments,
                Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyVideos,
                Environment.SpecialFolder.CommonDocuments, Environment.SpecialFolder.CommonMusic, Environment.SpecialFolder.CommonPictures,
                Environment.SpecialFolder.CommonVideos })
            {
                token.ThrowIfCancellationRequested();
                string? path = PreviewFolder(Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify));
                if (path == null) return ShortcutProtectionState.Unknown;
                folders.Add(path);
            }
            if (Environment.Is64BitOperatingSystem)
                foreach (var folder in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonProgramFilesX86 })
                {
                    token.ThrowIfCancellationRequested();
                    string? path = PreviewFolder(Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify));
                    if (path == null) return ShortcutProtectionState.Unknown;
                    folders.Add(path);
                }
            // Official KnownFolders.h IDs, requested with DONT_VERIFY and never CREATE.
            foreach (var folder in new[] { new Guid("374DE290-123F-4565-9164-39C4925E467B"),
                new Guid("4C5C32FF-BB9D-43B0-B5B4-2D72E54EAAA4"), new Guid("A520A1A4-1780-4FF6-BD18-167343C5AF16") })
            {
                token.ThrowIfCancellationRequested();
                string? path = PreviewFolder(WindowsShortcutPreviewNative.KnownFolder(folder));
                if (path == null) return ShortcutProtectionState.Unknown;
                folders.Add(path);
            }
            string? profile = PreviewFolder(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify));
            if (profile == null) return ShortcutProtectionState.Unknown;
            token.ThrowIfCancellationRequested();
            if (scope.RootPath.Equals(profile, StringComparison.OrdinalIgnoreCase) || scope.ShortcutPath.Equals(profile, StringComparison.OrdinalIgnoreCase) ||
                folders.Any(path => WithinPreview(scope.RootPath, path) || WithinPreview(scope.ShortcutPath, path))) return ShortcutProtectionState.Protected;
            var sensitive = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Windows", "Program Files", "Program Files (x86)", "ProgramData", "AppData", "Documents", "Downloads", "Music", "Pictures", "Videos",
                "Saved Games", "SavedGames", "Saves", "SaveData", "SaveGames", "Credentials", "Vault", ".ssh", ".aws", ".azure", ".gnupg",
                "Profiles", "User Data", "OneAuth", "TokenBroker", "IdentityCache", "Passwords", "Login Data", "Browsers", "Cookies", "History", "Startup"
            };
            if (scope.ShortcutPath.Split('\\').Any(sensitive.Contains)) return ShortcutProtectionState.Protected;
            return ShortcutProtectionState.OutsideKnownProtectedFolders;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or
            ArgumentException or NotSupportedException or DllNotFoundException or EntryPointNotFoundException)
        { return ShortcutProtectionState.Unknown; }
    }
    private static bool WithinPreview(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
    private static string? PreviewFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32767 || path != path.Trim()) return null;
        if (path.Length < 4 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\' || path.Contains('/') ||
            path[2..].IndexOfAny([':', '*', '?', '"', '<', '>', '|', '%']) >= 0) return null;
        var remaining = path.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out var rune, out int consumed) != OperationStatus.Done ||
                Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) return null;
            remaining = remaining[consumed..];
        }
        path = path.TrimEnd('\\');
        if (path.Length < 4 || path[3..].Split('\\').Any(part => part.Length == 0 || part is "." or ".." ||
            part.EndsWith('.') || part.EndsWith(' ') || PreviewDeviceName(part))) return null;
        return char.ToUpperInvariant(path[0]) + path[1..];
    }

    private static bool PreviewDeviceName(string component)
    {
        string stem = component.Split('.')[0];
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase)) return true;
        return stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
            stem[3] is >= '1' and <= '9' or '¹' or '²' or '³';
    }
}
