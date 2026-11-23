using System.Windows;
using System.Windows.Controls;
using Isle.Core;

namespace Isle.Views;

/// <summary>
/// The idle pill: a first-use hint and three small slots that other modules may fill (left: mascot, centre: clock,
/// right: weather temperature).
/// </summary>
public sealed class IdlePillView : Grid
{
    readonly ContentPresenter _center = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly ContentPresenter _left = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    readonly ContentPresenter _right = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
    readonly TextBlock _hint = new()
    {
        FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        IsHitTestVisible = false, Visibility = Visibility.Collapsed,
    };

    public IdlePillView()
    {
        Children.Add(_left);
        Children.Add(_center);
        _hint.SetResourceReference(TextBlock.ForegroundProperty, "FgDimBrush");
        Children.Add(_hint);
        Children.Add(_right);
    }

    public object? LeftSlot { get => _left.Content; set { _left.Content = value; RefreshHint(); } }
    public object? CenterSlot { get => _center.Content; set { _center.Content = value; RefreshHint(); } }
    public object? RightSlot { get => _right.Content; set { _right.Content = value; RefreshHint(); } }

    public double DesiredPillWidth { get; set; } = 120; // the Music pack widens it to the mini player in the Dev package

    public void Refresh() => RefreshHint();

    internal void DismissHint()
    {
        var settings = Settings.Current;
        if (settings.OptBool("shell.opened") || settings.LastTab.Length > 0) return;
        settings.SetOpt("shell.opened", true);
        RefreshHint();
        JsonStore.Save(Paths.SettingsFile, settings);
    }

    void RefreshHint()
    {
        var settings = Settings.Current;
        var show = !settings.OptBool("shell.opened") && settings.LastTab.Length == 0 &&
            CenterSlot is null && LeftSlot is null && RightSlot is null;
        _hint.Text = Strings.T("idle.openHint");
        _hint.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }
}
