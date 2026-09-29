# Architecture

Developer Island is two projects plus tests:

| Project | Target | Contents |
|---|---|---|
| `DeveloperIsland.Core` | `net10.0` | Everything that does not need Windows UI: models, parsers, providers, pricing, SQLite storage, settings, placement math, focus timer, island state machine, demo data. Fully unit-tested. |
| `DeveloperIsland` | `net10.0-windows10.0.22621.0`, WinUI 3 | The app: composition root, view models, views, motion, and platform integration (windowing, tray, autostart, media sessions, monitors). |
| `DeveloperIsland.Tests` | `net10.0`, xUnit v3 | 132 tests against Core. |

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

`IslandStateMachine` (Core, tested) decides the mode:

- **Compact** at rest.
- **Activity** on hover after a 280 ms dwell, or for an event (default 4.5 s, and never closes while hovered).
- **Expanded** on click.
- **Hidden** from the tray or for a fullscreen app.

## Composition root

`AppHost` wires everything and is the only place that crosses threads (`DispatcherQueue.TryEnqueue`). A single instance is enforced with a named mutex; a second launch signals a named event and exits. `--demo` swaps in an in-memory database, demo providers and a demo media queue, and uses its own settings file and instance name.
