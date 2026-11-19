; Keep AppId stable so upgrades reuse the installation and its uninstall entry.
#ifndef AppId
  #define AppId "{E4C90D35-3DD8-4CC6-A5D4-045E384DAE26}"
#endif
#ifndef AppName
  #define AppName "Pulse Battery"
#endif
#ifndef StartupValueName
  #define StartupValueName "PulseBattery"
#endif
#ifndef AppMutex
  #define AppMutex "Local\PulseBattery.Instance"
#endif
#ifndef AppVersion
  #define AppVersion "0.1.5"
#endif
#ifndef AppSource
  #define AppSource "..\artifacts\portable\win-x64\PulseBattery.exe"
#endif
#ifndef InstallerOutput
  #define InstallerOutput "..\artifacts\installer"
#endif
#ifndef OutputName
  #define OutputName "PulseBattery-Setup"
#endif

[Setup]
AppId={{#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Pulse Battery
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
DisableDirPage=no
DisableWelcomePage=yes
DisableReadyPage=yes
DisableFinishedPage=yes
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
Uninstallable=yes
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\PulseBattery.exe
AppMutex={#AppMutex}
SetupMutex=PulseBattery.Setup
CloseApplications=yes
RestartApplications=no
WizardStyle=modern
SetupIconFile=..\src\PulseBattery.App\Assets\Pulse.ico
OutputDir={#InstallerOutput}
OutputBaseFilename={#OutputName}
Compression=lzma2
SolidCompression=yes

[Languages]
Name: "ukrainian"; MessagesFile: "compiler:Languages\Ukrainian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
ukrainian.InstallTitle=Встановлення Pulse Battery
ukrainian.InstallDescription=Вибери папку та параметри встановлення.
ukrainian.InstallExplanation=Програму буде встановлено в цю папку. Щоб вибрати іншу, натисни «Огляд».
ukrainian.InstallHint=Вибери потрібні параметри нижче та натисни «Встановити».
ukrainian.StartupCheckbox=Запускати Pulse Battery разом із Windows
ukrainian.LaunchCheckbox=Відкрити Pulse Battery після встановлення
english.InstallTitle=Install Pulse Battery
english.InstallDescription=Choose the installation folder and options.
english.InstallExplanation=The app will be installed in this folder. Click Browse to choose a different location.
english.InstallHint=Choose the options below, then click Install.
english.StartupCheckbox=Start Pulse Battery with Windows
english.LaunchCheckbox=Open Pulse Battery after installation

[Files]
Source: "{#AppSource}"; DestDir: "{app}"; Flags: ignoreversion

[Registry]
Root: HKCU; Subkey: "Software\PulseBattery\InstallerStartup"; ValueType: dword; ValueName: "{app}\PulseBattery.exe"; ValueData: "{code:GetStartupValue}"; Flags: uninsdeletevalue uninsdeletekeyifempty
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#StartupValueName}"; ValueData: """{app}\PulseBattery.exe"" --autostart"; Check: StartupEnabled
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "{#StartupValueName}"; Flags: deletevalue; Check: not StartupEnabled

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\PulseBattery.exe"; Parameters: "--dashboard"; WorkingDir: "{app}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\PulseBattery.exe"; Parameters: "--dashboard"; WorkingDir: "{app}"; Check: DesktopIconEnabled

[Run]
Filename: "{app}\PulseBattery.exe"; Parameters: "--dashboard"; Flags: nowait skipifsilent; Check: LaunchEnabled

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\PulseBattery\DeviceBackend"

[Code]
var
  StartupCheckbox, DesktopCheckbox, LaunchCheckbox: TNewCheckBox;

function AddInstallCheckbox(Caption: String; Top: Integer): TNewCheckBox;
begin
  Result := TNewCheckBox.Create(WizardForm);
  Result.Parent := WizardForm.SelectDirPage;
  Result.Left := WizardForm.DirEdit.Left;
  Result.Top := Top;
  Result.Width := WizardForm.SelectDirPage.ClientWidth - Result.Left;
  Result.Height := ScaleY(22);
  Result.Caption := Caption;
end;

procedure InitializeWizard;
var
  StartupCommand, StartupParameter, DesktopParameter: String;
begin
  WizardForm.SelectDirLabel.Caption := CustomMessage('InstallExplanation');
  WizardForm.SelectDirBrowseLabel.Caption := CustomMessage('InstallHint');
  StartupCheckbox := AddInstallCheckbox(CustomMessage('StartupCheckbox'),
    WizardForm.DirEdit.Top + WizardForm.DirEdit.Height + ScaleY(28));
  StartupCheckbox.Checked := True;
  { Preserve an existing installation's explicit choice during upgrades. }
  if RegKeyExists(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#AppId}_is1') then
    StartupCheckbox.Checked := RegQueryStringValue(HKCU,
      'Software\Microsoft\Windows\CurrentVersion\Run', '{#StartupValueName}', StartupCommand);
  DesktopCheckbox := AddInstallCheckbox(CustomMessage('CreateDesktopIcon'), StartupCheckbox.Top + ScaleY(30));
  DesktopCheckbox.Checked := FileExists(ExpandConstant('{autodesktop}\{#AppName}.lnk'));
  LaunchCheckbox := AddInstallCheckbox(CustomMessage('LaunchCheckbox'), DesktopCheckbox.Top + ScaleY(30));
  LaunchCheckbox.Checked := True;
  { Silent deployment can explicitly choose /STARTUP=yes or /STARTUP=no. }
  StartupParameter := ExpandConstant('{param:STARTUP|}');
  if CompareText(StartupParameter, 'yes') = 0 then StartupCheckbox.Checked := True;
  if CompareText(StartupParameter, 'no') = 0 then StartupCheckbox.Checked := False;
  DesktopParameter := ExpandConstant('{param:DESKTOP|}');
  if CompareText(DesktopParameter, 'yes') = 0 then DesktopCheckbox.Checked := True;
  if CompareText(DesktopParameter, 'no') = 0 then DesktopCheckbox.Checked := False;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpSelectDir then
  begin
    WizardForm.PageNameLabel.Caption := CustomMessage('InstallTitle');
    WizardForm.PageDescriptionLabel.Caption := CustomMessage('InstallDescription');
    WizardForm.NextButton.Caption := SetupMessage(msgButtonInstall);
  end;
end;

function StartupEnabled: Boolean;
begin
  Result := StartupCheckbox.Checked;
end;

function DesktopIconEnabled: Boolean;
begin
  Result := DesktopCheckbox.Checked;
end;

function LaunchEnabled: Boolean;
begin
  Result := LaunchCheckbox.Checked;
end;

function GetStartupValue(Param: String): String;
begin
  if StartupEnabled then Result := '1' else Result := '0';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  StartupCommand: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    { Only remove startup if it still belongs to this installation. }
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run',
      '{#StartupValueName}', StartupCommand) then
      if CompareText(StartupCommand, '"' + ExpandConstant('{app}\PulseBattery.exe') + '" --autostart') = 0 then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', '{#StartupValueName}');
  end;
end;
