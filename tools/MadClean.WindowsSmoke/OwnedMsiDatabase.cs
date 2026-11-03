using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MadClean.WindowsSmoke;

// Used only by the separate GitHub-hosted inert MSI qualification mode. No downloaded
// package, transform, custom action, embedded binary or arbitrary command is accepted.
internal sealed class OwnedMsiDatabaseInvariantException : Exception { }

internal sealed class OwnedMsiDatabase : IDisposable
{
    internal const string ProductName = "MadClean MSI qualification fixture";
    internal const string Publisher = "MadClean validation";
    internal const string ProductVersion = "1.0.0";
    internal const string DialogTitle = "MadClean owned MSI qualification";
    private readonly List<uint> handles = [];
    private static readonly List<uint> retainedHandles = [];
    internal static bool RequiresRetention { get; private set; }
    internal static string? LastOperation { get; private set; }
    internal static bool HasKnownInvariantFailure { get; private set; }
    private uint database;
    private readonly Guid component = Guid.NewGuid(), upgrade = Guid.NewGuid(), packageCode = Guid.NewGuid();
    private static readonly string[] Tables = ["Property", "Directory", "Feature", "Component", "FeatureComponents", "File", "Media", "InstallExecuteSequence", "InstallUISequence", "Dialog", "Control", "ControlEvent", "TextStyle"];

    internal OwnedMsiDatabase(string package, Guid product, string target, int payloadBytes)
    {
        if (RequiresRetention || product == Guid.Empty || payloadBytes <= 0 || target.Length > 220 ||
            target.IndexOfAny(['\'', '"', '\r', '\n', '[', ']']) >= 0 || File.Exists(package)) throw new InvalidOperationException();
        MsiRequestPayloadBytes = payloadBytes; LastOperation = "OpenDatabase";
        try
        {
            uint opened = MsiOpenDatabaseW(package, 3, out database);
            ObserveHandle(opened, database);
            Sql("CREATE TABLE `Property` (`Property` CHAR(72) NOT NULL, `Value` CHAR(0) LOCALIZABLE PRIMARY KEY `Property`)");
            Sql("CREATE TABLE `Directory` (`Directory` CHAR(72) NOT NULL, `Directory_Parent` CHAR(72), `DefaultDir` CHAR(255) NOT NULL LOCALIZABLE PRIMARY KEY `Directory`)");
            Sql("CREATE TABLE `Feature` (`Feature` CHAR(38) NOT NULL, `Feature_Parent` CHAR(38), `Title` CHAR(64) LOCALIZABLE, `Description` CHAR(255) LOCALIZABLE, `Display` SHORT, `Level` SHORT NOT NULL, `Directory_` CHAR(72), `Attributes` SHORT NOT NULL PRIMARY KEY `Feature`)");
            Sql("CREATE TABLE `Component` (`Component` CHAR(72) NOT NULL, `ComponentId` CHAR(38), `Directory_` CHAR(72) NOT NULL, `Attributes` SHORT NOT NULL, `Condition` CHAR(255), `KeyPath` CHAR(72) PRIMARY KEY `Component`)");
            Sql("CREATE TABLE `FeatureComponents` (`Feature_` CHAR(38) NOT NULL, `Component_` CHAR(72) NOT NULL PRIMARY KEY `Feature_`, `Component_`)");
            Sql("CREATE TABLE `File` (`File` CHAR(72) NOT NULL, `Component_` CHAR(72) NOT NULL, `FileName` CHAR(255) NOT NULL LOCALIZABLE, `FileSize` LONG NOT NULL, `Version` CHAR(72), `Language` CHAR(20), `Attributes` SHORT, `Sequence` SHORT NOT NULL PRIMARY KEY `File`)");
            Sql("CREATE TABLE `Media` (`DiskId` SHORT NOT NULL, `LastSequence` SHORT NOT NULL, `DiskPrompt` CHAR(64) LOCALIZABLE, `Cabinet` CHAR(255), `VolumeLabel` CHAR(32), `Source` CHAR(72) PRIMARY KEY `DiskId`)");
            Sql("CREATE TABLE `InstallExecuteSequence` (`Action` CHAR(72) NOT NULL, `Condition` CHAR(255), `Sequence` SHORT PRIMARY KEY `Action`)");
            Sql("CREATE TABLE `InstallUISequence` (`Action` CHAR(72) NOT NULL, `Condition` CHAR(255), `Sequence` SHORT PRIMARY KEY `Action`)");
            Sql("CREATE TABLE `Dialog` (`Dialog` CHAR(72) NOT NULL, `HCentering` SHORT NOT NULL, `VCentering` SHORT NOT NULL, `Width` SHORT NOT NULL, `Height` SHORT NOT NULL, `Attributes` LONG, `Title` CHAR(128) LOCALIZABLE, `Control_First` CHAR(50) NOT NULL, `Control_Default` CHAR(50), `Control_Cancel` CHAR(50) PRIMARY KEY `Dialog`)");
            Sql("CREATE TABLE `Control` (`Dialog_` CHAR(72) NOT NULL, `Control` CHAR(50) NOT NULL, `Type` CHAR(20) NOT NULL, `X` SHORT NOT NULL, `Y` SHORT NOT NULL, `Width` SHORT NOT NULL, `Height` SHORT NOT NULL, `Attributes` LONG, `Property` CHAR(50), `Text` CHAR(0) LOCALIZABLE, `Control_Next` CHAR(50), `Help` CHAR(50) LOCALIZABLE PRIMARY KEY `Dialog_`, `Control`)");
            Sql("CREATE TABLE `ControlEvent` (`Dialog_` CHAR(72) NOT NULL, `Control_` CHAR(50) NOT NULL, `Event` CHAR(50) NOT NULL, `Argument` CHAR(255) NOT NULL, `Condition` CHAR(255), `Ordering` SHORT PRIMARY KEY `Dialog_`, `Control_`, `Event`, `Argument`, `Condition`)");
            Sql("CREATE TABLE `TextStyle` (`TextStyle` CHAR(72) NOT NULL, `FaceName` CHAR(32) NOT NULL, `Size` SHORT NOT NULL, `Color` LONG, `StyleBits` SHORT PRIMARY KEY `TextStyle`)");
            Property("ProductCode", product.ToString("B").ToUpperInvariant());
            Property("ProductName", ProductName); Property("ProductVersion", ProductVersion); Property("Manufacturer", Publisher);
            Property("ProductLanguage", "1033"); Property("INSTALLDIR", target.TrimEnd('\\') + "\\");
            Property("ARPINSTALLLOCATION", target); Property("REBOOT", "ReallySuppress"); Property("DefaultUIFont", "DefaultFont");
            Property("UpgradeCode", upgrade.ToString("B").ToUpperInvariant());
            Sql("INSERT INTO `Directory` (`Directory`, `DefaultDir`) VALUES ('TARGETDIR', 'SourceDir')");
            Sql("INSERT INTO `Directory` (`Directory`, `Directory_Parent`, `DefaultDir`) VALUES ('INSTALLDIR', 'TARGETDIR', '.')");
            Sql("INSERT INTO `Feature` (`Feature`, `Title`, `Display`, `Level`, `Directory_`, `Attributes`) VALUES ('FixtureFeature', 'Owned validation payload', 1, 1, 'INSTALLDIR', 0)");
            Sql($"INSERT INTO `Component` (`Component`, `ComponentId`, `Directory_`, `Attributes`, `KeyPath`) VALUES ('FixtureComponent', '{component.ToString("B").ToUpperInvariant()}', 'INSTALLDIR', 256, 'FixturePayload')");
            Sql("INSERT INTO `FeatureComponents` (`Feature_`, `Component_`) VALUES ('FixtureFeature', 'FixtureComponent')");
            Sql($"INSERT INTO `File` (`File`, `Component_`, `FileName`, `FileSize`, `Attributes`, `Sequence`) VALUES ('FixturePayload', 'FixtureComponent', 'fixture.txt', {payloadBytes}, 0, 1)");
            Sql("INSERT INTO `Media` (`DiskId`, `LastSequence`) VALUES (1, 1)");
            (string Name, int Sequence)[] execute = [("ValidateProductID", 700), ("CostInitialize", 800), ("FileCost", 900), ("CostFinalize", 1000), ("InstallValidate", 1400), ("InstallInitialize", 1500), ("ProcessComponents", 1600), ("UnpublishFeatures", 1800), ("RemoveFiles", 3500), ("InstallFiles", 4000), ("RegisterUser", 6000), ("RegisterProduct", 6100), ("PublishFeatures", 6300), ("PublishProduct", 6400), ("InstallFinalize", 6600)];
            foreach (var action in execute) Sql($"INSERT INTO `InstallExecuteSequence` (`Action`, `Sequence`) VALUES ('{action.Name}', {action.Sequence})");
            foreach (var action in new[] { ("CostInitialize", 800), ("FileCost", 900), ("CostFinalize", 1000), ("ExecuteAction", 1300) })
                Sql($"INSERT INTO `InstallUISequence` (`Action`, `Sequence`) VALUES ('{action.Item1}', {action.Item2})");
            Sql("INSERT INTO `InstallUISequence` (`Action`, `Condition`, `Sequence`) VALUES ('OwnedRemoveConfirm', 'REMOVE=\"ALL\"', 1100)");
            Sql("INSERT INTO `TextStyle` (`TextStyle`, `FaceName`, `Size`, `StyleBits`) VALUES ('DefaultFont', 'Tahoma', 8, 0)");
            Sql($"INSERT INTO `Dialog` (`Dialog`, `HCentering`, `VCentering`, `Width`, `Height`, `Attributes`, `Title`, `Control_First`, `Control_Default`, `Control_Cancel`) VALUES ('OwnedRemoveConfirm', 50, 50, 280, 95, 3, '{DialogTitle}', 'Remove', 'Remove', 'Cancel')");
            Sql("INSERT INTO `Control` (`Dialog_`, `Control`, `Type`, `X`, `Y`, `Width`, `Height`, `Attributes`, `Text`) VALUES ('OwnedRemoveConfirm', 'Text', 'Text', 10, 10, 260, 35, 3, 'Remove only the newly generated MadClean validation product?')");
            Sql("INSERT INTO `Control` (`Dialog_`, `Control`, `Type`, `X`, `Y`, `Width`, `Height`, `Attributes`, `Text`, `Control_Next`) VALUES ('OwnedRemoveConfirm', 'Remove', 'PushButton', 110, 60, 70, 20, 3, 'Remove', 'Cancel')");
            Sql("INSERT INTO `Control` (`Dialog_`, `Control`, `Type`, `X`, `Y`, `Width`, `Height`, `Attributes`, `Text`, `Control_Next`) VALUES ('OwnedRemoveConfirm', 'Cancel', 'PushButton', 190, 60, 70, 20, 3, 'Cancel', 'Remove')");
            Sql("INSERT INTO `ControlEvent` (`Dialog_`, `Control_`, `Event`, `Argument`, `Condition`, `Ordering`) VALUES ('OwnedRemoveConfirm', 'Remove', 'EndDialog', 'Return', '1', 1)");
            Sql("INSERT INTO `ControlEvent` (`Dialog_`, `Control_`, `Event`, `Argument`, `Condition`, `Ordering`) VALUES ('OwnedRemoveConfirm', 'Cancel', 'EndDialog', 'Exit', '1', 1)");
            LastOperation = "SummarySet";
            uint summaryCode = MsiGetSummaryInformationW(database, null, 6, out uint summary); ObserveHandle(summaryCode, summary);
            SetSummary(summary, 1, 2, 1252, null); SetSummary(summary, 2, 30, 0, ProductName);
            SetSummary(summary, 7, 30, 0, "x64;1033"); SetSummary(summary, 9, 30, 0, packageCode.ToString("B").ToUpperInvariant());
            SetSummary(summary, 14, 3, 200, null); SetSummary(summary, 15, 3, 0, null);
            Check(MsiSummaryInfoPersist(summary)); Close(summary); LastOperation = "Commit"; Check(MsiDatabaseCommit(database));
        }
        catch { Dispose(); throw; }
    }

    private void Property(string name, string value) => Sql($"INSERT INTO `Property` (`Property`, `Value`) VALUES ('{name}', '{value}')");
    private static void SetSummary(uint summary, uint property, uint type, int integer, string? text) => Check(MsiSummaryInfoSetPropertyW(summary, property, type, integer, 0, text));
    private void Sql(string sql)
    {
        string table = sql.Split('`')[1];
        if (!Tables.Contains(table)) throw new InvalidOperationException();
        LastOperation = (sql.StartsWith("CREATE TABLE", StringComparison.Ordinal) ? "Create" : "Insert") + table;
        uint view = 0;
        uint viewCode = MsiDatabaseOpenViewW(database, sql, out view); ObserveHandle(viewCode, view);
        try { Check(MsiViewExecute(view, 0)); Check(MsiViewClose(view)); }
        finally { Close(view); }
    }
    internal void Verify(Guid product, string target)
    {
        if (RequiresRetention) throw new InvalidOperationException();
        var actual = Strings("SELECT `Name` FROM `_Tables`", 64);
        if (actual.Count != Tables.Length || !Tables.All(actual.Contains)) FailInvariant();
        string?[][] properties = [
            ["ProductCode", product.ToString("B").ToUpperInvariant()], ["ProductName", ProductName], ["ProductVersion", ProductVersion], ["Manufacturer", Publisher],
            ["ProductLanguage", "1033"], ["INSTALLDIR", target.TrimEnd('\\') + "\\"], ["ARPINSTALLLOCATION", target], ["REBOOT", "ReallySuppress"],
            ["DefaultUIFont", "DefaultFont"], ["UpgradeCode", upgrade.ToString("B").ToUpperInvariant()]];
        VerifyRows("Property", ["Property", "Value"], [], properties);
        VerifyRows("Directory", ["Directory", "Directory_Parent", "DefaultDir"], [], [["TARGETDIR", null, "SourceDir"], ["INSTALLDIR", "TARGETDIR", "."]]);
        VerifyRows("Feature", ["Feature", "Feature_Parent", "Title", "Description", "Display", "Level", "Directory_", "Attributes"], [5, 6, 8],
            [["FixtureFeature", null, "Owned validation payload", null, "1", "1", "INSTALLDIR", "0"]]);
        VerifyRows("Component", ["Component", "ComponentId", "Directory_", "Attributes", "Condition", "KeyPath"], [4],
            [["FixtureComponent", component.ToString("B").ToUpperInvariant(), "INSTALLDIR", "256", null, "FixturePayload"]]);
        VerifyRows("FeatureComponents", ["Feature_", "Component_"], [], [["FixtureFeature", "FixtureComponent"]]);
        VerifyRows("File", ["File", "Component_", "FileName", "FileSize", "Version", "Language", "Attributes", "Sequence"], [4, 7, 8],
            [["FixturePayload", "FixtureComponent", "fixture.txt", MsiRequestPayloadBytes.ToString(System.Globalization.CultureInfo.InvariantCulture), null, null, "0", "1"]]);
        VerifyRows("Media", ["DiskId", "LastSequence", "DiskPrompt", "Cabinet", "VolumeLabel", "Source"], [1, 2], [["1", "1", null, null, null, null]]);
        string?[][] execution = [["ValidateProductID", null, "700"], ["CostInitialize", null, "800"], ["FileCost", null, "900"], ["CostFinalize", null, "1000"],
            ["InstallValidate", null, "1400"], ["InstallInitialize", null, "1500"], ["ProcessComponents", null, "1600"], ["UnpublishFeatures", null, "1800"],
            ["RemoveFiles", null, "3500"], ["InstallFiles", null, "4000"], ["RegisterUser", null, "6000"], ["RegisterProduct", null, "6100"],
            ["PublishFeatures", null, "6300"], ["PublishProduct", null, "6400"], ["InstallFinalize", null, "6600"]];
        VerifyRows("InstallExecuteSequence", ["Action", "Condition", "Sequence"], [3], execution);
        VerifyRows("InstallUISequence", ["Action", "Condition", "Sequence"], [3],
            [["CostInitialize", null, "800"], ["FileCost", null, "900"], ["CostFinalize", null, "1000"], ["ExecuteAction", null, "1300"], ["OwnedRemoveConfirm", "REMOVE=\"ALL\"", "1100"]]);
        VerifyRows("Dialog", ["Dialog", "HCentering", "VCentering", "Width", "Height", "Attributes", "Title", "Control_First", "Control_Default", "Control_Cancel"], [2, 3, 4, 5, 6],
            [["OwnedRemoveConfirm", "50", "50", "280", "95", "3", DialogTitle, "Remove", "Remove", "Cancel"]]);
        VerifyRows("Control", ["Dialog_", "Control", "Type", "X", "Y", "Width", "Height", "Attributes", "Property", "Text", "Control_Next", "Help"], [4, 5, 6, 7, 8],
            [["OwnedRemoveConfirm", "Text", "Text", "10", "10", "260", "35", "3", null, "Remove only the newly generated MadClean validation product?", null, null],
             ["OwnedRemoveConfirm", "Remove", "PushButton", "110", "60", "70", "20", "3", null, "Remove", "Cancel", null],
             ["OwnedRemoveConfirm", "Cancel", "PushButton", "190", "60", "70", "20", "3", null, "Cancel", "Remove", null]]);
        VerifyRows("ControlEvent", ["Dialog_", "Control_", "Event", "Argument", "Condition", "Ordering"], [6],
            [["OwnedRemoveConfirm", "Remove", "EndDialog", "Return", "1", "1"], ["OwnedRemoveConfirm", "Cancel", "EndDialog", "Exit", "1", "1"]]);
        VerifyRows("TextStyle", ["TextStyle", "FaceName", "Size", "Color", "StyleBits"], [3, 4, 5], [["DefaultFont", "Tahoma", "8", null, "0"]]);
        LastOperation = "SummaryRead";
        uint summaryCode = MsiGetSummaryInformationW(database, null, 0, out uint summary); ObserveHandle(summaryCode, summary);
        try
        {
            foreach (var expected in new[] { (7u, "x64;1033"), (9u, packageCode.ToString("B").ToUpperInvariant()) })
            {
                var value = new StringBuilder(128); uint size = 128;
                Check(MsiSummaryInfoGetPropertyW(summary, expected.Item1, out uint type, out _, 0, value, ref size));
                if (type != 30 || value.ToString() != expected.Item2) FailInvariant();
            }
            foreach (var expected in new[] { (14u, 200), (15u, 0) })
            {
                uint size = 0;
                Check(MsiSummaryInfoGetPropertyW(summary, expected.Item1, out uint type, out int value, 0, null, ref size));
                if (type != 3 || value != expected.Item2) FailInvariant();
            }
        }
        finally { Close(summary); }
    }
    private int MsiRequestPayloadBytes;
    private void VerifyRows(string table, string[] columns, int[] numericFields, string?[][] expected)
    {
        LastOperation = "Read" + table;
        uint viewCode = MsiDatabaseOpenViewW(database, "SELECT " + string.Join(", ", columns.Select(column => "`" + column + "`")) + " FROM `" + table + "`", out uint view); ObserveHandle(viewCode, view);
        var actual = new List<string?[]>();
        try
        {
            Check(MsiViewExecute(view, 0));
            for (int row = 0; row <= expected.Length; row++)
            {
                uint code = MsiViewFetch(view, out uint record);
                if (code == 259) { if (record != 0) RetainUnknown(record); break; }
                ObserveHandle(code, record);
                try
                {
                    if (row == expected.Length || MsiRecordGetFieldCount(record) != columns.Length) FailInvariant();
                    var values = new string?[columns.Length];
                    for (uint field = 1; field <= columns.Length; field++)
                    {
                        if (MsiRecordIsNull(record, field)) continue;
                        if (numericFields.Contains((int)field)) values[field - 1] = MsiRecordGetInteger(record, field).ToString(System.Globalization.CultureInfo.InvariantCulture);
                        else
                        {
                            var text = new StringBuilder(2048); uint length = 2048;
                            Check(MsiRecordGetStringW(record, field, text, ref length)); values[field - 1] = text.ToString();
                        }
                    }
                    actual.Add(values);
                }
                finally { Close(record); }
            }
            Check(MsiViewClose(view));
            if (actual.Count != expected.Length || expected.Any(row => actual.Count(item => item.SequenceEqual(row)) != 1)) FailInvariant();
        }
        finally { Close(view); }
    }
    private List<string> Strings(string sql, int maximum)
    {
        LastOperation = "ReadTableNames";
        uint viewCode = MsiDatabaseOpenViewW(database, sql, out uint view); ObserveHandle(viewCode, view);
        var result = new List<string>();
        try
        {
            Check(MsiViewExecute(view, 0));
            for (int i = 0; i <= maximum; i++)
            {
                uint code = MsiViewFetch(view, out uint record);
                if (code == 259) { if (record != 0) RetainUnknown(record); Check(MsiViewClose(view)); return result; }
                ObserveHandle(code, record);
                try
                {
                    var text = new StringBuilder(2048); uint size = 2048;
                    Check(MsiRecordGetStringW(record, 1, text, ref size)); result.Add(text.ToString());
                }
                finally { Close(record); }
            }
            throw new InvalidOperationException();
        }
        finally { Close(view); }
    }
    private void ObserveHandle(uint code, uint handle)
    {
        if (code != 0) { if (handle != 0) RetainUnknown(handle); Check(code); }
        Add(handle);
    }
    private void RetainUnknown(uint handle)
    {
        RequiresRetention = true; retainedHandles.Add(handle); retainedHandles.AddRange(handles); handles.Clear();
        throw new InvalidOperationException();
    }
    private void Add(uint handle) { if (handle == 0) FailInvariant(); handles.Add(handle); }
    private void Close(uint handle)
    {
        if (RequiresRetention) return;
        handles.Remove(handle);
        uint closeCode = MsiCloseHandle(handle);
        if (closeCode != 0)
        {
            LastOperation = "HandleClose";
            RequiresRetention = true; retainedHandles.Add(handle); retainedHandles.AddRange(handles); handles.Clear();
            throw new Win32Exception(unchecked((int)closeCode));
        }
    }
    public void Dispose()
    {
        if (RequiresRetention) return;
        foreach (uint handle in handles.ToArray().Reverse()) Close(handle);
        database = 0;
    }
    private static void FailInvariant() { HasKnownInvariantFailure = true; throw new OwnedMsiDatabaseInvariantException(); }
    private static void Check(uint code) { if (code != 0) throw new Win32Exception(unchecked((int)code)); }
    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint MsiOpenDatabaseW(string path, nint persist, out uint database);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint MsiDatabaseOpenViewW(uint database, string query, out uint view);
    [DllImport("msi.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint MsiViewExecute(uint view, uint record);
    [DllImport("msi.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint MsiViewFetch(uint view, out uint record);
    [DllImport("msi.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint MsiViewClose(uint view);
    [DllImport("msi.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint MsiCloseHandle(uint handle);
    [DllImport("msi.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint MsiDatabaseCommit(uint database);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint MsiGetSummaryInformationW(uint database, string? path, uint updateCount, out uint summary);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint MsiSummaryInfoSetPropertyW(uint summary, uint property, uint type, int value, nint time, string? text);
    [DllImport("msi.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint MsiSummaryInfoPersist(uint summary);
    [DllImport("msi.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint MsiRecordGetFieldCount(uint record);
    [DllImport("msi.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool MsiRecordIsNull(uint record, uint field);
    [DllImport("msi.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int MsiRecordGetInteger(uint record, uint field);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint MsiSummaryInfoGetPropertyW(uint summary, uint property, out uint type, out int value, nint time, StringBuilder? text, ref uint size);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint MsiRecordGetStringW(uint record, uint field, StringBuilder buffer, ref uint size);
}
