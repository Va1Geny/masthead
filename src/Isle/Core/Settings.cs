using System.Text.Json;
using System.Text.Json.Nodes;

namespace Isle.Core;

/// <summary>User settings (settings.json). One instance: <see cref="Current"/>. Call <see cref="Save"/> after changes.</summary>
public sealed class Settings
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    // Appearance
    public string Theme { get; set; } = "System";      // Dark | Light | System
    public double Scale { get; set; } = 1.0;           // 0.8 .. 2.0
    public string Language { get; set; } = "en";       // en | uk
    public string MonitorDevice { get; set; } = "";
    public int Monitor { get; set; } = -1;             // -1 = primary, else index

    // Behaviour
    public bool AutoHideEdge { get; set; }
    public bool ClickThrough { get; set; }
    public bool HideInFullscreen { get; set; } = true;
    public List<string> ExcludedApps { get; set; } = [];
    public bool ExcludedAppsOnlyOnOverlap { get; set; }
    public string LastTab { get; set; } = "";

    /// <summary>Module id → enabled. Missing id means the module's default.</summary>
    public Dictionary<string, bool> Modules { get; set; } = [];

    /// <summary>Hotkey action id → gesture text such as "Win+Alt+N". Missing id means the default.</summary>
    public Dictionary<string, string> Hotkeys { get; set; } = [];

    /// <summary>Free-form per-module options (strings); secrets are stored DPAPI-encrypted by their module.</summary>
    public Dictionary<string, string> Options { get; set; } = [];

    // ---- access ----

    static Settings? _current;
    public static Settings Current => _current ??= Load(Paths.SettingsFile);

    /// <summary>Raised (on the caller's thread) after <see cref="Save"/>; argument is the changed property name or "".</summary>
    public static event Action<string>? Changed;

    public bool IsModuleEnabled(string id, bool defaultValue = true) =>
        Modules.TryGetValue(id, out var on) ? on : defaultValue;

    public string Opt(string key, string fallback = "") =>
        Options.TryGetValue(key, out var v) ? v : fallback;

    public bool OptBool(string key, bool fallback = false) =>
        Options.TryGetValue(key, out var v) ? v == "1" : fallback;

    public int OptInt(string key, int fallback) =>
        Options.TryGetValue(key, out var v) && int.TryParse(v, out var n) ? n : fallback;

    public void SetOpt(string key, string value) => Options[key] = value;
    public void SetOpt(string key, bool value) => Options[key] = value ? "1" : "0";

    public void Save(string changed = "")
    {
        Normalize();
        JsonStore.Save(Paths.SettingsFile, this);
        try { Changed?.Invoke(changed); }
        catch (Exception ex) { Logger.Error("settings changed handler", ex); }
    }

    public static Settings Load(string path)
    {
        try
        {
            if (System.IO.File.Exists(path))
            {
                var node = JsonNode.Parse(System.IO.File.ReadAllText(path),
                    documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (node is JsonObject obj)
                {
                    Migrate(obj);
                    var s = obj.Deserialize<Settings>(JsonStore.Options) ?? new Settings();
                    s.Normalize();
                    return s;
                }
                throw new JsonException("settings root is not an object");
            }
        }
        catch (JsonException)
        {
            JsonStore.BackupCorrupt(path);
        }
        catch (Exception ex)
        {
            Logger.Error("load settings", ex);
        }
        return new Settings();
    }

    /// <summary>Upgrades older documents in place. Version 0 (pre-release) used "DarkMode": bool and "UiScale": percent.</summary>
    internal static void Migrate(JsonObject obj)
    {
        var version = obj["Version"]?.GetValue<int>() ?? 0;
        if (version < 1)
        {
            if (obj["DarkMode"] is JsonValue dm && dm.TryGetValue<bool>(out var dark))
            {
                obj["Theme"] = dark ? "Dark" : "Light";
                obj.Remove("DarkMode");
            }
            if (obj["UiScale"] is JsonValue us && us.TryGetValue<double>(out var pct))
            {
                obj["Scale"] = pct / 100.0;
                obj.Remove("UiScale");
            }
        }
        obj["Version"] = CurrentVersion;
    }

    void Normalize()
    {
        if (Theme is not ("Dark" or "Light" or "System")) Theme = "System";
        if (Language is not ("en" or "uk")) Language = "en";
        if (double.IsNaN(Scale)) Scale = 1.0;
        Scale = Math.Clamp(Scale, 0.8, 2.0);
        ExcludedApps ??= [];
        Modules ??= [];
        Hotkeys ??= [];
        Options ??= [];
        LastTab ??= "";
        Version = CurrentVersion;
    }
}
