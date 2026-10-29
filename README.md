# Dead Man's Switch

A small Windows tool that shuts the PC down unless someone keeps confirming
they're still there. Useful for leaving a machine running (a download, a render,
a game server) without it staying on all night if you fall asleep or walk away.

## How it works

1. A window opens on top of everything with a green **"Time until next check"**
   bar that drains over the check interval (5 minutes unless you change it).
2. Clicking **"I'm here"** refills the bar and starts the period again.
3. If the bar runs out, the window turns red and the **final warning** starts
   (60 seconds unless you change it). It plays a sound and pulls itself to the foreground every 2 seconds.
4. If nobody clicks before the warning ends, the PC shuts down
   (`shutdown /s /f /t 0`, which forces open apps to close).

**Cancel shutdown timer** closes the window without shutting down.

## Download and run

Get `DeadMansSwitch.exe` from the
[latest release](https://github.com/samschultzponsys/dead-mans-switch/releases/latest)
and run it. It needs no install.

To run the script directly instead:

```powershell
powershell -ExecutionPolicy Bypass -WindowStyle Hidden -File src\DeadMansSwitch.ps1
```

## Timer settings

Set the timers at the bottom of the window: **Check every** (1-1440 minutes)
and **Final warning** (10-600 seconds), then click **Save**. A running check
period restarts with the new length straight away; during a final warning
the new lengths apply from the next check.

Saved values are stored in `%APPDATA%\DeadMansSwitch\settings.json` and are
used every time the app opens. Delete that file to go back to the defaults.

## Other configuration

These live in the `CONFIG` block at the top of `src/DeadMansSwitch.ps1`.
Changing them in the exe means rebuilding it (see Building).

| Setting | Default | Meaning |
|---|---|---|
| `$ExtendSeconds` | `300` | Default check interval, in seconds, until one is saved |
| `$WarnSeconds` | `60` | Default final warning, in seconds, until one is saved |
| `$TickMs` | `250` | How often the countdown text refreshes, in ms |
| `$AllowCancel` | `$true` | Show the Cancel button and allow closing the window |
| `$PlaySound` | `$true` | Play a warning sound when the final warning starts |
| `$ShutdownExe` / `$ShutdownArgs` | `shutdown.exe` / `/s /f /t 0` | What runs at the end. Use `/r` to restart or `/h` to hibernate |
| `$CheckForUpdates` | `$true` | Check GitHub for a newer version on startup |

## Updates

On startup the app asks GitHub for this repo's latest release. If it's newer
than the running version and includes `DeadMansSwitch.exe`, it asks whether
to update. Saying yes downloads the new exe next to the current one and closes
the app; a small helper then replaces the exe and starts the new version.

If GitHub can't be reached, the check is skipped and the timer starts as normal.
When running the `.ps1` directly, the app only shows the download link.

## Versioning

Versions are `MAJOR.MINOR`, where the minor is a single digit that rolls over
after 9:

```
1.1 → 1.2 → … → 1.9 → 2.0 → 2.1 → … → 2.9 → 3.0
```

Each release moves up by exactly one step. The current version is in the
`VERSION` file, and releases are tagged `vMAJOR.MINOR` (for example `v1.2`).

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

Pushing the tag starts the **Release** GitHub Actions workflow, which builds
the exe on Windows and publishes it as a GitHub Release with the changelog
section as its notes. Installed copies pick it up on their next start.

The workflow can also be started by hand from the Actions tab; that builds the
exe and attaches it to the run as a download, without publishing a release.

## Building locally

On Windows, in Windows PowerShell 5.1:

```powershell
.\build\Build.ps1
```

This installs the [ps2exe](https://github.com/MScholtes/PS2EXE) module if
needed and writes `dist\DeadMansSwitch.exe`. The build stops if
`$AppVersion` in the script doesn't match `VERSION`.

## Project layout

```
src/DeadMansSwitch.ps1      the app (compiled into the exe)
build/Build.ps1             ps2exe build
tools/Bump-Version.ps1      version bump + changelog + tag
tools/make_icon.py          regenerates the icon (needs Python + Pillow)
assets/DeadMansSwitch.ico   app icon, embedded in the exe
.github/workflows/          release build on tag push
VERSION                     current version
CHANGELOG.md                release history
```
