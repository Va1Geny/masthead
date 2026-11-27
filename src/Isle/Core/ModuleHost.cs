namespace Isle.Core;

/// <summary>A feature that can be started and stopped at runtime. Start/Stop are called on the UI thread.</summary>
public interface IModule
{
    string Id { get; }
    void Start();
    void Stop();
}

/// <summary>
/// Owns module lifetime: modules are registered as factories (nothing is constructed until needed),
/// started when enabled in settings, stopped when disabled, and isolated from each other's failures.
/// </summary>
public sealed class ModuleHost
{
    sealed class Entry(string id, Func<IModule> factory, bool defaultEnabled)
    {
        public readonly string Id = id;
        public readonly Func<IModule> Factory = factory;
        public readonly bool DefaultEnabled = defaultEnabled;
        public IModule? Instance; // non-null only while running
    }

    readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> _order = [];
    readonly Func<string, bool, bool> _isEnabled;

    public ModuleHost(Func<string, bool, bool>? isEnabled = null) =>
        _isEnabled = isEnabled ?? ((id, def) => Settings.Current.IsModuleEnabled(id, def));

    public IReadOnlyList<string> Ids => _order;

    public void Register(string id, Func<IModule> factory, bool defaultEnabled = true)
    {
        _entries[id] = new Entry(id, factory, defaultEnabled);
        _order.Add(id);
    }

    public bool IsEnabled(string id) => _entries.TryGetValue(id, out var e) && _isEnabled(e.Id, e.DefaultEnabled);

    /// <summary>The running instance, or null when the module is stopped/failed/unknown.</summary>
    public T? Get<T>(string id) where T : class, IModule =>
        _entries.TryGetValue(id, out var e) ? e.Instance as T : null;

    /// <summary>Starts every enabled module and stops every disabled one. Call after settings change.</summary>
    public void Sync()
    {
        foreach (var id in _order)
        {
            if (IsEnabled(id)) Start(id);
            else Stop(id);
        }
    }

    public bool Start(string id)
    {
        if (!_entries.TryGetValue(id, out var e)) return false;
        if (e.Instance is not null) return true;
        IModule? instance = null;
        try
        {
            instance = e.Factory();
            instance.Start();
            e.Instance = instance;
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"module {id} failed to start", ex);
            try { instance?.Stop(); } catch { /* already failing */ }
            return false;
        }
    }

    public void Stop(string id)
    {
        if (!_entries.TryGetValue(id, out var e) || e.Instance is null) return;
        try { e.Instance.Stop(); }
        catch (Exception ex) { Logger.Error($"module {id} failed to stop", ex); }
        e.Instance = null;
    }

    public void StopAll()
    {
        for (var i = _order.Count - 1; i >= 0; i--) Stop(_order[i]);
    }
}
