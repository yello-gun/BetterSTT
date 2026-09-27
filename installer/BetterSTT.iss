; BetterSTT installer (per-user, no admin rights needed).
; Build with: tools\build.ps1
; The AppId is unchanged from the app's earlier names (CleanDictate, BetterTTS), so installing upgrades those versions.

#define AppName "BetterSTT"
; The build passes the real version (/DAppVersion=x.y.z) from the project file or the release tag.
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#define AppExe "BetterSTT.exe"

[Setup]
AppId={{6C2E3F0A-5B9D-4E7A-9C41-2D8F1B7A3E55}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=BetterSTT
AppPublisherURL=https://github.com/yello-gun/BetterSTT
; Product name and version are embedded in the installer; code signing checks them.
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}
VersionInfoProductTextVersion={#AppVersion}
VersionInfoVersion={#AppVersion}
VersionInfoTextVersion={#AppVersion}
VersionInfoCompany={#AppName}
VersionInfoDescription={#AppName} Setup
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UsePreviousAppDir=no
UsePreviousGroup=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=BetterSTT-Setup-{#AppVersion}
SetupIconFile=..\assets\BetterSTT.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=force

[Tasks]
; Only offered on a first install. An update leaves shortcuts and the startup entry as the user left them
; (the app manages "Start with Windows" itself), so a deleted desktop shortcut doesn't come back.
Name: "startup"; Description: "Start BetterSTT when I sign in to Windows"; GroupDescription: "Options:"; Check: not IsUpgrade
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Options:"; Flags: unchecked; Check: not IsUpgrade

[InstallDelete]
; Leftovers from the earlier names.
Type: filesandordirs; Name: "{localappdata}\Programs\CleanDictate"
Type: filesandordirs; Name: "{userprograms}\CleanDictate"
Type: files; Name: "{userdesktop}\CleanDictate.lnk"
Type: filesandordirs; Name: "{localappdata}\Programs\BetterTTS"
Type: filesandordirs; Name: "{userprograms}\BetterTTS"
Type: files; Name: "{userdesktop}\BetterTTS.lnk"

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; The app's path never changes, so existing Start menu shortcuts are kept rather than recreated on update.
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"; Check: IsMissing('{group}\{#AppName}.lnk')
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"; Check: IsMissing('{group}\Uninstall {#AppName}.lnk')
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "CleanDictate"; Flags: deletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "BetterTTS"; Flags: deletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; \
    ValueData: """{app}\{#AppExe}"" --background"; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch BetterSTT now"; Flags: nowait postinstall skipifsilent
; In-app updates run the installer silently with /RELAUNCH=window or /RELAUNCH=background to reopen the app afterwards.
Filename: "{app}\{#AppExe}"; Parameters: "{code:RelaunchArgs}"; Flags: nowait; Check: RelaunchRequested

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#AppExe}"; Flags: runhidden; RunOnceId: "KillApp"

[Code]
var
  Upgrading: Boolean;

function InitializeSetup: Boolean;
begin
  // Decided before anything is installed, since this install registers itself later on.
  Upgrading := RegValueExists(HKEY_CURRENT_USER,
    ExpandConstant('Software\Microsoft\Windows\CurrentVersion\Uninstall\{#emit SetupSetting("AppId")}_is1'), 'UninstallString');
  Result := True;
end;

function IsUpgrade: Boolean;
begin
  Result := Upgrading;
end;

function IsMissing(Path: String): Boolean;
begin
  Result := not FileExists(ExpandConstant(Path));
end;

function RelaunchRequested: Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|}') <> '';
end;

function RelaunchArgs(Param: String): String;
begin
  if ExpandConstant('{param:RELAUNCH|}') = 'background' then
    Result := '--background --updated'
  else
    Result := '--updated';
end;

// Close a running copy from before a rename so its folder can be removed.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM CleanDictate.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM BetterTTS.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', '{#AppName}');
    if DirExists(ExpandConstant('{localappdata}\{#AppName}')) then
      if MsgBox('Also delete your BetterSTT settings and downloaded speech models?',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(ExpandConstant('{localappdata}\{#AppName}'), True, True, True);
  end;
end;
