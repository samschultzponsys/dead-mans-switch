; Inno Setup script for Dead Man's Switch. CI compiles it with
;   iscc /DAppVersion=X.Y installer\DeadMansSwitch.iss
; after building dist\DeadMansSwitch.exe, producing dist\DeadMansSwitch-Setup-X.Y.exe.
;
; It installs per user (no admin prompt) under %LOCALAPPDATA%\Programs, so the
; app can update itself without needing permission to write to Program Files.

#ifndef AppVersion
  #error Pass the version: iscc /DAppVersion=X.Y
#endif

[Setup]
; AppId identifies the install for upgrades and uninstall. Never change it; the
; app reads its uninstall key (see Updater.cs) to know it was installed.
AppId={{8F3C2A51-6B1E-4D7A-9C2E-5A1F0B7D3E44}
AppName=Dead Man's Switch
AppVersion={#AppVersion}
AppVerName=Dead Man's Switch {#AppVersion}
AppPublisherURL=https://github.com/samschultzponsys/dead-mans-switch
AppSupportURL=https://github.com/samschultzponsys/dead-mans-switch/issues
AppUpdatesURL=https://github.com/samschultzponsys/dead-mans-switch/releases
VersionInfoVersion={#AppVersion}
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\DeadMansSwitch
DisableDirPage=auto
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=DeadMansSwitch-Setup-{#AppVersion}
SetupIconFile=..\assets\DeadMansSwitch.ico
UninstallDisplayIcon={app}\DeadMansSwitch.exe
UninstallDisplayName=Dead Man's Switch
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
MinVersion=10.0
; Close a running copy (it may be in the tray) before replacing it.
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "..\dist\DeadMansSwitch.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Dead Man's Switch"; Filename: "{app}\DeadMansSwitch.exe"
Name: "{autodesktop}\Dead Man's Switch"; Filename: "{app}\DeadMansSwitch.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\DeadMansSwitch.exe"; Description: "Start Dead Man's Switch now"; Flags: nowait postinstall skipifsilent
; The app's own updater runs this installer silently with /RELAUNCH to reopen afterwards.
Filename: "{app}\DeadMansSwitch.exe"; Flags: nowait; Check: RelaunchRequested

[UninstallRun]
; Stop a running copy so its exe can be removed.
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im DeadMansSwitch.exe"; Flags: runhidden; RunOnceId: "StopApp"

[Code]
function RelaunchRequested: Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/RELAUNCH') = 0 then
      Result := True;
end;
