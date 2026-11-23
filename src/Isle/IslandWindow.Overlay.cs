using System.Windows;
using System.Windows.Input;
using Isle.Core;

namespace Isle;

// A full-panel overlay above the tabs (command bar). Hidden again when the island collapses.
public partial class IslandWindow
{
    FrameworkElement? _overlay;
    IInputElement? _beforeOverlayFocus;

    public void ShowOverlay(FrameworkElement view)
    {
        // A tooltip is its own popup window and would outlive its button (the header is replaced): disable, then re-enable.
        for (var d = Mouse.DirectlyOver as DependencyObject; d is not null; d = d is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is FrameworkElement { ToolTip: not null } fe)
            {
                System.Windows.Controls.ToolTipService.SetIsEnabled(fe, false);
                Dispatcher.BeginInvoke(() => System.Windows.Controls.ToolTipService.SetIsEnabled(fe, true), System.Windows.Threading.DispatcherPriority.Background);
            }
        if (_overlay is not { Visibility: Visibility.Visible }) _beforeOverlayFocus = Keyboard.FocusedElement;
        if (!ReferenceEquals(_overlay, view))
        {
            if (_overlay is not null)
            {
                var previousFocus = _beforeOverlayFocus;
                HideOverlay(_overlay);
                _beforeOverlayFocus = previousFocus;
                Panel.Children.Remove(_overlay);
            }
            _overlay = view;
            System.Windows.Controls.Panel.SetZIndex(view, 5);
            Panel.Children.Add(view);
        }
        HideMoreOverlay();
        view.Visibility = Visibility.Visible;
        KeyboardNavigation.SetTabNavigation(view, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetControlTabNavigation(view, KeyboardNavigationMode.Cycle);
        if (!_expanded) Expand(sticky: true);
        view.Dispatcher.BeginInvoke(ApplyExStyle, System.Windows.Threading.DispatcherPriority.Loaded);
        Safe(() => (view as ITabView)?.OnShown(), "overlay shown");
        if (!_snapshotMode && view is Views.SettingsPanel.SettingsView)
        {
            AcquireFocus();
            _focusOnExpand = _hasFocus;
            FocusExpandedPanel();
        }
    }

    public void HideOverlay() => HideOverlay(null);

    public void HideOverlay(FrameworkElement? expected)
    {
        if (_overlay is not { Visibility: Visibility.Visible } o || (expected is not null && !ReferenceEquals(expected, o))) return;
        o.Visibility = Visibility.Collapsed;
        ApplyExStyle();
        Safe(() => (o as ITabView)?.OnHidden(), "overlay hidden");
        if (_hasFocus && _beforeOverlayFocus is UIElement { IsVisible: true, IsEnabled: true, Focusable: true } previous)
            previous.Focus();
        _beforeOverlayFocus = null;
    }
}
