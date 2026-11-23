# Security policy

## Supported versions

Only the latest release gets security fixes. Please update before reporting.

## Reporting a vulnerability

Please report privately, not in a public issue:

- GitHub private vulnerability reporting: the repository's **Security** tab → **Report a vulnerability**.
- Owner: GitHub offers private vulnerability reporting for public repositories only. Turn it on (Settings → Code security) when the repository is made public, or add a private contact here.

Include the Isle version, what you did, what happened and, if you can, a proof of concept. You should get an answer
within a week.

## Scope

Isle is a local Windows desktop app that runs as the signed-in user. In scope:

- **The app itself** (`Isle.exe`): anything that lets content it reads or shows make it run code, open something
  unexpected or leak data.
- **The named-pipe hook endpoint** used by `Isle.Hook.exe`: other users or remote machines reaching it, or a crafted
  hook message doing more than showing a card.
- **Agent hook installation**: the entries written into the Claude Code, Codex and Gemini CLI configs, their backups and removal.
- **Reading user config files**: ssh, kube, AWS, Azure and gcloud configs, agent session logs, log files and settings,
  including hostile or oversized content.
- **Opt-in network features**: the GitLab API, the Servers watch list (HTTP, status page and TLS checks) and
  GitHub data through the `gh` CLI.

Out of scope: attacks that already need the same Windows account (such a process can change Isle's files and the
agents' configs directly), bugs in the agents, `git`, `gh`, Docker or Windows themselves, and denial of service of
Isle by the user's own configuration.
