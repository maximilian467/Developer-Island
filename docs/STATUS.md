# Status

What was verified, how, and what is still open. Updated 2026-10-01.

## Verified

| Area | How |
|---|---|
| Unit and integration tests | 294 tests pass (`dotnet test`), including a real `git` repository, a Windows Credential Manager round trip, ICS recurrence across daylight saving and the island state machine's transitions. |
| Release build | `tools/build-release.ps1` builds tests, publish, portable zip and installer in a shell whose PATH has no .NET 10 SDK (the script finds it). |
| Every island state | Rendered offscreen with `--demo --snapshot` (35 renders) and compared with the previous build: compact, peek, activities, notch, each expanded module, empty and error states, each featured module, favorite rotation, every Settings page. |
| Claude plan bridge | `--claude-statusline` fed with a sample in the documented format: exit 0, no output, only percentages and reset times stored (no path, model, cost or session). |
| Hardware detection | `--system-report` on a Lenovo laptop without admin rights: CPU, memory, Intel GPU load, battery and two ACPI thermal zones detected; no CPU temperature, no fans, so those are hidden. |
| Auto-hide | Earlier iteration: Chrome maximized in front retracted the island live; foreground changes are now debounced. |
| Performance | See [PERFORMANCE.md](PERFORMANCE.md). |

## Not verified on real hardware or accounts

- Claude plan usage end to end with a Claude subscription: the status line format comes from Claude Code's built-in documentation and the bridge is tested with a sample, but no live session was connected during development (connecting edits the user's Claude Code settings).
- GitHub with the GitHub CLI signed in (only the JSON parsing is tested; the CLI is not installed on the development machine).
- A live calendar link (parsing and storage are tested with samples).
- GPU temperatures, storage temperatures and fans (the development machine exposes none without admin rights).
- Hover and click timing by hand: input was never simulated; behavior is covered by state machine tests.

## Known limitations

- Manual fan control is not offered: no supported interface guarantees that fans return to firmware control if the app crashes or is killed. Monitoring only.
- CPU package temperatures need LibreHardwareMonitor's driver and administrator rights; without them the CPU tile shows its load only.
- ACPI thermal zones are real sensors, but Windows does not say what they measure, so they are labelled "Thermal zone" and not attributed to the CPU.
- Plan usage updates when Claude Code sends a status line update (after replies), not on its own; after a window resets, the island shows no percentage until the next update.
- When Claude Code already has a status line, Developer Island does not replace it; plan usage then needs a manual pipe into `DeveloperIsland.exe --claude-statusline`.
- One placement is saved, not a layout per display. ARM64, Windows 10 and clean-machine installation still need independent validation.
