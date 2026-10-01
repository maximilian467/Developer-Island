# Contributing to Developer Island

Thanks for your interest! Developer Island is an early-stage project; bug reports from real setups (other laptops, monitors, AI tools) are especially valuable.

## Issues

- **Bugs:** use the bug report form. It asks for the Developer Island and Windows versions, the module, your monitor setup and the relevant log lines (`%LOCALAPPDATA%\DeveloperIsland\logs`).
- **Ideas:** use the feature request form. For larger changes, please open an issue first so we can agree on the approach before you invest time.
- **Security problems:** report them privately, see [SECURITY.md](SECURITY.md).

Never post calendar links, tokens, prompts or private paths. Demo mode (`--demo`) is the easiest way to show a problem without your own data.

## Setup

Requirements: Windows 10 2004 or later (Windows 11 recommended), the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and Git.

```powershell
git clone https://github.com/maximilian467/Developer-Island.git
cd Developer-Island
dotnet test tests/DeveloperIsland.Tests
.\run.cmd -Demo
```

More in [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md).

## Pull requests

1. Branch from `main` (`fix/notch-hover`, `feat/calendar-colors`) and keep a pull request to one change.
2. Run the same checks as CI:
   ```powershell
   dotnet test tests/DeveloperIsland.Tests
   dotnet build src/DeveloperIsland -c Release -p:Platform=x64
   ```
3. Add tests for logic. Behavior lives in `DeveloperIsland.Core` so it can be tested without UI (state machine, policies, parsers, formatting).
4. UI changes need a before/after screenshot or a short recording. `--demo --snapshot=<folder> --culture=en-US` renders every state offscreen.
5. Update the docs when behavior changes (README, `docs/`), and add a line to `CHANGELOG.md` under "Unreleased".
6. Commit messages follow [Conventional Commits](https://www.conventionalcommits.org): `fix: keep the notch size when the microphone is on`.

## Code style

- Match the surrounding code: C# with nullable reference types, file-scoped namespaces, one type per file where practical.
- Data flows one way: provider, state, view model, view. Views do not parse or compute.
- Event-driven over polling; a turned-off module does no work at all.
- No fake values: if something is unknown, show that it is unknown.
- Privacy: never log or store prompt, code, calendar or task content.
- The design rules are in [docs/design/DESIGN-PRINCIPLES.md](docs/design/DESIGN-PRINCIPLES.md).

By contributing, you agree that your contributions are licensed under the [MIT License](LICENSE).
