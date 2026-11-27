# DECISIONS

One line each: decision — why.

## One app
- The product is called Masthead; `Isle` stays in file, project, namespace, pipe, mutex, autostart and `%AppData%` names — renaming those would orphan settings and installed hooks for no user-visible gain.
- One `Isle.exe` holds the shell, Music and the developer modules. This fork keeps only these; the upstream repository keeps the community modules.
- The app targets `net10.0-windows10.0.19041.0` for the media session (GSMTC) through the built-in WinRT projection; no NuGet package for it.
- Modules are registered directly in `App.StartModules`; their settings rows are in `SettingsView.OptionsPage` and their text in `Strings.Data.cs`. There is no loader and no hook list.
- Icons stay in `Styles/Icons.xaml`: a few shared geometries.
- The music pill is a mini player with a play/pause button. Hovering a button on the collapsed island does not start the hover-open (`IsOverButton`), the rest of the pill still opens it.

## Developer modules
- System monitor reads Win32 (`GetSystemTimes`, `NtQuerySystemInformation`, `GlobalMemoryStatusEx`) and PDH for GPU and disk — no driver, no admin, no NuGet package. Temperatures would need a kernel driver and are left out.
- Sampling runs once a second only while the System tab is on screen; in the background only CPU and memory are read, every 5 s, for the overload pill.
- Ports come from `GetExtendedTcpTable`; listeners are merged per port and program (nginx workers are one row) and filtered: system processes, session-0 services that are not known dev servers, and the dynamic range ≥ 49152.
- Build detection reads command lines with `NtQueryInformationProcess` (class 60) only for processes not seen before, and keeps its own process handle so the exit code survives the exit.
- CI uses the GitHub REST API directly instead of requiring the `gh` CLI; the token is optional and stored with DPAPI like other secrets.

## WPF audit repairs
- SW-C2 publishes a per-launch endpoint and pins the executable at hook installation. Reinstall existing hooks to upgrade the helper and refresh the pin after moving the app.
- SW-C3 / UI-H2 presents the complete tool input, requires scrolling long inputs before Allow, and defers oversized or missing inputs to the terminal.
- UI-C1 handles Escape through focus inside the island. The expanded WPF panel no longer reserves the system key.
- UI-C2 rearms hover after leaving the pill and requires cursor movement without a held mouse button.
- SW-H3 removes the forced collection and working-set trim from ordinary collapse.
- SW-M1 distinguishes WinRT media sessions by object identity so a restarted player is rebound.
- UI-C3 / H5 uses distinct accent text and fill contrast, adds keyboard focus and accessibility names, and respects reduced motion.
- UI-M1–M4 fits the rendered stage to its monitor, preserves stable display and tab choices, and measures or wraps translated labels.

## Security review
- Links from outside data (CI runs, PRs/MRs, GitLab) go through `ProcessOps.OpenUrl`: absolute https only, re-escaped. `ProcessOps.Open` is for paths and links Isle builds itself.
- The hook pipe drops clients Windows reports a computer name for: `CurrentUserOnly` does not set `PIPE_REJECT_REMOTE_CLIENTS`, and the same domain account could otherwise connect over SMB. Same-user local processes are trusted (they can edit the agents' configs anyway), so there is no extra token.
- Isle's current directory is its own folder: tools are started by bare name and Windows searches the current directory before PATH.
- Compose buttons only for labels compose would write (compose project name, drive-rooted local paths): any image can set those labels.
- Codex/Gemini hook commands are shell strings; a helper path with characters a shell would interpret is refused (only a space is quoted; no quoting of `& ; ' % $` works in cmd, PowerShell and sh alike).
- ssh aliases are limited to letters, digits and `. _ - @ : +`: `wt.exe` splits its arguments at `;`.
- Response caps: GitLab 4 MiB, watch-list checks 1 MiB, followed Docker log lines 4096 chars (500 kept).

## M0
- Repo root is the solution root (`Isle.sln` at the top level).
- `Isle.sln` in classic `.sln` format (not `.slnx`) — matches the spec's file name and older tooling.
- Tray icon via raw `Shell_NotifyIcon` + native popup menu, not WinForms `NotifyIcon` — avoids loading WinForms (RAM budget).
- Tray icon pixels are generated in code (`TrayIcon.Render`) — original artwork, no asset files needed.
- Window is a WPF layered window (`AllowsTransparency`) — per-pixel alpha gives click-through outside the shape for free.
- **Software rendering by default** (`RenderOptions.ProcessRenderMode = SoftwareOnly`, option `render.hardware` to switch) —
  measured on this machine (i5-12450H, Intel Xe): hardware mode loads the Intel driver (`igd12dxva64` + `igc64`) and idles at
  157 MB working set / 109 MB private; software mode idles at 76 MB / 32 MB. The window is small (640×300 DIP) and layered
  windows need a GPU→CPU read-back anyway, so software rasterisation is the cheaper path. Smoothness to be measured in M1.
- `Memory.TrimSoon()` (full GC + `EmptyWorkingSet`) runs once after start-up and will run after heavy features finish —
  spec requires memory to be released after heavy use. Measured idle after trim: 8 MB working set, 31 MB private, 0 ms CPU / 30 s.
- GC: workstation, non-concurrent — no background GC thread, lower idle footprint.
- `TreatWarningsAsErrors` on for the app project — spec §5.
- Settings: typed core fields + a free-form `Options` string dictionary for per-module options — avoids touching the
  settings class and migration for every small module option.
- No NuGet packages in the app so far. Tests: xunit, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk (standard test stack).
- `logs/` is git-ignored — it holds the owner's run logs and my verification screenshots (`logs/shots`).

## M1
- Expand/collapse uses a hand-written damped spring (`Core/Spring`, semi-implicit Euler, 240 Hz sub-steps) on `CompositionTarget.Rendering`, subscribed only while moving — easier to retarget mid-flight (hover in/out quickly) than storyboards; expand k=170/c=20.5 (≈400 ms, ~2 % overshoot), collapse k=290/c=30.6 (≈260 ms).
- The shape is a `RectangleGeometry` clip on the content + three stacked translucent shadow paths (no `DropShadowEffect`, which would force an offscreen blur pass every frame in software rendering).
- Hover cursor timer (30 ms) runs only while the pointer is engaged or the panel is open; it stops itself when idle — idle CPU stays ~0.
- Fullscreen check: event-driven on foreground change (`SetWinEventHook`) plus a 2 s check only while "hide in fullscreen" is on (catches F11 without a foreground change; spec allows ≥1 s polling).
- Text boxes take focus by activating the window on click (`AcquireFocus`); focus is handed back to the previous window on collapse.
- Esc is a temporary global hotkey registered only while expanded (the island is not activated, so it gets no key events otherwise).
- `IslandWindow.Foreground` renamed to `Watcher` (hid `Control.Foreground`); `Views.Icon` is fully qualified inside the window (clashes with `Window.Icon`).
- Toggle hotkey (Win+Alt+I; Win+Alt+Space collides with PowerToys Command Palette) expands/collapses the panel and un-hides a user-hidden island; tray Show/Hide uses a "user" hide reason so it composes with fullscreen/excluded hiding.
- Snapshot renderer: off-screen `IslandWindow(snapshotMode: true)`, settings changed in memory only, rendered at 192 DPI on a grey backdrop. States are a registry (`SnapshotStates`) so later modules add theirs.

## M2
- GSMTC via the built-in WinRT projection of the `net10.0-windows10.0.19041.0` TFM — no NuGet package needed.
- Session choice: playing > system "current" > most recently active (`PlaybackMath.Choose`, tested); re-evaluated on every play-state change so starting another player switches over.
- Paused music keeps the pill for 2 minutes, then the pill returns to idle (the tab still shows the paused track) — a permanently paused player should not occupy the pill forever.
- Track banner (title + artist, 2.2 s) is shown the first time a track is seen *playing* (a track that appears paused gets it when it starts).
- Album art decoded at 192 px wide (enough for the 104-DIP art at 175 %), frozen; dominant colour = score-weighted average of the most saturated mid-light pixels of a 16×16 downsample; greyscale art → default blue.
- Accent brushes are replaced (not mutated) on every change and their colour animation is started before they enter `Application.Resources` — the dictionary freezes plain Freezables, and frozen brushes cannot animate (found live).
- Lyrics: LRCLIB `/api/get` (needs artist + duration) then `/api/search` with a cleaned title ("feat.", "Official Video", "Remastered" removed); search hits must be within 5 s of the track length; synced beats plain. "Not found" is cached for 7 days, network errors are never cached. User-Agent `Isle/1.0 (Windows desktop overlay; personal use)` (no personal data).
- Lyrics are fetched only when the lyrics view is open, or when option `lyrics.alwaysFetch` / `lyrics.inPill` is on. The HttpClient is disposed after 2 minutes idle.
- Lyrics are a separate module (`lyrics`) so the network part can be switched off; the Music tab says so when it is off.
- Equalizer animates at 20 fps (each frame repaints the whole layered window); measured cost while music plays collapsed: ~0.35 % of the machine.
- Live testing without sound: `scratch/fake-session.ps1` (outside the repo) creates a real SMTC session with a silent WAV at volume 0 and display properties set on a `MediaPlaybackItem`.

## M13
- Claude Code hooks follow https://code.claude.com/docs/en/hooks (read 2026-11-15): `PermissionRequest` → `hookSpecificOutput.decision.behavior` allow/deny (+ `message` on deny); `AskUserQuestion` is answered on `PreToolUse` with `permissionDecision: "allow"` + `updatedInput` = original `tool_input` + `answers` {question → label} (the docs say allow alone is not enough). Status events (SessionStart, UserPromptSubmit, PreToolUse, Notification, Stop, SessionEnd) are registered with `async: true` so they never delay the agent; only the two waiting hooks are synchronous.
- Claude Code handlers use the documented exec form (`command` = exe path, `args` = list) — no shell, so paths with spaces need no quoting in Git Bash or PowerShell. Hook `timeout` = Isle's wait + 30 s so Claude never kills the helper before it fails open.
- Codex: its hooks use the same shape (`~/.codex/hooks.json`, enabled by default, `PermissionRequest` with `decision.behavior`), so approvals work for Codex too; no Notification event and no AskUserQuestion there. Codex commands are shell strings (path quoted when it has spaces); status hooks get `timeout: 3` because Codex caps SessionEnd at 3 s. The old `notify` setting was not needed.
- `Isle.Hook.exe` = tiny framework-dependent console app (no packages), one pipe connection per call, 400 ms connect timeout and a `WaitNamedPipe` probe so a missing island costs ~nothing; any failure/timeout → exit 0, no output (fail open). Protocol (`HookProtocol.cs`) is one JSON line each way, compiled into both projects (linked file).
- Pipe `Isle.Agents.v1.<user>` created with `PipeOptions.CurrentUserOnly`; the first server instance is created synchronously in `Start()` so a hook arriving right after start-up finds it (found by flaky tests). The server notices a hook that left (timeout, Ctrl+C) by a pending 1-byte read and cancels the card.
- Hook-timeout cancel is posted to the UI thread from the `catch`, not via `ct.Register`: `WaitAsync`'s own cancellation callback resumes the handler inline and disposing the registration dropped ours before it ran, leaving a stale "waiting" card whose Allow went nowhere (found live with UI Automation).
- Install copies the helper to `%AppData%\Isle\bin\` (stable path across rebuilds), backs up the config to `<file>.isle-backup-<timestamp>`, merges (Isle handlers recognised by `Isle.Hook.exe` in `command`), never writes invalid/commented JSON; Remove takes out only Isle's handlers. The panel shows the exact plan (file, helper path, events) and needs a Confirm press. Not run against the owner's real `~/.claude/settings.json`.
- Interrupted runs send no Stop: a 1-min check runs only while some session is "working" and demotes ones silent for a while; sessions not heard from for 3 h are dropped.

## M15
- Settings live inside the island as a full-panel overlay (`Views/SettingsPanel/SettingsView`), not a separate window: the spec allows either and the island already hosts full-panel overlays (command bar, gallery). Left section list (General · Modules · Options · Hotkeys · About), scrolling page on the right; opened by the gear and the tray menu.
- Simple settings are written by the panel itself (`Settings.Save(name)` → existing `Settings.Changed` handlers apply them live); options a module reads only at start (clipboard limit, command-bar folders, OBS port/password, downloads folder, weather units/pill/alerts) restart that one module (`Host.Stop` + `Start`). Links open the existing in-module UIs instead of duplicating them: AI provider/key (AI tab settings), agent hooks (Agents tab hooks panel), character gallery, weather place (Weather tab).
- UI-scale slider: 80–200 % in 5 % steps, applied 160 ms after the thumb rests (live preview without re-laying out the window on every pixel).
- Icon-only buttons and tabs get their tooltip as UI Automation name (style setter) — screen readers, and the live/soak scripts find them by name.
- Memory is trimmed 5 s after the panel collapses (tab views paged in while open go back out). The trim timer runs at Background priority: at ApplicationIdle it never fired while the music equalizer animated every frame (found in the soak test, trims logged since).
- Known snapshot artefact: switches created already "on" render "off" in `--snapshot` PNGs because the switch template moves the thumb with a storyboard that does not run during off-screen rendering; live they are correct (checked with screenshots).

## Performance measurements
| When | State | Working set | Private | CPU |
|---|---|---|---|---|
| M0 | idle pill, tray only, software rendering, after trim | 8 MB | 31 MB | 0 ms over 30 s |
| M0 | same, before trim | 76 MB | 32 MB | — |
| M0 | hardware rendering (rejected default) | 157 MB | 109 MB | — |
| M1 | idle pill, tray, hover logic; 60 s after 1 expand/collapse | 38.7 MB | 41.3 MB | 63 ms over 60 s (0.009 %) |
| M1 | start-up to input-idle | — | — | 457 ms |
| M2 | music playing (fake SMTC session), pill collapsed, 20 s | 33 MB | — | 844 ms / 20 s (0.35 % of machine) |
