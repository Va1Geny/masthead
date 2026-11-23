## What and why

<!-- One topic per pull request. Link the issue: "Closes #123". -->

## How it was checked

- [ ] `.\scripts\build.ps1` passes (build, tests, publish, smoke checks)
- [ ] New logic has a focused test
- [ ] UI changes: snapshots checked in Dark/Light and en/uk (attach one below)
- [ ] Live run, if behaviour depends on the real window, hover or other apps

## Checklist

- [ ] New user-facing strings are in both `en` and `uk`
- [ ] No new network access, or it is opt-in; destructive actions ask first
- [ ] No real names, hosts, paths or tokens in code, tests, snapshots or commits
- [ ] `CHANGELOG.md` → `Unreleased` updated if users will notice the change
