# Changelog

All notable changes to Developer Island are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- **Quit Developer Island** in Settings, General: ends the whole app (island, tray icon, background work) after a confirmation. Closing Settings or hiding the island keeps it running.

### Fixed

- The island no longer disappears while switching apps: the Alt+Tab switcher and Task View were taken for fullscreen apps and hid it on every switch.
- The island stays above other apps' always-on-top windows: it takes the top again whenever another app comes to the front, without taking focus.
- Alt+F4 on the open island closes it like Esc instead of removing it until the next start.
- The usage graph reacts to hover, click and tap again; pointer input went to the scroll area behind it.
- The favorite star sits in the same place on every tab, Usage (Claude Code) included, and is no longer pushed aside or cut off by long names. On Usage it stands for the enabled providers.
- Git and GitHub CLI calls still running when the app quits end with it.

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
