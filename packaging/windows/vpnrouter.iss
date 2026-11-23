; Inno Setup 6 script for the VPNRouter Windows installer (owner-approved proposal: plans/phase-installer-proposal-2026-09-30.md).
; Built by tools\build-installer.ps1 from the same tree that goes into VPNRouter-vX-win.zip (Start VPN.cmd, README.txt, app\).
; Command line for IT: VPNRouter-Setup-vX.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART [/DIR="..."] [/TASKS="service,defender"]
;   Extra switches (Setup and uninstaller): /NOCLEANUP (skip the system cleanup on uninstall), /DELETEDATA (also delete
;   %ProgramData%\VPNRouter on uninstall), /NOSYSTEMCHANGES (scratch or staging installs: no ACL change on the data folder,
;   no change to the old script-installer's registry entry or the Start Menu shortcut, no service/Defender changes, no data deletion),
;   /CLEANUPARGS=--dry-run and /CLEANUPLOG=<file> (used by the tests of the uninstaller).

#ifndef AppVersion
  #error Pass /DAppVersion=<x.y.z or x.y.z-rN> (tools\build-installer.ps1 does).
#endif
#ifndef VersionNumeric
  #define VersionNumeric "0.0.0.0"
#endif
#ifndef PayloadDir
  #error Pass /DPayloadDir=<folder that holds Start VPN.cmd, README.txt and app\>.
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif

[Setup]
AppId={{BAAAEA6C-F1D8-4D34-B00D-91EC746D8527}
AppName=VPNRouter
AppVersion={#AppVersion}
AppVerName=VPNRouter {#AppVersion}
AppPublisher=NiniTux
AppPublisherURL=https://github.com/PavelLizunov/VPNRouter
AppSupportURL=https://github.com/PavelLizunov/VPNRouter/issues
AppUpdatesURL=https://github.com/PavelLizunov/VPNRouter/releases
VersionInfoVersion={#VersionNumeric}
VersionInfoTextVersion={#AppVersion}
VersionInfoProductTextVersion={#AppVersion}
VersionInfoProductName=VPNRouter
VersionInfoDescription=VPNRouter setup
DefaultDirName={autopf}\VPNRouter
DisableProgramGroupPage=yes
PrivilegesRequired=admin
#ifdef NinaBundleDir
ArchitecturesAllowed=x64os
#else
ArchitecturesAllowed=x64compatible
#endif
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
#ifdef NinaBundleDir
OutputBaseFilename=VPNRouter-Setup-with-NINA-v{#AppVersion}
#else
OutputBaseFilename=VPNRouter-Setup-v{#AppVersion}
#endif
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=VPNRouter
UninstallDisplayIcon={app}\app\VPNRouter.App.exe
CloseApplications=no
RestartApplications=no
UsePreviousAppDir=yes
UsePreviousTasks=yes
; Autostart writes HKCU\...\Run, i.e. for the account that runs Setup elevated (the signed-in user on a normal PC; another admin
; account's hive if a standard user types that account's password at the UAC prompt). Known limit, documented in the I2 brief.
UsedUserAreasWarning=no
#ifdef IconFile
SetupIconFile={#IconFile}
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[CustomMessages]
#ifdef NinaBundleDir
english.NinaInstallFailed=VPNRouter was installed. The optional NINA service setup could not be completed.
russian.NinaInstallFailed=VPNRouter установлен. Не удалось установить дополнительную службу NINA.
#endif
english.TaskGroupStartup=Startup:
english.TaskAutostart=Start VPNRouter when I sign in
english.TaskService=Install the VPNRouter Windows service (starts with Windows)
english.TaskGroupSecurity=Antivirus:
english.TaskDefender=Add Microsoft Defender exclusions for VPNRouter (recommended until the program is code-signed)
english.CleanupWarning=VPNRouter could not remove everything it added to Windows (see the log). You can run "VPNRouter.CLI.exe cleanup" later.
english.AskDeleteData=Also delete VPNRouter settings, profiles and logs (%ProgramData%\VPNRouter)?
russian.TaskGroupStartup=Запуск:
russian.TaskAutostart=Запускать VPNRouter при входе в систему
russian.TaskService=Установить службу Windows VPNRouter (запускается вместе с Windows)
russian.TaskGroupSecurity=Антивирус:
russian.TaskDefender=Добавить исключения Microsoft Defender для VPNRouter (рекомендуется, пока программа не подписана)
russian.CleanupWarning=VPNRouter не смог убрать из Windows всё, что добавил (см. журнал). Позже можно выполнить "VPNRouter.CLI.exe cleanup".
russian.AskDeleteData=Удалить также настройки, профили и журналы VPNRouter (%ProgramData%\VPNRouter)?

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autostart"; Description: "{cm:TaskAutostart}"; GroupDescription: "{cm:TaskGroupStartup}"; Flags: unchecked
Name: "service"; Description: "{cm:TaskService}"; GroupDescription: "{cm:TaskGroupStartup}"; Flags: unchecked
Name: "defender"; Description: "{cm:TaskDefender}"; GroupDescription: "{cm:TaskGroupSecurity}"; Flags: unchecked

[Files]
#ifdef NinaBundleDir
Source: "{#NinaBundleDir}\*"; DestDir: "{tmp}\nina"; Flags: deleteafterinstall; Check: SystemChangesAllowed
#endif
Source: "installer\zapret-maintenance.ps1"; DestDir: "{tmp}"; Flags: dontcopy
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion restartreplace
Source: "installer\stop-vpnrouter.ps1"; DestDir: "{app}\installer"; Flags: ignoreversion
Source: "installer\stop-vpnrouter.ps1"; DestDir: "{tmp}"; Flags: dontcopy

[Icons]
Name: "{commonprograms}\VPNRouter"; Filename: "{app}\app\VPNRouter.GUI.exe"; WorkingDir: "{app}\app"; IconFilename: "{app}\app\VPNRouter.App.exe"; Comment: "Virtual Penguin Network - split-tunnel VPN router"; Check: SystemChangesAllowed
Name: "{autodesktop}\VPNRouter"; Filename: "{app}\app\VPNRouter.GUI.exe"; WorkingDir: "{app}\app"; IconFilename: "{app}\app\VPNRouter.App.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "VPNRouter"; ValueData: """{app}\app\VPNRouter.App.exe"" --minimized"; Flags: uninsdeletevalue; Tasks: autostart
Root: HKLM; Subkey: "Software\VPNRouter\Installer"; ValueType: dword; ValueName: "DefenderExclusions"; ValueData: 1; Flags: uninsdeletekey; Tasks: defender

[Run]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Add-MpPreference -ExclusionPath '{app}','{commonappdata}\VPNRouter'"""; Flags: runhidden; Tasks: defender
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\installer\stop-vpnrouter.ps1"" -InstallDir ""{app}"" -InstallService"; Flags: runhidden; Tasks: service; Check: SystemChangesAllowed
Filename: "{app}\app\VPNRouter.GUI.exe"; Description: "{cm:LaunchProgram,VPNRouter}"; WorkingDir: "{app}\app"; Flags: nowait postinstall skipifsilent runascurrentuser

[UninstallDelete]
Type: filesandordirs; Name: "{app}\app"
Type: filesandordirs; Name: "{app}\app.bak"
Type: filesandordirs; Name: "{app}\app.bak.tmp"
Type: files; Name: "{app}\app.bak.id"
Type: files; Name: "{app}\.update-backup.lock"
Type: files; Name: "{app}\.update-failed"
Type: filesandordirs; Name: "{app}\installer"

[Code]
const
  OldUninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\VPNRouter';
  PowerShellExe = '{sys}\WindowsPowerShell\v1.0\powershell.exe';

var
  ServiceWasRunning: Boolean;

#include "installer\purge-zapret-runtime.iss"

function SwitchPresent(const Name: string): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/' + Name) = 0 then
    begin
      Result := True;
      Exit;
    end;
end;

function ParamValue(const Name, Default: string): string;
var
  I: Integer;
  Prefix: string;
begin
  Result := Default;
  Prefix := '/' + Name + '=';
  for I := 1 to ParamCount do
    if CompareText(Copy(ParamStr(I), 1, Length(Prefix)), Prefix) = 0 then
    begin
      Result := Copy(ParamStr(I), Length(Prefix) + 1, Length(ParamStr(I)));
      Exit;
    end;
end;

function SystemChangesAllowed: Boolean;
begin
  Result := not SwitchPresent('NOSYSTEMCHANGES');
end;

function DataDirectory: string;
begin
  Result := ExpandConstant('{commonappdata}\VPNRouter');
end;

{ Stops VPNRouter, but only what runs from the install folder (and the data folder on a normal install). }
function StopVpnrouter(const ScriptPath, InstallDir: string; RemoveService: Boolean): Integer;
var
  Params: string;
  Code: Integer;
begin
  Params := '-NoProfile -ExecutionPolicy Bypass -File "' + ScriptPath + '" -InstallDir "' + RemoveBackslash(InstallDir) + '"';
  if SystemChangesAllowed then
    Params := Params + ' -DataDir "' + DataDirectory + '"';
  if RemoveService then
    Params := Params + ' -RemoveService';
  if Exec(ExpandConstant(PowerShellExe), Params, '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Result := Code
  else
    Result := -1;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  if SystemChangesAllowed then
    if not PrepareZapretUpgrade('VPNRouter') then begin
      Result := 'Could not secure the legacy Zapret service before upgrade.';
      Exit;
    end;
  ExtractTemporaryFile('stop-vpnrouter.ps1');
  Code := StopVpnrouter(ExpandConstant('{tmp}\stop-vpnrouter.ps1'), WizardDirValue, False);
  ServiceWasRunning := (Code = 10);
  if (Code = 0) or ServiceWasRunning then Result := ''
  else Result := 'Could not stop the installed VPNRouter processes safely.';
end;

{ The script installer wrote HKLM\...\Uninstall\VPNRouter; this installer has its own entry, so the old one would show twice. }
procedure MigrateOldScriptInstall;
var
  Location: string;
begin
  if RegQueryStringValue(HKLM, OldUninstallKey, 'InstallLocation', Location) then
    if CompareText(RemoveBackslash(Location), RemoveBackslash(WizardDirValue)) = 0 then
      RegDeleteKeyIncludingSubkeys(HKLM, OldUninstallKey);
end;

procedure RestrictDataDirectoryAcl;
var
  Code: Integer;
begin
  ForceDirectories(DataDirectory);
  Exec(ExpandConstant('{sys}\icacls.exe'),
    '"' + DataDirectory + '" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F',
    '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
begin
  if (CurStep = ssPostInstall) and SystemChangesAllowed then
  begin
    MigrateOldScriptInstall;
    RestrictDataDirectoryAcl;
    FinishZapretUpgrade;
    if ServiceWasRunning and (not WizardIsTaskSelected('service')) then
      Exec(ExpandConstant(PowerShellExe), '-NoProfile -ExecutionPolicy Bypass -File "' +
        ExpandConstant('{app}\installer\stop-vpnrouter.ps1') + '" -RestartService -InstallDir "' +
        ExpandConstant('{app}') + '"', '', SW_HIDE, ewWaitUntilTerminated, Code);
#ifdef NinaBundleDir
    if (not Exec(ExpandConstant(PowerShellExe),
        '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
        ExpandConstant('{tmp}\nina\nina-windows-install.ps1') + '" -Headless -BuildDir "' +
        ExpandConstant('{tmp}\nina') + '" -BundleDir "' + ExpandConstant('{tmp}\nina') + '"',
        '', SW_HIDE, ewWaitUntilTerminated, Code)) or (Code <> 0) then
    begin
      Log('The optional NINA setup failed.');
      SuppressibleMsgBox(CustomMessage('NinaInstallFailed'), mbInformation, MB_OK, IDOK);
    end;
#endif
  end;
end;

function InitializeUninstall: Boolean;
var Code: Integer;
begin
  Code := StopVpnrouter(ExpandConstant('{app}\installer\stop-vpnrouter.ps1'), ExpandConstant('{app}'), SystemChangesAllowed);
  Result := (Code = 0) or (Code = 10);
end;

procedure RunSystemCleanup;
var
  Cli, LogFile, Args: string;
  Code: Integer;
begin
  Cli := ExpandConstant('{app}\app\VPNRouter.CLI.exe');
  if not FileExists(Cli) then
    Exit;
  LogFile := ParamValue('CLEANUPLOG', ExpandConstant('{tmp}\vpnrouter-cleanup.log'));
  Args := Trim('cleanup ' + ParamValue('CLEANUPARGS', ''));
  if (not Exec(ExpandConstant('{cmd}'), '/C ""' + Cli + '" ' + Args + ' > "' + LogFile + '" 2>&1"', '', SW_HIDE, ewWaitUntilTerminated, Code))
     or (Code <> 0) then
    SuppressibleMsgBox(CustomMessage('CleanupWarning'), mbInformation, MB_OK, IDOK);
end;

procedure RemoveDefenderExclusions;
var
  Flag: Cardinal;
  Code: Integer;
begin
  if RegQueryDWordValue(HKLM, 'Software\VPNRouter\Installer', 'DefenderExclusions', Flag) and (Flag = 1) then
    Exec(ExpandConstant(PowerShellExe),
      '-NoProfile -ExecutionPolicy Bypass -Command "Remove-MpPreference -ExclusionPath ''' + ExpandConstant('{app}') + ''',''' + DataDirectory + '''"',
      '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    if (not SwitchPresent('NOCLEANUP')) and (SystemChangesAllowed or
       (CompareText(Trim(ParamValue('CLEANUPARGS', '')), '--dry-run') = 0)) then
      RunSystemCleanup;
    if SystemChangesAllowed then
      RemoveDefenderExclusions;
    if SystemChangesAllowed and (SwitchPresent('DELETEDATA') or (DirExists(DataDirectory) and (not UninstallSilent) and
       (SuppressibleMsgBox(CustomMessage('AskDeleteData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES))) then begin
      PurgeZapretData('VPNRouter', 'PurgeData');
      PurgeZapretData('VPNRouter', 'PurgeRuntime');
    end;
  end;
end;
