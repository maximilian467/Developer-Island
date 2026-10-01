# Security

## Supported versions

| Version | Supported |
|---|---|
| 0.1.x (latest release) | Yes |
| Older builds | No, please update |

## Reporting a vulnerability

Please do not open a public issue for security problems. Use GitHub's [private vulnerability reporting](https://github.com/maximilian467/Developer-Island/security/advisories/new) instead. As this is an early-stage solo project, responses are best effort.

Never include secrets in a report or an issue: no calendar links, tokens, prompts, logs you have not read through, or private paths. If a secret is needed to reproduce a problem, describe it instead.

## Security model

Developer Island is a local, single-user desktop app.

- It runs without administrator rights and opens no network ports.
- It makes network requests only for what you set up: the calendar links you add (HTTPS or webcal), and the GitHub CLI's read-only requests when the GitHub module is on. Links in the panels open in your browser.
- It reads local files of other tools (Claude Code transcripts, Codex rollout files) and extracts only usage metadata; it does not upload them.
- Private calendar links are stored in Windows Credential Manager for your user account, never in settings files or logs.
- "Claude plan usage" adds Developer Island as Claude Code's status line command in `~/.claude/settings.json` only when you click Connect, with a backup first, and never replaces a status line you already have. Disconnect removes it.
- The GitHub module uses the `gh` CLI and its sign-in; Developer Island never sees or stores a GitHub token.
- Hardware sensors are read through LibreHardwareMonitorLib in read-only mode; fans are never controlled.

Reports about any of these boundaries (for example data leaving the machine, a secret appearing in logs, or settings of other tools being changed unexpectedly) are especially welcome.
