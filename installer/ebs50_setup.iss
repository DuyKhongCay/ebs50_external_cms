; ==============================================================================
; Inno Setup Script: ebs50_setup.iss
; Application: EBS-50 E-Tag Management & MES Dispatcher System
; Features: Self-Contained .NET 8 Runtime, Native Binaries, Automatic Windows
;           Service Registration, Auto-Recovery, Firewall Port 6789, Desktop Shortcuts.
; ==============================================================================

#define MyAppName "EBS-50 E-Tag Management System"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Smart Factory / MES Team"
#define MyAppURL "http://localhost:6789"
#define MyAppExeName "ebs50_backend.exe"
#define MyServiceName "Ebs50TagService"
#define MyServiceDisplayName "EBS-50 E-Tag Management Service"
#define MyServicePort "6789"

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
UninstallIconFile=..\demonstration.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern

; Require full administrator rights to install Windows Service and modify Firewall
PrivilegesRequired=admin
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
Source: "..\publish\win-x64\etag_database.db"; DestDir: "{app}"; Flags: onlyifdoesntexist

[Icons]
Name: "{group}\{#MyAppName} Dashboard"; Filename: "http://localhost:{#MyServicePort}"; IconFilename: "{app}\demonstration.ico"
Name: "{group}\Swagger MES API Docs"; Filename: "http://localhost:{#MyServicePort}/swagger"; IconFilename: "{app}\demonstration.ico"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "http://localhost:{#MyServicePort}"; Tasks: desktopicon; IconFilename: "{app}\demonstration.ico"

[Run]
; Step 1: Register Windows Service via sc.exe
Filename: "{sys}\sc.exe"; Parameters: "create {#MyServiceName} binPath= ""{app}\{#MyAppExeName}"" start= auto DisplayName= ""{#MyServiceDisplayName}"""; Flags: runhidden; StatusMsg: "Registering Windows Service..."

; Step 2: Set Service Description
Filename: "{sys}\sc.exe"; Parameters: "description {#MyServiceName} ""Opticon EBS-50 E-Tag Management & MES Dispatcher Service (Port {#MyServicePort})."""; Flags: runhidden; StatusMsg: "Configuring Service Description..."

; Step 3: Configure Service Recovery Policy (Restart on crash after 60 seconds)
Filename: "{sys}\sc.exe"; Parameters: "failure {#MyServiceName} reset= 86400 actions= restart/60000/restart/60000/restart/60000"; Flags: runhidden; StatusMsg: "Configuring Service Auto-Recovery..."

; Step 4: Configure Windows Firewall Rule for Port 6789
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""EBS-50 E-Tag Service (Port {#MyServicePort})"" dir=in action=allow protocol=TCP localport={#MyServicePort} profile=any"; Flags: runhidden; StatusMsg: "Configuring Windows Firewall (Port {#MyServicePort})..."

; Step 5: Start the Windows Service
Filename: "{sys}\sc.exe"; Parameters: "start {#MyServiceName}"; Flags: runhidden; StatusMsg: "Starting Windows Service..."

; Step 6: Optionally launch browser to open dashboard after installation completes
Filename: "http://localhost:{#MyServicePort}"; Description: "Open Web Dashboard (http://localhost:{#MyServicePort})"; Flags: postinstall shellexec skipifsilent

[UninstallRun]
; Stop the service before removal
Filename: "{sys}\sc.exe"; Parameters: "stop {#MyServiceName}"; Flags: runhidden; StatusMsg: "Stopping Windows Service..."

; Wait 2 seconds for service process shutdown
Filename: "{sys}\timeout.exe"; Parameters: "/t 2 /nobreak"; Flags: runhidden

; Delete the service registration
Filename: "{sys}\sc.exe"; Parameters: "delete {#MyServiceName}"; Flags: runhidden; StatusMsg: "Removing Windows Service..."

; Remove Windows Firewall rule
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""EBS-50 E-Tag Service (Port {#MyServicePort})"""; Flags: runhidden; StatusMsg: "Removing Firewall Rule..."

[Code]
// Pre-installation check: stop existing service if running
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#MyServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1500);
  Result := '';
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

