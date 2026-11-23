using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Isle.Core;
using Isle.Modules.Setup;

namespace Isle.Views.Setup;

/// <summary>
/// Full-panel first-run setup: step 1 "Your tools" (one chip per choice, detected ones marked), step 2 "What Isle shows"
/// (toggle chips with an N / 10 counter). Start hands the answers to <c>onStart</c>; nothing is written here.
/// </summary>
public sealed class SetupView : Grid
{
    readonly Action<IReadOnlyDictionary<string, string>, ISet<string>> _onStart;
    readonly Action _onClose;
    readonly IReadOnlyDictionary<string, HashSet<string>> _found;
    readonly bool _kubeconfig;
    readonly Dictionary<string, string> _answers;
    readonly HashSet<string> _ticked;
    string _defaultsFor;
    int _step;

    public SetupView(IReadOnlyDictionary<string, HashSet<string>> found, bool kubeconfig, Dictionary<string, string> answers, HashSet<string> ticked,
        Action<IReadOnlyDictionary<string, string>, ISet<string>> onStart, Action onClose, int step = 0)
    {
        (_found, _kubeconfig, _answers, _ticked, _onStart, _onClose, _step) = (found, kubeconfig, answers, ticked, onStart, onClose, step);
        _defaultsFor = answers["agent"];
        SetResourceReference(BackgroundProperty, "BgBrush");
        Margin = new Thickness(0);
        Render();
    }

    void Render()
    {
        Children.Clear();
        var title = new TextBlock { Text = Strings.T(_step == 0 ? "setup.title1" : "setup.title2") };
        var close = Ui.IconButton(Ui.MakeIcon("Icon.Close", 12), 28, _onClose, "setup close");
        close.ToolTip = Strings.T("setup.close");
        System.Windows.Automation.AutomationProperties.SetName(close, Strings.T("setup.close"));
        var counter = new TextBlock { Text = $"{_ticked.Count} / {SetupModel.Limit}", Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = Strings.T("setup.limit", SetupModel.Limit) };
        counter.SetResourceReference(StyleProperty, "TextDim");

        var body = new DockPanel { LastChildFill = true };
        var footer = Footer();
        DockPanel.SetDock(footer, Dock.Bottom);
        body.Children.Add(footer);
        body.Children.Add(new ScrollViewer { Content = _step == 0 ? Tools() : Shows(), VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var frame = Ui.OverlayFrame(title, body, close, _step == 1 ? counter : null, 4);
        frame.Margin = new Thickness(16, 12, 16, 8);
        Children.Add(frame);
    }

    FrameworkElement Tools()
    {
        var panel = new StackPanel();
        foreach (var q in SetupModel.Questions)
        {
            var caption = new TextBlock { Text = Strings.T(q.LabelKey), Width = 92, VerticalAlignment = VerticalAlignment.Center };
            caption.SetResourceReference(StyleProperty, "TextCaption");
            var line = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            DockPanel.SetDock(caption, Dock.Left);
            line.Children.Add(caption);
            var row = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            line.Children.Add(row);
            foreach (var c in q.Choices)
            {
                var found = _found[q.Option].Contains(c.Value);
                var chip = new RadioButton { GroupName = q.Option, Content = Strings.T(c.LabelKey) + (found ? "  ✓" : ""), IsChecked = _answers[q.Option] == c.Value, Tag = q.Option + ":" + c.Value };
                chip.SetResourceReference(StyleProperty, "ChipRadio");
                if (found) chip.ToolTip = Strings.T("setup.found");
                var (option, value) = (q.Option, c.Value);
                chip.Checked += (_, _) => _answers[option] = value;
                row.Children.Add(chip);
            }
            panel.Children.Add(line);
        }
        if (_found.Values.Any(v => v.Count > 0)) panel.Children.Add(Ui.Dim("✓  " + Strings.T("setup.found"), wrap: true));
        return panel;
    }

    FrameworkElement Shows()
    {
        var panel = new StackPanel();
        foreach (var alert in new[] { false, true })
        {
            var caption = new TextBlock { Text = Strings.T(alert ? "setup.alerts" : "setup.tabs"), Margin = new Thickness(0, 2, 0, 3) };
            caption.SetResourceReference(StyleProperty, "TextCaption");
            panel.Children.Add(caption);
            var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            foreach (var item in SetupModel.Items.Where(i => i.Alert == alert))
            {
                var offered = SetupModel.Offered(item.Id, _answers["agent"]);
                var on = _ticked.Contains(item.Id);
                var chip = new ToggleButton
                {
                    Content = (item.Online ? "\U0001F310 " : "") + Strings.T(item.LabelKey), IsChecked = on, Tag = item.Id,
                    IsEnabled = offered && (on || SetupModel.CanTick(_ticked, item.Id)), Margin = new Thickness(0, 0, 4, 4),
                };
                chip.SetResourceReference(StyleProperty, "Chip");
                var id = item.Id;
                chip.Click += (_, _) =>
                {
                    if (chip.IsChecked == true) _ticked.Add(id); else _ticked.Remove(id);
                    Render();
                };
                row.Children.Add(chip);
            }
            panel.Children.Add(row);
        }
        return panel;
    }

    FrameworkElement Footer()
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        if (_step == 1)
        {
            var back = Ui.TextButton(Strings.T("setup.back"), "FlatButton", new Thickness(14, 4, 14, 4), () => { _step = 0; Render(); }, "setup back");
            back.Margin = new Thickness(0, 0, 6, 0);
            bar.Children.Add(back);
        }
        bar.Children.Add(Ui.TextButton(Strings.T(_step == 0 ? "setup.next" : "setup.start"), "AccentButton", new Thickness(14, 4, 14, 4), _step == 0 ? Next : () => _onStart(_answers, _ticked), "setup next"));
        return bar;
    }

    void Next()
    {
        if (_defaultsFor != _answers["agent"]) // the agent changed: Agents / Usage defaults follow it
        {
            _defaultsFor = _answers["agent"];
            _ticked.Clear();
            _ticked.UnionWith(SetupModel.Defaults(_defaultsFor, _kubeconfig));
        }
        _step = 1;
        Render();
    }

    /// <summary>Stand-in for the Usage tab when the chosen agent is Codex (its usage comes later).</summary>
    internal static FrameworkElement ComingSoon()
    {
        var t = Ui.Dim(Strings.T("setup.codexSoon"), wrap: true);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        t.VerticalAlignment = VerticalAlignment.Center;
        return new Grid { Children = { t } };
    }
}
