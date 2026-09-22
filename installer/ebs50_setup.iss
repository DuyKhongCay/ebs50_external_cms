; ==============================================================================
; Inno Setup Script: ebs50_setup.iss
; Application: EBS-50 E-Tag Management & MES Dispatcher System
; Features: Self-Contained .NET 8 Runtime, Native Binaries, Automatic Windows
;           Service Registration, Auto-Recovery, Firewall Port 6789, Desktop Shortcuts.
; ==============================================================================

#define MyAppName "EBS-50 E-Tag Management System"
#define MyAppVersion "1.3.0"
#define MyAppPublisher "Smart Factory / MES Team"
#define MyAppURL "http://localhost:6789"
#define MyAppExeName "ebs50_backend.exe"
#define MyServiceName "Ebs50TagService"
#define MyServiceDisplayName "EBS-50 E-Tag Management Service"
#define MyServicePort "6789"

; Direct compilation must publish current source before reading [Files].
#ifndef AppPublished
  #if Exec('powershell.exe', '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + SourcePath + '..\scripts\Publish-App.ps1"', SourcePath, 1) != 0
    #error Publish failed. Installer compilation aborted.
  #endif
#endif

[Setup]
; Basic Application Details
AppId={{D5B7829A-3481-4C3D-B274-06B7F981D5E2}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}

; Destination Directory and Privileges
DefaultDirName={autopf}\Ebs50TagManagement
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir=..\dist
OutputBaseFilename=Ebs50_Setup_v{#MyAppVersion}
SetupIconFile=..\demonstration.ico
UninstallDisplayIcon={app}\demonstration.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern

; Require full administrator rights to install Windows Service and modify Firewall
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Copy all self-contained publish output files
Source: "..\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "etag_database.db"

; Copy application icon
Source: "..\demonstration.ico"; DestDir: "{app}"; Flags: ignoreversion

; Copy database file ONLY if it does not already exist (Preserve production data on upgrade)
Source: "..\publish\win-x64\etag_database.db"; DestDir: "{app}"; Flags: onlyifdoesntexist uninsneveruninstall

[Icons]
Name: "{group}\{#MyAppName} Dashboard"; Filename: "http://localhost:{#MyServicePort}"; IconFilename: "{app}\demonstration.ico"
Name: "{group}\Swagger MES API Docs"; Filename: "http://localhost:{#MyServicePort}/swagger"; IconFilename: "{app}\demonstration.ico"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "http://localhost:{#MyServicePort}"; Tasks: desktopicon; IconFilename: "{app}\demonstration.ico"

[Run]
; Step 6: Optionally launch browser to open dashboard after installation completes
Filename: "http://localhost:{#MyServicePort}"; Description: "Open Web Dashboard (http://localhost:{#MyServicePort})"; Flags: postinstall shellexec skipifsilent

[UninstallRun]
; Delete the service registration
Filename: "{sys}\sc.exe"; Parameters: "delete {#MyServiceName}"; Flags: runhidden; StatusMsg: "Removing Windows Service..."; RunOnceId: "DeleteEbs50Service"

; Remove Windows Firewall rule
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Ebs50-Web-Network-1"""; Flags: runhidden; RunOnceId: "DeleteNetwork1Firewall"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Ebs50-Web-Network-2"""; Flags: runhidden; RunOnceId: "DeleteNetwork2Firewall"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""EBS-50 E-Tag Service (Port {#MyServicePort})"""; Flags: runhidden; StatusMsg: "Removing Firewall Rule..."; RunOnceId: "DeleteLegacyFirewall"

[Code]
const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{D5B7829A-3481-4C3D-B274-06B7F981D5E2}_is1';
var
  MaintenancePage: TInputOptionWizardPage;
  InstalledVersion, UninstallCommand: String;
  HasInstallation, BlockInstall, SameVersion, ClosingAfterUninstall: Boolean;

function InitializeSetup(): Boolean;
var
  InstalledNumber, PackageNumber: Int64;
begin
  HasInstallation := RegKeyExists(HKLM64, UninstallKey);
  if HasInstallation then
  begin
    RegQueryStringValue(HKLM64, UninstallKey, 'DisplayVersion', InstalledVersion);
    RegQueryStringValue(HKLM64, UninstallKey, 'UninstallString', UninstallCommand);
    // Unknown versions cannot safely be treated as older versions.
    BlockInstall := True;
    if StrToVersion(InstalledVersion, InstalledNumber) and
       StrToVersion('{#MyAppVersion}', PackageNumber) then
    begin
      BlockInstall := InstalledNumber > PackageNumber;
      SameVersion := InstalledNumber = PackageNumber;
    end;
  end;
  Result := True;
  if WizardSilent and BlockInstall then
  begin
    Log('Installation blocked: installed version is newer or cannot be compared.');
    Result := False;
  end;
end;

procedure InitializeWizard();
var
  ActionLabel, Description: String;
begin
  if not HasInstallation then Exit;
  Description := 'Installed version: ' + InstalledVersion + #13#10 +
    'Installer version: {#MyAppVersion}';
  if BlockInstall then
    Description := Description + #13#10 +
      'Downgrade is blocked (newer or unknown installed version). You can uninstall the installed application.';
  MaintenancePage := CreateInputOptionPage(wpWelcome, 'Application maintenance',
    'Choose an action for the installed application.', Description, True, False);
  if SameVersion then
    ActionLabel := 'Reinstall (keep existing database)'
  else
    ActionLabel := 'Update (keep existing database)';
  MaintenancePage.Add(ActionLabel);
  MaintenancePage.Add('Uninstall the installed application');
  MaintenancePage.CheckListBox.ItemEnabled[0] := not BlockInstall;
  if BlockInstall then MaintenancePage.SelectedValueIndex := 1
  else MaintenancePage.SelectedValueIndex := 0;
end;

procedure CancelButtonClick(CurPageID: Integer; var Cancel, Confirm: Boolean);
begin
  if ClosingAfterUninstall then Confirm := False;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  if not HasInstallation then Exit;
  if CurPageID <> MaintenancePage.ID then Exit;
  if MaintenancePage.SelectedValueIndex = 1 then
  begin
    Result := False;
    if UninstallCommand = '' then
    begin
      MsgBox('The installed uninstaller could not be found.', mbError, MB_OK);
      Exit;
    end;
    if not Exec('>', UninstallCommand, '', SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode) then
      MsgBox('Could not launch the uninstaller: ' + SysErrorMessage(ResultCode), mbError, MB_OK)
    else if ResultCode <> 0 then
      MsgBox('Uninstall did not complete. Exit code: ' + IntToStr(ResultCode), mbError, MB_OK)
    else
    begin
      ClosingAfterUninstall := True;
      WizardForm.Close;
    end;
  end
  else if BlockInstall then Result := False;
end;

function StopApplicationService(): String;
var
  ResultCode: Integer;
  Command: String;
begin
  Result := '';
  // WaitForStatus avoids replacing files while the service is still stopping.
  Command := '-NoProfile -NonInteractive -Command "try { ' +
    '$ErrorActionPreference = ''Stop''; ' +
    '$s = Get-Service -ErrorAction Stop | Where-Object { $_.Name -eq ''{#MyServiceName}'' }; ' +
    'if ($s) { if ($s.Status -ne ''Stopped'') { ' +
    '$s.Stop(); ' +
    '$s.WaitForStatus(''Stopped'', [TimeSpan]::FromSeconds(60)) } }; exit 0 ' +
    '} catch { exit 1 }"';
  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      Command, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Result := 'Could not check/stop the application service: ' + SysErrorMessage(ResultCode)
  else if ResultCode <> 0 then
    Result := 'The application service did not stop. Stop it manually and retry.';
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  if BlockInstall then
    Result := 'Installation of an older or unverified version is blocked.'
  else
    Result := StopApplicationService();
end;

function InitializeUninstall(): Boolean;
var
  ErrorMessage: String;
begin
  ErrorMessage := StopApplicationService();
  Result := ErrorMessage = '';
  if not Result then MsgBox(ErrorMessage, mbError, MB_OK);
end;

procedure RunServiceCommand(Parameters: String);
var
  ResultCode: Integer;
begin
  if not Exec(ExpandConstant('{sys}\sc.exe'), Parameters, '', SW_HIDE,
      ewWaitUntilTerminated, ResultCode) then
    RaiseException('Could not configure service: ' + SysErrorMessage(ResultCode));
  if ResultCode <> 0 then
    RaiseException('Service command failed (' + IntToStr(ResultCode) + '): ' + Parameters);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Operation: String;
begin
  if CurStep = ssPostInstall then
  begin
    if RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\{#MyServiceName}') then
      Operation := 'config'
    else Operation := 'create';
    RunServiceCommand(Operation + ' {#MyServiceName} binPath= "\"' +
      ExpandConstant('{app}\{#MyAppExeName}') + '\"" start= auto DisplayName= "{#MyServiceDisplayName}"');
    RunServiceCommand('description {#MyServiceName} "Opticon EBS-50 E-Tag Management & MES Dispatcher Service (Port {#MyServicePort})."');
    RunServiceCommand('failure {#MyServiceName} reset= 86400 actions= restart/60000/restart/60000/restart/60000');
    RunServiceCommand('start {#MyServiceName}');
  end;
end;
// Prompt user during uninstall to preserve or remove database
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DbPath: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DbPath := ExpandConstant('{app}\etag_database.db');
    if FileExists(DbPath) then
    begin
      if MsgBox('Do you want to delete the database file (etag_database.db)?' + #13#10 +
                'Select No to keep your tag configurations and dispatch history.',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      begin
        DeleteFile(DbPath);
      end;
    end;
  end;
end;
