#define MyAppName "Veltrix-Control"
#define MyAppVersion "0.10.6"
#define MyAppPublisher "Veltrix-Control"
#define MyAppExeName "Veltrix-Control.Controller.exe"

[Setup]
AppId={{8D46C9A7-A35C-4AF7-9124-296D6339E0F1}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\Veltrix-Control
DefaultGroupName=Veltrix-Control
DisableProgramGroupPage=yes
OutputDir=..\outputs
OutputBaseFilename=Veltrix-Control-Setup
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
WizardStyle=modern
WizardSizePercent=115
DisableWelcomePage=no
UninstallDisplayName=Veltrix-Control
VersionInfoVersion={#MyAppVersion}.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=Veltrix-Control Fleet Management Setup
VersionInfoProductName={#MyAppName}
SetupLogging=yes
CloseApplications=yes
RestartApplications=no

[Files]
Source: "..\artifacts\publish\controller\*"; DestDir: "{app}\Controller"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: InstallController
Source: "..\artifacts\publish\agent\*"; DestDir: "{app}\Agent"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: InstallAgent
Source: "..\artifacts\publish\simulator\*"; DestDir: "{app}\Simulator"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: InstallController
Source: "..\artifacts\publish\desktop\*"; DestDir: "{app}\Desktop"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: InstallController
Source: "..\artifacts\publish\launcher\*"; DestDir: "{app}\Launcher"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: InstallController
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\CHANGELOG.md"; DestDir: "{app}"; Flags: ignoreversion

[Dirs]
Name: "{commonappdata}\Veltrix-Control"; Permissions: admins-full system-full; Check: InstallController
Name: "{commonappdata}\Veltrix-Control\Agent"; Permissions: admins-full system-full; Check: InstallAgent

[Icons]
Name: "{group}\Veltrix-Control"; Filename: "{app}\Launcher\Veltrix-Control.exe"; WorkingDir: "{app}\Launcher"; Check: InstallController
Name: "{autodesktop}\Veltrix-Control"; Filename: "{app}\Launcher\Veltrix-Control.exe"; WorkingDir: "{app}\Launcher"; Tasks: desktopicon; Check: InstallController

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Check: InstallController

[Run]
Filename: "{sys}\sc.exe"; Parameters: "stop Veltrix-Control-Controller"; Flags: runhidden waituntilterminated; Check: InstallController
Filename: "{app}\Controller\Veltrix-Control.Controller.exe"; Parameters: "--prepare-combined-role"; Flags: runhidden waituntilterminated; Check: InstallBoth
Filename: "{sys}\sc.exe"; Parameters: "create Veltrix-Control-Controller binPath= ""{app}\Controller\Veltrix-Control.Controller.exe"" start= auto DisplayName= ""Veltrix-Control Controller"""; Flags: runhidden waituntilterminated; Check: InstallController
Filename: "{sys}\sc.exe"; Parameters: "config Veltrix-Control-Controller binPath= ""{app}\Controller\Veltrix-Control.Controller.exe"" start= auto DisplayName= ""Veltrix-Control Controller"""; Flags: runhidden waituntilterminated; Check: InstallController
Filename: "{sys}\sc.exe"; Parameters: "description Veltrix-Control-Controller ""Secure controller for explicitly enrolled Veltrix-Control devices."""; Flags: runhidden waituntilterminated; Check: InstallController
Filename: "{sys}\sc.exe"; Parameters: "start Veltrix-Control-Controller"; Flags: runhidden waituntilterminated; Check: InstallController
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""Veltrix-Control Controller HTTPS"" dir=in action=allow protocol=TCP localport=5443"; Flags: runhidden waituntilterminated; Check: InstallController

Filename: "{sys}\sc.exe"; Parameters: "stop Veltrix-Control-Agent"; Flags: runhidden waituntilterminated; Check: InstallAgent; BeforeInstall: WriteAgentConfiguration
Filename: "{sys}\sc.exe"; Parameters: "create Veltrix-Control-Agent binPath= ""{app}\Agent\Veltrix-Control.Agent.exe"" start= auto DisplayName= ""Veltrix-Control Managed Node"""; Flags: runhidden waituntilterminated; Check: InstallAgent
Filename: "{sys}\sc.exe"; Parameters: "config Veltrix-Control-Agent binPath= ""{app}\Agent\Veltrix-Control.Agent.exe"" start= auto DisplayName= ""Veltrix-Control Managed Node"""; Flags: runhidden waituntilterminated; Check: InstallAgent
Filename: "{sys}\sc.exe"; Parameters: "description Veltrix-Control-Agent ""Visible management service for an explicitly authorized Veltrix-Control node."""; Flags: runhidden waituntilterminated; Check: InstallAgent
Filename: "{sys}\sc.exe"; Parameters: "start Veltrix-Control-Agent"; Flags: runhidden waituntilterminated; Check: InstallAgent

Filename: "{app}\Launcher\Veltrix-Control.exe"; Description: "Launch Veltrix-Control"; Flags: postinstall nowait skipifsilent; Check: InstallController

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop Veltrix-Control-Agent"; Flags: runhidden waituntilterminated; RunOnceId: "StopAgent"
Filename: "{sys}\sc.exe"; Parameters: "delete Veltrix-Control-Agent"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteAgent"
Filename: "{sys}\sc.exe"; Parameters: "stop Veltrix-Control-Controller"; Flags: runhidden waituntilterminated; RunOnceId: "StopController"
Filename: "{sys}\sc.exe"; Parameters: "delete Veltrix-Control-Controller"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteController"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Veltrix-Control Controller HTTPS"""; Flags: runhidden waituntilterminated; RunOnceId: "Firewall"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
[Code]
const
  CarbonWindow = $100E0C;
  CarbonSurface = $171512;
  CarbonCard = $201D19;
  CarbonBorder = $312E28;
  CarbonText = $EFF1ED;
  CarbonMuted = $9D9F95;
  CarbonAccent = $BDE58D;

var
  RolePage: TWizardPage;
  RoleController: TNewRadioButton;
  RoleAgent: TNewRadioButton;
  RoleBoth: TNewRadioButton;
  RoleCards: array[0..2] of TPanel;
  RoleAccents: array[0..2] of TPanel;
  RoleRadios: array[0..2] of TNewRadioButton;
  NodePage: TInputQueryWizardPage;
  RoleParameter: String;

procedure UpdateRoleAccents;
var
  Index: Integer;
begin
  for Index := 0 to 2 do
  begin
    if RoleRadios[Index] = nil then Continue;
    if RoleRadios[Index].Checked then
      RoleAccents[Index].Color := CarbonAccent
    else
      RoleAccents[Index].Color := CarbonBorder;
  end;
end;

procedure RoleCardClick(Sender: TObject);
begin
  UpdateRoleAccents;
end;

procedure ApplyCarbonTheme;
begin
  WizardForm.Color := CarbonWindow;
  WizardForm.Font.Name := 'Segoe UI';
  WizardForm.Font.Color := CarbonText;
  WizardForm.MainPanel.Color := CarbonSurface;
  WizardForm.WelcomePage.Color := CarbonSurface;
  WizardForm.InnerPage.Color := CarbonSurface;
  WizardForm.InstallingPage.Color := CarbonSurface;
  WizardForm.FinishedPage.Color := CarbonSurface;
  WizardForm.Bevel.Visible := False;
  WizardForm.Bevel1.Visible := False;
  WizardForm.WelcomeLabel1.Font.Color := CarbonText;
  WizardForm.WelcomeLabel2.Font.Color := CarbonMuted;
  WizardForm.PageNameLabel.Font.Color := CarbonAccent;
  WizardForm.PageNameLabel.Font.Style := [fsBold];
  WizardForm.PageDescriptionLabel.Font.Color := CarbonMuted;
  WizardForm.FinishedHeadingLabel.Font.Color := CarbonText;
  WizardForm.FinishedLabel.Font.Color := CarbonMuted;
  WizardForm.StatusLabel.Font.Color := CarbonMuted;
  WizardForm.NextButton.Font.Color := CarbonAccent;
  WizardForm.NextButton.Font.Style := [fsBold];
  WizardForm.BackButton.Font.Color := CarbonMuted;
  WizardForm.CancelButton.Font.Color := CarbonMuted;
end;

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

procedure AddRoleOption(Page: TWizardPage; var Control: TNewRadioButton; Index, Top: Integer; Title, Detail: String);
var
  Card, Accent: TPanel;
  Description: TNewStaticText;
begin
  Card := TPanel.Create(Page);
  Card.Parent := Page.Surface;
  Card.Left := ScaleX(8);
  Card.Top := ScaleY(Top);
  Card.Width := Page.SurfaceWidth - ScaleX(16);
  Card.Height := ScaleY(72);
  Card.BevelOuter := bvNone;
  Card.Color := CarbonCard;
  Card.ParentBackground := False;
  Card.OnClick := @RoleCardClick;
  RoleCards[Index] := Card;

  Accent := TPanel.Create(Page);
  Accent.Parent := Card;
  Accent.Left := 0;
  Accent.Top := 0;
  Accent.Width := ScaleX(3);
  Accent.Height := Card.Height;
  Accent.BevelOuter := bvNone;
  Accent.Color := CarbonBorder;
  RoleAccents[Index] := Accent;

  Control := TNewRadioButton.Create(Page);
  Control.Parent := Card;
  Control.Left := ScaleX(18);
  Control.Top := ScaleY(12);
  Control.Width := Card.Width - ScaleX(30);
  Control.Height := ScaleY(22);
  Control.Caption := Title;
  Control.Font.Size := 11;
  Control.Font.Style := [fsBold];
  Control.Font.Color := CarbonText;
  Control.OnClick := @RoleCardClick;
  RoleRadios[Index] := Control;

  Description := TNewStaticText.Create(Page);
  Description.Parent := Card;
  Description.Left := ScaleX(40);
  Description.Top := ScaleY(38);
  Description.Width := Card.Width - ScaleX(58);
  Description.Height := ScaleY(30);
  Description.AutoSize := False;
  Description.WordWrap := True;
  Description.Caption := Detail;
  Description.Font.Color := CarbonMuted;
end;

procedure InitializeWizard;
begin
  ApplyCarbonTheme;
  WizardForm.Caption := 'Veltrix-Control Secure Setup';
  WizardForm.WelcomeLabel1.Caption := 'Welcome to Veltrix-Control';
  WizardForm.WelcomeLabel2.Caption := 'One secure installer for your Controller and explicitly authorized Windows nodes.';

  RolePage := CreateCustomPage(wpWelcome, 'Choose this computer''s role', 'Install only the components this device needs.');
  AddRoleOption(RolePage, RoleController, 0, 12, 'Controller', 'Manage your devices in the native desktop app. Includes a browser fallback and node simulator.');
  AddRoleOption(RolePage, RoleAgent, 1, 100, 'Managed Node', 'Connect this computer to an existing Controller with a single-use enrollment code.');
  AddRoleOption(RolePage, RoleBoth, 2, 188, 'Controller + Managed Node', 'Manage your fleet and enroll this computer as a local managed node.');
  RoleParameter := Lowercase(ExpandConstant('{param:ROLE|controller}'));
  RoleController.Checked := RoleParameter = 'controller';
  RoleAgent.Checked := RoleParameter = 'agent';
  RoleBoth.Checked := RoleParameter = 'both';
  if not (RoleController.Checked or RoleAgent.Checked or RoleBoth.Checked) then
    RoleController.Checked := True;
  UpdateRoleAccents;

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
  if CurPageID = RolePage.ID then
    UpdateRoleAccents;
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
  AgentData := ExpandConstant('{commonappdata}\Veltrix-Control\Agent');
  ControllerAddress := Trim(NodePage.Values[0]);
  Fingerprint := Trim(NodePage.Values[1]);
  if RoleBoth.Checked then
  begin
    ControllerAddress := 'https://localhost:5443';
    Fingerprint := '';
    for Retry := 1 to 100 do
    begin
      if LoadStringFromFile(ExpandConstant('{commonappdata}\Veltrix-Control\controller-certificate.sha256'), FingerprintFile) then
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
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop Veltrix-Control-Agent', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop Veltrix-Control-Controller', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  // Interactive uninstall removes everything this product created: the database with
  // user accounts, certificates, node identity, transfers, desktop settings, and logs.
  // Silent uninstalls (used by upgrades and repair) keep the data so fleet state survives.
  if (CurUninstallStep = usPostUninstall) and (not UninstallSilent) then
  begin
    DelTree(ExpandConstant('{commonappdata}\Veltrix-Control'), True, True, True);
    DelTree(ExpandConstant('{localappdata}\Veltrix-Control'), True, True, True);
  end;
end;
