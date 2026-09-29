# Performance

Developer Island runs all day, so idle cost matters more than peak speed.

## Measurements

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
