using System.ComponentModel;
using System.Globalization;
using System.IO;
using MadClean.Core;
using MadClean.Windows;
using Microsoft.Win32.SafeHandles;

namespace MadClean.WindowsSmoke;

internal static partial class NativeFixtures
{
    private static void SystemToolEmbeddedVersions(SmokeReport report, CancellationToken token)
    {
        const string check = "system-tool-embedded-version-fixture";
        var outcomes = new List<string>();
        int matched = 0;
        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (!Path.IsPathFullyQualified(system))
        {
            report.Unknown(check, "Independent OS system directory unavailable; no fixed-file parser read occurred. No executable identity or crypto-trust proof is claimed.");
            return;
        }
        system = Path.TrimEndingDirectorySeparator(Path.GetFullPath(system));
        foreach (var operation in new[] { SystemToolOperation.DismCheckHealth, SystemToolOperation.SfcVerifyOnly })
        {
            List<SafeFileHandle>? directories = null;
            SafeFileHandle? handle = null;
            FileStream? stream = null;
            try
            {
                token.ThrowIfCancellationRequested();
                var definition = SystemToolActionsCatalog.Find(operation)!;
                string path = Path.Combine(system, definition.FileName);
                // Same existing read-only no-reparse/fixed-local ancestry, attributes and final
                // path guards, but no WinTrust, Prepare, loader/MUI API or tool command here.
                directories = SystemToolTrustNative.LockDirectoryChain(system);
                handle = SystemToolTrustNative.OpenFile(path);
                SystemToolTrustNative.ValidateAttributes(handle, directory: false);
                SystemToolTrustNative.ValidateFinalPath(handle, path);
                stream = new FileStream(handle, FileAccess.Read, 65_536, isAsync: false);
                handle = null; // The FileStream exclusively owns this safe handle now.
                if (stream.Length is <= 0 or > 33_554_432)
                {
                    outcomes.Add($"{operation}:state=Unsupported/resources=unknown/originalNames=unknown/streamRestored=unknown/sourceSite=none/rootLength=unknown/blobLength=unknown/zeroTail=unknown");
                    continue;
                }
                stream.Position = 0;
                var parsed = SystemToolEmbeddedVersionReader.Read(stream, definition.FileName, token);
                bool restored = stream.CanRead && stream.CanSeek && stream.Position == 0;
                string state = Enum.IsDefined(parsed.State) ? parsed.State.ToString() : "Unknown";
                string resources = parsed.ResourceCount is >= 0 and <= 8 ? parsed.ResourceCount.ToString(CultureInfo.InvariantCulture) : "unknown";
                string originals = parsed.OriginalFilenameCount is >= 0 and <= 128 ? parsed.OriginalFilenameCount.ToString(CultureInfo.InvariantCulture) : "unknown";
                string sourceSite = parsed.FailureSite is int site && site is >= 1 and <= 1000 ? site.ToString(CultureInfo.InvariantCulture) : "none";
                string rootLength = parsed.DeclaredRootLength is int declared && declared is >= 0 and <= 65535 ? declared.ToString(CultureInfo.InvariantCulture) : "unknown";
                string blobLength = parsed.ResourceBlobLength is int blob && blob is >= 0 and <= 65535 ? blob.ToString(CultureInfo.InvariantCulture) : "unknown";
                string zeroTail = parsed.RootTrailingBytesZero is bool zero ? zero ? "true" : "false" : "unknown";
                outcomes.Add($"{operation}:state={state}/resources={resources}/originalNames={originals}/streamRestored={restored}/sourceSite={sourceSite}/rootLength={rootLength}/blobLength={blobLength}/zeroTail={zeroTail}");
                if (!restored)
                {
                    report.Fail(check, "The fixed-file embedded reader did not leave its stream open at the original zero position. No metadata values exported or trust inferred.");
                    return;
                }
                if (parsed.State != SystemToolEmbeddedVersionState.Matched) continue;
                if (parsed.ResourceCount is < 1 or > 8 || parsed.OriginalFilenameCount < 1 ||
                    parsed.OriginalFilenameCount > parsed.ResourceCount * 16 || !CanonicalFixedFileVersion(parsed.FileVersion))
                {
                    report.Fail(check, "A Matched fixed-file result contradicted bounded positive resource/name counts or canonical four-part version metadata. No version/original-name values exported.");
                    return;
                }
                matched++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or ArgumentException or
                OperationCanceledException or System.Security.SecurityException or SystemToolTrustFailure)
            {
                outcomes.Add($"{operation}:state=Unavailable/resources=unknown/originalNames=unknown/streamRestored=unknown/sourceSite=none/rootLength=unknown/blobLength=unknown/zeroTail=unknown");
            }
            finally
            {
                stream?.Dispose(); handle?.Dispose();
                if (directories != null)
                    foreach (var directory in directories.AsEnumerable().Reverse()) directory.Dispose();
            }
        }
        string detail = $"fixedMetadataMatchCount={matched}; noCommands=true; cryptoTrustProof=false; " + string.Join("; ", outcomes) +
            ". Only guarded held-stream parsing of the two fixed OS files was requested, without path loaders/MUI, arbitrary files or servicing. This is embedded VERSIONINFO parser evidence, not a signature, publisher, executable identity, Windows integrity or action-eligibility verdict. No paths, original names, versions, hashes or certificate values exported.";
        if (matched == 2) report.Pass(check, detail);
        else report.Unknown(check, detail);
    }

    private static bool CanonicalFixedFileVersion(string? value)
    {
        if (value == null) return false;
        string[] parts = value.Split('.');
        return parts.Length == 4 && parts.All(part => ushort.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out ushort number) &&
            number.ToString(CultureInfo.InvariantCulture) == part);
    }
}
