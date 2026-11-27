using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Shapes;
using Isle.Core;
using Isle.Modules.Agents;

namespace Isle.Views.Agents;

/// <summary>What the Agents tab can ask its module to do.</summary>
public sealed record AgentsActions(
    Action<AgentRequest> Allow,
    Action<AgentRequest> Deny,
    Action<AgentRequest> Terminal,
    Action<AgentSession> OpenTerminal,
    Action<AgentRequest, IReadOnlyList<IReadOnlyCollection<string>>> Answer,
    Action<AgentSession> Dismiss,
    Func<IReadOnlyList<HookTarget>> Targets,
    Func<HookTarget, string> Install,
    Func<HookTarget, string> Remove,
    Func<int> TimeoutSeconds);

/// <summary>
/// Agents tab: one card per session (project, agent, state, last action). Waiting cards show the tool call with
/// Allow · Deny · In terminal, or the agent's question with one button per option. A full-cover overlay installs /
/// removes the hooks (with a preview of exactly what changes before anything is written).
/// </summary>
public sealed class AgentsView : Grid
{
    readonly AgentsActions _a;
    readonly StackPanel _list = new();
    readonly TextBlock _count = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    readonly Border _overlay = new() { Visibility = Visibility.Collapsed };
    /// <summary>Progress through multi-question requests (survives re-renders): request id → (question index, picks).</summary>
    readonly Dictionary<long, (int Index, List<HashSet<string>> Picks)> _progress = [];
    readonly Dictionary<long, Border> _requestCards = [];
    string? _workerKey;
    StackPanel? _workerPanel;
    IReadOnlyList<AgentSession> _sessions = [];
    bool _anyHooks = true;

    public AgentsView(AgentsActions actions)
    {
        _a = actions;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // No negative top margin: TabContent clips, and it cut the top of the hooks button's focus ring.
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var hooks = IconAction("Icon.Plug", "agents.hooksTip", () => ShowHooks());
        DockPanel.SetDock(hooks, Dock.Right);
        head.Children.Add(hooks);
        var title = new TextBlock { Text = Strings.T("tab.agents"), VerticalAlignment = VerticalAlignment.Center };
        title.SetResourceReference(StyleProperty, "TextTitle");
        _count.SetResourceReference(StyleProperty, "TextDim");
        head.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { title, _count } });
        Children.Add(head);

        var scroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        SetRow(scroll, 1);
        Children.Add(scroll);

        _overlay.SetResourceReference(Border.BackgroundProperty, "BgBrush");
        SetRowSpan(_overlay, 2);
        Children.Add(_overlay);
    }


    public void Show(IReadOnlyList<AgentSession> sessions, bool anyHooks)
    {
        _sessions = sessions;
        _anyHooks = anyHooks;
        if (_workerPanel is { } panel && _workerKey is { } key)
        {
            if (sessions.FirstOrDefault(s => s.Key == key) is { } session) RenderWorkers(panel, session);
            else HideOverlay();
        }
        var live = sessions.SelectMany(s => s.Pending).Select(r => r.Id).ToHashSet();
        foreach (var id in _progress.Keys.Where(k => !live.Contains(k)).ToList()) _progress.Remove(id);

        _count.Text = sessions.Count > 0 ? sessions.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
        foreach (var id in _requestCards.Keys.Where(id => !live.Contains(id)).ToList()) _requestCards.Remove(id);
        var retained = sessions.Select(s => s.Pending.FirstOrDefault()?.Id).Where(id => id.HasValue)
            .Select(id => _requestCards.GetValueOrDefault(id!.Value)).OfType<Border>().ToHashSet();
        // Keep live request controls attached: status events must not reset focus, scroll, or the review gate.
        foreach (UIElement child in _list.Children.Cast<UIElement>().ToList())
            if (child is not Border card || !retained.Contains(card)) _list.Children.Remove(child);
        if (sessions.Count == 0)
        {
            _list.Children.Add(Wrap(Strings.T(anyHooks ? "agents.empty" : "agents.emptySetup")));
            if (!anyHooks)
            {
                var setup = Small("agents.setup", () => ShowHooks(), accent: true);
                setup.HorizontalAlignment = HorizontalAlignment.Left;
                setup.Margin = new Thickness(0, 8, 0, 0);
                _list.Children.Add(setup);
            }
            return;
        }
        foreach (var s in sessions)
        {
            var request = s.Pending.FirstOrDefault();
            var card = Card(s, request is not null ? _requestCards.GetValueOrDefault(request.Id) : null);
            if (request is not null) _requestCards[request.Id] = card;
            if (!_list.Children.Contains(card)) _list.Children.Add(card);
        }
    }

    void Refresh() => Show(_sessions, _anyHooks);

    // ---- cards ----

    Border Card(AgentSession s, Border? existing = null)
    {
        var request = s.Pending.FirstOrDefault();
        var stack = existing?.Child as StackPanel ?? new StackPanel();
        var body = stack.Children.OfType<FrameworkElement>().FirstOrDefault(child => Equals(child.Tag, request?.Id));
        foreach (UIElement child in stack.Children.Cast<UIElement>().ToList())
            if (child != body) stack.Children.Remove(child);

        var top = new DockPanel();
        var dot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        dot.SetResourceReference(Shape.FillProperty, StateBrush(s.State));
        DockPanel.SetDock(dot, Dock.Left);
        top.Children.Add(dot);
        if (s.State is AgentState.Done or AgentState.Idle)
        {
            var hide = IconAction("Icon.Close", "agents.dismiss", () => _a.Dismiss(s));
            hide.Width = hide.Height = 20;
            hide.Margin = new Thickness(4, -2, -4, -2);
            DockPanel.SetDock(hide, Dock.Right);
            top.Children.Add(hide);
        }
        var state = new TextBlock { Text = Strings.T("agents.state." + s.State.ToString().ToLowerInvariant()), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        state.SetResourceReference(TextBlock.ForegroundProperty, s.State == AgentState.Waiting ? "WarnBrush" : "FgDimBrush");
        DockPanel.SetDock(state, Dock.Right);
        top.Children.Add(state);
        var name = new TextBlock { FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        name.Inlines.Add(s.Project.Length > 0 ? s.Project : AgentText.AgentName(s.Agent));
        var agent = new System.Windows.Documents.Run("  " + AgentText.AgentName(s.Agent)) { FontWeight = FontWeights.Normal, FontSize = 11 };
        agent.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "FgDimBrush");
        name.Inlines.Add(agent);
        if (s.Cwd.Length > 0) name.ToolTip = s.Cwd;
        name.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
        top.Children.Add(name);
        stack.Children.Insert(0, top);
        if (request is null)
        {
            if (s.LastAction.Length > 0)
            {
                var last = new TextBlock { Text = s.LastAction, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(16, 2, 0, 0) };
                last.SetResourceReference(TextBlock.ForegroundProperty, "FgDimBrush");
                stack.Children.Add(last);
            }
        }
        else if (body is null)
        {
            body = request.IsQuestion ? QuestionBody(request) : PermissionBody(request);
            body.Tag = request.Id;
            stack.Children.Add(body);
        }
        if (s.Pending.Count > 1)
        {
            var more = new TextBlock { Text = Strings.T("agents.more", s.Pending.Count - 1), FontSize = 11, Margin = new Thickness(16, 4, 0, 0) };
            more.SetResourceReference(TextBlock.ForegroundProperty, "FgDimBrush");
            stack.Children.Add(more);
        }

        var actions = new WrapPanel { Margin = new Thickness(16, 3, 0, 0) };
        if (request is null && s.Terminal is not null)
            actions.Children.Add(Small("agents.openTerminal", () => _a.OpenTerminal(s), ghost: true));
        if (s.Workers.Count > 0)
            actions.Children.Add(Small(null, () => ShowWorkers(s), ghost: true,
                text: Strings.T("agents.children", s.Workers.Count(w => w.Done), s.Workers.Count)));
        if (s.LatestChange is { } change)
        {
            var nameWithCounts = AgentText.OneLine(change.FileName, 28) + Counts(change);
            var diff = Small(null, () => ShowDiff(change), ghost: true, tipKey: "agents.showDiff", text: nameWithCounts);
            System.Windows.Automation.AutomationProperties.SetName(diff, Strings.T("agents.showDiff") + " · " + nameWithCounts);
            actions.Children.Add(diff);
        }
        if (actions.Children.Count > 0) stack.Children.Add(actions);

        var card = existing ?? new Border { Child = stack, Margin = new Thickness(0, 0, 0, 6) };
        card.SetResourceReference(StyleProperty, "Card");
        return card;
    }

    FrameworkElement PermissionBody(AgentRequest r)
    {
        var body = new Grid { Margin = new Thickness(16, 2, 0, 0) };
        body.ColumnDefinitions.Add(new ColumnDefinition());
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
        var what = new TextBlock { Text = Strings.T("agents.wantsTool", r.Tool), FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = r.Tool };
        what.SetResourceReference(TextBlock.ForegroundProperty, "FgDimBrush");
        labels.Children.Add(what);
        body.Children.Add(labels);
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (r.ApprovalInput is { } input)
        {
            var allow = Small("agents.allow", () => _a.Allow(r), accent: true);
            allow.IsDefault = false;
            allow.IsEnabled = false;
            var detail = new TextBlock { Text = AgentText.ReviewInput(input), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(7, 3, 7, 3) };
            detail.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            detail.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
            var scroll = new ScrollViewer
            {
                Content = detail, MaxHeight = 50, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = true,
            };
            System.Windows.Automation.AutomationProperties.SetName(scroll, Strings.T("agents.reviewInput"));
            var hint = Wrap(Strings.T("agents.scrollToEnd"));
            hint.FontSize = 11;
            void UpdateGate()
            {
                // A completed review stays complete for this immutable request, even after resizing.
                allow.IsEnabled |= scroll.ViewportHeight > 0 && scroll.VerticalOffset >= scroll.ScrollableHeight - 0.5;
                hint.Visibility = allow.IsEnabled ? Visibility.Collapsed : Visibility.Visible;
            }
            scroll.ScrollChanged += (_, _) => UpdateGate();
            scroll.Loaded += (_, _) => UpdateGate();
            var box = new Border { Child = scroll, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 3, 0, 0) };
            box.SetResourceReference(Border.BackgroundProperty, "SurfaceHoverBrush");
            labels.Children.Add(hint);
            SetRow(box, 1);
            SetColumnSpan(box, 2);
            body.Children.Add(box);
            row.Children.Add(allow);
        }
        else
        {
            var explanation = Wrap(Strings.T("agents.reviewInTerminal"));
            SetRow(explanation, 1);
            SetColumnSpan(explanation, 2);
            body.Children.Add(explanation);
        }
        row.Children.Add(Small("agents.deny", () => _a.Deny(r)));
        row.Children.Add(Small("agents.terminal", () => _a.Terminal(r), ghost: true, tipKey: "agents.terminalTip"));
        SetColumn(row, 1);
        body.Children.Add(row);
        return body;
    }

    FrameworkElement QuestionBody(AgentRequest r)
    {
        if (!_progress.TryGetValue(r.Id, out var p))
        {
            p = (0, [.. r.Questions.Select(_ => new HashSet<string>())]);
            _progress[r.Id] = p;
        }
        var q = r.Questions[Math.Min(p.Index, r.Questions.Count - 1)];
        var stack = new StackPanel { Margin = new Thickness(16, 4, 0, 0) };
        var text = new TextBlock { Text = q.Text, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        text.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
        if (r.Questions.Count > 1 || q.Multi)
        {
            var hint = new TextBlock { FontSize = 11, Margin = new Thickness(0, 0, 0, 1),
                Text = string.Join(" · ", new[] { r.Questions.Count > 1 ? Strings.T("agents.question", p.Index + 1, r.Questions.Count) : "", q.Multi ? Strings.T("agents.multi") : "" }.Where(x => x.Length > 0)) };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "FgDimBrush");
            stack.Children.Add(hint);
        }
        stack.Children.Add(text);

        var options = new WrapPanel { Margin = new Thickness(0, 5, 0, 0) };
        var picks = p.Picks[p.Index];
        foreach (var (label, description) in q.Options)
        {
            var l = label;
            if (q.Multi)
            {
                var chip = new ToggleButton { Content = label, IsChecked = picks.Contains(label), Margin = new Thickness(0, 0, 6, 4), ToolTip = description.Length > 0 ? description : null };
                chip.SetResourceReference(StyleProperty, "Chip");
                chip.Click += (_, _) => { if (chip.IsChecked == true) picks.Add(l); else picks.Remove(l); };
                options.Children.Add(chip);
            }
            else
            {
                var b = Small(null, () => { picks.Clear(); picks.Add(l); Advance(r); }, text: label);
                b.ToolTip = description.Length > 0 ? description : null;
                b.Margin = new Thickness(0, 0, 6, 4);
                options.Children.Add(b);
            }
        }
        stack.Children.Add(options);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        if (q.Multi) row.Children.Add(Small(p.Index + 1 < r.Questions.Count ? "agents.next" : "agents.send", () => { if (picks.Count > 0) Advance(r); }, accent: true));
        row.Children.Add(Small("agents.terminal", () => _a.Terminal(r), ghost: true, tipKey: "agents.terminalTip"));
        stack.Children.Add(row);
        return stack;
    }

    void Advance(AgentRequest r)
    {
        if (!_progress.TryGetValue(r.Id, out var p)) return;
        if (p.Index + 1 < r.Questions.Count)
        {
            _progress[r.Id] = (p.Index + 1, p.Picks);
            _requestCards.Remove(r.Id);
            Refresh();
            return;
        }
        _progress.Remove(r.Id);
        _a.Answer(r, [.. p.Picks]);
    }

    static string StateBrush(AgentState s) => s switch
    {
        AgentState.Waiting => "WarnBrush",
        AgentState.Working => "AccentTextBrush",
        AgentState.Done => "OkBrush",
        _ => "FgFaintBrush",
    };

    internal void ShowWorkers(AgentSession session)
    {
        var panel = new StackPanel();
        RenderWorkers(panel, session);
        ShowOverlay(Strings.T("agents.childrenTitle"), new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        _workerKey = session.Key;
        _workerPanel = panel;
    }

    static void RenderWorkers(StackPanel panel, AgentSession session)
    {
        panel.Children.Clear();
        var summary = Wrap(Strings.T("agents.childrenDone", session.Workers.Count(w => w.Done), session.Workers.Count));
        summary.Margin = new Thickness(0, 0, 0, 6);
        panel.Children.Add(summary);
        foreach (var worker in session.Workers.OrderBy(w => w.Done).ThenByDescending(w => w.ObservedAt))
        {
            var top = new DockPanel();
            var state = Wrap(Strings.T(worker.Done ? "agents.state.done" : "agents.state.working"), worker.Done ? "OkBrush" : "AccentTextBrush");
            state.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(state, Dock.Right);
            top.Children.Add(state);
            var name = worker.Name.Length > 0 ? worker.Name : Strings.T("agents.child");
            var id = worker.Id.Length <= 8 ? worker.Id : worker.Id[^8..];
            var label = Wrap(name + " · " + id, "FgBrush");
            label.ToolTip = worker.Id;
            top.Children.Add(label);
            var row = new StackPanel { Children = { top } };
            if (worker.Activity.Length > 0) row.Children.Add(Wrap(worker.Activity));
            var card = new Border { Child = row, Margin = new Thickness(0, 0, 0, 5), Padding = new Thickness(8, 4, 8, 4) };
            card.SetResourceReference(StyleProperty, "Card");
            panel.Children.Add(card);
        }
    }

    internal void ShowDiff(AgentChange change)
    {
        var panel = new StackPanel();
        var path = Wrap(change.Path, "FgBrush");
        path.Margin = new Thickness(0, 0, 0, 4);
        panel.Children.Add(path);
        if (change.Preview.Length > 0)
        {
            var diff = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, FlowDirection = FlowDirection.LeftToRight };
            diff.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            using var reader = new StringReader(change.Preview);
            while (reader.ReadLine() is { } line)
            {
                var run = new Run(line + "\n");
                run.SetResourceReference(TextElement.ForegroundProperty, line.StartsWith('+') ? "OkBrush" : line.StartsWith('-') ? "DangerBrush" : "FgDimBrush");
                diff.Inlines.Add(run);
            }
            panel.Children.Add(diff);
        }
        if (change.Limited || change.Preview.Length == 0)
            panel.Children.Add(Wrap(Strings.T(change.Preview.Length == 0 ? "agents.noDiff" : "agents.limitedDiff")));
        ShowOverlay(Strings.T("agents.latestChange") + Counts(change), new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = true });
    }

    static string Counts(AgentChange change) => change is { Added: int added, Removed: int removed }
        ? FormattableString.Invariant($"  +{added} −{removed}") : "";

    // ---- hooks overlay ----

    public void ShowHooks(string? message = null)
    {
        var stack = new StackPanel();
        foreach (var t in _a.Targets())
        {
            var installed = HookInstaller.IsInstalled(t);
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            if (installed || t.Detected)
            {
                var b = Small(installed ? "agents.remove" : "agents.install", () => Confirm(t, install: !installed), accent: !installed);
                b.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(b, Dock.Right);
                row.Children.Add(b);
                if (installed)
                {
                    var re = Small("agents.reinstall", () => Confirm(t, install: true), ghost: true);
                    re.VerticalAlignment = VerticalAlignment.Center;
                    DockPanel.SetDock(re, Dock.Right);
                    row.Children.Add(re);
                }
            }
            var info = new StackPanel();
            var name = new TextBlock { Text = t.Name, FontSize = 12.5, FontWeight = FontWeights.SemiBold };
            name.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
            info.Children.Add(name);
            var status = new TextBlock { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis,
                Text = Strings.T(installed ? "agents.installed" : t.Detected ? "agents.notInstalled" : "agents.notFound") + " · " + t.ConfigPath };
            status.SetResourceReference(TextBlock.ForegroundProperty, installed ? "OkBrush" : "FgDimBrush");
            info.Children.Add(status);
            row.Children.Add(info);
            var card = new Border { Child = row, Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(12, 7, 10, 3) };
            card.SetResourceReference(StyleProperty, "Card");
            stack.Children.Add(card);
        }
        stack.Children.Add(Wrap(message ?? Strings.T("agents.timeoutHint", _a.TimeoutSeconds()), message is null ? "FgDimBrush" : "FgBrush"));
        ShowOverlay(Strings.T("agents.hooks"), stack);
    }

    internal void Confirm(HookTarget t, bool install)
    {
        var plan = HookInstaller.Plan(t, DateTime.Now);
        var lines = new List<string>();
        if (plan.FileExists) lines.Add(Strings.T("agents.planBackup", plan.BackupPath!));
        else if (install) lines.Add(Strings.T("agents.planNew", t.ConfigPath));
        if (install)
        {
            lines.Add(Strings.T("agents.planCopy", plan.HookExe));
            lines.Add(Strings.T("agents.planEvents", string.Join(", ", plan.Events)));
            lines.Add(Strings.T(plan.AlreadyInstalled ? "agents.planReplace" : "agents.planKeep"));
        }
        else lines.Add(Strings.T("agents.planRemove"));
        lines.Add(Strings.T("agents.planRestart"));

        var stack = new StackPanel();
        foreach (var l in lines)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
            var bullet = Wrap("•");
            bullet.Width = 14;
            DockPanel.SetDock(bullet, Dock.Left);
            row.Children.Add(bullet);
            var tb = Wrap(l);
            tb.FontSize = 11.5;
            row.Children.Add(tb);
            stack.Children.Add(row);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(Small("agents.cancel", () => ShowHooks()));
        buttons.Children.Add(Small("agents.confirm", () => ShowHooks(install ? _a.Install(t) : _a.Remove(t)), accent: true));
        ShowOverlay(Strings.T(install ? "agents.confirmInstall" : "agents.confirmRemove", t.Name),
            new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, buttons, closable: false);
    }

    void ShowOverlay(string title, FrameworkElement body, FrameworkElement? extra = null, bool closable = true)
    {
        _workerKey = null;
        _workerPanel = null;
        var close = closable ? IconAction("Icon.Close", "ai.close", HideOverlay) : null;
        _overlay.Child = Ui.OverlayFrame(new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis }, body, close, extra, 6);
        _overlay.Visibility = Visibility.Visible;
    }

    public void HideOverlay()
    {
        _workerKey = null;
        _workerPanel = null;
        _overlay.Visibility = Visibility.Collapsed;
        _overlay.Child = null;
    }

    // ---- small helpers ----

    static TextBlock Wrap(string text, string brush = "FgDimBrush")
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    static Button Small(string? key, Action click, bool accent = false, bool ghost = false, string? tipKey = null, string? text = null)
    {
        var b = Ui.TextButton(text ?? Strings.T(key!), accent ? "AccentButton" : ghost ? "GhostButton" : "FlatButton", new Thickness(12, 3, 12, 4), click, "agents action");
        b.Margin = new Thickness(0, 0, 6, 0);
        b.FontSize = 12;
        if (tipKey is not null) b.ToolTip = Strings.T(tipKey);
        return b;
    }

    static Button IconAction(string icon, string tipKey, Action click) => Ui.IconAction(icon, tipKey, click, "agents action", "FgBrush");
}
