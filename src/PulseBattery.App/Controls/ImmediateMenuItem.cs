using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using WpfContextMenu = System.Windows.Controls.ContextMenu;

namespace PulseBattery.App.Controls;

// Context-menu rows switch branches immediately. Descendants and keyboard navigation
// keep the normal MenuItem behavior, including access to the open popup.
public sealed class ImmediateMenuItem : MenuItem
{
    private DispatcherTimer? _leaveTimer;
    public ImmediateMenuItem()
    {
        SetResourceReference(StyleProperty, typeof(MenuItem));
        SubmenuClosed += (_, _) => _leaveTimer?.Stop();
        Unloaded += (_, _) => _leaveTimer?.Stop();
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        if (ItemsControlFromItemContainer(this) is WpfContextMenu) SelectHoveredRow();
        else base.OnMouseEnter(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (ItemsControlFromItemContainer(this) is WpfContextMenu) SelectHoveredRow();
        else base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (ItemsControlFromItemContainer(this) is not WpfContextMenu) { base.OnMouseLeave(e); return; }
        if (!IsSubmenuOpen) { ClearHover(); return; }
        // Allow crossing the small gap into the popup. Other rows still switch immediately.
        _leaveTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Input, (_, _) =>
        {
            _leaveTimer!.Stop();
            if (!PointerInsideBranch()) ClearHover();
        }, Dispatcher);
        _leaveTimer.Stop(); _leaveTimer.Start();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        _leaveTimer?.Stop();
        base.OnPreviewKeyDown(e);
    }

    private void SelectHoveredRow()
    {
        if (!IsEnabled || !new Rect(RenderSize).Contains(Mouse.GetPosition(this)) ||
            ItemsControlFromItemContainer(this) is not WpfContextMenu { IsOpen: true } menu) return;
        _leaveTimer?.Stop();
        foreach (var sibling in menu.Items.OfType<MenuItem>().Where(item => item != this))
        {
            sibling.SetCurrentValue(IsSubmenuOpenProperty, false);
            Selector.SetIsSelected(sibling, false);
        }
        if (!Selector.GetIsSelected(this)) Focus();
        Selector.SetIsSelected(this, true);
        SetCurrentValue(IsSubmenuOpenProperty, HasItems);
    }

    private bool PointerInsideBranch() => new Rect(RenderSize).Contains(Mouse.GetPosition(this)) ||
        (IsSubmenuOpen && Template.FindName("PART_Popup", this) is Popup { Child: FrameworkElement child } &&
         new Rect(child.RenderSize).Contains(Mouse.GetPosition(child)));

    private void ClearHover()
    {
        _leaveTimer?.Stop();
        if (ItemsControlFromItemContainer(this) is WpfContextMenu { IsOpen: true } menu && IsKeyboardFocusWithin) menu.Focus();
        SetCurrentValue(IsSubmenuOpenProperty, false);
        Selector.SetIsSelected(this, false);
    }
}
