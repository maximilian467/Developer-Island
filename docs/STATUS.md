# Status

What was verified, how, and what is still open. Updated 2026-10-01.

## Verified

| Area | How |
|---|---|
| Unit and integration tests | 356 tests pass (`dotnet test`), including a real `git` repository, a Windows Credential Manager round trip, ICS recurrence across daylight saving and the island state machine's transitions. |
| Release build | `tools/build-release.ps1` builds tests, publish, portable zip and installer in a shell whose PATH has no .NET 10 SDK (the script finds it). |
| Every island state | Rendered offscreen with `--demo --snapshot` (41 renders) and compared with the previous build: compact, peek, activities, notch, each expanded module, the usage-graph tooltip, empty and error states, each featured module, favorite rotation, camera and microphone marks in compact, notch, activity and expanded, every Settings page. |
| Camera and microphone | `--system-report` reads the consent store on this machine (both "not in use" at the time). The in-use rule and the island behavior (marks in every state, notch instead of hide, never opening) are covered by tests. |
| Claude plan bridge | `--claude-statusline` fed with a sample in the documented format: exit 0, no output, only percentages and reset times stored (no path, model, cost or session). |
| Hardware detection | `--system-report` on a Lenovo laptop without admin rights: CPU, memory, Intel GPU load, battery and two ACPI thermal zones detected; no CPU temperature, no fans, so those are hidden. |
| Auto-hide | Earlier iteration: Chrome maximized in front retracted the island live; foreground changes are now debounced. One rule for every module: tests cover Claude, Codex, Music, Focus, System, Calendar and the open island with Chrome and VS Code in front. |
| Performance | See [PERFORMANCE.md](PERFORMANCE.md). |

## Not verified on real hardware or accounts

- Claude plan usage end to end with a Claude subscription in a terminal session. On the development machine the bridge is connected, but Claude Code was used through the VS Code extension, which does not run status lines, so no plan data arrived (Settings now says so instead of only "Connected").
- Camera and microphone marks with a device actually in use (requires starting a call or recording; the change notification and the in-use rule are implemented and tested separately).
- GitHub with the GitHub CLI signed in (only the JSON parsing is tested; the CLI is not installed on the development machine).
- A live calendar link (parsing and storage are tested with samples).
- GPU temperatures, storage temperatures and fans (the development machine exposes none without admin rights).
- Fan RPM is not available on the development machine (Lenovo 21KH): LibreHardwareMonitor finds no fan or controller sensors (the Lenovo board "LNVNB161216" has no supported embedded controller or Super I/O), Windows exposes the ACPI fan device without a speed (`Win32_Fan` is empty, no fan performance counter), and Lenovo's WMI offers only BIOS settings. The fan row stays hidden; no value is estimated.
- Hover and click timing by hand: input was never simulated; behavior is covered by state machine tests.

## Known limitations

- Manual fan control is not offered: no supported interface guarantees that fans return to firmware control if the app crashes or is killed. Monitoring only.
- CPU package temperatures need LibreHardwareMonitor's driver and administrator rights; without them the CPU tile shows its load only.
- ACPI thermal zones are real sensors, but Windows does not say what they measure, so they are labelled "Thermal zone" and not attributed to the CPU.
- Plan usage updates when Claude Code sends a status line update (after replies in a terminal session), not on its own and not from the VS Code extension; after a window resets, the island shows no percentage until the next update.
- When Claude Code already has a status line, Developer Island does not replace it; plan usage then needs a manual pipe into `DeveloperIsland.exe --claude-statusline`.
- The camera and microphone marks disappear while the island is hidden from the tray or for a fullscreen app (both are hides you asked for); Smart Auto-Hide keeps the notch instead.
- One placement is saved, not a layout per display. ARM64, Windows 10 and clean-machine installation still need independent validation.
