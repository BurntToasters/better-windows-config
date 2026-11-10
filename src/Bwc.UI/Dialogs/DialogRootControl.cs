using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace Bwc.UI.Dialogs;

/// <summary>
/// The dialog root. A plain Panel has no automation peer, so its AutomationId never reaches UIA.
/// This content control creates a peer and carries the DialogRoot id for tests and automation.
/// </summary>
public sealed class DialogRootControl : ContentControl
{
    protected override AutomationPeer OnCreateAutomationPeer() =>
        new FrameworkElementAutomationPeer(this);
}
