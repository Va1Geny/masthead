using System.Windows;
using System.Windows.Controls;
using Isle.Core;

namespace Isle.Views;

/// <summary>The idle pill: a first-use hint.</summary>
public sealed class IdlePillView : Grid
{
    readonly TextBlock _hint = new()
    {
        FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        IsHitTestVisible = false, Visibility = Visibility.Collapsed,
    };

    public IdlePillView()
    {
        _hint.SetResourceReference(TextBlock.ForegroundProperty, "FgDimBrush");
        Children.Add(_hint);
    }

    public double DesiredPillWidth { get; set; } = 120; // Music widens it to the mini player

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
        var show = !settings.OptBool("shell.opened") && settings.LastTab.Length == 0;
        _hint.Text = Strings.T("idle.openHint");
        _hint.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }
}
