; Builds dist\RezTray-Setup.exe. Build the Release exe first, then run:
;   ISCC.exe installer\RezTray.iss
; It installs for the current user only, so it never asks for admin rights.

#define Exe "..\bin\Release\net48\RezTray.exe"
#define FullVersion GetVersionNumbersString(Exe)
#define AppVersion Copy(FullVersion, 1, RPos(".", FullVersion) - 1)
#define RunKey "Software\Microsoft\Windows\CurrentVersion\Run"

[Setup]
AppId={{7E0F3B52-6C1D-4B7A-9A44-2D5C8E1F6A31}
AppName=RezTray
AppVersion={#AppVersion}
AppVerName=RezTray {#AppVersion}
AppPublisher=PrimePcs
AppPublisherURL=https://github.com/PrimePcs/RezTray
AppSupportURL=https://github.com/PrimePcs/RezTray
AppUpdatesURL=https://github.com/PrimePcs/RezTray/releases/latest
VersionInfoVersion={#FullVersion}
DefaultDirName={autopf}\RezTray
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\dist
OutputBaseFilename=RezTray-Setup
SetupIconFile=..\assets\app.ico
UninstallDisplayIcon={app}\RezTray.exe
UninstallDisplayName=RezTray
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes

[Tasks]
Name: "desktopicon"; Description: "Add a desktop shortcut"; Flags: unchecked
Name: "startup"; Description: "Start RezTray with Windows"; Flags: unchecked

[Files]
Source: "{#Exe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\RezTray"; Filename: "{app}\RezTray.exe"
Name: "{autodesktop}\RezTray"; Filename: "{app}\RezTray.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "{#RunKey}"; ValueType: string; ValueName: "RezTray"; ValueData: """{app}\RezTray.exe"" --startup"; Tasks: startup

[Run]
Filename: "{app}\RezTray.exe"; Description: "Open RezTray now"; Flags: nowait postinstall skipifsilent

[Code]
{ Any running copy has to close: an update could not replace its file, and a portable
  copy elsewhere would stop the new one from starting. }
procedure StopRunningCopies;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im RezTray.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(500);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningCopies;
  Result := '';
end;

{ Settings in AppData stay, so a reinstall keeps profiles, hotkeys and setups. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    StopRunningCopies;
    RegDeleteValue(HKEY_CURRENT_USER, '{#RunKey}', 'RezTray');
  end;
end;
