# Performance

Developer Island runs all day, so idle cost matters more than peak speed.

## Measurements

### Iteration 3 (2026-10-01)

Lenovo laptop (i7-13700H, Intel Iris Xe, battery), Windows 11, no administrator rights. The user's own instance kept running during the measurements, so normal-mode figures come from the parts that changed.

| Scenario | Result |
|---|---|
| System module sampling (1 Hz, GPU every second, temperatures and fans every 5 s), Release, 60 s | 125 ms CPU (0.2 % of one core) |
| Same after switching off sensor groups that deliver nothing, Debug, 30 s | 78 ms |
| Demo island with a running focus timer, Debug, 45 s, before this iteration | 1.2-2.0 s (the capsule re-measured and morphed every second) |
| Same with tabular figures | 0.84-0.95 s |
| Disabled modules | No timers, watchers, sensors or processes (covered by tests) |

The hardware sensor library adds memory while the System module is on (the measuring process had a 113 MB working set, 84 MB private); turning System off closes it. On this device LibreHardwareMonitor's CPU group would cost about 9 ms per update without delivering temperatures (they need a driver and administrator rights), so it is switched off after detection.

### V1

Windows 11 (26200), 1920 × 1200 at 125 % plus a 2560 × 1440 second monitor, Release build, self-contained, x64.

| Scenario | Working set | Private bytes | CPU |
|---|---|---|---|
| Idle, compact, normal mode, 60 s window | **55 MB** | 79 MB | **16 ms / 60 s (0.03 % of one core)** |
| Idle, compact, before the optimizations below | 204 MB | 118 MB | 250 ms / 30 s (0.8 %) |

Other timings:

| Scenario | Time |
|---|---|
| First scan: 42 Claude transcripts, about 14k events | 1.6 s, on a background thread |
| Warm start: only one changed transcript re-read (cursors) | 47 ms |
| Morph (compact to expanded) | About 250 ms spring on the compositor thread |

Measured with `Get-Process` over the stated window, after the startup scan.

## What keeps it cheap

- **Modules that are off do nothing.** No watchers, timers, sensors or child processes. System samples once a second only while enabled and wakes the UI only while its panel is open or it is featured; Git starts `git` only while its panel is open (or once for a new repository); the favorite rotation runs only while the compact capsule is visible; the outside-click hook exists only while expanded.
- **Event-driven only.**
  - FileSystemWatcher and SMTC events feed the providers.
  - One-shot timers handle midnight and the active-flag timeout.
  - The focus timer ticks only while a session runs.
  - The media position ticks only while the expanded Music tab is visible and playing.
- **No rendering loop.** All motion is composition animation (springs, key frames and expressions). Nothing redraws while idle, and there are no perpetual animations.
- **Incremental work.**
  - File cursors mean only appended bytes are parsed.
  - Lines are pre-filtered by substring before any JSON parsing.
  - Daily totals are running accumulators.
  - The history graph recolours its cells in place.
- **Memory trim after the first scan.** One compacting GC and an `EmptyWorkingSet` call drop the start-up peak.
- **Lean runtime.** Only the needed Windows App SDK components ship (no AI, ML, ONNX or Widgets), about 45 MB less.
- **Runtime settings.** Workstation GC without concurrent background threads, no tiered PGO instrumentation, and ReadyToRun for faster start.

## Reproduce

```powershell
$p = Start-Process .\DeveloperIsland.exe -PassThru; Start-Sleep 25
$c1 = $p.TotalProcessorTime; Start-Sleep 60; $p.Refresh()
"{0:N1} MB, CPU {1} ms" -f ($p.WorkingSet64/1MB), ($p.TotalProcessorTime - $c1).TotalMilliseconds
```
