using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Isle.Core;

namespace Isle;

// Debug renderer: `Isle.exe --snapshot <state|all> <out.png|dir> [--theme Dark|Light] [--lang en|uk] [--scale 1.25]`.
// Builds the island off-screen in snapshot mode (no hooks/timers), puts it into the named state and saves a PNG.
public partial class IslandWindow
{
    /// <summary>Registered snapshot states: name → setup. Modules add their own states in later milestones.</summary>
    internal static readonly Dictionary<string, Action<IslandWindow>> SnapshotStates = new()
    {
        ["idle"] = _ => { },
        ["expanded-empty"] = w => w.Expand(),
    };

    /// <summary>Entry point for --snapshot. Returns the process exit code.</summary>
    public static int RunSnapshot(string[] args)
    {
        try
        {
            if (args.Length < 3) { Console.Error.WriteLine("usage: --snapshot <state|all> <out.png|dir> [--theme Dark|Light] [--lang en|uk] [--scale s]"); return 2; }
            Modules.Agents.AgentSnapshots.Register(SnapshotStates);
            Modules.Media.MediaSnapshots.Register(SnapshotStates);
            Modules.SystemMonitor.SystemMonitorSnapshots.Register(SnapshotStates);
            Modules.Ports.PortsSnapshots.Register(SnapshotStates);
            Modules.Projects.ProjectsSnapshots.Register(SnapshotStates);
            Modules.Kube.KubeSnapshots.Register(SnapshotStates);
            Modules.Docker.DockerSnapshots.Register(SnapshotStates);
            Modules.Usage.UsageSnapshots.Register(SnapshotStates);
            Modules.Setup.SetupSnapshots.Register(SnapshotStates);
            SnapshotStates["logs-pill"] = w => w.Pill.Set(new PillItem { Id = "logs.match", Priority = PillPriority.Transient, Width = 300, Peek = true, View = () => Modules.Logs.LogWatchModule.PillView("app.log", "ERROR 500 POST /api/orders: NullReferenceException in OrderService.Create") });
            Views.SettingsPanel.SettingsSnapshots.Register(SnapshotStates);
            var state = args[1];
            var output = args[2];
            string? theme = Arg(args, "--theme"), lang = Arg(args, "--lang");
            var scale = double.TryParse(Arg(args, "--scale"), System.Globalization.CultureInfo.InvariantCulture, out var sc) ? sc : 1.0;

            if (state == "all")
            {
                Directory.CreateDirectory(output);
                foreach (var name in SnapshotStates.Keys)
                foreach (var th in new[] { "Dark", "Light" })
                foreach (var lg in new[] { "en", "uk" })
                    RenderOne(name, Path.Combine(output, $"{name}.{th.ToLowerInvariant()}.{lg}.png"), th, lg, scale);
                return 0;
            }
            if (!SnapshotStates.ContainsKey(state)) { Console.Error.WriteLine($"unknown state '{state}'. Known: {string.Join(", ", SnapshotStates.Keys)}"); return 2; }
            RenderOne(state, output, theme ?? "Dark", lang ?? "en", scale);
            return 0;
        }
        catch (Exception ex)
        {
            Logger.Error("snapshot", ex);
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    static void RenderOne(string state, string path, string theme, string lang, double scale)
    {
        // in-memory only: snapshot never saves settings
        var s = Settings.Current;
        s.Theme = theme;
        s.Language = lang;
        s.Scale = scale;
        Strings.Language = lang;
        Theme.Apply(theme);
        Theme.SetAccent(null);

        var w = new IslandWindow(snapshotMode: true);
        SnapshotStates[state](w);
        w.SetTargets(immediate: true);

        var root = w.Root;
        var size = new Size(BaseWidth * scale, BaseHeight * scale);
        root.Measure(size);
        root.Arrange(new Rect(size));
        root.UpdateLayout();
        w.Tabs.UpdateLayout(); // indicator position is computed after layout
        root.UpdateLayout();

        const double dpi = 192; // 2× so details are visible when inspecting
        var bmp = new RenderTargetBitmap((int)(size.Width * 2), (int)(size.Height * 2), dpi, dpi, PixelFormats.Pbgra32);
        // checkerboard-free backdrop: mid grey so both themes and the shadow are visible
        var backdrop = new DrawingVisual();
        using (var dc = backdrop.RenderOpen())
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x7A, 0x84, 0x90)), null, new Rect(size));
        bmp.Render(backdrop);
        bmp.Render(root);

        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir is not null) Directory.CreateDirectory(dir);
        using var fs = File.Create(path);
        enc.Save(fs);
        w.Close();
    }
}
