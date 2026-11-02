; Inno Setup script for Dead Man's Switch. CI compiles it with
;   iscc /DAppVersion=X.Y installer\DeadMansSwitch.iss
; after building dist\DeadMansSwitch.exe, producing dist\DeadMansSwitch-Setup-X.Y.exe,
; the only file a release ships.
;
; The first page asks how to set it up:
;  - Install: per user (no admin prompt) under %LOCALAPPDATA%\Programs, with a
;    Start menu entry and an uninstaller in Settings > Apps.
;  - Portable: copies the app into a folder of the user's choice and adds
;    nothing to Windows. A DeadMansSwitch.portable marker beside the exe tells
;    the app to keep its settings in that folder.
;
; The app updates itself by running this setup silently: /SILENT /RELAUNCH for
; an installed copy, plus /PORTABLE /DIR=<its folder> for a portable one (and
; /REPLACES=<old exe> when the old exe had another name, so it gets removed).

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
DefaultDirName={code:DefaultDir}
DisableDirPage=no
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=DeadMansSwitch-Setup-{#AppVersion}
SetupIconFile=..\assets\DeadMansSwitch.ico
UninstallDisplayIcon={app}\DeadMansSwitch.exe
UninstallDisplayName=Dead Man's Switch
; A portable copy leaves no trace in Windows: no uninstaller, no Apps entry.
Uninstallable=not IsPortable
CreateUninstallRegKey=not IsPortable
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
MinVersion=10.0
; Close a running copy (it may be in the tray) before replacing it.
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; Flags: unchecked; Check: not IsPortable

[Files]
Source: "..\dist\DeadMansSwitch.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Dead Man's Switch"; Filename: "{app}\DeadMansSwitch.exe"; Check: not IsPortable
Name: "{autodesktop}\Dead Man's Switch"; Filename: "{app}\DeadMansSwitch.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\DeadMansSwitch.exe"; Description: "Start Dead Man's Switch now"; Flags: nowait postinstall skipifsilent
; After the app's own silent update (/RELAUNCH), reopen it.
Filename: "{app}\DeadMansSwitch.exe"; Parameters: "{code:RelaunchParams}"; Flags: nowait; Check: HasParam('/RELAUNCH')

[UninstallRun]
; Stop a running copy so its exe can be removed.
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im DeadMansSwitch.exe"; Flags: runhidden; RunOnceId: "StopApp"

[Code]
var
  ModePage: TInputOptionWizardPage;

function HasParam(Name: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), Name) = 0 then
      Result := True;
end;

function IsPortable: Boolean;
begin
  if WizardSilent or (ModePage = nil) then
    Result := HasParam('/PORTABLE')
  else
    Result := ModePage.SelectedValueIndex = 1;
end;

function InstalledDir: String;
begin
  Result := ExpandConstant('{localappdata}\Programs\DeadMansSwitch');
end;

function PortableDir: String;
begin
  Result := ExpandConstant('{userdesktop}\DeadMansSwitch');
end;

function DefaultDir(Param: String): String;
begin
  if HasParam('/PORTABLE') then
    Result := PortableDir
  else
    Result := InstalledDir;
end;

function RelaunchParams(Param: String): String;
var
  Replaces: String;
begin
  Result := '--updated';
  Replaces := ExpandConstant('{param:REPLACES|}');
  if Replaces <> '' then
    Result := Result + ' --replaces "' + Replaces + '"';
end;

procedure InitializeWizard;
begin
  ModePage := CreateInputOptionPage(wpWelcome,
    'How do you want to set it up?', '',
    'Choose Install unless you want to carry the app around or keep it out of Windows.',
    True, False);
  ModePage.Add('Install (recommended)' + #13#10 +
    'Adds Dead Man''s Switch to the Start menu and to Settings > Apps, where you can uninstall it.');
  ModePage.Add('Portable' + #13#10 +
    'Puts it in a folder you choose, with its settings beside it. Nothing is added to Windows.');
  if HasParam('/PORTABLE') then
    ModePage.SelectedValueIndex := 1
  else
    ModePage.SelectedValueIndex := 0;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  // Inno also runs this for silent installs, where the folder comes from /DIR
  // (the app's updater relies on that) and mustn't be replaced by a default.
  if (CurPageID = ModePage.ID) and not WizardSilent then
  begin
    if IsPortable then
      WizardForm.DirEdit.Text := PortableDir
    else
      WizardForm.DirEdit.Text := InstalledDir;
  end;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  // Only a portable copy needs a folder picked; an install goes to the usual place.
  Result := (PageID = wpSelectDir) and not IsPortable;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Marker: String;
begin
  if CurStep = ssPostInstall then
  begin
    Marker := ExpandConstant('{app}\DeadMansSwitch.portable');
    if IsPortable then
      SaveStringToFile(Marker, 'This file makes Dead Man''s Switch keep its settings in this folder.', False)
    else
      DeleteFile(Marker);
  end;
end;
