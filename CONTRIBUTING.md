# Contributing to Isle

Thanks for helping. This fork is the developer edition of Isle: one `Isle.exe` with the shell, Music and the developer
tabs. Features of the Community app (timers, notes, chat…) belong in the
[upstream repository](https://github.com/BKyrychenko/island), not here.

## Before you start

- **Bugs and ideas:** open an issue with the bug report or feature request form. For anything larger than a small fix,
  please agree on the approach in an issue first.
- **Security problems:** never in a public issue. Follow [`SECURITY.md`](SECURITY.md).

## Build, test and run

Requirements: Windows 10 (19041) or 11 x64 and the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
.\scripts\build.ps1               # build, run all tests, publish into dist\ and smoke-check the packages
.\scripts\build.ps1 -SkipTests    # faster local loop
dotnet test                       # tests only
.\dist\Isle\Isle.exe              # run it (only one Isle runs at a time; close yours first)
```

Use `ISLE_DATA_DIR=<temp folder>` to run with throwaway settings instead of your own `%AppData%\Isle`.

## Checking UI changes

Every UI state can be rendered to PNG without opening the real window:

```powershell
.\dist\Isle\Isle.exe --snapshot all out\              # every state × Dark/Light × en/uk
.\dist\Isle\Isle.exe --snapshot docker-tab docker.png --theme Dark --lang en
```

New views should register their states in the module's `*Snapshots.cs` with demo data. Look at both themes and both
languages before opening a pull request: Ukrainian strings are often longer, so check for clipping.

README screenshots are rendered the same way with `--backdrop 0d1117` and cropped to the island.

## Code guidelines

- Keep it small: reuse what is already in `src/Isle/Core` before adding helpers, and add no NuGet packages without
  discussing it first.
- Every user-facing string goes into both the `en` and `uk` tables of `src/Isle/Core/Strings.Data.cs`; a test checks
  that they match.
- Network access only for features the user turns on. Config files of other tools are read, never written.
  Destructive actions need a confirm.
- Untrusted data (repo names, container labels, server responses, config files) never reaches a shell: pass process
  arguments with `ArgumentList` and open links with `ProcessOps.OpenUrl`.
- No real names, hosts, paths or tokens in code, tests, snapshots or commit messages. Use `example.com` and demo data.
- Add a focused test for non-trivial logic (parsers, decisions, matching). The build treats warnings as errors.

## Commits and pull requests

- Use [Conventional Commits](https://www.conventionalcommits.org/): `feat(docker): …`, `fix(servers): …`, `docs: …`.
- Keep a pull request to one topic. Fill in the template, link the issue, and attach a snapshot for UI changes.
- CI must pass (build, tests, publish and smoke checks). Add a line to the `Unreleased` section of
  [`CHANGELOG.md`](CHANGELOG.md) for anything a user would notice.

## Releases

Maintainers bump `<Version>` in `Directory.Build.props`, move `Unreleased` in the changelog under the new version, and
push a matching `vX.Y.Z` tag. The release workflow builds, tests and publishes both packages.

By contributing you agree that your contributions are licensed under the [MIT License](LICENSE) and that you follow
the [Code of Conduct](CODE_OF_CONDUCT.md).
