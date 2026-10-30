# Architecture

Developer Island is two projects plus tests:

| Project | Target | Contents |
|---|---|---|
| `DeveloperIsland.Core` | `net10.0` | Everything that does not need Windows UI: models, parsers, providers, pricing, SQLite storage, settings, placement math, focus timer, island state machine, module logic (Git, GitHub, calendar, tasks, system, plan usage, favorites), the update check and its verification, demo data. Fully unit-tested. |
| `DeveloperIsland` | `net10.0-windows10.0.22621.0`, WinUI 3 | The app: composition root, view models, views, motion, and platform integration (windowing, tray, autostart, media sessions, monitors, hardware sensors, hotkey). |
| `DeveloperIsland.Tests` | `net10.0`, xUnit v3 | 559 tests against Core, including a real `git` repository and a Windows Credential Manager round trip. |

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

Which figure the island shows is the `TokenMetric` setting (`Fresh` or `AllProcessed`). `TokenMetrics` in Core turns `TokenCounts` and `UsageDay` into the chosen value, its label and the graph levels, so the ViewModels only bind; the breakdown always shows every category.

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
| Staying on top | The window is always-on-top, but Windows orders that band by recency: another app's always-on-top window that comes to the front later covers it. On every foreground switch (`ForegroundWatcher.Switched`) the island takes the top of the band again with `SetWindowPos(HWND_TOPMOST, SWP_NOACTIVATE)`, which never activates it. |
| Closing | The island lives as long as the app: a close request (Alt+F4 while it is open) is cancelled and closes the island like Esc. Only Quit (tray or Settings) ends the app; `git` and `gh` run in a kill-on-close job object (`HelperProcessJob`), so a call still running ends with the app. |
| DPI and multi-monitor | Placement math is pure (`IslandPlacement`, `IslandDrag`) and works in physical pixels. The gap and offset use the monitor's scale, the window's content the DPI Windows renders it at; the two differ right after a move to another monitor. On `WM_DPICHANGED`, WinUI would apply Windows' suggestion (the old rectangle scaled), so `DpiChangeHook` subclasses the window and replaces it with the island's own rectangle for the new scale. A drag keeps the grabbed point in DIPs and converts it with the window's current DPI on every move; placement waits until the drop, and a short timer carries the drag on if the DPI change takes the pointer capture. The island's monitor (fullscreen, auto-hide, arm zone) is the one under the capsule's anchor, never `MonitorFromWindow` on the much larger window. The monitor is saved by its device interface path, which survives Windows renumbering the `\\.\DISPLAYn` names (kept as a fallback for older settings); display change bursts are placed once they settle (400 ms). |
| Fullscreen apps | `FullscreenWatcher` combines a foreground WinEvent hook with the shell's `ABN_FULLSCREENAPP` notification. `FullscreenPolicy` (Core, tested) decides: maximized windows, captioned windows, our own windows and the shell's own surfaces never count. The Alt+Tab switcher and Task View cover the monitor without a caption; counted as fullscreen apps, they hid the island on every app switch. |

`IslandStateMachine` (Core, tested) decides the mode. Its inputs are named (`IslandInput`: PointerEntered, PointerExited, HoverDelayElapsed, Clicked, ClickedOutside, ActiveAppChanged, ModuleEvent, DragStarted, DragEnded, FileDropCompleted, DropSucceeded, DropFailed) and documented in a transition table in the source.

- **Compact** at rest, or **Retracted** (the notch) or **Hidden** while an auto-hide app is in front.
- **Activity** (medium) on hover after a 280 ms dwell (450 ms on the notch), or for an event (default 4.5 s).
- **Expanded** on click or the global shortcut.
- **Hidden** from the tray or for a fullscreen app.
- **Drop** (Home Cloud) with `DropPhase` Hover, Uploading, Succeeded, Failed. It is reachable from every mode (`SetDropEnabled` gates it) and returns to the resting mode, also Hidden. Inputs: `FileDropCompleted`, `DropSucceeded`, `DropFailed`. A running upload holds Drop; success lingers 1.5 s, failure 4 s. While in Drop, hover, clicks, module events and island drags do nothing. A `DropProbe` re-checks every 400 ms that the drag is still over the zone.
- **Dragging** is a flag of the machine: timers, events, hover and clicks wait until the drag ends.

Race safety:

- Every timer carries a generation number; a timer overtaken by a newer input never acts.
- When a timer fires, `PointerProbe` reports where the pointer really is, so a lost pointer-exit cannot keep an activity or peek open (an open peek re-checks every second).
- `HoverTracker` judges hover against the visible capsule and only counts movement as entering. A click counts only after the pointer moved onto the capsule, so a capsule that grows under a resting mouse cannot turn the next click into an expansion.
- While expanded, `OutsideClickWatcher` (a low-level mouse hook installed only in that state) closes the island on any press outside it, even if Windows refused it focus.

## Home Cloud drop zone

`HomeCloudUploader` and `HomeCloudReachability` (Core, tested) copy through a `.part` file with an atomic rename and a sequential queue; `HomeCloudDropPolicy`, `HomeCloudFeedback` and `DragArmDetector` hold the testable rules. In the app, `IslandWindow.Drop` and `DropView` handle `DragEnter`/`Drop` (`AllowDrop` and handlers exist only while Home Cloud is on) and run the upload on `Task.Run`.

**DragArming.** A retracted or hidden island cannot be hit by a drag, so `DragArming` (Platform/Windowing) installs a `WH_MOUSE_LL` hook on its own thread with its own message loop, so the hook never waits on the UI thread. It is installed only in Retracted or Hidden, only for an island on the top edge, and removed otherwise. The arm zone is 300 DIP wide around the drop zone's centre and 60 DIP from the monitor's top edge, converted per monitor DPI. `DragArmDetector` arms when the button is down, the cursor moved past `SM_CXDRAG`/`SM_CYDRAG` and it is inside the zone; a press made inside the zone never arms it, and neither does a hidden cursor (game protection). Armed, a hidden island is shown without activation and fully transparent, and only the window region grows to the zone. Release or leaving restores window, region and mode. The hook reads only button state and position.

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

## Awake

`AwakeController` (Core, tested with fakes for `IPowerControl` and `IAwakeBackstop`) runs a timed or indefinite session. In the app, `WindowsPowerControl` holds a power request (`PowerCreateRequest`, system- and execution-required; Windows drops it with the process) and writes `LIDACTION` in the active scheme through `powrprof` (`PowerWrite{AC,DC}ValueIndex`, then `PowerSetActiveScheme`), without admin rights or a `powercfg` process.

The lid change outlives a crash, so it follows a journal-first rule:

| Step | Order |
|---|---|
| Start | read the original values, write `awake-session.json`, then write "do nothing", then the power request, then schedule the backstop. Without a journal, the lid is not changed. |
| End (timer, End, battery floor, module off, Quit) | release the request, put back each value that still reads "do nothing" (`AwakeRules.Restoration`), then delete the journal and the backstop. A failed restore keeps the journal as an ended session. |
| App start | `AwakeRules.OnStartup`: an indefinite or unexpired session resumes; an ended one is restored. |
| Backstop | `ScheduledTaskBackstop` registers a one-time task (UTF-16 task XML through `schtasks /Create /XML`, `StartWhenAvailable`, no battery limits, expires and deletes itself a week later). `DeveloperIsland.exe --awake-restore` exits at once while the island runs (its instance mutex exists), otherwise restores an ended session. |

Battery level, power source and lid state come from `RegisterPowerSettingNotification` on the host window, only while a session runs; lid events are logged, not acted on.

## State, content and global signals

Four things are kept apart:

| Concern | Owner | Decides |
|---|---|---|
| Display state | `IslandStateMachine` | Hidden, Retracted, Compact, Activity, Expanded (plus the dragging flag) |
| Content | `CompactSelector`, `IslandContent` | Which module the capsule, the peek and the opened island show |
| Foreground app | `ForegroundWatcher`, `SmartHidePolicy` | The resting mode (Compact, Retracted or Hidden) |
| Privacy | `PrivacyWatcher`, `PrivacyIndicator` | Whether the camera and microphone marks show |

The display priority is Dragging > Auto-hide or hidden > explicitly expanded > hover peek > compact. An auto-hide app in front retracts the island at once from any state, even when expanded; a drag finishes first. Favorites and the last active module only choose content: `IslandContent.ChoosePeek` and `ChooseTabOnOpen` show the featured module (favorite, else the one opened last) on hover and on open, never a fixed default. The privacy marks are visible in every state except a hide the user asked for (tray, fullscreen): icons in compact, activity and expanded, 4 DIP dots in the notch (whose size never changes); with a device in use, `SmartHidePolicy` turns "hide" into the notch.

## Compact island and favorites

`CompactSelector` picks the featured module: one favorite is shown, several rotate every 7 s (skipping those with nothing to say), otherwise the module opened last (`ui-state.json`), otherwise the classic summary. The rotation timer runs only while the compact capsule is on screen. Time-critical chips (focus, next event, system alert, failing CI) stay unless the featured module already shows them.

Ticking values sit in a `StableText`: an invisible copy of the widest form (`CompactWidth.Reserve`: "CPU 9%" reserves "CPU 000%", "376k" and "1.12M" reserve "000.00M") holds the slot and tabular figures make every digit equally wide, so the capsule changes width only when its content changes kind.

## Camera and microphone

Windows records each app's device use under `HKCU\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\{microphone|webcam}` (packaged apps directly, desktop apps under `NonPackaged`): a `LastUsedTimeStart` without a `LastUsedTimeStop` means in use. `PrivacyWatcher` arms `RegNotifyChangeKeyValue` on both keys (subtree, names and values), reads them once, and waits; a change re-arms and re-reads after 120 ms. One background thread, no polling, no app names kept or logged. `ConsentStore` and `PrivacyIndicator` (Core) hold the testable rules.

Since the Windows 11 updates of October 2026 (KB5127647 and following, seen on 25H2 build 26200), that record is no longer written on some systems: the microphone mark stayed off. `PrivacyWatcher` therefore runs a second thread that reads the audio sessions of every active recording device once a second through NAudio (WASAPI `IAudioSessionManager2`, on an MTA thread): a session in `AudioSessionStateActive` that is not the island's own means the microphone is in use (`CaptureSessions`, Core, tested). The microphone shows when either source says so; the camera still comes from the consent store only. Both threads report through one lock, so their states never arrive out of order. `--system-report` prints both microphone sources and the WhisperFlow link.

## Plan usage

Claude plan usage comes from Claude Code's documented status line input (`rate_limits.five_hour` and `seven_day`: `used_percentage`, `resets_at`). `ClaudeStatusLineSetup` adds Developer Island as the `statusLine` command in Claude Code's `settings.json` on request (backup first, never replacing a status line the user already has). `DeveloperIsland.exe --claude-statusline` runs before any UI, keeps only those numbers in `claude-plan.json`, prints nothing and always exits 0. `PlanUsageService` follows that file with a watcher. Codex's 5-hour and weekly windows come from its own rollout records. A window that has reset no longer shows its percentage. Claude Code runs status lines only in its terminal interface, so `PlanStatus` separates the link (connected or not) from the data (waiting, receiving, stale after an hour), and Settings and the Usage panel say which one applies.

## WhisperFlow dictation

The local WhisperFlow tool writes its state to `%LOCALAPPDATA%\WhisperFlow\status.json` (`{"version":1,"state":"recording","at":"<ISO time>","pid":1234}`, replaced in one move). `WhisperFlowService` (Core, tested) follows the file with a watcher only while `WhisperFlowEnabled` is on. `WhisperFlowStatusFile.Effective` decides what shows: recording and transcribing while WhisperFlow's process runs (checked every 5 s, so a killed WhisperFlow clears), done, empty and error for 3 s after they were written, anything else (loading, ready, stopped, unknown states) nothing. An unreadable file keeps the last state. The phase joins the camera and microphone marks in `PrivacyViewModel`: it counts as active for Smart Auto-Hide, `PrivacyIndicators` adds the label (and a `ProgressRing` while transcribing), `PrivacyDots` a coloured dot.

## Auto-hide in apps

`AutoHideApps` (settings) lists apps with their own switches; the older `SmartHideProcesses` list is kept in sync for compatibility. `ForegroundWatcher` (WinEvent hooks, no polling) reports the foreground app; `SmartHidePolicy` decides Compact, Retracted or Hidden from the rules, the anchor (top center only) and, optionally, whether the window is maximized. `ForegroundWatcher` judges the root owner of the window actually in front (`GetForegroundWindow` when an event is processed), so a queued event from a fast switch or a browser popup cannot decide. `AutoHideCoordinator` (Core, tested) numbers every change: stepping aside applies at once, a return to the capsule waits 80 ms and is verified against the real foreground, and only the newest version may apply. A 750 ms check, running only while auto-hide can apply, re-reads the foreground and catches an event Windows never delivered.

## Updates

The optional update check and one-click install. The services live in Core (`Updates/`, tested on fake time with a fake HTTP handler); the app adds the dialog, the tray item and the setup launch.

| Piece | Where | Does |
|---|---|---|
| `UpdateService` | Core | Off until "Check for updates" is on. One anonymous `GET .../releases/latest` (plain `HttpClient`, `User-Agent: DeveloperIsland/<version>`, no token): 30 s after the start, then a day after the last answered check; an unanswered one is retried after an hour (`DueIn`). "Check now" asks at once. `update-state.json` (`UpdateStateStore`) keeps the last check and the release found, so a restart within the day neither asks again nor loses the notice. Failures are logged by error type only. |
| `ReleaseJson`, `ReleaseVersion` | Core | Parse the release: drafts, prereleases and tags that are no version are skipped; the setup, `SHA256SUMS-<version>.txt` and its `.sig` are picked by name, and only from `https://github.com/maximilian467/Developer-Island/releases/`. Semantic version precedence. |
| `UpdateDownloader`, `UpdateSignature`, `ChecksumFile` | Core | Into `%LOCALAPPDATA%\DeveloperIsland\updates`: the checksums and their signature first, verified (ECDSA P-256, SHA-256, DER) against the public key embedded from `Updates/release-signing-public-key.pem`; then the setup, hashed while it streams to a `.part` file that becomes the setup only if it matches the signed SHA-256. A build without the key, an unsigned release, a bad signature or a mismatch installs nothing. |
| `UpdateWindow`, `SetupLauncher` | App | The dialog (question; Install now, Release notes, Later, Skip this version; progress and failures). The setup runs with `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RESTARTAPP` and the app quits; only a copy the setup installed (its uninstall entry's `InstallLocation`) updates in place, a portable one is sent to the release page. |
| Installer | `installer/DeveloperIsland.iss` | `UsePreviousTasks` keeps the autostart choice; a `[Run]` entry with `skipifnotsilent` and the `RestartAppRequested` check starts the new version after a `/RESTARTAPP` run. |

The island announces a release once per run as a live activity (`ActivityKind.UpdateAvailable`) and asks the one-time question the same way (`UpdateQuestion`); a click on such a notice opens the dialog instead of expanding the island. A skipped version (`SkippedUpdateVersion`) is no longer announced, in the island or the tray. Demo mode makes no request; the snapshot run renders the notices and the dialog with a pretend release.

## Secrets

Private calendar links are kept in Windows Credential Manager (`CredentialManagerSecretStore`, generic credentials for this user). `settings.json` holds only an id and the provider name; earlier plain-text links are migrated on start. Demo mode uses an in-memory store.

## Composition root

`AppHost` wires everything and is the only place that crosses threads (`DispatcherQueue.TryEnqueue`); `ModuleHost` does the same for the newer modules. A single instance is enforced with a named mutex; a second launch signals a named event and exits. `--demo` swaps in an in-memory database, demo providers and a demo media queue, and uses its own settings file and instance name.
