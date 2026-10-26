# Changelog

All notable changes to Developer Island are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- **Token metric** in Settings, Modules: show **fresh** tokens (input, cache writes and output; the default, as before) or **all processed** tokens including cache reads in the capsule, peek, tooltips, usage graph and day inspector, for Claude Code and Codex alike. Every figure is labelled ("fresh tokens", "tokens incl. cache reads"); the breakdown still lists every category, and each API response still counts once.
- Choose the active Windows playback output from the Music panel.
- **WSL support** ([#9](https://github.com/maximilian467/Developer-Island/issues/9)). Settings, Modules, **Include WSL** (off by default) lists the installed distros (read from the registry, so nothing starts) and adds, for each one left on:
  - Claude Code and Codex usage from `~/.claude` and `~/.codex` inside the distro, counted into the same totals. The WSL share sends no reliable change notifications, so these folders are polled (recent files every 5 s, the whole folder every minute); every response is still counted once.
  - Their project folders: Linux paths (`/home/you/proj`) become `\\wsl.localhost\<distro>\home\you\proj` for the Git module; `/mnt/c/...` becomes `C:\...` again.
  - Git for repositories inside WSL runs the distro's own git through `wsl.exe` (no "dubious ownership", no slow share), re-read every 10 s while the Git panel is open. `\\wsl$\...` and `\\wsl.localhost\...` repositories can also be added in Settings, Git repositories.
  - Only running distros are read: Developer Island never starts one. A stopped distro, a distro without git and a PC without WSL each get their own message in the Git panel.

### Fixed

- The first usage of a day kept no working folder until the next reply, so the Git module could miss the session's repository.

## [0.3.0] - 2026-10-15

### Added

- **Awake** module (a coffee cup): keeps the PC awake for 1, 4 or 8 hours, a custom length or Always, also with the lid closed, so coding agents keep working. During a session the lid-close action is "Do nothing"; it is journaled first and put back at the end, at the next start or by a one-time scheduled task if the app is gone. On battery, a session ends below 10 % (adjustable). Countdown in the capsule, Stay Awake in the tray menu.
- With nine tabs, the tab buttons of the open island are 28 instead of 32 DIP wide, so the section name still fits.

## [0.2.0] - 2026-10-13

### Added

- **Home Cloud drop zone:** drop files or folders onto the island to copy them to your own server. It is off until you set a target folder. It works in every state, also while the island is a notch or hidden: dragging toward the top edge arms the zone. Progress, success and error feedback; uploads go through a `.part` file and a rename. File names and paths are never logged. Settings, Home Cloud sets the target and the side, and turns it all off.
- `--homecloud-upload=<path>` sends a file through the Home Cloud pipeline for testing.
- **Quit Developer Island** in Settings, General: ends the whole app (island, tray icon, background work) after a confirmation. Closing Settings or hiding the island keeps it running.

### Fixed

- The island no longer disappears while switching apps: the Alt+Tab switcher and Task View were taken for fullscreen apps and hid it on every switch.
- The island stays above other apps' always-on-top windows: it takes the top again whenever another app comes to the front, without taking focus.
- Alt+F4 on the open island closes it like Esc instead of removing it until the next start.
- The usage graph reacts to hover, click and tap again; pointer input went to the scroll area behind it.
- The favorite star sits in the same place on every tab, Usage (Claude Code) included, and is no longer pushed aside or cut off by long names. On Usage it stands for the enabled providers.
- Git and GitHub CLI calls still running when the app quits end with it.
- The island can be dragged onto a monitor with a different scale. It used to jump back to its old monitor, and the capsule slid away from the cursor; now the point you hold stays under it, and a DPI change cannot end the drag.
- After moving to a monitor with another scale, the island lands at its anchor at once instead of briefly at the wrong size and place.
- The island remembers its monitor by hardware id, so it returns to the right display after docking or reconnecting, when Windows renumbers the displays.
- Fullscreen apps and auto-hide apps count only on the island's own monitor, also when its window reaches into the neighbouring one.
- Connecting or disconnecting displays places the island once the change has settled, and the Monitor list in Settings updates.

### Security

- Git status ignores a repository's own `core.fsmonitor` setting, so a prepared repository in an AI session's folder cannot start a program through the Git module.
- Calendar downloads are capped at 16 MB also when the server sends no size.

## [0.1.0] - 2026-10-01

First public version.

### Added

- **The island:** a native WinUI 3 capsule with compact, live activity and expanded states, spring morphs from its anchor, six anchors or a free position on any monitor, a global shortcut (Ctrl + Alt + Space), tray menu and Start with Windows.
- **Claude Code and Codex usage** from their local logs: fresh and processed tokens by category, sessions, a 26-week usage graph with day tooltips, and an estimated API equivalent at list prices.
- **Plan usage:** Claude's 5-hour and weekly windows through Claude Code's status line (terminal sessions), Codex's windows from its own logs.
- **Music** for any app using Windows media sessions, with artwork, timeline and controls.
- **Focus** timer with presets, restore after restart and daily history.
- **Calendar** from private iCal links (stored in Windows Credential Manager) or `.ics` files.
- **System** monitoring: CPU, memory, GPU and battery with 60-second graphs colored by load, plus the temperatures and fans the device exposes.
- **Git**, **GitHub** (through the GitHub CLI, read-only) and **Tasks** modules.
- **Favorites:** star modules to feature them in the compact island; otherwise the module opened last is shown.
- **Auto-hide in apps:** retracts into a small notch (or hides) while a listed app such as Chrome is in front, deterministic under fast app switching.
- **Camera and microphone indicators** in every state, as small dots in the notch.
- **Demo mode** (`--demo`) with invented data, offscreen snapshots (`--snapshot`) and a hardware report (`--system-report`).
- Per-user installer and portable zip for Windows x64.
