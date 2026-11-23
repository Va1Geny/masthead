using Isle.Core;
using Isle.Views.SettingsPanel;

namespace Isle.Home.Views;

/// <summary>The Settings parts the Home pack adds: idle clock, edge auto-hide and click-through rows, and the Modules and Hotkeys pages.</summary>
static class HomeSettings
{
    static Settings S => Settings.Current;

    public static void Register()
    {
        SettingsView.PackAppearance.Add(v => v.Row("set.clock", SettingsView.Switch(S.ShowClock, on => { S.ShowClock = on; v.Save("ShowClock"); })));
        SettingsView.PackBehaviour.Add(v =>
        {
            v.Row("set.edge", SettingsView.Switch(S.AutoHideEdge, on => { S.AutoHideEdge = on; v.Save("AutoHideEdge"); }), "set.edge.hint");
            v.Row("set.clickThrough", SettingsView.Switch(S.ClickThrough, on => { S.ClickThrough = on; v.Save("ClickThrough"); }), "set.clickThrough.hint");
        });
        SettingsView.AddSection("modules", "options", Modules);
        SettingsView.AddSection("hotkeys", "about", Hotkeys);
    }

    static void Modules(SettingsView v)
    {
        v.Hint("set.modules.hint");
        foreach (var id in SettingsView.ModuleIds)
        {
            var failed = v.Actions.StateOf(id) == ModuleState.Failed;
            var sw = SettingsView.Switch(S.IsModuleEnabled(id), on => { S.Modules[id] = on; S.Save("Modules"); v.Actions.SyncModules(); v.Render(); });
            v.Row("mod." + id, sw, failed ? "set.failed" : "mod." + id + ".hint", hintBrush: failed ? "DangerBrush" : null);
        }
    }

    static void Hotkeys(SettingsView v)
    {
        v.Hint("set.hotkeys.hint");
        if (v.HotkeyNote is { } note)
        {
            var n = SettingsView.Text(note, dim: false);
            n.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "WarnBrush");
            n.Margin = new System.Windows.Thickness(0, 0, 0, 6);
            v.Page.Children.Add(n);
        }
        foreach (var h in v.Hotkeys()) v.HotkeyRow(h);
    }
}
