# Dead Man's Switch

A small Windows app that shuts the PC down unless someone keeps confirming
they're still there. Useful for leaving a machine running (a download, a render,
a game server) without it staying on all night if you fall asleep or walk away.

<p>
  <img src="https://raw.githubusercontent.com/samschultzponsys/dead-mans-switch/screenshots/check.png" width="260" alt="Countdown to the next check">
  <img src="https://raw.githubusercontent.com/samschultzponsys/dead-mans-switch/screenshots/warning.png" width="260" alt="Final warning">
  <img src="https://raw.githubusercontent.com/samschultzponsys/dead-mans-switch/screenshots/settings.png" width="260" alt="Settings">
  <img src="https://raw.githubusercontent.com/samschultzponsys/dead-mans-switch/screenshots/changelog.png" width="260" alt="What's new">
  <img src="https://raw.githubusercontent.com/samschultzponsys/dead-mans-switch/screenshots/paused.png" width="260" alt="Paused">
  <img src="https://raw.githubusercontent.com/samschultzponsys/dead-mans-switch/screenshots/tray-question.png" width="260" alt="Close to tray question">
</p>

## How it works

1. A window stays on top with a green ring counting down to the next check
   (5 minutes unless you change it).
2. Clicking **I'm here** refills the ring and starts the countdown again.
   **Pause** freezes it until you click **Resume**.
3. If the ring runs out, the window turns red and the **final warning** starts
   (60 seconds unless you change it). It plays a sound and pulls itself to the
   front every 2 seconds, even if minimized or in the tray. Press **Space** or
   click the button to reset the timer.
4. If nobody responds before the warning ends, the PC shuts down, restarts or
   hibernates, depending on the setting. Shutdown and restart force open apps
   to close.

**Stop timer and quit** closes the app without doing anything. Only one copy
runs at a time; starting it again brings up the one already running.

## Tray icon

The first time you close the window (×), the app asks whether to keep running
in the system tray or quit, and remembers the answer. You can change it later
in Settings. In the tray, the timer keeps counting:

- Click the icon to open the window.
- Right-click for **I'm here**, **Pause**/**Resume** and **Stop timer and quit**.
- Hover over it to see the time left.

## Download and run

Get `DeadMansSwitch-X.Y.exe` (for example `DeadMansSwitch-1.2.exe`) from the
[latest release](https://github.com/samschultzponsys/dead-mans-switch/releases/latest)
and run it. There's nothing to install. It needs .NET Framework 4.8, which is
built into Windows 10 (May 2019 update or later) and Windows 11.

## Settings

Click the gear in the title bar:

| Setting | Default | Range |
|---|---|---|
| Check every | 5 minutes | 1-1440 minutes |
| Final warning | 60 seconds | 10-600 seconds |
| When time runs out | Shut down | Shut down, Restart, Hibernate |
| Play a sound at the final warning | On | |
| Keep running in the tray when closed | Asked the first time you close it | |
| Check for updates when the app opens | On | |

**Reset to defaults** fills in the default values; click **Save** to keep them.
**Save** applies the settings right away: a running check restarts with the
new length, and a final warning already in progress keeps its length.
Settings are stored in `%APPDATA%\DeadMansSwitch\settings.json` and used every
time the app opens. Delete that file to go back to the defaults.

## What's new and updates

Click the version next to the title (for example `v1.2`) to see the changelog.
It also opens by itself the first time a new version runs.

When the app opens, and every 6 hours while it runs, it asks GitHub for this
repo's releases. If there's a newer version, a banner offers to update, and its
notes are shown once in the changelog, so you can see what's new before
installing it. Clicking **Update** downloads the new exe and starts it. A file
named with its version (`DeadMansSwitch-1.2.exe`) is replaced by one with the
new version's name (`DeadMansSwitch-1.3.exe`); a file with any other name is
updated in place. If GitHub can't be reached, nothing is shown and the timer
runs as normal.

Updating needs write access to the folder the exe is in, so keep it somewhere
you own (Desktop, Documents, a tools folder) rather than `Program Files`.

## Versioning

Versions are `MAJOR.MINOR`, where the minor is a single digit that rolls over
after 9:

```
1.1 → 1.2 → … → 1.9 → 2.0 → 2.1 → … → 2.9 → 3.0
```

Each release moves up by exactly one step. The current version is in the
`VERSION` file, which the build stamps into the exe. Releases are tagged
`vMAJOR.MINOR` (for example `v1.2`) by the build.

## Making a release

1. Note the changes under `## [Unreleased]` in `CHANGELOG.md`.
2. Move to the next version, which also dates that changelog section:
   ```powershell
   .\tools\Bump-Version.ps1
   ```
3. Commit and push to `main`.

GitHub Actions builds every push to `main` on Windows and runs the app's
self-test; the exe is attached to each run as a download. When `VERSION`
holds a version that hasn't been released yet, the run also tags it
`vMAJOR.MINOR` and publishes a GitHub Release with the changelog section as
its notes. Installed copies pick it up the next time they open.

## Building locally

With the [.NET SDK](https://dotnet.microsoft.com/download) (8 or later), on
Windows or Linux:

```
dotnet build src/DeadMansSwitch/DeadMansSwitch.csproj -c Release -o dist
```

This writes `dist/DeadMansSwitch.exe` (CI adds the version to the name). On Windows, check that the window
loads with:

```
dist\DeadMansSwitch.exe --selftest
```

It exits with code 0 if the self-test passes.

## Project layout

```
src/DeadMansSwitch/
  MainWindow.xaml             window layout and styles (loaded at runtime)
  MainView.cs                 timer phases, ring, panels, tray and close handling
  Changelog.cs                built-in changelog and its renderer
  TrayIcon.cs                 notification-area icon and menu
  Settings.cs                 settings file
  Updater.cs                  GitHub release check and exe swap
  Program.cs                  entry point, single instance, --selftest
assets/DeadMansSwitch.ico     app icon (tools/make_icon.py redraws it)
tools/Bump-Version.ps1        version bump + changelog
.github/workflows/build.yml   Windows build, self-test and releases
VERSION                       current version
CHANGELOG.md                  release history
```

Versions before 1.1 were a PowerShell script packaged with ps2exe. That script
is in the git history.
