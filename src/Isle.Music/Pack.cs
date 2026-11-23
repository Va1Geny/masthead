using Isle.Core;
using Isle.Views.SettingsPanel;

namespace Isle.Music;

/// <summary>Entry point of the Music pack (media session + music pill). Isle's <c>Core.Packs</c> calls <see cref="Register"/> first.</summary>
public static class Pack
{
    public static void Register()
    {
        Strings.Add(MusicStrings.En, MusicStrings.Uk);
        App.Host.Register("media", () => new Modules.Media.MediaModule(App.Island!));
        SettingsView.AddModules("agents", "media");
        Modules.Media.MediaSnapshots.Register(IslandWindow.SnapshotStates);
    }
}
