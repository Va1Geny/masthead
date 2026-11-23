# ARCHITECTURE

Hand-written module map. One paragraph per module: what it owns, public surface, lifetime, data files.
Keep it short and current (update at the end of every milestone).

## Process shape
One WPF process, `Isle.exe` (`src/Isle`, `net10.0-windows10.0.19041.0`). `App.OnStartup` installs exception handlers,
takes the single-instance mutex, applies language and render mode, shows `IslandWindow`, and only after the first frame
(`DispatcherPriority.ApplicationIdle`) registers the modules (tray, agents, media and the developer modules below) and
calls `ModuleHost.Sync()`.
Static access points: `App.Host` (modules), `App.Island` (the window), `Settings.Current`.

## Shell
- **App** (`App.xaml.cs`) — start-up/shutdown, single instance (`Local\Isle.SingleInstance.*` mutex), global exception
  logging, `ToggleIsland()`, `Quit()`, repositions the island on resume from sleep.
- **IslandWindow** (`IslandWindow.xaml(.cs)` + partials) — the only always-present window. Fixed size (`BaseWidth×BaseHeight`
  × UI scale), transparent, topmost, `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`, no click-through.
  `Reposition()` puts it top-centre of the selected monitor (physical pixels). Partials:
  - `.Motion.cs` — springs (`Core/Spring`) for width/height/expansion stepped on `CompositionTarget.Rendering`
    only while moving; `ApplyShape()` writes the clip, shadow paths and content opacities (no layout, no window resize).
  - `.Hover.cs` — 30 ms cursor timer (runs only while engaged), hover intent/leave delay, `Expand(tab, sticky)`,
    `Collapse()`, `Toggle()`, `HoldOpen/ReleaseOpen(key)`, keyboard focus hand-over (`AcquireFocus`, `PasteTarget`),
    `SetHidden(reason, bool)` (user / fullscreen / excluded), foreground checks.
  - `.Content.cs` — pill (`Pill` = `Core/PillQueue`; primary view + compact secondary), tabs (`AddTab(TabInfo)`,
    `RemoveTab`, `SelectTab`, `RequestTab`; views created lazily, `ITabView.OnShown/OnHidden`), "more" overlay, `ShowToast`.
  - `.Snapshot.cs` — `--snapshot <state|all> <out.png|dir> [--theme] [--lang] [--scale]`; `SnapshotStates` registry.
  Public members used by modules: `Pill`, `Hotkeys`, `Watcher`, `IdleView`, tab API (`TabViewFor`), `ShowToast`,
  `Pulse`, `ExpandedChanged`. `PillItem.HoverExpands = false` for banners with buttons.
- **Native** (`Native.cs`) — P/Invoke declarations only.

## Core (`src/Isle/Core`)
- **Paths** — data root `%AppData%\Isle` (override: `ISLE_DATA_DIR` or `Paths.Root`), file/dir helpers.
- **Logger** — `log.txt`, rotates to `log.txt.1` at 1 MB, never throws. No secrets/user content.
- **JsonStore** — `Load<T>` / `Save<T>` / `WriteAtomic` (temp + replace) / `BackupCorrupt` (`*.bak-<timestamp>`). Every store uses it.
- **Settings** — `settings.json`; typed fields + `Modules` (id → enabled), `Hotkeys`, `Options` (free-form per-module
  strings). `Settings.Current`, `Save(changedName)` raises `Settings.Changed`. `Migrate()` upgrades old versions.
- **Strings** — EN/UK dictionaries (`Strings.Data.cs`), `Strings.T(key)`, XAML `{core:Loc key}` (live language switch).
- **EventBus** — typed publish/subscribe between modules and views.
- **ModuleHost / IModule** — modules are registered as factories, constructed and started only when enabled
  (`Sync()`), stopped/disposed when disabled; a throwing module becomes `Failed` without affecting others.
- **Screens** — monitor enumeration (physical pixels, DPI scale); `Selected(index)`.
- **Autostart** — `HKCU\...\Run` value `Isle`.
- **Theme** — Dark/Light/System palette as brushes in `Application.Resources` (`BgBrush`, `FgBrush`, `FgDimBrush`,
  `SurfaceBrush`, `AccentBrush`, …), `Apply()` swaps them live; `SetAccent(color)` animates the accent (album art).
- **Spring** — damped spring value used by the island's motion.
- **PillQueue / PillItem / PillPriority** — what the collapsed pill shows; pure `Select()` rule (tested).
- **TabInfo / ITabView** — tab registration record and the shown/hidden callbacks for tab views.
- **ForegroundWatcher** — `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)`; `LastExternal`, `IsFullscreen`, `ProcessName`.
- **Memory** — `TrimSoon()`: GC + `EmptyWorkingSet` after start-up and after heavy features.

## Styles / Views
- `Styles/Theme.xaml` — the flat control set (FlatButton, AccentButton, GhostButton, IconButton, Switch, Chip, slider,
  FlatTextBox, scrollbars, FlatList, TabButton, Card, text styles). `Styles/Icons.xaml` — original 24×24 line icons.
- `Views/Icon` (draws an icon geometry), `Views/TabBar` (tab strip + sliding indicator + more + gear), `Views/IdlePillView`
  (first-use hint).

## Modules
Shell (`src/Isle/Modules`): Agents, Hotkeys, Tray, Media (Music). Developer modules: see below.

- **Media** (`media`, `Modules/Media`) — `MediaSession` wraps GSMTC (manager + chosen session events, marshalled to the
  UI thread; a session without a title yet publishes an empty-title state) and exposes `IMediaSource` (`Current:
  MediaState`, `Changed`, `PlayPause/Next/Previous/Seek`). `MediaModule` shows the mini player (`Views/Music/MiniPlayerPill`:
  art, title — artist, play/pause; "App — loading…" while the session has no track) as the `music` pill item (compact
  equalizer for the side slot) and the `music.track` banner, and sets `Theme.SetAccent` from the art. The mini player
  stays while paused. Pure helpers: `PlaybackMath`, `ArtColors`. `FakeMediaSource` + `MediaSnapshots` provide snapshot states.
- **Hotkeys** (`HotkeyService`, owned by the island, not a module) — `Register(id, titleKey, default, pressed, released?)`,
  `Rebind`, `List()` for the settings table; user overrides in `Settings.Hotkeys`; failures logged, never fatal.
  Hold-hotkeys poll the key state at 30 ms only while held. Default: `toggle` = Win+Alt+Space.
- **Tray** (`tray`) — notification-area icon (`Shell_NotifyIcon`, hidden message window), native menu:
  Show/Hide, Settings, Start with Windows, Quit. Icon drawn by `TrayIcon.Render`. Re-adds itself on `TaskbarCreated`.

- **Agents** (`agents`, `Modules/Agents` + `src/Isle.Hook`) — `Isle.Hook.exe <claude|codex> [--timeout s] [--async]`
  is registered as a command hook, reads the hook JSON from stdin and sends it over the named pipe
  `Isle.Agents.v1.<user>` (`HookProtocol`: one JSON line each way, `BuildOutput` makes the hook's stdout; fails open).
  `AgentPipeServer` (accept loop, per-connection serve, disconnect watch) → `AgentsModule.Handle` → `AgentBook`
  (sessions keyed by agent + session_id, states idle/working/waiting/done, pending `AgentRequest`s with a reply TCS).
  View `Views/Agents/AgentsView` (cards with Allow / Deny / In terminal, question option chips, hooks panel with
  plan + confirm), pill `AgentPill` (dot: working / waiting / done check). `HookInstaller` (plan, install, remove,
  backup; helper copied to `%AppData%\Isle\bin`). Options `agents.approvals`, `agents.timeout`.

### Developer modules
Registered in `App.StartModules`, on by default; their settings rows are in `SettingsView.OptionsPage` and their snapshot
states in `IslandWindow.RunSnapshot`. Helpers: `Core/Cli` (run a console tool, capture stdout, timeout),
`Core/ProcessOps` (end a process if its name still matches, open with the shell).

- **System** (`sysmon`, `Modules/SystemMonitor`) — `SystemSampler` (GetSystemTimes, per-core
  `NtQuerySystemInformation`, `GlobalMemoryStatusEx`, network interface counters, PDH for GPU engines, GPU memory and
  disk; DXGI for total VRAM), `SystemMath` (pure), `SustainedAlert` (threshold held for a while, then a cooldown).
  View `Views/SystemMonitor/SystemMonitorView` samples once a second only while shown. Option `sysmon.alerts`.
- **Ports** (`ports`, `Modules/Ports`) — `PortScanner` (`GetExtendedTcpTable`, owner PIDs, session-0 flag),
  `PortFilter` (merge per port and program, filter system processes / vendor services / dynamic ports),
  `Containers` (`docker ps`, `wsl -l -v`, stop). View `Views/Ports/PortsView`.
  Option `ports.notify`.
- **Projects** (`projects`, `Modules/Projects`) — `RepoParse` (git porcelain v2, GitHub remotes, Actions runs, search
  counts), `BuildWatcher` + `BuildCommand` (recognise build/test command lines via `NtQueryInformationProcess`, keep a
  process handle for the exit code), `ProjectsModule` (git via `Cli`, GitHub REST with an optional DPAPI token, pills).
  View `Views/Projects/ProjectsView`. Options `projects.paths`, `projects.builds`, `projects.githubToken`.
- **Log watcher** (`logs`, `Modules/Logs`) — `LogTail` (follow a file from its end, survive rotation, first matching
  line), `LogWatchModule` (2 s poll, pill, 15 s quiet period). Options `logs.files`, `logs.pattern`.
- **Kubernetes context** (`kube`, `Modules/Kube`) — `KubeConfig` (kubectl file order from `KUBECONFIG` or
  `~\.kube\config`, reads only the top-level `current-context:` line, prod regex with a timeout), `KubeModule`
  (FileSystemWatcher + 300 ms debounce; persistent red indicator while the context matches, 6 s banner on a switch).
  Option `kube.prodPattern`.
- **Claude usage** (`usage`, `Modules/Usage`) — `UsageScanner` / `UsageService` (incremental read of Claude Code
  transcripts under `~\.claude\projects` or `CLAUDE_CONFIG_DIR`: token counters only, deduplicated, cached in
  `usage.json`; scans only while the tab is shown or on refresh), `UsagePrices` (API-equivalent USD per model),
  `UsageReport` (today / 7 / 30 days, recent sessions). View `Views/Usage/UsageView`.

## Settings panel (`Views/SettingsPanel`)
`SettingsView` — full-panel overlay opened by `App.OpenSettings(section?)` (tab-bar gear, tray "Settings"). Sections
General (appearance, behaviour, the toggle hotkey) · Options (per-module rows and groups) · About. Writes
`Settings.Current` and saves (`Settings.Changed` handlers apply live); module restarts, hotkey rebind and the agent hooks
link go through `SettingsActions`. `SettingsSnapshots` registers `settings-*` snapshot states.

## Tests (`tests/Isle.Tests`)
xUnit; `TestSetup` redirects `Paths.Root` to a temp folder. Pure logic only; UI is checked through snapshots (from M1).
