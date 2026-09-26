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
Name: "startup"; Description: "Start BetterSTT when I sign in to Windows"; GroupDescription: "Options:"
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Options:"; Flags: unchecked

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
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "CleanDictate"; Flags: deletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "BetterTTS"; Flags: deletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; \
    ValueData: """{app}\{#AppExe}"" --background"; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch BetterSTT now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#AppExe}"; Flags: runhidden; RunOnceId: "KillApp"

[Code]
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
