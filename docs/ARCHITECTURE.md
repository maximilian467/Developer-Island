# Architecture

Developer Island is two projects plus tests:

| Project | Target | Contents |
|---|---|---|
| `DeveloperIsland.Core` | `net10.0` | Everything that does not need Windows UI: models, parsers, providers, pricing, SQLite storage, settings, placement math, focus timer, island state machine, module logic (Git, GitHub, calendar, tasks, system, plan usage, favorites), demo data. Fully unit-tested. |
| `DeveloperIsland` | `net10.0-windows10.0.22621.0`, WinUI 3 | The app: composition root, view models, views, motion, and platform integration (windowing, tray, autostart, media sessions, monitors, hardware sensors, hotkey). |
| `DeveloperIsland.Tests` | `net10.0`, xUnit v3 | 294 tests against Core, including a real `git` repository and a Windows Credential Manager round trip. |

## Data flow

```
 local logs / SMTC / timer                   (background threads)
          │
   Provider  ──────────►  Snapshot (immutable record)
          │                      │
   ProviderHost (isolation)      │   marshalled to the UI thread by AppHost
          │                      ▼
   UsageHistoryService ──►  ViewModel (display strings, flags)
   (SQLite)                      │
                                 ▼
                           Views (x:Bind only, no parsing)
```

- **Providers never touch UI.** They publish immutable snapshots (`AiUsageSnapshot`, `MediaSnapshot`, `FocusSnapshot`).
- **View models format.** Numbers, currency, durations and dates are formatted via `DisplayFormat` (locale-aware, hyphen-only).
- **Views bind.** XAML uses compiled `x:Bind`; code-behind only handles input and motion.

## Providers

`LogFileUsageProvider` is the shared base of the Claude Code and Codex providers:

1. **Initial scan** on a background thread. Each file has a persisted cursor (`file_cursors`: length, write time, byte offset, parser state). Unchanged files are skipped and changed files are read from their offset, so a restart re-reads only what was appended. History is backfilled for 190 days.
2. **FileSystemWatcher**, debounced by 0.9 s, drives incremental reads. A watcher buffer overflow schedules one rescan. There is no polling.
3. **Two timers only.** One fires at local midnight (day rollover); the other clears the "active" flag once a tool has been quiet for 3 minutes. Both are one-shot.
4. **If the tool is not installed**, a watcher on the parent folder waits for its folder to appear.

`JsonlTailReader` reads complete lines only. A partially written trailing line stays for the next read.

| Source | Key | Notes |
|---|---|---|
| Claude Code transcript line | `message.id` + `requestId` | Claude Code writes one line per content block and repeats the usage, so events are de-duplicated. Only `usage`, `model`, IDs, timestamp and `cwd` are read. |
| Codex `token_usage_record` | `response_id` | Cached input is split out of input. |
| Codex `token_count` (older builds) | session + cumulative total | Repeated totals are skipped. `rate_limits.primary` provides the 5-hour window usage. |

`ProviderHost` owns the providers. An exception from a provider's start, dispose or snapshot becomes an `Error` snapshot plus a log entry, and never reaches the app. An exception from a UI handler is logged and swallowed.

## Storage

SQLite (`Microsoft.Data.Sqlite`) at `%LOCALAPPDATA%\DeveloperIsland\usage.db`, WAL mode, versioned through `PRAGMA user_version` with append-only migrations.

| Table | Contents |
|---|---|
| `usage_events` | Provider, key, timestamp, local day, model, token counts, session, project. `INSERT OR IGNORE` de-duplicates. Pruned after 400 days. |
| `daily_usage` | Per day and provider: tokens, API value, partial flag, sessions. Kept indefinitely. |
| `focus_sessions` | Start, end, day, planned and focused seconds, completed. |
| `file_cursors` | Read positions and parser state for incremental parsing. |
| `plan_usage_peaks` | Migration 2. The highest plan usage actually reported per day, provider and window. Never computed or backfilled. |

`UsageHistoryService` keeps running accumulators for the three most recent days. A write adds only the newly inserted events instead of re-reading the day. A randomized test checks that incremental totals equal a full recompute.

Settings are JSON (`settings.json`, source-generated serializer) and are written atomically: to a temp file, then renamed. A corrupt file is kept as `settings.corrupt.json` and defaults are used.

## Pricing

`ModelPricingService` holds public list prices in USD per million tokens (input, output, cache read, 5-minute and 1-hour cache writes) and converts them to EUR. The rate defaults to 0.86 and is overridable.

- **Name matching.** A price matches an exact model ID or a dated snapshot of it (`claude-sonnet-4-20250514`). Newer models (`claude-opus-5-7`, `gpt-5.2`) stay unpriced instead of borrowing a guess.
- **Partial pricing.** Unpriced tokens mark the value as partial.
- **Overrides.** `pricing.json` next to the settings can override prices and the rate.

## The island window

The hard parts of a Dynamic-Island-style window on Windows, and how they are solved:

| Problem | Solution |
|---|---|
| True per-pixel transparency in WinUI 3 | A custom `SystemBackdrop` that sets a fully transparent composition brush, plus the DWM sheet-of-glass setup (extend frame and an empty-region blur-behind). |
| Smooth morph without window-resize jank | The window has a **fixed size** (the largest capsule plus a 40 DIP margin). Only a composition shape animates. `IslandMorph` drives the rounded-rectangle geometry, the content clip and the shadow from one animated property set, using spring animations that can be retargeted mid-flight. |
| Transparent areas must not block clicks | `SetWindowRgn` follows the capsule. During a morph the region covers both shapes, and it shrinks when the springs settle. Outside the region, clicks reach the windows below. |
| Windows 11 border lines | The region stays 4 DIP inside the window edge. The active-window border is also suppressed on every activation. |
| Shadow | `LayerVisual`/`DropShadow` renders opaque on a transparent window, so a pre-rendered nine-grid texture (`Assets/island-shadow.png`) is stretched instead. It needs no offscreen pass per frame. |
| Focus stealing | `WS_EX_NOACTIVATE` means hover, click and drag never take focus. Expanding activates the window explicitly, and Esc returns focus to the previous window. |
| DPI and multi-monitor | Placement math is pure (`IslandPlacement`) and works in physical pixels per monitor scale. After load and after any DPI change the window is placed again, because Windows rescales windows that move between monitors. |
| Fullscreen apps | `FullscreenWatcher` combines a foreground WinEvent hook with the shell's `ABN_FULLSCREENAPP` notification. It ignores maximized windows, captioned windows and our own windows. |

`IslandStateMachine` (Core, tested) decides the mode. Its inputs are named (`IslandInput`: PointerEntered, PointerExited, HoverDelayElapsed, Clicked, ClickedOutside, ActiveAppChanged, ModuleEvent, DragStarted, DragEnded) and documented in a transition table in the source.

- **Compact** at rest, or **Retracted** (the notch) or **Hidden** while an auto-hide app is in front.
- **Activity** (medium) on hover after a 280 ms dwell (450 ms on the notch), or for an event (default 4.5 s).
- **Expanded** on click or the global shortcut.
- **Hidden** from the tray or for a fullscreen app.
- **Dragging** is a flag of the machine: timers, events, hover and clicks wait until the drag ends.

Race safety:

- Every timer carries a generation number; a timer overtaken by a newer input never acts.
- When a timer fires, `PointerProbe` reports where the pointer really is, so a lost pointer-exit cannot keep an activity or peek open (an open peek re-checks every second).
- `HoverTracker` judges hover against the visible capsule and only counts movement as entering. A click counts only after the pointer moved onto the capsule, so a capsule that grows under a resting mouse cannot turn the next click into an expansion.
- While expanded, `OutsideClickWatcher` (a low-level mouse hook installed only in that state) closes the island on any press outside it, even if Windows refused it focus.

## Modules

Every module follows **Provider, then State, then ViewModel, then View**, with one panel per module and the shared states of `ModuleStatus` (disabled, loading, ready, empty, unavailable, error). `ModuleHost` owns the services added after V1 and applies settings: a disabled module's service runs no watchers, timers or processes.

| Module | Source | Cadence |
|---|---|---|
| Git | `git` CLI for repositories added in Settings or seen in AI sessions (walks up from a session folder only) | Watches `.git` metadata; reads only while the panel is open, else on first sight; a branch switch is read from `.git/HEAD` without starting git |
| GitHub | `gh api` (read-only, the user's own sign-in) | Every 5 min while enabled, on open when older than 1 min |
| Calendar | ICS links or files (`ICalendarSource`) | Every 15 min |
| Tasks | `tasks.json` | On change |
| System | `SystemMetricsReader` (CPU, memory, battery), `HardwareSensors` (LibreHardwareMonitor: GPU load every second, temperatures and fans every 5 s; ACPI thermal zones via PDH) | 1 Hz, 60-sample history; the UI hears about samples only while the panel is open or System is featured |

`HardwareCapabilities` is derived from the samples themselves, so a component that does not exist simply never appears. Manual fan control is gated by `FanControlCapability`, which requires a crash-safe fallback to firmware control; no supported interface offers that, so the island monitors only. `DeveloperIsland.exe --system-report` prints what a device exposes.

## Compact island and favorites

`CompactSelector` picks the featured module: one favorite is shown, several rotate every 7 s (skipping those with nothing to say), otherwise the module opened last (`ui-state.json`), otherwise the classic summary. The rotation timer runs only while the compact capsule is on screen. Time-critical chips (focus, next event, system alert, failing CI) stay unless the featured module already shows them.

## Plan usage

Claude plan usage comes from Claude Code's documented status line input (`rate_limits.five_hour` and `seven_day`: `used_percentage`, `resets_at`). `ClaudeStatusLineSetup` adds Developer Island as the `statusLine` command in Claude Code's `settings.json` on request (backup first, never replacing a status line the user already has). `DeveloperIsland.exe --claude-statusline` runs before any UI, keeps only those numbers in `claude-plan.json`, prints nothing and always exits 0. `PlanUsageService` follows that file with a watcher. Codex's 5-hour and weekly windows come from its own rollout records. A window that has reset no longer shows its percentage.

## Auto-hide in apps

`AutoHideApps` (settings) lists apps with their own switches; the older `SmartHideProcesses` list is kept in sync for compatibility. `ForegroundWatcher` (WinEvent hooks, no polling) reports the foreground app; `SmartHidePolicy` decides Compact, Retracted or Hidden from the rules, the anchor (top center only) and, optionally, whether the window is maximized. Foreground bursts settle for 150 ms.

## Secrets

Private calendar links are kept in Windows Credential Manager (`CredentialManagerSecretStore`, generic credentials for this user). `settings.json` holds only an id and the provider name; earlier plain-text links are migrated on start. Demo mode uses an in-memory store.

## Composition root

`AppHost` wires everything and is the only place that crosses threads (`DispatcherQueue.TryEnqueue`); `ModuleHost` does the same for the newer modules. A single instance is enforced with a named mutex; a second launch signals a named event and exits. `--demo` swaps in an in-memory database, demo providers and a demo media queue, and uses its own settings file and instance name.
