using System.IO;
using System.Text.Json.Nodes;
using Isle.Core;
using Isle.Modules.Tray;

namespace Isle.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "isle-tests-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
}

public class SettingsTests
{
    [Fact]
    public async Task Atomic_writes_keep_complete_files_when_writers_overlap()
    {
        using var dir = new TempDir();
        var path = dir.File("notes.json");
        var payloads = Enumerable.Range(0, 8).Select(i => new string((char)('a' + i), 100_000)).ToArray();
        await Task.WhenAll(payloads.Select(value => Task.Run(() => JsonStore.WriteAtomic(path, value))));
        Assert.Contains(File.ReadAllText(path), payloads);
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp"));
    }

    [Fact]
    public void Empty_document_recovers_the_latest_readable_backup()
    {
        using var dir = new TempDir();
        var path = dir.File("notes.json");
        File.WriteAllText(path + ".bak-1", """{"Theme":"Dark"}""");
        File.WriteAllText(path, "");
        Assert.Equal("Dark", JsonStore.Load<Settings>(path).Theme);
        Assert.True(File.Exists(path + ".bak-1"));
    }

    [Fact]
    public void Missing_file_gives_defaults()
    {
        using var dir = new TempDir();
        var s = Settings.Load(dir.File("settings.json"));
        Assert.Equal("System", s.Theme);
        Assert.Equal(1.0, s.Scale);
        Assert.Equal("en", s.Language);
        Assert.True(s.HideInFullscreen);
        Assert.True(s.IsModuleEnabled("anything"));
    }

    [Fact]
    public void Roundtrip_keeps_values()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        var s = new Settings { Theme = "Light", Scale = 1.5, Language = "uk" };
        s.Modules["media"] = false;
        s.SetOpt("weather.city", "Київ");
        Assert.True(JsonStore.Save(path, s));
        var back = Settings.Load(path);
        Assert.Equal("Light", back.Theme);
        Assert.Equal(1.5, back.Scale);
        Assert.Equal("uk", back.Language);
        Assert.False(back.IsModuleEnabled("media"));
        Assert.Equal("Київ", back.Opt("weather.city"));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Corrupt_file_is_backed_up_and_defaults_returned()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        File.WriteAllText(path, "{ this is not json");
        var s = Settings.Load(path);
        Assert.Equal("System", s.Theme);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(dir.Path, "settings.json.bak-*"));
    }

    [Fact]
    public void Migrates_version_0_fields()
    {
        var obj = JsonNode.Parse("""{ "DarkMode": true, "UiScale": 150 }""")!.AsObject();
        Settings.Migrate(obj);
        Assert.Equal("Dark", obj["Theme"]!.GetValue<string>());
        Assert.Equal(1.5, obj["Scale"]!.GetValue<double>());
        Assert.Equal(Settings.CurrentVersion, obj["Version"]!.GetValue<int>());
        Assert.False(obj.ContainsKey("DarkMode"));
    }

    [Fact]
    public void Out_of_range_values_are_normalized()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        File.WriteAllText(path, """{ "Version": 1, "Theme": "Pink", "Scale": 9, "Language": "xx", "ExcludedApps": null }""");
        var s = Settings.Load(path);
        Assert.Equal("System", s.Theme);
        Assert.Equal(2.0, s.Scale);
        Assert.Equal("en", s.Language);
        Assert.NotNull(s.ExcludedApps);
    }
}

public class StringsTests
{
    [Fact]
    public void Both_languages_have_the_same_keys()
    {
        var missingUk = Strings.English.Keys.Except(Strings.Ukrainian.Keys).ToList();
        var missingEn = Strings.Ukrainian.Keys.Except(Strings.English.Keys).ToList();
        Assert.True(missingUk.Count == 0, "missing in uk: " + string.Join(", ", missingUk));
        Assert.True(missingEn.Count == 0, "missing in en: " + string.Join(", ", missingEn));
    }

    [Fact]
    public void No_empty_strings()
    {
        Assert.DoesNotContain(Strings.English, kv => string.IsNullOrWhiteSpace(kv.Value));
        Assert.DoesNotContain(Strings.Ukrainian, kv => string.IsNullOrWhiteSpace(kv.Value));
    }

    [Fact]
    public void Unknown_key_returns_key()
    {
        Assert.Equal("no.such.key", Strings.T("no.such.key"));
    }
}

public class ModuleHostTests
{
    sealed class Fake(string id, bool failOnStart = false) : IModule
    {
        public int Starts, Stops;
        public string Id => id;
        public void Start() { if (failOnStart) throw new InvalidOperationException("boom"); Starts++; }
        public void Stop() => Stops++;
    }

    [Fact]
    public void Modules_are_lazy_and_follow_enabled_state()
    {
        var enabled = new Dictionary<string, bool> { ["a"] = true, ["b"] = false };
        var host = new ModuleHost((id, def) => enabled.TryGetValue(id, out var on) ? on : def);
        int builtA = 0, builtB = 0;
        Fake? a = null;
        host.Register("a", () => { builtA++; return a = new Fake("a"); });
        host.Register("b", () => { builtB++; return new Fake("b"); });
        Assert.Equal(0, builtA);

        host.Sync();
        Assert.Equal(1, builtA);
        Assert.Equal(0, builtB);
        Assert.Equal(ModuleState.Running, host.StateOf("a"));
        Assert.Equal(ModuleState.Stopped, host.StateOf("b"));
        Assert.NotNull(host.Get<Fake>("a"));

        enabled["a"] = false; enabled["b"] = true;
        host.Sync();
        Assert.Equal(1, a!.Stops);
        Assert.Equal(1, builtB);
        Assert.Null(host.Get<Fake>("a"));
    }

    [Fact]
    public void Failing_module_does_not_break_others()
    {
        var host = new ModuleHost((_, _) => true);
        host.Register("bad", () => new Fake("bad", failOnStart: true));
        host.Register("good", () => new Fake("good"));
        host.Sync();
        Assert.Equal(ModuleState.Failed, host.StateOf("bad"));
        Assert.Equal(ModuleState.Running, host.StateOf("good"));
        host.StopAll();
        Assert.Equal(ModuleState.Stopped, host.StateOf("good"));
    }
}

public class EventBusTests
{
    record Ping(int N);

    [Fact]
    public void Publish_reaches_subscribers_until_disposed_and_survives_throwing_handlers()
    {
        var got = 0;
        using var bad = EventBus.Subscribe<Ping>(_ => throw new Exception("x"));
        var sub = EventBus.Subscribe<Ping>(p => got += p.N);
        EventBus.Publish(new Ping(2));
        sub.Dispose();
        EventBus.Publish(new Ping(5));
        Assert.Equal(2, got);
    }
}

public class TrayIconTests
{
    [Fact]
    public void Icon_has_opaque_centre_and_transparent_corners()
    {
        var px = TrayIcon.Render(32);
        Assert.Equal(32 * 32 * 4, px.Length);
        Assert.Equal(0, px[3]);                               // top-left alpha
        Assert.Equal(255, px[(16 * 32 + 12) * 4 + 3]);        // centre alpha
    }
}
