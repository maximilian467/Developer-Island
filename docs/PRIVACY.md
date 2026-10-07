# Privacy

What Developer Island reads, what it stores and what leaves your PC. The short version is in the [README](../README.md#privacy).

**Local-first. Developer Island never uploads your prompts or code.** (The AI tools themselves talk to their providers as usual; Developer Island only reads what they leave on your disk.)

- No accounts, telemetry or cloud sync. The only network traffic is what you set up: the calendar links you add, the GitHub CLI's read-only requests to GitHub when the GitHub module is on and `gh` is signed in, and the file copies to your Home Cloud target folder when you drop files.
- The app reads local JSONL files that can also contain conversation content, but extracts only usage and session metadata: token counts, model, timestamps, session IDs and the working directory. It does not retain, display, log or upload prompt, message or code content.
- The local database (`%LOCALAPPDATA%\DeveloperIsland\usage.db`) contains token counts, model names, timestamps, session IDs, project folder names, daily totals and focus sessions.
- `settings.json` holds your settings, including repository paths, favorites and the auto-hide app list; `tasks.json` holds your tasks; `ui-state.json` remembers the module you opened last. Private calendar links are kept in Windows Credential Manager and appear in settings only as "Google Calendar" or similar. Calendar event titles are kept in memory only.
- Camera and microphone marks read Windows' own record of which apps use those devices (the capability consent store in the registry, as Windows' privacy indicator does). Only "in use or not" is kept; app names are never stored, shown or logged, and nothing is recorded.
- `claude-plan.json` holds the last Claude plan percentages and reset times that Claude Code handed over, nothing else from its status line input (no paths, model, cost or session).
- **Home Cloud** copies the files you drop onto the island, and only those, to the folder in `HomeCloudTargetPath`, normally a share on your own server. There is no cloud service and no other destination. Files are not read for anything but the copy.
- To make the drop zone reachable while the island is a notch or hidden, a low-level mouse hook (`WH_MOUSE_LL`) evaluates only the button state and the cursor position. It runs only while Home Cloud is on and the island is retracted or hidden, and it stores and logs nothing.
- Logs (`%LOCALAPPDATA%\DeveloperIsland\logs`) contain app events and error types, never content, repository names, task titles, event titles or calendar links. For Home Cloud they hold only the number of files, bytes, duration, status and the type of an exception, never file names, folder names, paths or contents. They are kept for 7 days.
- The API equivalent uses a static price catalog and exchange rate. You can adjust both in `%LOCALAPPDATA%\DeveloperIsland\pricing.json`, for example:

  ```json
  { "usdToEur": 0.86, "models": { "your-model-id": { "input": 2.0, "output": 8.0 } } }
  ```

  These are illustrative values, not a current price quote. Prices are USD per million tokens and load on startup.
