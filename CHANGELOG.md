# Changelog

All notable changes to Masthead (the developer edition of Isle) are listed here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added
- Contributing guide, Code of Conduct, issue forms, pull request template, CODEOWNERS and `.editorconfig`.
- `--backdrop <rrggbb>` option for `--snapshot`, used for the README screenshots.

### Changed
- Renamed to **Masthead** in the UI, release names and docs. Files, folders and `%AppData%\Isle` keep the
  Isle name, so settings and installed agent hooks keep working.
- README: dark screenshots in a gallery, download, security and contributing sections.

## [1.1.0] - 2026-11-23

### Added
- **Docker tab**: containers grouped by compose project over the Docker or Podman engine pipe, updated live from engine
  events; start / stop / restart, live-following logs, compose up / restart / down, labels for local databases, disk
  usage and a confirmed cleanup that never prunes volumes. Pills when a container keeps crashing or the engine stops.
- **Servers tab**: hosts from `~/.ssh/config` with opt-in reachability and Open in Windows Terminal; AWS SSO login expiry,
  Azure subscription and gcloud account (read-only); a watch list for downtime, expiring or untrusted TLS certificates
  and status-page incidents, managed in the tab.
- **Projects**: GitHub pull requests waiting for your review, GitLab merge requests and pipelines (cloud and
  self-hosted), Fetch and fast-forward-only Pull, build-time sparkline per project.
- **Usage**: Codex sessions, rate-limit windows and a per-day split.
- **Agents**: Gemini CLI hooks; switching agents removes Isle's hooks from the others.
- **Setup**: GitLab and Podman choices; new modules start right after setup without a restart.
- **Music**: previous and next buttons in the mini player.
- Eased, slower mouse-wheel scrolling everywhere; the scrollbar stops short of the island's bottom edge.
- CI on every push and pull request, releases from version tags, Dependabot updates, CodeQL (once the repository is
  public), `SECURITY.md`.

### Changed
- GitHub CI status now comes through the `gh` CLI; the personal access token setting is gone.
- Ports lists WSL distributions only; containers moved to the Docker tab.
- The Docker engine-down pill hides after 7 seconds.
- Tabs have a fixed, unique order; the setup list is no longer capped at 10 items.
- Removed the `Vortice.DXGI` and `ProtectedData` packages (VRAM is read from the display driver's registry key).

### Fixed
- Kubernetes warning now notices a `~/.kube` folder created after Isle started.
- Gemini CLI is detected by its own settings file rather than the shared `~/.gemini` folder.
- Confirm buttons and long Ukrainian lines are no longer clipped; tooltips close when an overlay opens.
- TLS checks time out instead of freezing the Servers tab; git fetch and pull never wait for a password prompt.
- The Docker footer clears when the engine stops; the settings dropdown follows the dark theme.

### Security
- Links from outside data (CI, pull and merge requests, watch list) open only if they are `https`.
- Compose actions run only for projects whose compose files are on a local drive.
- Command-line tools are never picked up from the folder Isle was started in.
- The hook pipe refuses connections from other machines.
- Size limits for followed Docker logs, status pages and GitLab responses.
- ssh aliases that could inject commands are skipped; the GitLab token goes only to the configured host; shell-unsafe
  hook helper paths are refused.
- The build fails on NuGet packages with known high or critical vulnerabilities; workflow actions are pinned to commit
  SHAs.

## [1.0.0]

- First release of the developer edition: one `Isle.exe` with the shell, Music, Agents, System, Ports, Projects, Log
  watcher, Kubernetes warning and Usage.

[Unreleased]: https://github.com/Va1Geny/masthead/compare/v1.1.0...HEAD
[1.1.0]: https://github.com/Va1Geny/masthead/releases/tag/v1.1.0
