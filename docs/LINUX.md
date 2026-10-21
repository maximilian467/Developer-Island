# Developer Island on Linux

Developer Island runs natively on Linux desktops. The Linux app (`src/DeveloperIsland.Linux`) shares
the core library and the view models with the Windows app and replaces the WinUI 3 shell with an
[Avalonia](https://avaloniaui.net/) UI that reproduces the same design, plus a Linux platform layer.
It is developed and tested on **Ubuntu 24.04 (GNOME)**, and should work on other X11 or Wayland
desktops with the limits listed below.

## Install

### Ubuntu / Debian (.deb)

```bash
sudo apt install ./developer-island_<version>_amd64.deb
developer-island            # or start "Developer Island" from the app grid
```

The package installs a self-contained build (no .NET runtime needed) into `/opt/developer-island`,
a `developer-island` launcher on `PATH`, a menu entry and the icon. Uninstall with
`sudo apt remove developer-island`; your data stays in `~/.local/share/developer-island`.

### Portable tarball

```bash
tar xzf DeveloperIsland-<version>-linux-x64.tar.gz
./developer-island-<version>/developer-island
```

### Recommended packages

| Package | Used for |
|---|---|
| `pulseaudio-utils` (`pactl`) | Audio output switching and the microphone privacy indicator (works with PipeWire too) |
| `libsecret-tools` (`secret-tool`) | Storing calendar links in the desktop keyring (otherwise a `0600` file) |
| `git`, `gh` | Git and GitHub modules |
| `gnome-shell-extension-appindicator` | The status-area icon on GNOME (preinstalled on Ubuntu) |

## Wayland and X11

The island window uses X11, so on a Wayland session it runs through **XWayland** (preinstalled on
Ubuntu). That is enough for drawing, clicking, dragging and the always-on-top capsule. Wayland does
not let ordinary apps see which window is focused, so two features need a little help on GNOME:

- **Auto-hide in apps / fullscreen detection**: Settings, Position, *GNOME Shell helper*, **Install**.
  This installs a tiny GNOME Shell extension (bundled, `gnome-extension/`) that reports the focused
  window over D-Bus. Log out and in once on Wayland so GNOME loads it. On X11 sessions none of this
  is needed.
- **Global shortcut**: on GNOME the shortcut is registered as a custom keyboard shortcut
  (Settings, Keyboard, Custom Shortcuts) that runs `developer-island --toggle`. On other X11 desktops
  the app grabs the key itself. On other Wayland compositors, bind `developer-island --toggle` to a key
  in your desktop's settings.

## What maps to what

| Feature | Windows | Linux |
|---|---|---|
| Music | Windows media sessions | MPRIS over D-Bus (Spotify, Firefox, Chromium, VLC, Rhythmbox...) |
| Audio output | Core Audio | `pactl` (PulseAudio / PipeWire) |
| System | PDH, LibreHardwareMonitor | `/proc`, `/sys/class/hwmon`, amdgpu, NVML (NVIDIA driver) |
| Awake | `SetThreadExecutionState`, lid power plan | `systemd-inhibit` (sleep and lid-switch locks that end with the app) |
| Privacy dots | Capability access manager | PulseAudio/PipeWire source outputs (mic), open `/dev/video*` devices (camera) |
| Start at login | Run registry key | `~/.config/autostart/developer-island.desktop` |
| Secrets | Credential Manager | Secret Service (`secret-tool`), file fallback |
| Tray icon | Notification area | StatusNotifierItem (AppIndicator) |
| Single instance | Named pipe | Unix socket in `$XDG_RUNTIME_DIR` |
| Data | `%LOCALAPPDATA%\DeveloperIsland` | `~/.local/share/developer-island` |

Claude Code and Codex usage, focus timer, calendar, tasks, Git, GitHub and Home Cloud use the shared
core library and behave exactly as on Windows.

## Build from source

Requirements: the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and Git.

```bash
git clone https://github.com/maximilian467/Developer-Island.git
cd Developer-Island
dotnet test tests/DeveloperIsland.Tests
dotnet run --project src/DeveloperIsland.Linux -- --demo     # start with demo data
tools/build-deb.sh                                           # tests, publish, tarball and .deb in artifacts/
```

`DeveloperIsland.Linux.slnx` opens the Linux app, the core library and the tests in an IDE.
Offscreen snapshots work as on Windows: `developer-island --demo --snapshot=<folder>`.

## Known limitations on Linux

- Wayland sessions other than GNOME cannot report the focused window, so auto-hide in apps is off there.
- Some docks and panels do not show StatusNotifierItem icons (GNOME needs the AppIndicator extension).
- Hardware sensors depend on the kernel drivers your machine loads; anything not exposed is hidden.
