using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Isle.Core;
using Isle.Modules.Tray;
using Microsoft.Win32;

namespace Isle;

public partial class App : Application
{
    const string MutexName = @"Local\Isle.SingleInstance.7c1f0b52";
    Mutex? _mutex;
    bool _quitting;

    public static ModuleHost Host { get; } = new();
    public static IslandWindow? Island { get; private set; }
    /// <summary>True while running the --snapshot renderer.</summary>
    public static bool IsSnapshot { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        HookExceptionHandlers();
        Views.SmoothScroll.Enable();

        if (e.Args.Length > 0 && e.Args[0] == "--snapshot")
        {
            IsSnapshot = true;
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            Shutdown(IslandWindow.RunSnapshot(e.Args));
            return;
        }

        // Tools are started by bare name (git, gh, docker, wt.exe), and Windows looks in the current directory before PATH.
        // Isle started from a shell inside a cloned repository would otherwise run that repository's git.exe.
        Environment.CurrentDirectory = AppContext.BaseDirectory;

        _mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirst);
        if (!isFirst)
        {
            _mutex.Dispose();
            _mutex = null;
            Shutdown(0);
            return;
        }

        Strings.Language = Settings.Current.Language;
        Theme.Apply();
        if (Autostart.IsEnabled) Autostart.Set(true); // older builds registered Isle.Launcher.exe, which no longer ships

        // Software rendering keeps the GPU driver (~80 MB) out of the process; see DECISIONS.md.
        if (!Settings.Current.OptBool("render.hardware"))
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        Island = new IslandWindow();
        Island.Closing += OnIslandClosing;
        Island.Show();

        // Everything else waits until the pill has been drawn once.
        Dispatcher.BeginInvoke(StartModules, DispatcherPriority.ApplicationIdle);
    }

    void StartModules()
    {
        try
        {
            Host.Register("tray", () => new TrayModule(
                isIslandVisible: () => Island?.IsHiddenByUser == false,
                toggleIsland: ToggleIsland,
                openSettings: () => OpenSettings(),
                quit: Quit));
            Host.Register("agents", () => new Modules.Agents.AgentsModule(Island!));
            Host.Register("media", () => new Modules.Media.MediaModule(Island!));
            Host.Register("sysmon", () => new Modules.SystemMonitor.SystemMonitorModule(Island!));
            Host.Register("docker", () => new Modules.Docker.DockerModule(Island!));
            Host.Register("ports", () => new Modules.Ports.PortsModule(Island!));
            Host.Register("projects", () => new Modules.Projects.ProjectsModule(Island!));
            Host.Register("logs", () => new Modules.Logs.LogWatchModule(Island!));
            Host.Register("kube", () => new Modules.Kube.KubeModule(Island!));
            Host.Register("servers", () => new Modules.Servers.ServersModule(Island!));
            Host.Register("usage", () => new Modules.Usage.UsageModule(Island!));
            if (Island is { } island) Modules.Setup.SetupFlow.ShowOnFirstExpand(island);
            Host.Sync();
            if (Island is not null) Island.SettingsRequested += () => OpenSettings();
            Island?.Hotkeys.Register("toggle", "hotkey.toggle", "Win+Alt+I", () => { if (Island is null) return; Island.SetHidden("user", false); Island.Toggle(); });

            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            Logger.Info($"Isle started, {Host.Ids.Count} module(s) registered");
            Memory.TrimSoon();
        }
        catch (Exception ex) { Logger.Error("module startup", ex); }
    }

    void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
            Dispatcher.BeginInvoke(() => Island?.Reposition());
    }

    public static void ToggleIsland()
    {
        if (Island is null) return;
        Island.SetHidden("user", !Island.IsHiddenByUser);
    }

    static Views.SettingsPanel.SettingsView? _settings;

    /// <summary>Shows the settings panel inside the island (gear button, tray menu).</summary>
    public static void OpenSettings(string? section = null)
    {
        if (Island is not { } w) return;
        try
        {
            w.SetHidden("user", false);
            _settings ??= new Views.SettingsPanel.SettingsView(new Views.SettingsPanel.SettingsActions(
                Hotkeys: () => w.Hotkeys.List(),
                Rebind: (id, g) => w.Hotkeys.Rebind(id, g),
                SuspendHotkeys: on => w.Hotkeys.Suspend(on),
                RestartModule: id => { if (!Host.IsEnabled(id)) return; Host.Stop(id); Host.Start(id); },
                Open: OpenFromSettings,
                Close: w.HideOverlay));
            if (section is not null) _settings.ShowSection(section);
            w.ShowOverlay(_settings);
        }
        catch (Exception ex) { Logger.Error("open settings", ex); }
    }

    static void OpenFromSettings(string target)
    {
        if (Island is not { } w) return;
        if (target == "hooks" && Host.Get<Modules.Agents.AgentsModule>("agents") is { } agents) agents.OpenHooks();
        else w.ShowToast(Strings.T("set.moduleOff"));
    }

    void OnIslandClosing(object? sender, CancelEventArgs e)
    {
        e.Cancel = true;
        Quit();
    }

    async void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        try
        {
            // Leave the native Closing callback before WPF begins shutting down its windows.
            await Dispatcher.Yield(DispatcherPriority.Normal);
            if (Island is not null) Island.Closing -= OnIslandClosing;
            Shutdown(0);
        }
        catch (Exception ex) { Logger.Error("quit", ex); }
        finally { _quitting = false; }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            Host.StopAll();
        }
        catch (Exception ex) { Logger.Error("shutdown", ex); }
        if (_mutex is not null)
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
        base.OnExit(e);
    }

    void HookExceptionHandlers()
    {
        DispatcherUnhandledException += (_, a) =>
        {
            Logger.Error("unhandled (UI)", a.Exception);
            a.Handled = true; // keep the island alive
        };
        AppDomain.CurrentDomain.UnhandledException += (_, a) =>
            Logger.Error("unhandled (domain)", a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) =>
        {
            Logger.Error("unobserved task", a.Exception);
            a.SetObserved();
        };
    }
}
