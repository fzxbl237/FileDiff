using Avalonia.Automation.Peers;
using Avalonia.Controls;

namespace FileDiff.Controls;

/// <summary>A peer with a fixed control type, for the editors' self-composed groups (toolbar, list, radio group, list item).</summary>
internal sealed class EditorsGroupAutomationPeer(Control owner, AutomationControlType type) : ControlAutomationPeer(owner)
{
    protected override AutomationControlType GetAutomationControlTypeCore() => type;

    protected override bool IsContentElementCore() => true;

    protected override bool IsControlElementCore() => true;
}
