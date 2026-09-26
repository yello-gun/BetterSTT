; BetterTTS installer (per-user, no admin rights needed).
; Build with: tools\build.ps1
; The AppId is unchanged from when the app was called CleanDictate, so installing upgrades that version.

#define AppName "BetterTTS"
#define AppVersion "2.1.0"
#define AppExe "BetterTTS.exe"

[Setup]
AppId={{6C2E3F0A-5B9D-4E7A-9C41-2D8F1B7A3E55}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=BetterTTS
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UsePreviousAppDir=no
UsePreviousGroup=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=BetterTTS-Setup-{#AppVersion}
SetupIconFile=..\assets\BetterTTS.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=force

[Tasks]
Name: "startup"; Description: "Start BetterTTS when I sign in to Windows"; GroupDescription: "Options:"
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Options:"; Flags: unchecked

[InstallDelete]
; Leftovers from the CleanDictate name.
Type: filesandordirs; Name: "{localappdata}\Programs\CleanDictate"
Type: filesandordirs; Name: "{userprograms}\CleanDictate"
Type: files; Name: "{userdesktop}\CleanDictate.lnk"

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "CleanDictate"; Flags: deletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; \
    ValueData: """{app}\{#AppExe}"" --background"; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch BetterTTS now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#AppExe}"; Flags: runhidden; RunOnceId: "KillApp"

[Code]
// Close a running copy from before the rename so its folder can be removed.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM CleanDictate.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', '{#AppName}');
    if DirExists(ExpandConstant('{localappdata}\{#AppName}')) then
      if MsgBox('Also delete your BetterTTS settings and downloaded speech models?',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(ExpandConstant('{localappdata}\{#AppName}'), True, True, True);
  end;
end;
