; Private pilot. Reuses the app payload and path-scoped stop helper of VPNRouter.
#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef PayloadDir
  #error PayloadDir is required
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif
[Setup]
AppId={{6AE22A46-D788-43DC-8AA8-B0B2A1826F90}
AppName=VPNRouter Business
AppVersion={#AppVersion}
AppPublisher=NiniTux
DefaultDirName={autopf}\VPNRouterBusiness
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename=VPNRouterBusiness-Setup-v{#AppVersion}
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\app\VPNRouter.App.exe
CloseApplications=no
RestartApplications=no
UsedUserAreasWarning=no
[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
[Files]
Source: "business-README.ru.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\app\*"; DestDir: "{app}\app"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "installer\stop-vpnrouter.ps1"; DestDir: "{app}\installer"; Flags: ignoreversion
Source: "installer\stop-vpnrouter.ps1"; DestDir: "{tmp}"; Flags: dontcopy
[Icons]
Name: "{commonprograms}\VPNRouter Business"; Filename: "{app}\app\VPNRouter.App.exe"; WorkingDir: "{app}\app"
Name: "{autodesktop}\VPNRouter Business"; Filename: "{app}\app\VPNRouter.App.exe"; WorkingDir: "{app}\app"
[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "VPNRouterBusiness"; ValueData: """{app}\app\VPNRouter.App.exe"" --minimized"; Flags: uninsdeletevalue
[Run]
Filename: "{app}\app\VPNRouter.App.exe"; Description: "{cm:LaunchProgram,VPNRouter Business}"; WorkingDir: "{app}\app"; Flags: nowait postinstall skipifsilent runascurrentuser
[Code]
function StopBusiness: Boolean;
var Code: Integer;
begin
  ExtractTemporaryFile('stop-vpnrouter.ps1');
  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{tmp}\stop-vpnrouter.ps1') +
    '" -InstallDir "' + ExpandConstant('{app}') + '" -DataDir "' +
    ExpandConstant('{commonappdata}\VPNRouterBusiness') + '"', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Result := Result and ((Code = 0) or (Code = 10));
end;
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not StopBusiness then Result := 'Close VPNRouter Business and try again.';
end;
function InitializeUninstall: Boolean;
var Code: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\installer\stop-vpnrouter.ps1') +
    '" -InstallDir "' + ExpandConstant('{app}') + '" -DataDir "' +
    ExpandConstant('{commonappdata}\VPNRouterBusiness') + '"', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Result := Result and ((Code = 0) or (Code = 10));
end;
