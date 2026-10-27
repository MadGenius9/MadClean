using System.Collections.Generic;
using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class GuidedWindowsActionsTests
{
    [Theory]
    [InlineData(GuidedWindowsTask.StartupSettings, "ms-settings:startupapps")]
    [InlineData(GuidedWindowsTask.InstalledApps, "ms-settings:appsfeatures")]
    [InlineData(GuidedWindowsTask.DefenderProtection, "ms-settings:windowsdefender")]
    [InlineData(GuidedWindowsTask.WindowsUpdates, "ms-settings:windowsupdate")]
    [InlineData(GuidedWindowsTask.OptionalDriverUpdates, "ms-settings:windowsupdate-optionalupdates")]
    [InlineData(GuidedWindowsTask.SystemFileRepairGuide, "https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/sfc")]
    public void EachTaskHasOnlyItsReviewedFixedNavigationTarget(GuidedWindowsTask task, string target)
    {
        var action = GuidedWindowsActions.Find(task);
        Assert.NotNull(action);
        Assert.Equal(target, action.TargetUri.OriginalString);
        Assert.True(GuidedWindowsActions.IsAllowedUri(task, new Uri(target)));
        Assert.Contains("Unknown", action.Limitations);
        Assert.Contains("no elevation", action.Permissions, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(999)]
    public void UnknownTasksDoNotGetAnArbitraryFallback(int value)
    {
        var task = (GuidedWindowsTask)value;
        Assert.Null(GuidedWindowsActions.Find(task));
        Assert.False(GuidedWindowsActions.IsAllowedUri(task, new Uri("ms-settings:startupapps")));
    }

    [Fact]
    public void ATaskCannotBeRedirectedToAnotherOtherwiseAllowedTask()
    {
        Assert.False(GuidedWindowsActions.IsAllowedUri(GuidedWindowsTask.InstalledApps, new Uri("ms-settings:windowsupdate")));
        Assert.False(GuidedWindowsActions.IsAllowedUri(GuidedWindowsTask.WindowsUpdates, new Uri("ms-settings:appsfeatures")));
    }

    [Theory]
    [InlineData("MS-SETTINGS:startupapps")]
    [InlineData("ms-settings:StartupApps")]
    [InlineData("ms-settings:%73tartupapps")]
    [InlineData("ms-settings:startupapps?app=Example")]
    [InlineData("ms-settings:startupapps#Example")]
    [InlineData("ms-settings://startupapps")]
    [InlineData("ms-settings://user@startupapps")]
    [InlineData("ms-settings:startupapps/")]
    [InlineData("ms-settings:startupapps/../appsfeatures")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("powershell:Start-Process")]
    [InlineData("https://example.com/settings")]
    public void SettingsAliasesPayloadsAndOtherHandlersCannotWidenTheTask(string value) =>
        Assert.False(GuidedWindowsActions.IsAllowedUri(GuidedWindowsTask.StartupSettings, new Uri(value)));

    [Theory]
    [InlineData("ms-settings:windowsupdate-action")]
    [InlineData("ms-settings:windowsupdate-seekerondemand")]
    [InlineData("ms-settings:windowsupdate-restartoptions")]
    [InlineData("ms-settings:windowsupdate?update=Example")]
    public void UpdateNavigationCannotBecomeASeekActionRestartRouteOrSelectedUpdate(string value) =>
        Assert.False(GuidedWindowsActions.IsAllowedUri(GuidedWindowsTask.WindowsUpdates, new Uri(value)));

    [Fact]
    public void WindowsSecurityUsesTheVerifiedSettingsPageRatherThanAnUnverifiedScanDeepLink()
    {
        var action = GuidedWindowsActions.Find(GuidedWindowsTask.DefenderProtection)!;
        Assert.Equal("ms-settings:windowsdefender", action.TargetUri.OriginalString);
        Assert.Contains("Settings only", action.ExactEffect);
        Assert.Contains("No Defender scan", action.ExactEffect);
        Assert.False(GuidedWindowsActions.IsAllowedUri(GuidedWindowsTask.DefenderProtection, new Uri("windowsdefender://threat/")));
    }

    [Fact]
    public void InstalledAppsNavigationDoesNotSelectAProductOrReadAnUninstallerCommand()
    {
        var action = GuidedWindowsActions.Find(GuidedWindowsTask.InstalledApps)!;
        Assert.Equal("", action.TargetUri.Query);
        Assert.Contains("No application identifier or uninstall command is passed", action.ExactEffect);
        Assert.Contains("does not complete a queued MadClean uninstall", action.Limitations);
        Assert.False(GuidedWindowsActions.IsAllowedUri(GuidedWindowsTask.InstalledApps, new Uri("ms-settings:appsfeatures-app?Example.Package")));
    }

    [Theory]
    [InlineData("http://learn.microsoft.com/en-us/windows-server/administration/windows-commands/sfc")]
    [InlineData("HTTPS://learn.microsoft.com/en-us/windows-server/administration/windows-commands/sfc")]
    [InlineData("https://LEARN.MICROSOFT.COM/en-us/windows-server/administration/windows-commands/sfc")]
    [InlineData("https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/SFC")]
    [InlineData("https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/%73fc")]
    [InlineData("https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/sfc?command=scannow")]
    [InlineData("https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/sfc#run")]
    [InlineData("https://user@learn.microsoft.com/en-us/windows-server/administration/windows-commands/sfc")]
    [InlineData("https://learn.microsoft.com:443/en-us/windows-server/administration/windows-commands/sfc")]
    [InlineData("https://learn.microsoft.com:8443/en-us/windows-server/administration/windows-commands/sfc")]
    [InlineData("https://learn.microsoft.com.evil.example/en-us/windows-server/administration/windows-commands/sfc")]
    [InlineData("https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/unused/../sfc")]
    [InlineData("https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/sfc/")]
    [InlineData("https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/dism")]
    public void RepairGuideRequiresTheExactAuthoredHttpsUri(string value) =>
        Assert.False(GuidedWindowsActions.IsAllowedUri(GuidedWindowsTask.SystemFileRepairGuide, new Uri(value)));

    [Fact]
    public void RepairGuideIsDocumentationAndNotARepairCommandDescriptor()
    {
        var action = GuidedWindowsActions.Find(GuidedWindowsTask.SystemFileRepairGuide)!;
        Assert.Equal("https", action.TargetUri.Scheme);
        Assert.Equal("learn.microsoft.com", action.TargetUri.Host);
        Assert.Contains("No shell, SFC, DISM", action.ExactEffect);
        Assert.Contains("does not finish the integrity-check or repair roadmap", action.Limitations);
    }

    [Fact]
    public void WindowsUpdateSourceQueriesAndOsPolicyStayDisclosed()
    {
        foreach (var task in new[] { GuidedWindowsTask.WindowsUpdates, GuidedWindowsTask.OptionalDriverUpdates })
        {
            var action = GuidedWindowsActions.Find(task)!;
            Assert.Contains("configured", action.Limitations);
            Assert.Contains("completion remains Unknown", action.Limitations);
        }
    }

    [Fact]
    public void RelativeAndNullTargetsAreNeverLaunchInputs()
    {
        Assert.False(GuidedWindowsActions.IsAllowedUri(GuidedWindowsTask.StartupSettings, new Uri("startupapps", UriKind.Relative)));
        Assert.False(GuidedWindowsActions.IsAllowedUri(GuidedWindowsTask.StartupSettings, null!));
    }

    [Fact]
    public void PublicCatalogCannotBeMutatedToReplaceTheTrustedUri()
    {
        var mutableView = Assert.IsAssignableFrom<IList<GuidedWindowsAction>>(GuidedWindowsActions.All);
        var original = GuidedWindowsActions.Find(GuidedWindowsTask.StartupSettings)!;
        Assert.Throws<NotSupportedException>(() => mutableView[0] = original with { TargetUri = new Uri("https://example.com/unsafe") });
        Assert.Equal("ms-settings:startupapps", GuidedWindowsActions.Find(GuidedWindowsTask.StartupSettings)!.TargetUri.OriginalString);
    }
}
