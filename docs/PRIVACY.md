# Privacy

What Developer Island reads, what it stores and what leaves your PC. The short version is in the [README](../README.md#privacy).

**Local-first. Developer Island never uploads your prompts or code.** (The AI tools themselves talk to their providers as usual; Developer Island only reads what they leave on your disk.)

- No accounts, telemetry or cloud sync. The only network requests are the ones you set up: the calendar links you add, and the GitHub CLI's read-only requests to GitHub when the GitHub module is on and `gh` is signed in.
- The app reads local JSONL files that can also contain conversation content, but extracts only usage and session metadata: token counts, model, timestamps, session IDs and the working directory. It does not retain, display, log or upload prompt, message or code content.
- The local database (`%LOCALAPPDATA%\DeveloperIsland\usage.db`) contains token counts, model names, timestamps, session IDs, project folder names, daily totals and focus sessions.
- `settings.json` holds your settings, including repository paths, favorites and the auto-hide app list; `tasks.json` holds your tasks; `ui-state.json` remembers the module you opened last. Private calendar links are kept in Windows Credential Manager and appear in settings only as "Google Calendar" or similar. Calendar event titles are kept in memory only.
- Camera and microphone marks read Windows' own record of which apps use those devices (the capability consent store in the registry, as Windows' privacy indicator does). Only "in use or not" is kept; app names are never stored, shown or logged, and nothing is recorded.
- `claude-plan.json` holds the last Claude plan percentages and reset times that Claude Code handed over, nothing else from its status line input (no paths, model, cost or session).
- Logs (`%LOCALAPPDATA%\DeveloperIsland\logs`) contain app events and error types, never content, repository names, task titles, event titles or calendar links. They are kept for 7 days.
- The API equivalent uses a static price catalog and exchange rate. You can adjust both in `%LOCALAPPDATA%\DeveloperIsland\pricing.json`, for example:

  ```json
  { "usdToEur": 0.86, "models": { "your-model-id": { "input": 2.0, "output": 8.0 } } }
  ```

  These are illustrative values, not a current price quote. Prices are USD per million tokens and load on startup.
