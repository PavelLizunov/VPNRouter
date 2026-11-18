function ZapretMaintenance(const ScriptPath, Edition, Operation: string): Boolean;
var Code: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + ScriptPath + '" -Edition ' + Edition +
    ' -Operation ' + Operation, '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
end;

function PrepareZapretUpgrade(const Edition: string): Boolean;
begin
  ExtractTemporaryFile('zapret-maintenance.ps1');
  Result := ZapretMaintenance(ExpandConstant('{tmp}\zapret-maintenance.ps1'), Edition, 'Prepare');
end;

procedure FinishZapretUpgrade;
var Code: Integer;
begin
  if not Exec(ExpandConstant('{app}\app\VPNRouter.App.exe'), '--migrate-zapret',
    ExpandConstant('{app}\app'), SW_HIDE, ewWaitUntilTerminated, Code) or ((Code <> 0) and (Code <> 2)) then
    RaiseException('Could not migrate the Zapret service. Run VPNRouter.App.exe --migrate-zapret as administrator before restarting.');
  if Code = 2 then
    Log('Zapret choice retained. Download Zapret in the DPI Bypass tab to finish migration.');
end;

procedure PurgeZapretData(const Edition, Operation: string);
begin
  if not ZapretMaintenance(ExpandConstant('{app}\app\zapret-maintenance.ps1'), Edition, Operation) then
    Log('Retained the data directory because ownership or deletion could not be verified: ' + Operation);
end;
