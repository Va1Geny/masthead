namespace Isle.Core;

/// <summary>User settings (settings.json). One instance: <see cref="Current"/>. Call <see cref="Save"/> after changes.</summary>
public sealed class Settings
{
    // Appearance
    public string Theme { get; set; } = "System";      // Dark | Light | System
    public double Scale { get; set; } = 1.0;           // 0.8 .. 2.0
    public string Language { get; set; } = "en";       // en | uk
    public string MonitorDevice { get; set; } = "";
    public int Monitor { get; set; } = -1;             // -1 = primary, else index

    // Behaviour
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
        var s = JsonStore.Load<Settings>(path);
        s.Normalize();
        return s;
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
    }
}
