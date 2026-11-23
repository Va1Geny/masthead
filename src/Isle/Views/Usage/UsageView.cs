using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Isle.Core;
using Isle.Modules.Usage;

namespace Isle.Views.Usage;

/// <summary>
/// Usage tab: tokens and an API-equivalent cost estimate for Claude Code sessions: totals for today / 7 / 30 days,
/// then the most recent sessions. Scans (off the UI thread) only when shown and on the refresh button.
/// </summary>
public sealed class UsageView : Grid, ITabView
{
    // widths of: models, started, duration, tokens, cost (the project column takes the rest)
    static readonly double[] Cols = [104, 100, 84, 56, 72];

    readonly Func<Task<UsageReport>> _scan;
    readonly ContentControl _body = new();
    UsageReport? _report;
    bool _busy, _failed;

    public UsageView(Func<Task<UsageReport>> scan)
    {
        _scan = scan;
        Children.Add(_body);
        Render();
    }

    public void OnShown()
    {
        if (_report is not null) Render(); // picks up a language switch
        _ = Refresh();
    }

    public void OnHidden() { }

    async Task Refresh()
    {
        if (_busy) return;
        _busy = true;
        _failed = false;
        Render();
        try { _report = await _scan(); }
        catch (OperationCanceledException) { _busy = false; Render(); return; }
        catch (Exception ex) { Logger.Error("usage scan", ex); _failed = true; }
        _busy = false;
        Render();
    }

    void Render() => _body.Content = _report switch
    {
        null => Centered(Strings.T(_failed ? "usage.error" : "usage.scanning"), retry: _failed),
        { IsEmpty: true } => Centered(Strings.T("usage.none"), retry: true),
        var r => Content(r),
    };

    FrameworkElement Content(UsageReport r)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var cards = new UniformGrid { Rows = 1, Margin = new Thickness(0, 0, -6, 0) };
        cards.Children.Add(Card("usage.today", r.Today));
        cards.Children.Add(Card("usage.week", r.Week));
        cards.Children.Add(Card("usage.month", r.Month));
        grid.Children.Add(cards);

        var bar = new DockPanel { Margin = new Thickness(2, 3, 0, 0) };
        var refresh = Ui.IconAction("Icon.Reset", "usage.refresh", () => _ = Refresh(), "usage refresh");
        refresh.IsEnabled = !_busy;
        DockPanel.SetDock(refresh, Dock.Right);
        bar.Children.Add(refresh);
        var status = Ui.Dim(_busy ? Strings.T("usage.scanning") : Strings.T("usage.updated", r.At.ToString("HH:mm", CultureInfo.InvariantCulture)));
        status.FontSize = 10.5;
        status.Margin = new Thickness(8, 0, 4, 0);
        DockPanel.SetDock(status, Dock.Right);
        bar.Children.Add(status);
        var note = Ui.Dim(Strings.T("usage.note"));
        note.FontSize = 10.5;
        note.ToolTip = Strings.T("usage.note");
        bar.Children.Add(note);
        SetRow(bar, 1);
        grid.Children.Add(bar);

        var list = new StackPanel();
        var culture = CultureInfo.GetCultureInfo(Strings.Language == "uk" ? "uk-UA" : "en-GB");
        foreach (var s in r.Sessions)
            list.Children.Add(Row(s.Project, string.Join(", ", s.Models.Select(UsageReport.ModelName)),
                s.Start.ToString("d MMM HH:mm", culture), Duration(s.Duration), UsageReport.Count(s.Tokens.Total), UsageReport.Money(s.Cost, s.Partial)));
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0) };
        SetRow(scroll, 2);
        grid.Children.Add(scroll);
        return grid;
    }

    static FrameworkElement Card(string labelKey, UsageTotal t)
    {
        var stack = new StackPanel();
        var head = new DockPanel();
        var cost = new TextBlock { Text = UsageReport.Money(t.Cost, t.Partial) };
        cost.SetResourceReference(StyleProperty, "TextTitle");
        DockPanel.SetDock(cost, Dock.Right);
        head.Children.Add(cost);
        var label = new TextBlock { Text = Strings.T(labelKey) };
        label.SetResourceReference(StyleProperty, "TextCaption");
        head.Children.Add(label);
        stack.Children.Add(head);
        foreach (var line in new[]
        {
            Strings.T("usage.io", UsageReport.Count(t.Tokens.In), UsageReport.Count(t.Tokens.Out)),
            Strings.T("usage.cache", UsageReport.Count(t.Tokens.CacheRead), UsageReport.Count(t.Tokens.CacheWrite)),
        })
        {
            var dim = Ui.Dim(line);
            dim.FontSize = 10.5;
            stack.Children.Add(dim);
        }
        var card = new Border { Child = stack, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(10, 5, 10, 5) };
        card.SetResourceReference(StyleProperty, "Card");
        return card;
    }

    /// <summary>One session line: project, models, started, duration, tokens, cost.</summary>
    static FrameworkElement Row(params string[] cells)
    {
        var grid = new Grid { Margin = new Thickness(4, 0, 0, 0), MinHeight = 22 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var w in Cols) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
        for (var i = 0; i < cells.Length; i++)
        {
            var t = new TextBlock { Text = cells[i], Margin = new Thickness(0, 0, 8, 0), FontSize = 11.5 };
            t.SetResourceReference(StyleProperty, "TextDim");
            if (i is 0 or 5) t.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
            if (i == 5) t.FontWeight = FontWeights.SemiBold;
            if (i == 1) t.ToolTip = cells[1];
            if (i >= 3) t.HorizontalAlignment = HorizontalAlignment.Right;
            SetColumn(t, i);
            grid.Children.Add(t);
        }
        return grid;
    }

    FrameworkElement Centered(string text, bool retry)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var icon = Ui.MakeIcon("Icon.Code", 24, "FgFaintBrush", stroke: 1.6);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.Margin = new Thickness(0, 0, 0, 6);
        stack.Children.Add(icon);
        var t = Ui.Dim(text, wrap: true);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        t.TextAlignment = TextAlignment.Center;
        stack.Children.Add(t);
        if (retry && !_busy)
        {
            var b = Ui.TextButton(Strings.T("usage.retry"), "FlatButton", new Thickness(12, 4, 12, 4), () => _ = Refresh(), "usage refresh", minHeight: 30);
            b.Margin = new Thickness(0, 8, 0, 0);
            b.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Children.Add(b);
        }
        return stack;
    }

    /// <summary>"1h 12m", "5m", "&lt;1m".</summary>
    static string Duration(TimeSpan d) =>
        d.TotalHours >= 1 ? Strings.T("usage.dur.hm", (int)d.TotalHours, d.Minutes)
        : d.TotalMinutes >= 1 ? Strings.T("usage.dur.m", (int)d.TotalMinutes)
        : Strings.T("usage.dur.s");
}
