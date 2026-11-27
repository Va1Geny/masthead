using Isle.Core;
using Isle.Modules.Hotkeys;

namespace Isle.Views.SettingsPanel;

/// <summary>--snapshot states for the settings panel (one per section, plus a scrolled options page).</summary>
static class SettingsSnapshots
{
    static readonly HotkeyInfo[] DemoHotkeys = [new("toggle", "hotkey.toggle", "Win+Alt+I", "Win+Alt+I", true)];

    static SettingsView Make() => new(new SettingsActions(
        Hotkeys: () => DemoHotkeys, Rebind: (_, _) => true, SuspendHotkeys: _ => { },
        RestartModule: _ => { }, Open: _ => { }, Close: () => { }));

    public static void Register(Dictionary<string, Action<IslandWindow>> states)
    {
        foreach (var section in SettingsView.Sections)
            states["settings-" + section] = w =>
            {
                var v = Make();
                v.ShowSection(section);
                w.ShowOverlay(v);
            };
        states["settings-options-end"] = w =>
        {
            var v = Make();
            v.ShowSection("options");
            w.ShowOverlay(v);
            v.ScrollToEnd();
        };
    }
}
