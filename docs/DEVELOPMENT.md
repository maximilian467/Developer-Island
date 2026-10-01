# Development

Everything builds with the .NET CLI; Visual Studio is optional.

## Requirements

- Windows 10 version 2004 or later (development happens on Windows 11 x64)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Git](https://git-scm.com/download/win)
- For the installer only: [Inno Setup 6](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup`)

## Build, run and test

```powershell
git clone https://github.com/maximilian467/Developer-Island.git
cd Developer-Island

dotnet test tests/DeveloperIsland.Tests                 # restore, build and run the tests
dotnet build src/DeveloperIsland -c Debug               # build the app
.un.cmd -Demo                                         # build and start with demo data
```

`run.cmd` builds and starts the app from source: `.un.cmd` uses your real data, `-Demo` invented data with separate demo settings, `-Release` an optimized build. The scripts find the .NET 10 SDK themselves, even when the `dotnet` on PATH is an older install without it.

The built app is at `src/DeveloperIsland/bin/x64/Debug/net10.0-windows10.0.22621.0/win-x64/DeveloperIsland.exe`. Useful switches:

| Command | What it does |
|---|---|
| `DeveloperIsland.exe --demo` | Invented data only; real usage, tasks and repositories are never read or changed. Preferences are saved separately in `settings.demo.json`. |
| `DeveloperIsland.exe --demo --snapshot=C:	emp\shots --culture=en-US` | Renders every state (compact, peek, activities, notch, each module, empty and error states, privacy marks, Settings) to PNG files offscreen, without touching the desktop, then exits. |
| `DeveloperIsland.exe --system-report` | Prints what the System module can read on this device (GPU, temperatures, fans, camera and microphone state) and exits. |
| `DeveloperIsland.exe --settings=modules` | Opens Settings at a section (`general`, `position`, `modules`, `appearance`, `about`). |

Set `DI_LOG_DEBUG=1` to write debug-level logs to `%LOCALAPPDATA%\DeveloperIsland\logs`.

## Release build

```powershell
powershell -ExecutionPolicy Bypass -File tools/build-release.ps1   # tests, publish, zip, installer
```

See [RELEASING.md](RELEASING.md) for the full release checklist.

## README media

The screenshots and the hero animation come from the app's own offscreen renders:

```powershell
DeveloperIsland.exe --demo --snapshot=C:	emp\shots --culture=en-US
python tools/make-readme-media.py C:	emp\shots          # needs Pillow: pip install pillow
```

To record the live app instead (real spring motion), follow [RECORDING.md](RECORDING.md).

## Project structure

```
src/
  DeveloperIsland.Core/     platform-independent logic, covered by unit tests
    Providers/  Usage/      Claude Code and Codex providers, parsers, aggregation, history
    Pricing/  Storage/      API equivalent; SQLite (events, daily totals, focus, cursors)
    Modules/                module ids, states and order; the featured-module selector
    Plan/                   Claude plan usage: status line input, store, Claude Code settings
    Git/  GitHub/           git status via the git CLI; read-only GitHub via the gh CLI
    Calendar/  Tasks/       ICS parsing and calendar sources; the task list
    SystemInfo/  Focus/     system alerts and sampling; focus timer, history and restore
    Island/  Placement/     state machine, hover tracking, notch policy; monitor math
    Settings/  Demo/        JSON settings; demo data
    Privacy/  Formatting/   camera and microphone rules; display formats and compact width reserves
  DeveloperIsland/          WinUI 3 app
    App/                    composition root (AppHost, ModuleHost), entry point, single instance
    ViewModels/             one view model per module (the UI never parses anything)
    UI/                     island window, compact and activity views, one panel per module, settings
    Platform/               windowing, foreground tracking, hotkey, tray, media, system metrics, camera and microphone
tests/DeveloperIsland.Tests
installer/                  Inno Setup script
tools/                      release build, run script, asset and README media generators
docs/                       architecture, modules, performance, status, design, release and recording guides
```

Data flows one way: **Provider, then State, then ViewModel, then View.** See [ARCHITECTURE.md](ARCHITECTURE.md), [PERFORMANCE.md](PERFORMANCE.md) and [STATUS.md](STATUS.md).

## Tests

The tests cover:

- token accounting (fresh versus processed, cache categories, local-day boundaries and daylight saving)
- usage aggregation, de-duplication, cost calculation and incremental daily totals
- malformed log records, persisted Codex limits, cursor upgrades and limit expiry
- the island state machine (named inputs, lost pointer events, drags, stale timers), hover tracking, click arming and the per-app auto-hide rules
- favorites, the module opened last and compact rotation
- Claude plan usage from the status line input, its store and Claude Code settings changes; Codex weekly windows; measured plan peaks
- hardware capabilities, the 60-second history and the fan-control safety rules
- calendar links in Windows Credential Manager and the migration from plain text
- the focus timer, including restore after restart
- media session choice and track changes
- Git status parsing and a real repository end to end; GitHub CLI output; ICS parsing and recurrence; tasks; system alerts
- module order and settings persistence, including corrupt-file recovery
- monitor placement, provider error isolation, SQLite persistence and display formatting
- foreground auto-hide under fast app switching (latest event wins), camera and microphone indicators, compact width reserves, hardware levels and the usage-graph layout

## Design

The UI follows three design sources, in this order:

1. The Apple design analysis in [`design/apple-design-analysis.md`](design/apple-design-analysis.md).
2. The [Taste Skill](https://github.com/Leonxlnx/taste-skill) (`design-taste-frontend`).
3. The [Vercel Web Interface Guidelines](https://github.com/vercel-labs/web-interface-guidelines).

They are merged into [`design/DESIGN-PRINCIPLES.md`](design/DESIGN-PRINCIPLES.md); the reviews are recorded in [`design/DESIGN-AUDIT.md`](design/DESIGN-AUDIT.md). UI changes are checked against offscreen renders before and after.
