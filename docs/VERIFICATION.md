# VERIFICATION

## Initial WPF audit repairs

| Check | Result |
|---|---|
| Release suite | 556 passed on Windows 11 with .NET 10. |
| Escape and pointer intent | An isolated desktop profile showed the old WPF version reserved Escape while expanded. The repaired version leaves Escape free and stays collapsed during a held-button hover. |
| Keyboard navigation | Win+Alt+Space focused the expanded island, Tab reached a radio button, focused Escape collapsed it, and Ctrl+Alt+Space focused the command field. |
| Published package | The .NET 10 x64 package and single-file hook helper were built. The package rendered a Ukrainian light-theme snapshot and opened its visible desktop window. |
| Tier 2 idle budget | 60 one-second samples after startup on the i5-9300H with default modules. Average 19.0 MiB working set, 28.8 MiB private, 0.36% of one logical CPU and 0.045% of machine CPU. Peak working set 23.4 MiB. |
| Agent approval | Authenticated pipe round trips passed and a long Ukrainian approval snapshot showed scrolling and a disabled Allow action. Existing agents need the hook helper upgraded through Isle before they connect to the new endpoint. |
| Contrast and layouts | Palette matrix tested both themes. Exported Ukrainian control center, settings, reminder, overflow, command and agent approval snapshots. |
| Media and monitors | Automated suite passed. Restarting a real media player under the same AUMID and mixed-DPI docking were not exercised. |

## Compact WPF controls at 80%

Measured the arranged WPF controls at 80% in isolated English and Ukrainian snapshot profiles, in both themes. The original 28-DIP tab target measured 22.4 effective DIPs and its 12-DIP label measured 9.6. The expanded capsule remained 416 × 176 effective DIPs.

| Control | Result at 80% |
|---|---|
| Navigation | Icon-only tabs measured 27.2 × 24 DIPs. More and Settings measured 24 × 24. The selected tab, overflow list and Settings navigation used 12-DIP text. The active Ukrainian Hotkeys label fit in full. |
| Sliders | The Settings scale and Music seek controls have 24-DIP hit areas at 80%. The visible thumb and rail retain their original sizes. Routed drag and keyboard checks passed. |

## Developer edition

| Feature | Method | Result |
|---|---|---|
| System monitor: CPU, cores, memory, GPU, VRAM, network, disk, top processes | auto-test (math + real sampler) + snapshot + live render on this machine | ok; temperatures not included (need a driver) |
| Overload pill (CPU or memory ≥ 90% for 30 s) | auto-test (`SustainedAlert`) + snapshot | ok |
| Ports: listeners, merge, filters, command `port N` | auto-test + live (real listeners: Herd, nginx, php, node) | ok |
| WSL distributions in Ports (containers moved to the Docker tab in 1.1) | auto-test (output parser) | ok |
| Projects: git status, remotes, Actions runs, review count | auto-test (parsers) + live (`git status` of this repository) | ok; GitHub API calls manual-needed: add a repository with Actions and watch a run finish |
| Build watcher (command lines, exit code, duration) | auto-test (classifier + a real child process with exit code 3) + live (`dotnet build` detected) | ok |
| Log watcher (new lines only, rotation, pattern) | auto-test | ok |

Every feature → how it was verified and the result.
Methods: `auto-test` (xUnit), `snapshot` (PNG looked at), `live` (ran the real app and observed/measured),
`manual-needed` (could not be truly exercised here — steps for the owner), `unavailable` (with reason).

| Feature | Method | Result |
|---|---|---|
| Solution builds (Release, warnings as errors) | live | ok |
| Settings: defaults, roundtrip, corruption backup, migration, normalisation | auto-test | ok (5 tests) |
| Strings EN/UK key parity | auto-test | ok |
| ModuleHost: lazy start, enable/disable, failure isolation | auto-test | ok |
| EventBus | auto-test | ok |
| Single instance | live | ok — second process exits immediately |
| Pill visible top-centre, black, 120×32 @100% | live (screenshot at 125% DPI) | ok |
| Tray icon + menu | manual-needed | Right-click the Isle icon in the notification area: Show/Hide, Settings (opens the settings panel in the island), Start with Windows, Quit should work |
| Idle budget (M0) | live | 8 MB WS / 31 MB private / 0% CPU |
| Hover expand (120 ms intent) / collapse on leave (250 ms) | live (cursor driven by script, screenshots) | ok |
| Spring expand/collapse (~400 ms perceived, slight overshoot, collapse faster) | auto-test + live | ok |
| Tab bar with sliding indicator, label on selected tab, "more" overflow list, gear | snapshot (dark/light × en/uk, 150% scale) | ok |
| Toast inside the panel (text + action) | snapshot | ok |
| Themes Dark / Light / System (follows Windows live via WM_SETTINGCHANGE) | snapshot (dark/light); System: manual-needed | Switch Windows Settings → Personalisation → Colours → app mode; the island recolours within a second |
| UI scale 80–200% | snapshot at 150% + manual-needed for the live preview | ok; Settings → General → Size: drag the slider, the island resizes as you go |
| Fullscreen auto-hide | manual-needed | Play a video fullscreen / press F11 in a browser → island disappears; exit → it returns |
| Excluded apps | auto-test (name matching) + manual-needed | Settings → General → "Hide while these apps are in front" → type `notepad`, Enter → island hides while Notepad is in front |
| Global hotkeys infrastructure (RegisterHotKey, failures non-fatal, shown in Settings → Hotkeys) | auto-test (gesture parsing) + manual-needed | Press Win+Alt+I → panel opens/closes; j/k rows, h/l and 1-9 tabs, q closes |
| Esc collapses | manual-needed | Expand, press Esc |
| Pill priority queue (selection, sharing, banners) | auto-test | ok |
| --snapshot tool | live | `Isle.exe --snapshot all <dir>` renders every state × theme × language |
| Start-up to visible pill | live | 457 ms to input-idle |
| Idle budget (M1) | live | 38.7 MB WS / 41.3 MB private / 0.009 % CPU over 60 s |
| Music: multiple sessions (prefer playing, else current/recent) | auto-test (`PlaybackMath.Choose`) | ok |
| Agents: hook JSON (PermissionRequest allow/deny, AskUserQuestion answers via updatedInput, no-decision cases) | auto-test | ok |
| Agents: pipe protocol — real client vs real server (allow/deny, delayed answer, question, status no-wait, fail open without island, hook timeout cancels the server wait) | auto-test | ok |
| Agents: Isle.Hook.exe ↔ running Isle (status event, waiting pill, timeout → no output, card removed; Allow pressed in the tab → `{"behavior":"allow"}` on stdout in ~35 ms) | live (UI Automation) | ok |
| Agents: tab cards, question chips, empty/setup, hooks panel, install confirm, pills (waiting/working/done) | snapshot (dark/light, en/uk) | ok |
| Agents: install/remove hooks (backup, merge, only Isle entries removed, invalid JSON never overwritten, missing file created) | auto-test (temp configs) | ok |
| Agents: real Claude Code / Codex session through the installed hooks | manual-needed | Agents tab → plug icon → Claude Code → Install → Confirm; start a new `claude` session, ask it to run `dir`; the island shows "needs you" with Allow/Deny — press Allow and the command runs. Remove restores the file (a backup `settings.json.isle-backup-*` is kept). Same for Codex. |
| Ukrainian translation review (all 650+ strings; consistent "папка", typographic apostrophes) | auto-test (key parity) + snapshot sheets (104 states) | ok |
| All UI states in both themes and both languages | snapshot (`--snapshot all`: 104 states × Dark/Light × EN/UK = 416 PNGs, inspected as contact sheets) | ok (switches that start "on" render "off" in snapshots only — template animation; live they are correct) |
| `build.ps1` → `dist\Isle\Isle.exe` (framework-dependent, 69 MB) and `dist\Isle-selfcontained\Isle.exe` | live | both start and show the pill; Whisper `--transcribe` works from both (self-contained needed loose native DLLs — fixed) |

## Phase 2 (1.1)

| Feature | Method | Result |
|---|---|---|
| Docker tab: engine pipe, events, compose grouping, start/stop/restart, compose up/down | auto-test (fake engine over a real pipe) + live (Docker Desktop, throwaway containers) | ok |
| Docker logs live follow, framed/TTY demux | auto-test (partial frames) + live (throwaway container, backlog plus new lines) | ok |
| Docker prune | auto-test + live (confirm opened and cancelled only) | ok; never run against real data |
| Docker crash-loop and engine-down pills (down pill hides after 7 s) | auto-test + live (engine down → up → down) | ok |
| Podman pipe | auto-test (pipe choice) | manual-needed: no Podman machine here |
| Servers: ssh config (Include, Host/Match), opt-in TCP reachability | auto-test (fixtures) + live (real ssh config, TCP only) | ok |
| Servers watch list: down, TLS expiry, untrusted certificate, status page | auto-test + live (expired / self-signed / wrong-host badssl.com, example.com) | ok |
| Servers: Open in Windows Terminal | — | manual-needed |
| AWS SSO expiry, Azure, gcloud | auto-test (fixtures) | ok; no SSO profile or Azure/gcloud files on this machine |
| Projects: GitHub CI via `gh`, review requests via `gh` | auto-test (recorded JSON) + live (read-only `gh` calls) | ok |
| Projects: GitLab MRs and pipelines | auto-test (recorded API responses) | manual-needed: no GitLab account here |
| Projects: fetch, fast-forward-only pull | auto-test + live (throwaway repos: up to date, fast-forward, diverged) | ok |
| Build-time history per project (command line or process working folder) | auto-test (path matching) + live (working folder read of a real process) | ok |
| Agents: Codex usage and rate limits | auto-test (rollout fixtures) | ok |
| Agents: Gemini CLI hooks, removal of the other agents' hooks on switch | auto-test | Gemini manual-needed: Gemini CLI not installed |
| First-run setup, "Run setup again", new modules started without a restart | live | ok |
| Mini player previous / next | snapshot | ok |
| Eased wheel scrolling, scrollbar inset | auto-test (step and easing math) + live | ok |
| Clipped UI (overlay buttons, long Ukrainian lines, Servers bottom inset) | snapshot (dark, en/uk) | ok |
