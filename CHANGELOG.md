# Changelog

All notable changes to Dead Man's Switch are recorded here.
Versions are `MAJOR.MINOR` with a single-digit minor that rolls over after 9
(`1.1 … 1.9, 2.0, 2.1 …`). See the README's "Versioning" section.

## [Unreleased]

## [1.1] - 2026-10-29

First versioned release. The source was recovered from the existing
`DeadManSwitchWin.exe` (a ps2exe build with file version 0.0.0.0). The timer
itself works the same as before.

### Changed
- Renamed from "Shutdown Guard" to **Dead Man's Switch**. The exe is now
  `DeadMansSwitch.exe` and the script `src/DeadMansSwitch.ps1`.

### Added
- App icon (a red power symbol), embedded in the exe.
- Automatic updater: on startup, checks this repo's latest GitHub Release and,
  if it is newer, offers to download and install it. The exe is swapped and
  relaunched once the running copy exits. Turn off with `$CheckForUpdates = $false`.
- `$AppVersion` in the script; the version is shown in the window title and
  stamped into the exe's file properties.
- `build/Build.ps1` to compile the exe with ps2exe.
- `tools/Bump-Version.ps1` to move to the next version, date the changelog and tag the release.
- GitHub Actions workflow that builds the exe on Windows and publishes the
  release when a `v*` tag is pushed.

## [0.0] - pre-versioning

The original `DeadManSwitchWin.exe`: a WPF window with a 5-minute "next check"
bar, a 60-second final warning that forces itself to the foreground and plays
a sound, and a forced shutdown (`shutdown /s /f /t 0`) if nobody responds.
