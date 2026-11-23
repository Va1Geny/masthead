using System.Windows.Threading;
using Isle.Core;

namespace Isle.Modules.Hotkeys;

/// <summary>A parsed global hotkey such as "Win+Alt+N".</summary>
public readonly record struct Gesture(uint Modifiers, uint Key)
{
    static readonly (string Name, uint Vk)[] Named =
    [
        ("Space", 0x20), ("Enter", 0x0D), ("Tab", 0x09), ("Esc", 0x1B), ("Backspace", 0x08), ("Delete", 0x2E), ("Insert", 0x2D),
        ("Home", 0x24), ("End", 0x23), ("PageUp", 0x21), ("PageDown", 0x22), ("Left", 0x25), ("Up", 0x26), ("Right", 0x27), ("Down", 0x28),
        ("PrintScreen", 0x2C), ("Pause", 0x13), ("`", 0xC0), ("-", 0xBD), ("=", 0xBB), ("[", 0xDB), ("]", 0xDD), ("\\", 0xDC),
        (";", 0xBA), ("'", 0xDE), (",", 0xBC), (".", 0xBE), ("/", 0xBF),
    ];

    public bool IsValid => Key != 0;

    public static bool TryParse(string? text, out Gesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        uint mods = 0, key = 0;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= Native.MOD_CONTROL; continue;
                case "alt": mods |= Native.MOD_ALT; continue;
                case "shift": mods |= Native.MOD_SHIFT; continue;
                case "win" or "windows": mods |= Native.MOD_WIN; continue;
            }
            if (key != 0) return false; // two main keys
            key = ParseKey(raw);
            if (key == 0) return false;
        }
        if (key == 0) return false;
        gesture = new Gesture(mods, key);
        return true;
    }

    static uint ParseKey(string s)
    {
        if (s.Length == 1)
        {
            var c = char.ToUpperInvariant(s[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') return c;
        }
        if (s.Length is 2 or 3 && (s[0] is 'F' or 'f') && int.TryParse(s.AsSpan(1), out var f) && f is >= 1 and <= 24)
            return (uint)(0x70 + f - 1);
        foreach (var (name, vk) in Named)
            if (string.Equals(name, s, StringComparison.OrdinalIgnoreCase)) return vk;
        if (string.Equals(s, "Escape", StringComparison.OrdinalIgnoreCase)) return 0x1B;
        return 0;
    }

    public static string KeyName(uint vk)
    {
        if (vk is >= 'A' and <= 'Z' or >= '0' and <= '9') return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x70 + 1);
        foreach (var (name, v) in Named)
            if (v == vk) return name;
        return "";
    }

    public override string ToString()
    {
        if (!IsValid) return "";
        var parts = new List<string>(5);
        if ((Modifiers & Native.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((Modifiers & Native.MOD_WIN) != 0) parts.Add("Win");
        if ((Modifiers & Native.MOD_ALT) != 0) parts.Add("Alt");
        if ((Modifiers & Native.MOD_SHIFT) != 0) parts.Add("Shift");
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }
}

/// <summary>State of one hotkey for the settings table.</summary>
public sealed record HotkeyInfo(string Id, string TitleKey, string Gesture, string DefaultGesture, bool Registered);

/// <summary>
/// Global hotkeys via RegisterHotKey on the island's window. Actions are registered by id with a default gesture;
/// the user's override comes from Settings.Hotkeys. Registration failures are recorded, never fatal.
/// </summary>
public sealed class HotkeyService
{
    sealed class Entry
    {
        public required string Id, TitleKey, Default;
        public required Action Pressed;
        public Action? Released;
        public int NativeId;
        public Gesture Gesture;
        public bool Registered;
    }

    readonly Dictionary<string, Entry> _entries = [];
    readonly List<string> _order = [];
    IntPtr _hwnd;
    int _nextId = 0x4000;
    DispatcherTimer? _holdTimer;
    Entry? _held;
    bool _suspended;

    public event Action? Changed;
    public event Action<string>? RegistrationFailed;

    /// <summary>Called by the island once its window handle exists; registers everything declared so far.</summary>
    public void Attach(IntPtr hwnd)
    {
        _hwnd = hwnd;
        foreach (var e in _entries.Values) Bind(e);
        Changed?.Invoke();
    }

    /// <summary>Temporarily releases every hotkey (while the settings panel records a new combination) and takes them back.</summary>
    public void Suspend(bool suspended)
    {
        if (_hwnd == IntPtr.Zero || _suspended == suspended) return;
        _suspended = suspended;
        foreach (var e in _entries.Values)
        {
            if (suspended) Unbind(e);
            else Bind(e);
        }
    }

    public void Detach()
    {
        foreach (var e in _entries.Values) Unbind(e, releaseHold: false);
        _hwnd = IntPtr.Zero;
        _suspended = false;
    }

    /// <param name="released">Optional: called when the key is let go (hold-to-talk).</param>
    public void Register(string id, string titleKey, string defaultGesture, Action pressed, Action? released = null)
    {
        if (_entries.TryGetValue(id, out var old)) Unbind(old);
        else _order.Add(id);
        var e = new Entry { Id = id, TitleKey = titleKey, Default = defaultGesture, Pressed = pressed, Released = released, NativeId = _nextId++ };
        _entries[id] = e;
        Bind(e);
        Changed?.Invoke();
    }

    /// <summary>Changes a hotkey ("" = disabled, null = back to default) and saves it.</summary>
    public bool Rebind(string id, string? gesture)
    {
        if (!_entries.TryGetValue(id, out var e)) return false;
        var s = Settings.Current;
        var candidate = gesture ?? e.Default;
        if (candidate.Length > 0 && (!Gesture.TryParse(candidate, out var parsed) ||
            _entries.Values.Any(other => other != e && Gesture.TryParse(GestureText(other), out var existing) && existing == parsed)))
            return false;
        var hadOverride = s.Hotkeys.TryGetValue(id, out var previous);
        if (gesture is null) s.Hotkeys.Remove(id);
        else s.Hotkeys[id] = gesture;
        Bind(e);
        if (!e.Registered && candidate.Length > 0)
        {
            if (hadOverride) s.Hotkeys[id] = previous!;
            else s.Hotkeys.Remove(id);
            Bind(e);
            Changed?.Invoke();
            return false;
        }
        s.Save("Hotkeys");
        Changed?.Invoke();
        return true;
    }

    public IReadOnlyList<HotkeyInfo> List() =>
        [.. _order.Select(id => _entries[id]).Select(e => new HotkeyInfo(e.Id, e.TitleKey, GestureText(e), e.Default, e.Registered))];

    static string GestureText(Entry e) =>
        Settings.Current.Hotkeys.TryGetValue(e.Id, out var g) ? g : e.Default;

    void Bind(Entry e)
    {
        Unbind(e);
        e.Registered = false;
        e.Gesture = default;
        if (_hwnd == IntPtr.Zero) return;
        var text = GestureText(e);
        if (text.Length == 0 || !Gesture.TryParse(text, out var g)) return;
        e.Gesture = g;
        e.Registered = Native.RegisterHotKey(_hwnd, e.NativeId, g.Modifiers | Native.MOD_NOREPEAT, g.Key);
        if (!e.Registered)
        {
            Logger.Warn($"hotkey {e.Id} ({text}) could not be registered (in use by another app?)");
            RegistrationFailed?.Invoke(text);
        }
    }

    void Unbind(Entry e, bool releaseHold = true)
    {
        if (e.Registered && _hwnd != IntPtr.Zero) Native.UnregisterHotKey(_hwnd, e.NativeId);
        e.Registered = false;
        if (ReferenceEquals(_held, e)) StopHold(fire: releaseHold);
    }

    /// <summary>Feed WM_HOTKEY here. Returns true when a shortcut is handled.</summary>
    public bool HandleHotkey(int nativeId)
    {
        if (_suspended) return false; // A WM_HOTKEY queued before the recorder gained focus.
        foreach (var e in _entries.Values)
        {
            if (e.NativeId != nativeId) continue;
            try
            {
                e.Pressed();
                if (e.Released is not null) StartHold(e);
            }
            catch (Exception ex) { Logger.Error($"hotkey {e.Id}", ex); }
            return true;
        }
        return false;
    }

    // RegisterHotKey reports no key-up, so while a hold-hotkey is down we watch the key state (30 ms, only during the hold).
    void StartHold(Entry e)
    {
        StopHold(fire: false);
        _held = e;
        _holdTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Input, (_, _) =>
        {
            if (_held is null) { _holdTimer!.Stop(); return; }
            if ((Native.GetAsyncKeyState((int)_held.Gesture.Key) & 0x8000) == 0) StopHold(fire: true);
        }, Dispatcher.CurrentDispatcher);
        _holdTimer.Start();
    }

    void StopHold(bool fire)
    {
        var e = _held;
        _held = null;
        _holdTimer?.Stop();
        if (fire && e?.Released is not null)
        {
            try { e.Released(); }
            catch (Exception ex) { Logger.Error($"hotkey {e.Id} release", ex); }
        }
    }
}
