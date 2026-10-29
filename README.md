# Dead Man's Switch

A small Windows app that shuts the PC down unless someone keeps confirming
they're still there. Useful for leaving a machine running (a download, a render,
a game server) without it staying on all night if you fall asleep or walk away.

## How it works

1. A window stays on top with a green ring counting down to the next check
   (5 minutes unless you change it).
2. Clicking **I'm here** refills the ring and starts the countdown again.
3. If the ring runs out, the window turns red and the **final warning** starts
   (60 seconds unless you change it). It plays a sound and pulls itself to the
   front every 2 seconds, even if minimized.
4. If nobody clicks before the warning ends, the PC shuts down, restarts or
   hibernates, depending on the setting. Shutdown and restart force open apps
   to close.

**Stop timer and close** (or the × button) closes the app without doing anything.

## Download and run

Get `DeadMansSwitch.exe` from the
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
| Check for updates when the app opens | On | |

**Save** applies the settings right away: a running check restarts with the
new length, and a final warning already in progress keeps its length.
Settings are stored in `%APPDATA%\DeadMansSwitch\settings.json` and used every
time the app opens. Delete that file to go back to the defaults.

## Updates

When the app opens it asks GitHub for this repo's latest release. If that's
newer than the running version, a banner offers to update. Clicking **Update**
downloads the new exe, puts it in place of the current one, and starts it. If
GitHub can't be reached, nothing is shown and the timer runs as normal.

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
`vMAJOR.MINOR` (for example `v1.2`).

## Making a release

1. Note the changes under `## [Unreleased]` in `CHANGELOG.md`.
2. Bump the version, commit and tag:
   ```powershell
   .\tools\Bump-Version.ps1 -Tag
   ```
3. Push the commit and tag:
   ```powershell
   git push --follow-tags
   ```

GitHub Actions builds every push to `main` on Windows and runs the app's
self-test; the exe is attached to each run as a download. When the push is a
`v*` tag, it also publishes a GitHub Release with the changelog section as its
notes. Installed copies pick it up the next time they open.

## Building locally

With the [.NET SDK](https://dotnet.microsoft.com/download) (8 or later), on
Windows or Linux:

```
dotnet build src/DeadMansSwitch/DeadMansSwitch.csproj -c Release -o dist
```

This writes `dist/DeadMansSwitch.exe`. On Windows, check that the window
loads with:

```
dist\DeadMansSwitch.exe --selftest
```

It exits with code 0 if the self-test passes.

## Project layout

```
src/DeadMansSwitch/
  MainWindow.xaml             window layout and styles (loaded at runtime)
  MainView.cs                 timer phases, ring drawing, settings panel
  Settings.cs                 settings file
  Updater.cs                  GitHub release check and exe swap
  Program.cs                  entry point and --selftest
assets/DeadMansSwitch.ico     app icon (tools/make_icon.py redraws it)
tools/Bump-Version.ps1        version bump + changelog + tag
.github/workflows/build.yml   Windows build, self-test and releases
VERSION                       current version
CHANGELOG.md                  release history
```

Versions before 1.1 were a PowerShell script packaged with ps2exe. That script
is in the git history.
