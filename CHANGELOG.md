# Changelog

All notable changes to Dead Man's Switch are recorded here.
Versions are `MAJOR.MINOR` with a single-digit minor that rolls over after 9
(`1.1 … 1.9, 2.0, 2.1 …`). See the README's "Versioning" section.

## [Unreleased]

## [1.1] - 2026-10-29

First versioned release, rewritten as a native Windows app (C# / WPF on
.NET Framework 4.8) instead of a PowerShell script packaged with ps2exe. That
packaging was often flagged by antivirus.

### Changed
- Renamed from "Shutdown Guard" to **Dead Man's Switch**. The exe is now
  `DeadMansSwitch.exe`.
- New look: a dark, rounded window with a countdown ring and large digits in
  place of the two bars. The window turns red during the final warning.

### Added
- Settings panel (gear button): check interval, final-warning length, what
  happens at the end (shut down, restart or hibernate), the warning sound and
  update checks. Saved to `%APPDATA%\DeadMansSwitch\settings.json` and used
  every time the app opens.
- Automatic updates: when the app opens it checks this repo's latest GitHub
  Release and shows a banner if there's a newer version. One click downloads it,
  swaps it in and restarts.
- App icon, embedded in the exe, and the version shown in the title bar and the
  exe's file properties.
- `tools/Bump-Version.ps1` to move to the next version, date the changelog and
  tag the release.
- GitHub Actions build on Windows for every push, with a self-test
  (`--selftest`), and a GitHub Release for each `v*` tag.

### Fixed
- The button said "add 10 minutes" but restarted a 5-minute period.
- If the shutdown command fails, the app now shows the error and stays open
  instead of closing silently.

## [0.0] - pre-versioning

The original `DeadManSwitchWin.exe`: a WPF window with a 5-minute "next check"
bar, a 60-second final warning that forces itself to the foreground and plays
a sound, and a forced shutdown (`shutdown /s /f /t 0`) if nobody responds.
