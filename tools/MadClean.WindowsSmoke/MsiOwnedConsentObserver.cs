using System.Windows.Automation;
using MadClean.Windows;

namespace MadClean.WindowsSmoke;

internal enum MsiOwnedConsentCheckpoint { Progress, MissingClient, ConsentObserved, ConsentUnverified }

internal sealed class MsiOwnedConsentObservation
{
    public bool? OwnClientPidObserved { get; set; }
    public bool? OwnedWindowSeen { get; set; }
    public bool? AuthoredTitleSeen { get; set; }
    public bool? AuthoredWindowVisibleEnabled { get; set; }
    public bool? RemoveButtonSeen { get; set; }
    public bool? RemoveButtonVisibleEnabled { get; set; }
    public bool? RemovePatternSeen { get; set; }
    public bool? RemoveInvoked { get; set; }
    public bool? GenericTitleSeen { get; set; }
    public bool? GenericWindowVisibleEnabled { get; set; }
    public bool? GenericPromptMatched { get; set; }
    public bool? YesControlSeen { get; set; }
    public bool? YesControlVisibleEnabled { get; set; }
    public bool? YesPatternSeen { get; set; }
    public bool? YesInvoked { get; set; }
    public string? YesSelectionKind { get; set; }
    public int? YesCandidateCount { get; set; }
    public bool? EnumerationFailed { get; set; }
    public int? PollCount { get; set; }
    public int? ManagedExceptionHResult { get; set; }
}

// Exact owned live-client UI observation shared by the backend and public UI cycles.
// It neither authorizes dispatch nor observes foreign clients or application rows.
internal static class MsiOwnedConsentObserver
{
    internal static Task ObserveAsync(MsiUninstallFixtureClient client, MsiOwnedConsentObservation facts,
        Action<MsiOwnedConsentCheckpoint> checkpoint) => Task.Run(async () =>
    {
        var started = DateTimeOffset.UtcNow;
        var lastSaved = started;
        bool authored = false, countOverflowed = false;
        facts.PollCount = 0;
        checkpoint(MsiOwnedConsentCheckpoint.Progress);
        static bool Sticky(bool? previous, bool observed) => previous == true || observed;
        AutomationElementCollection Enumerate(AutomationElement element, TreeScope tree, Condition condition)
        {
            try
            {
                var found = element.FindAll(tree, condition);
                facts.EnumerationFailed = Sticky(facts.EnumerationFailed, false);
                return found;
            }
            catch
            {
                facts.EnumerationFailed = true;
                throw;
            }
        }
        try
        {
            if (client.ProcessId is not int processId || processId <= 0)
            { facts.OwnClientPidObserved = false; checkpoint(MsiOwnedConsentCheckpoint.MissingClient); return; }
            facts.OwnClientPidObserved = processId > 0;
            while (client.HasExited == false && DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(90))
            {
                if (!countOverflowed)
                {
                    if (facts.PollCount is int count && count < 4096) facts.PollCount = count + 1;
                    else { facts.PollCount = null; countOverflowed = true; }
                }
                var windows = Enumerate(AutomationElement.RootElement, TreeScope.Children,
                    new PropertyCondition(AutomationElement.ProcessIdProperty, processId));
                facts.OwnedWindowSeen = Sticky(facts.OwnedWindowSeen, windows.Count > 0);
                foreach (AutomationElement window in windows)
                {
                    if (client.HasExited != false) break;
                    string title = window.Current.Name;
                    bool authoredTitle = title == OwnedMsiDatabase.DialogTitle;
                    bool genericTitle = title == "Windows Installer";
                    facts.AuthoredTitleSeen = Sticky(facts.AuthoredTitleSeen, authoredTitle);
                    facts.GenericTitleSeen = Sticky(facts.GenericTitleSeen, genericTitle);
                    if (authoredTitle)
                    {
                        bool visible = VisibleAndEnabled(window);
                        facts.AuthoredWindowVisibleEnabled = Sticky(facts.AuthoredWindowVisibleEnabled, visible);
                        if (visible)
                        {
                            var button = window.FindFirst(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button), new PropertyCondition(AutomationElement.NameProperty, "Remove")));
                            facts.RemoveButtonSeen = Sticky(facts.RemoveButtonSeen, button != null);
                            if (button != null)
                            {
                                bool buttonVisible = VisibleAndEnabled(button);
                                facts.RemoveButtonVisibleEnabled = Sticky(facts.RemoveButtonVisibleEnabled, buttonVisible);
                                if (buttonVisible && client.HasExited == false)
                                {
                                    bool patternSeen = button.TryGetCurrentPattern(InvokePattern.Pattern, out object? pattern);
                                    facts.RemovePatternSeen = Sticky(facts.RemovePatternSeen, patternSeen);
                                    if (patternSeen)
                                    {
                                        bool invoked = client.TryInvokeIfRunning(() => ((InvokePattern)pattern!).Invoke());
                                        facts.RemoveInvoked = Sticky(facts.RemoveInvoked, invoked);
                                        if (invoked) { authored = true; checkpoint(MsiOwnedConsentCheckpoint.ConsentObserved); return; }
                                    }
                                }
                            }
                        }
                    }
                    else if (genericTitle)
                    {
                        bool visible = VisibleAndEnabled(window);
                        facts.GenericWindowVisibleEnabled = Sticky(facts.GenericWindowVisibleEnabled, visible);
                        if (visible)
                        {
                            // The same exact live borrowed client and fixed English
                            // question are required. AutomationId values from another
                            // application are not a decimal Win32 control-ID contract.
                            var texts = Enumerate(window, TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
                            bool ownText = texts.Cast<AutomationElement>().Any(item => item.Current.Name == "Are you sure you want to uninstall this product?");
                            facts.GenericPromptMatched = Sticky(facts.GenericPromptMatched, ownText);
                            // Once the exact Yes control has been invoked, preserve its
                            // count/kind evidence and wait only for authored Remove.
                            // Never accept a repeated generic confirmation.
                            if (ownText && facts.YesInvoked != true)
                            {
                                var candidates = Enumerate(window, TreeScope.Descendants, new AndCondition(
                                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                                    new PropertyCondition(AutomationElement.NameProperty, "Yes")));
                                facts.YesSelectionKind = "UniqueButtonName";
                                facts.YesCandidateCount = candidates.Count <= 64 ? candidates.Count : null;
                                facts.YesControlSeen = Sticky(facts.YesControlSeen, candidates.Count > 0);
                                if (candidates.Count == 1)
                                {
                                    var yes = candidates[0];
                                    bool yesVisible = VisibleAndEnabled(yes);
                                    facts.YesControlVisibleEnabled = Sticky(facts.YesControlVisibleEnabled, yesVisible);
                                    if (yesVisible && client.HasExited == false)
                                    {
                                        bool patternSeen = yes.TryGetCurrentPattern(InvokePattern.Pattern, out object? pattern);
                                        facts.YesPatternSeen = Sticky(facts.YesPatternSeen, patternSeen);
                                        if (patternSeen)
                                        {
                                            // Recheck the actual owned window, exact
                                            // question and selected control immediately
                                            // before using the retained Invoke pattern.
                                            var currentTexts = Enumerate(window, TreeScope.Descendants,
                                                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
                                            bool currentPrompt = currentTexts.Cast<AutomationElement>().Any(item =>
                                                item.Current.Name == "Are you sure you want to uninstall this product?");
                                            var currentCandidates = Enumerate(window, TreeScope.Descendants, new AndCondition(
                                                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                                                new PropertyCondition(AutomationElement.NameProperty, "Yes")));
                                            facts.YesCandidateCount = currentCandidates.Count <= 64 ? currentCandidates.Count : null;
                                            facts.YesControlSeen = Sticky(facts.YesControlSeen, currentCandidates.Count > 0);
                                            if (client.HasExited == false && window.Current.ProcessId == processId &&
                                                window.Current.Name == "Windows Installer" && VisibleAndEnabled(window) && currentPrompt &&
                                                currentCandidates.Count == 1 && Automation.Compare(yes, currentCandidates[0]) &&
                                                yes.Current.ProcessId == processId && yes.Current.ControlType == ControlType.Button &&
                                                yes.Current.Name == "Yes" && VisibleAndEnabled(yes))
                                                facts.YesInvoked = Sticky(facts.YesInvoked,
                                                    client.TryInvokeIfRunning(() => ((InvokePattern)pattern!).Invoke()));
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                if (DateTimeOffset.UtcNow - lastSaved >= TimeSpan.FromSeconds(1)) { checkpoint(MsiOwnedConsentCheckpoint.Progress); lastSaved = DateTimeOffset.UtcNow; }
                await Task.Delay(100);
            }
            if (!authored) { checkpoint(MsiOwnedConsentCheckpoint.ConsentUnverified); }
        }
        catch (Exception exception)
        {
            facts.ManagedExceptionHResult = exception.HResult;
            try { checkpoint(MsiOwnedConsentCheckpoint.Progress); } catch { }
            throw;
        }
    });
    private static bool VisibleAndEnabled(AutomationElement element)
    {
        var current = element.Current; var rectangle = current.BoundingRectangle;
        return !current.IsOffscreen && current.IsEnabled && !rectangle.IsEmpty &&
            double.IsFinite(rectangle.X) && double.IsFinite(rectangle.Y) && double.IsFinite(rectangle.Width) && double.IsFinite(rectangle.Height) && rectangle.Width > 0 && rectangle.Height > 0;
    }
}
