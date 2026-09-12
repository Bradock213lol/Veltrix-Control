#define MyAppName "NexaGrid"
#define MyAppVersion "0.1.0"
#define MyAppPublisher "NexaGrid"
#define MyAppExeName "NexaGrid.Controller.exe"

[Setup]
AppId={{8D46C9A7-A35C-4AF7-9124-296D6339E0F1}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\NexaGrid
DefaultGroupName=NexaGrid
DisableProgramGroupPage=yes
OutputDir=..\outputs
OutputBaseFilename=NexaGridSetup
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
WizardStyle=modern
WizardSizePercent=115
DisableWelcomePage=no
UninstallDisplayName=NexaGrid
VersionInfoVersion={#MyAppVersion}.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=NexaGrid Fleet Management Setup
VersionInfoProductName={#MyAppName}
SetupLogging=yes
CloseApplications=yes
RestartApplications=no

[Files]
Source: "..\artifacts\publish\controller\*"; DestDir: "{app}\Controller"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: InstallController
Source: "..\artifacts\publish\agent\*"; DestDir: "{app}\Agent"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: InstallAgent
Source: "..\artifacts\publish\simulator\*"; DestDir: "{app}\Simulator"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: InstallController
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\CHANGELOG.md"; DestDir: "{app}"; Flags: ignoreversion

[Dirs]
Name: "{commonappdata}\NexaGrid"; Permissions: admins-full system-full; Check: InstallController
Name: "{commonappdata}\NexaGrid\Agent"; Permissions: admins-full system-full; Check: InstallAgent

[Icons]
Name: "{group}\NexaGrid Control"; Filename: "http://localhost:5187"; Check: InstallController
Name: "{autodesktop}\NexaGrid Control"; Filename: "http://localhost:5187"; Tasks: desktopicon; Check: InstallController

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Check: InstallController

[Run]
Filename: "{sys}\sc.exe"; Parameters: "stop NexaGridController"; Flags: runhidden waituntilterminated; Check: InstallController
Filename: "{app}\Controller\NexaGrid.Controller.exe"; Parameters: "--prepare-combined-role"; Flags: runhidden waituntilterminated; Check: InstallBoth
Filename: "{sys}\sc.exe"; Parameters: "create NexaGridController binPath= ""{app}\Controller\NexaGrid.Controller.exe"" start= auto DisplayName= ""NexaGrid Controller"""; Flags: runhidden waituntilterminated; Check: InstallController
Filename: "{sys}\sc.exe"; Parameters: "config NexaGridController binPath= ""{app}\Controller\NexaGrid.Controller.exe"" start= auto DisplayName= ""NexaGrid Controller"""; Flags: runhidden waituntilterminated; Check: InstallController
Filename: "{sys}\sc.exe"; Parameters: "description NexaGridController ""Secure controller for explicitly enrolled NexaGrid devices."""; Flags: runhidden waituntilterminated; Check: InstallController
Filename: "{sys}\sc.exe"; Parameters: "start NexaGridController"; Flags: runhidden waituntilterminated; Check: InstallController
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""NexaGrid Controller HTTPS"" dir=in action=allow protocol=TCP localport=5443"; Flags: runhidden waituntilterminated; Check: InstallController

Filename: "{sys}\sc.exe"; Parameters: "stop NexaGridAgent"; Flags: runhidden waituntilterminated; Check: InstallAgent; BeforeInstall: WriteAgentConfiguration
Filename: "{sys}\sc.exe"; Parameters: "create NexaGridAgent binPath= ""{app}\Agent\NexaGrid.Agent.exe"" start= auto DisplayName= ""NexaGrid Managed Node"""; Flags: runhidden waituntilterminated; Check: InstallAgent
Filename: "{sys}\sc.exe"; Parameters: "config NexaGridAgent binPath= ""{app}\Agent\NexaGrid.Agent.exe"" start= auto DisplayName= ""NexaGrid Managed Node"""; Flags: runhidden waituntilterminated; Check: InstallAgent
Filename: "{sys}\sc.exe"; Parameters: "description NexaGridAgent ""Visible management service for an explicitly authorized NexaGrid node."""; Flags: runhidden waituntilterminated; Check: InstallAgent
Filename: "{sys}\sc.exe"; Parameters: "start NexaGridAgent"; Flags: runhidden waituntilterminated; Check: InstallAgent

Filename: "http://localhost:5187"; Description: "Launch NexaGrid Control"; Flags: shellexec postinstall nowait skipifsilent; Check: InstallController

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop NexaGridAgent"; Flags: runhidden waituntilterminated; RunOnceId: "StopAgent"
Filename: "{sys}\sc.exe"; Parameters: "delete NexaGridAgent"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteAgent"
Filename: "{sys}\sc.exe"; Parameters: "stop NexaGridController"; Flags: runhidden waituntilterminated; RunOnceId: "StopController"
Filename: "{sys}\sc.exe"; Parameters: "delete NexaGridController"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteController"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""NexaGrid Controller HTTPS"""; Flags: runhidden waituntilterminated; RunOnceId: "Firewall"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
var
  RolePage: TWizardPage;
  RoleController: TNewRadioButton;
  RoleAgent: TNewRadioButton;
  RoleBoth: TNewRadioButton;
  NodePage: TInputQueryWizardPage;
  RoleParameter: String;

function InstallController: Boolean;
begin
  Result := RoleController.Checked or RoleBoth.Checked;
end;

function InstallAgent: Boolean;
begin
  Result := RoleAgent.Checked or RoleBoth.Checked;
end;

function InstallBoth: Boolean;
begin
  Result := RoleBoth.Checked;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = NodePage.ID) and not InstallAgent;
end;

procedure AddRoleOption(Page: TWizardPage; var Control: TNewRadioButton; Top: Integer; Title, Detail: String);
var
  Description: TNewStaticText;
begin
  Control := TNewRadioButton.Create(Page);
  Control.Parent := Page.Surface;
  Control.Left := ScaleX(12);
  Control.Top := ScaleY(Top);
  Control.Width := Page.SurfaceWidth - ScaleX(24);
  Control.Height := ScaleY(26);
  Control.Caption := Title;
  Control.Font.Size := 11;
  Control.Font.Style := [fsBold];

  Description := TNewStaticText.Create(Page);
  Description.Parent := Page.Surface;
  Description.Left := ScaleX(37);
  Description.Top := ScaleY(Top + 26);
  Description.Width := Page.SurfaceWidth - ScaleX(55);
  Description.Height := ScaleY(34);
  Description.AutoSize := False;
  Description.WordWrap := True;
  Description.Caption := Detail;
  Description.Font.Color := clGray;
end;

procedure InitializeWizard;
begin
  WizardForm.Caption := 'NexaGrid Secure Setup';
  WizardForm.WelcomeLabel1.Caption := 'Welcome to NexaGrid';
  WizardForm.WelcomeLabel2.Caption := 'One secure installer for your Controller and explicitly authorized Windows nodes.';

  RolePage := CreateCustomPage(wpWelcome, 'Choose this computer''s role', 'Install only the components this device needs.');
  AddRoleOption(RolePage, RoleController, 12, 'Controller', 'Manage enrolled devices from this computer. Includes the web control plane and node simulator.');
  AddRoleOption(RolePage, RoleAgent, 90, 'Managed Node', 'Connect this computer to an existing Controller using a single-use enrollment code.');
  AddRoleOption(RolePage, RoleBoth, 168, 'Controller + Managed Node', 'Manage the fleet and enroll this computer as a managed node.');
  RoleParameter := Lowercase(ExpandConstant('{param:ROLE|controller}'));
  RoleController.Checked := RoleParameter = 'controller';
  RoleAgent.Checked := RoleParameter = 'agent';
  RoleBoth.Checked := RoleParameter = 'both';
  if not (RoleController.Checked or RoleAgent.Checked or RoleBoth.Checked) then
    RoleController.Checked := True;

  NodePage := CreateInputQueryPage(RolePage.ID, 'Connect this managed node', 'Enter the secure enrollment details from your Controller.', 'Verify the certificate fingerprint out of band before continuing.');
  NodePage.Add('Controller URL:', False);
  NodePage.Add('Certificate SHA-256 thumbprint:', False);
  NodePage.Add('Single-use enrollment code:', True);
  NodePage.Add('Managed file root:', False);
  NodePage.Values[0] := ExpandConstant('{param:CONTROLLERURL|https://localhost:5443}');
  NodePage.Values[1] := ExpandConstant('{param:FINGERPRINT|}');
  NodePage.Values[2] := ExpandConstant('{param:TOKEN|}');
  NodePage.Values[3] := ExpandConstant('{param:MANAGEDROOT|}');
  if NodePage.Values[3] = '' then NodePage.Values[3] := ExpandConstant('{commondocs}');
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = NodePage.ID then
  begin
    if RoleBoth.Checked then
    begin
      NodePage.Values[0] := 'https://localhost:5443';
      NodePage.Values[1] := '(created automatically)';
      NodePage.Values[2] := '(created automatically)';
      NodePage.Edits[0].Enabled := False;
      NodePage.Edits[1].Enabled := False;
      NodePage.Edits[2].Enabled := False;
    end
    else
    begin
      if NodePage.Values[1] = '(created automatically)' then NodePage.Values[1] := '';
      if NodePage.Values[2] = '(created automatically)' then NodePage.Values[2] := '';
      NodePage.Edits[0].Enabled := True;
      NodePage.Edits[1].Enabled := True;
      NodePage.Edits[2].Enabled := True;
    end;
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = RolePage.ID then
  begin
    Result := RoleController.Checked or RoleAgent.Checked or RoleBoth.Checked;
    if not Result then MsgBox('Select a role to continue.', mbError, MB_OK);
  end
  else if (CurPageID = NodePage.ID) and InstallAgent then
  begin
    if RoleBoth.Checked then
    begin
      NodePage.Values[0] := 'https://localhost:5443';
      NodePage.Values[1] := '';
    end;
    Result := (Pos('https://', Lowercase(NodePage.Values[0])) = 1) and
      (RoleBoth.Checked or (Length(Trim(NodePage.Values[1])) >= 40)) and
      (RoleBoth.Checked or (Length(Trim(NodePage.Values[2])) >= 16)) and
      (Length(Trim(NodePage.Values[3])) > 2);
    if not Result then
      MsgBox('Use an HTTPS Controller URL and enter the certificate thumbprint, enrollment code, and managed root.', mbError, MB_OK);
  end;
end;

function JsonEscape(Value: String): String;
begin
  Result := Value;
  StringChangeEx(Result, '\', '\\', True);
  StringChangeEx(Result, '"', '\"', True);
end;

procedure WriteAgentConfiguration;
var
  Config: String;
  AgentData: String;
  ControllerAddress: String;
  Fingerprint: String;
  FingerprintFile: AnsiString;
  Retry: Integer;
begin
  AgentData := ExpandConstant('{commonappdata}\NexaGrid\Agent');
  ControllerAddress := Trim(NodePage.Values[0]);
  Fingerprint := Trim(NodePage.Values[1]);
  if RoleBoth.Checked then
  begin
    ControllerAddress := 'https://localhost:5443';
    Fingerprint := '';
    for Retry := 1 to 100 do
    begin
      if LoadStringFromFile(ExpandConstant('{commonappdata}\NexaGrid\controller-certificate.sha256'), FingerprintFile) then
      begin
        Fingerprint := FingerprintFile;
        Break;
      end;
      Sleep(100);
    end;
    Fingerprint := Trim(Fingerprint);
    if Fingerprint = '' then
      RaiseException('The Controller certificate fingerprint was not created. Review the Controller service log and run repair.');
  end;
  Config := '{' + #13#10 +
      '  "Agent": {' + #13#10 +
      '    "ControllerUrl": "' + JsonEscape(ControllerAddress) + '",' + #13#10 +
      '    "ControllerCertificateThumbprint": "' + JsonEscape(Fingerprint) + '",' + #13#10 +
      '    "DataDirectory": "' + JsonEscape(AgentData) + '",' + #13#10 +
      '    "EnrollmentCodeFile": "enrollment-code.txt",' + #13#10 +
      '    "ManagedRoot": "' + JsonEscape(Trim(NodePage.Values[3])) + '",' + #13#10 +
      '    "AllowPowerActions": false,' + #13#10 +
      '    "AllowInsecureLoopback": false' + #13#10 +
      '  }' + #13#10 +
      '}';
  SaveStringToFile(ExpandConstant('{app}\Agent\appsettings.json'), Config, False);
  if not RoleBoth.Checked then
    SaveStringToFile(AgentData + '\enrollment-code.txt', Trim(NodePage.Values[2]), False);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop NexaGridAgent', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop NexaGridController', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;
