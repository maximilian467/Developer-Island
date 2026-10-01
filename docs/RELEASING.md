# Releasing

Releases are built locally and published as GitHub releases with three files: the installer, the portable zip and their SHA-256 checksums.

## Checklist

1. **Version.** Set `<Version>` in [`Directory.Build.props`](../Directory.Build.props) (semantic versioning: `0.1.0`, `0.1.1`, `0.2.0`).
2. **Changelog.** Move the entries under "Unreleased" in [`CHANGELOG.md`](../CHANGELOG.md) to a new section with the version and date.
3. **Build.** Quit any Developer Island that runs from `artifacts/publish`, then:

   ```powershell
   powershell -ExecutionPolicy Bypass -File tools/build-release.ps1
   ```

   It runs the tests and writes to `artifacts/`:

   | File | What it is |
   |---|---|
   | `DeveloperIsland-Setup-<version>.exe` | Per-user installer (no administrator rights, self-contained) |
   | `DeveloperIsland-<version>-win-x64-portable.zip` | Portable build; extract everything and run `DeveloperIsland.exe` |
   | `SHA256SUMS-<version>.txt` | Checksums of both |

4. **Smoke test** on a machine (or a fresh user account) without the .NET SDK: install, start, open each module in normal and demo mode, check Settings, About shows the version, uninstall.
5. **Commit and tag:**

   ```powershell
   git commit -am "release: 0.1.0"
   git tag -a v0.1.0 -m "Developer Island 0.1.0"
   git push origin main v0.1.0
   ```

6. **Publish** the release on GitHub (Releases, Draft a new release, tag `v0.1.0`), with the changelog section as notes and the three files attached. With the [GitHub CLI](https://cli.github.com):

   ```powershell
   gh release create v0.1.0 --title "Developer Island 0.1.0" --notes-file notes.md `
     artifacts/DeveloperIsland-Setup-0.1.0.exe `
     artifacts/DeveloperIsland-0.1.0-win-x64-portable.zip `
     artifacts/SHA256SUMS-0.1.0.txt
   ```

## Notes

- The installer is not code-signed yet, so Windows SmartScreen asks for confirmation on first run. Mention this in the release notes.
- Only x64 is built and tested. `-Runtime win-arm64 -SkipInstaller` produces an experimental ARM64 zip that has not been validated.
- Uninstalling keeps `%LOCALAPPDATA%\DeveloperIsland` (history and settings), so a reinstall continues where it left off.
