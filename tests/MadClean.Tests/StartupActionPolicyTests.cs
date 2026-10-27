using System.Text;
using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class StartupActionPolicyTests
{
    [Fact]
    public void MutationGateHasNoRuntimeOptIn() => Assert.False(StartupActionPolicy.ProductionMutationValidated);

    [Theory]
    [InlineData("software\\MadClean.WindowsSmoke\\0123456789abcdef0123456789abcdef", true)]
    [InlineData("Software\\MadClean.WindowsSmoke\\01234567-89ab-cdef-0123-456789abcdef", true)]
    [InlineData("Software\\Microsoft\\Windows\\CurrentVersion", false)]
    [InlineData("Software\\MadClean.WindowsSmoke\\0123456789abcdef0123456789abcdef\\Run", false)]
    [InlineData("Software\\MadClean.WindowsSmoke\\00000000000000000000000000000000", false)]
    [InlineData("Software\\MadClean.WindowsSmoke\\..", false)]
    public void FixtureScopeAcceptsOnlyOwnedSingleGuidSubtree(string path, bool expected) =>
        Assert.Equal(expected, StartupActionPolicy.TryFixtureRegistryBase(path, out _));

    [Theory]
    [InlineData("C:\\repo\\artifacts\\windows-validation-fixtures-0123456789abcdef0123456789abcdef\\startup-backups", true)]
    [InlineData("C:\\repo\\artifacts\\windows-validation-fixtures-0123456789abcdef0123456789abcdee\\startup-backups", false)]
    [InlineData("C:\\repo\\artifacts\\windows-validation-fixtures-0123456789abcdef0123456789abcdef\\startup-backups\\child", false)]
    [InlineData("\\\\server\\artifacts\\windows-validation-fixtures-0123456789abcdef0123456789abcdef\\startup-backups", false)]
    public void BackupScopeRequiresSameGuidAndExactLocalTopology(string path, bool expected) =>
        Assert.Equal(expected, StartupActionPolicy.IsFixtureBackupDirectory(path, @"C:\repo\artifacts", Guid.Parse("0123456789abcdef0123456789abcdef")));

    [Fact]
    public void RawValuesPreserveTypeWithoutExpandingEnvironmentOrNormalizingCommands()
    {
        byte[] bytes = Encoding.Unicode.GetBytes("%LOCALAPPDATA%\\sample.exe /test\0");
        Assert.True(StartupActionPolicy.IsSupportedRawValue(1, bytes));
        Assert.True(StartupActionPolicy.IsSupportedRawValue(2, bytes));
        Assert.False(StartupActionPolicy.IsSupportedRawValue(3, bytes));
        Assert.False(StartupActionPolicy.IsSupportedRawValue(7, bytes));
    }

    [Fact]
    public void MalformedOrAmbiguousRawMetadataCannotBecomeARecoverableValue()
    {
        Assert.False(StartupActionPolicy.IsSupportedRawValue(1, []));
        Assert.False(StartupActionPolicy.IsSupportedRawValue(1, [65, 0, 0]));
        Assert.False(StartupActionPolicy.IsSupportedRawValue(1, [65, 0]));
        Assert.False(StartupActionPolicy.IsSupportedRawValue(1, [0, 0, 65, 0, 0, 0]));
        Assert.False(StartupActionPolicy.IsSupportedRawValue(1, [0, 0xD8, 0, 0]));
        Assert.False(StartupActionPolicy.IsSupportedRawValue(1, [0, 0xDC, 0, 0]));
        Assert.True(StartupActionPolicy.IsSupportedRawValue(1, [0x3D, 0xD8, 0, 0xDE, 0, 0]));
        Assert.False(StartupActionPolicy.IsSupportedRawValue(1, Encoding.Unicode.GetBytes(new string('a', 261) + "\0")));
    }

    [Fact]
    public void ReferencesCannotSelectUnknownSourceOrHideNamesWithControls()
    {
        Assert.False(StartupActionPolicy.IsValidReference(new((StartupRegistrationSource)99, "Selected")));
        Assert.False(StartupActionPolicy.IsValidReference(new(StartupRegistrationSource.CurrentUserRun, "hidden\u202ename")));
        Assert.False(StartupActionPolicy.IsValidReference(new(StartupRegistrationSource.CurrentUserRun, "line\nbreak")));
        Assert.True(StartupActionPolicy.IsValidReference(new(StartupRegistrationSource.CurrentUserRunOnce, "!Deferred")));
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef", true)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF", false)]
    [InlineData("01234567-89ab-cdef-0123-456789abcdef", false)]
    [InlineData("../backup", false)]
    public void BackupIdentityNeverBecomesAnArbitraryPath(string id, bool expected) =>
        Assert.Equal(expected, StartupActionPolicy.IsOpaqueId(id));
}
