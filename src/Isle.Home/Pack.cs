using System.Globalization;
using Isle.Core;
using Isle.Modules.AiChat;
using Isle.Modules.Characters;
using Isle.Modules.Tools;
using Isle.Modules.Weather;
using Isle.Views.SettingsPanel;

namespace Isle.Home;

/// <summary>
/// Entry point of the Home pack (community features). Isle's <c>Core.Packs</c> calls <see cref="Register"/> once, after
/// the Music pack, at start-up and in snapshot mode.
/// </summary>
public static class Pack
{
    static ToolsModule? _tools;
    static Views.IdleClock? _clock;

    public static void Register()
    {
        Strings.Add(HomeStrings.En, HomeStrings.Uk);
        var host = App.Host;
        host.Register("lyrics", () => new Modules.Lyrics.LyricsModule());
        host.Register("timers", () => new Modules.Timers.TimersModule(App.Island!));
        host.Register("notes", () => new Modules.Notes.NotesModule(App.Island!));
        host.Register("control", () => new Modules.ControlCenter.ControlModule(App.Island!));
        host.Register("live", () => new Modules.LiveEvents.LiveEventsModule(App.Island!));
        host.Register("clipboard", () => new Modules.Clipboard.ClipboardModule(App.Island!));
        host.Register("shelf", () => new Modules.Shelf.ShelfModule(App.Island!));
        host.Register("commands", () => new Modules.Commands.CommandModule(App.Island!, () => App.Host.Get<Modules.Timers.TimersModule>("timers")?.Controller));
        host.Register("tools", () => _tools ??= new ToolsModule(App.Island!));
        host.Register("weather", () => new WeatherModule(App.Island!));
        host.Register("ai", () => new AiModule(App.Island!));
        host.Register("voice", () => new Modules.Voice.VoiceModule(App.Island!));
        host.Register("characters", () => new CharactersModule(App.Island!));
        Views.HomeSettings.Register();
        if (App.Island is { } island) // the idle clock: shown while the "Clock in the idle pill" setting is on
        {
            void Clock() => island.IdleView.CenterSlot = Settings.Current.ShowClock ? _clock ??= new Views.IdleClock() : null;
            Clock();
            Settings.Changed += _ => Clock();
        }
        SettingsView.AddModules("agents", "lyrics", "timers", "notes", "control", "live", "clipboard", "shelf", "commands", "tools", "weather", "ai", "voice");
        SettingsView.AddModules("tray", "characters");
        SettingsView.PackOptions.Add(Options);
        App.OpenTargets["weather"] = () => App.Host.Get<WeatherModule>("weather") is { } weather ? weather.OpenSetup : null;
        App.OpenTargets["ai"] = () => App.Host.Get<AiModule>("ai") is { } ai ? ai.OpenSettings : null;
        App.OpenTargets["characters"] = () => App.Host.Get<CharactersModule>("characters") is { } ch ? ch.OpenGallery : null;
        App.BeforeQuit.Add(() => _tools?.FinishRecordingAsync(exiting: true) ?? Task.FromResult(true));
        App.DebugCommands["--transcribe"] = Modules.Voice.VoiceSelfTest.RunAsync;
        Modules.Lyrics.MusicTab.Register();

        var states = IslandWindow.SnapshotStates;
        states["idle-clock"] = w => w.IdleView.CenterSlot = new Views.IdleClock();
        Modules.Lyrics.MusicTab.RegisterSnapshots(states);
        Modules.Timers.TimersSnapshots.Register(states);
        Modules.Notes.NotesSnapshots.Register(states);
        Modules.ControlCenter.ControlSnapshots.Register(states);
        Modules.LiveEvents.LiveSnapshots.Register(states);
        Modules.Clipboard.ClipSnapshots.Register(states);
        Modules.Commands.CommandSnapshots.Register(states);
        ToolsSnapshots.Register(states);
        Modules.Weather.WeatherSnapshots.Register(states);
        AiSnapshots.Register(states);
        Modules.Voice.VoiceSnapshots.Register(states);
        CharacterSnapshots.Register(states);
    }

    static void Options(SettingsView view, Action<string> group)
    {
        var s = Settings.Current;
        group("media");
        view.Row("set.lyricsInPill", view.OptSwitch("lyrics.inPill", false));
        view.Row("set.lyricsAlways", view.OptSwitch("lyrics.alwaysFetch", false));

        group("weather");
        var city = s.Opt("weather.city");
        view.Row("set.wxPlace", SettingsView.Link(city.Length > 0 ? city : Strings.T(s.OptBool("weather.ip") ? "set.wxIp" : "set.wxNone"), "set.change", () => view.Actions.Open("weather")));
        view.Row("set.wxUnits", view.Choice(["C", "F"], s.Opt("weather.units", "C") == "F" ? "F" : "C", v => { s.SetOpt("weather.units", v); view.Save("Options", restart: "weather"); }, null, v => "°" + v));
        view.Row("set.wxInPill", view.OptSwitch("weather.inPill", true, restart: "weather"));
        view.Row("set.wxRain", view.OptSwitch("weather.rainAlerts", true, restart: "weather"));

        group("live");
        foreach (var name in Modules.LiveEvents.LiveEventsModule.SourceNames)
        {
            if (name == "notifications")
            {
                var off = SettingsView.Switch(false, _ => { });
                off.IsEnabled = false;
                view.Row("set.live." + name, off, "set.live.notifications.hint");
                continue;
            }
            view.Row("set.live." + name, view.OptSwitch("live." + name, true));
        }
        view.Row("set.obsPort", SettingsView.TextField(s.OptInt("live.obsPort", 4455).ToString(CultureInfo.InvariantCulture), v =>
        {
            if (int.TryParse(v, out var p) && p is > 0 and < 65536) { s.SetOpt("live.obsPort", p.ToString(CultureInfo.InvariantCulture)); view.Save("Options", restart: "live"); }
            else view.Render();
        }, "4455", width: 70));
        view.Row("set.obsPassword", view.SecretField("live.obsPassword", restart: "live"));
        view.Row("set.downloads", view.OptText("live.downloadsFolder", Strings.T("set.default"), "live"), wide: true);

        group("clipboard");
        view.Row("set.clipLimit", view.Choice(["50", "100", "200", "500", "1000"], s.OptInt("clip.limit", 200).ToString(CultureInfo.InvariantCulture),
            v => { s.SetOpt("clip.limit", v); view.Save("Options", restart: "clipboard"); }, null, v => v));
        view.Row("set.clipExcluded", view.OptText("clip.excludedApps", "keepass, 1password"), "set.clipExcluded.hint", wide: true);

        group("shelf");
        view.Row("set.shelfRemove", view.OptSwitch("shelf.removeAfterDrop", false));

        group("commands");
        view.Row("set.cmdFolders", view.OptText("cmd.folders", @"C:\Users\me\Projects", "commands"), "set.cmdFolders.hint", wide: true);

        group("tools");
        view.Row("set.recAudio", view.OptSwitch("rec.systemAudio", true));
        view.Row("set.recMic", view.OptSwitch("rec.mic", false));

        group("ai");
        view.Row("set.aiProvider", SettingsView.Link(Strings.T(AiProviders.TitleKey(AiProviders.FromSettings(s))), "set.open", () => view.Actions.Open("ai")));

        group("voice");
        view.Row("set.voiceModel", view.Choice(["base", "small"], s.Opt("voice.model", "base") == "small" ? "small" : "base", v => { s.SetOpt("voice.model", v); view.Save("Options"); }, v => "set.voice." + v));
        view.Row("set.voiceLang", view.Choice(["auto", "en", "uk", "nl"], s.Opt("voice.lang", "auto") is "en" or "uk" or "nl" ? s.Opt("voice.lang") : "auto",
            v => { s.SetOpt("voice.lang", v); view.Save("Options"); }, v => "set.voiceLang." + v));

        group("characters");
        view.Row("set.gallery", SettingsView.Link(Strings.T("chars." + s.Opt("chars.kind", "pebb")), "set.open", () => view.Actions.Open("characters")));
    }
}
