using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace MadClean.App;

// Navigation lives in separate controls. Expose this headerless page host as a pane
// with its actual visual children, rather than tab headers that it does not render.
public sealed class PageHost : TabControl
{
    protected override AutomationPeer OnCreateAutomationPeer() => new PageHostAutomationPeer(this);

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        UIElementAutomationPeer.FromElement(this)?.InvalidatePeer();
    }

    protected override void OnSelectionChanged(SelectionChangedEventArgs e)
    {
        base.OnSelectionChanged(e);
        UIElementAutomationPeer.FromElement(this)?.InvalidatePeer();
    }

    private sealed class PageHostAutomationPeer(PageHost owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
        protected override string GetClassNameCore() => nameof(PageHost);
        protected override bool IsControlElementCore() => true;
        protected override bool IsContentElementCore() => true;
        // Inherited GetChildrenCore traverses the real template's visual children and
        // returns their existing framework peers. No status, tab or selection peers are invented.
    }
}
