# Recording the island

The README media (`docs/assets/hero.gif`, the screenshots and `social-preview.png`) are generated from the app's own offscreen renders, so they are reproducible and never show personal data:

```powershell
DeveloperIsland.exe --demo --snapshot=C:\temp\shots --culture=en-US
python tools/make-readme-media.py C:\temp\shots     # pip install pillow
```

The hero animation joins those renders with short crossfades. The live app morphs between states with springs instead. To show that motion, record the running app as below.

## Recording the live app

**Goal:** an 8 to 12 second clip, about 700 × 950 px around the top center of the screen, at 100 % or 125 % scaling.

1. **Clean the desktop.** Close or minimize windows near the top of the screen, hide desktop icons (right-click the desktop, View) and turn on Do Not Disturb so no notification shows up. `tools/backdrop.ps1` puts a plain dark band behind the island for 2 minutes:

   ```powershell
   powershell -ExecutionPolicy Bypass -File tools/backdrop.ps1 -Seconds 120
   ```

2. **Start demo mode** (invented data, nothing of yours): the **Developer Island (Demo)** Start menu shortcut, or

   ```powershell
   DeveloperIsland.exe --demo
   ```

   In demo mode the normal island can keep running; demo runs as a separate instance. Quit the normal island anyway, so only one capsule is on screen.

3. **Start a region recorder** such as [ScreenToGif](https://www.screentogif.com) or ShareX, at 30 fps, on a region around the island.

4. **Play the sequence** at a calm pace:

   | Step | Action | Hold |
   |---|---|---|
   | 1 | Compact island at rest | 1.5 s |
   | 2 | Hover the capsule until the peek opens | 1.5 s |
   | 3 | Click: the expanded island opens on Claude and Codex usage | 2.5 s |
   | 4 | Click the Music tab | 1.5 s |
   | 5 | Click the System tab | 1.5 s |
   | 6 | Press Esc: the island collapses | 1 s |

5. **Export.** Trim the start and end, keep it under 12 seconds and under about 4 MB as GIF (ScreenToGif: Save as GIF, "KGy SOFT" encoder, 256 colors, no dithering). An MP4 or WebM copy is useful for social posts.

6. **Check before committing:** no notifications, no private browser tabs or window titles, no real calendar events, repositories or usage. Save it as `docs/assets/hero.gif` (it replaces the generated one).

## Screenshots of the live app

`tools/screenshot.ps1` captures a region around the top or bottom center of the primary screen (physical pixels), for example:

```powershell
powershell -ExecutionPolicy Bypass -File tools/screenshot.ps1 -Out C:\temp\island.png -Edge top -Width 700 -Height 420
```

Use the same scaling and the same backdrop for every capture, and prefer the offscreen renders above for documentation.
