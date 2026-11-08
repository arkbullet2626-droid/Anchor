; Anchor by Opifex Studios: per-user installer (no admin needed).
; Built by GitHub Actions:  ISCC.exe /DAppVersion=1.2.3 installer\Anchor.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{6C2A7E1B-4F0D-4E9B-9B1E-0A9C3D5F7A21}
AppName=Anchor
AppVersion={#AppVersion}
AppVerName=Anchor {#AppVersion}
AppPublisher=Opifex Studios
AppPublisherURL=https://opifexstudios.ca
AppSupportURL=https://opifexstudios.ca
AppUpdatesURL=https://github.com/arkbullet2626-droid/Anchor/releases
AppCopyright=Copyright (c) 2026 Opifex Studios
VersionInfoVersion={#AppVersion}
VersionInfoCompany=Opifex Studios
VersionInfoDescription=Anchor installer
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=
DefaultDirName={localappdata}\Programs\Anchor
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
DefaultGroupName=Anchor
UninstallDisplayName=Anchor
UninstallDisplayIcon={app}\Anchor.exe
SetupIconFile=..\assets\anchor.ico
WizardStyle=modern
WizardSizePercent=100
OutputDir=..\publish
OutputBaseFilename=AnchorSetup
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
CloseApplications=no
RestartApplications=no
LicenseFile=..\LICENSE

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Messages]
WelcomeLabel2=This will install Anchor [ver] on your PC.%n%nAnchor sits in the tray and puts your windows back where they belong after sleep, monitor changes and game resolution changes. It's free, and nothing leaves your PC.

[Tasks]
Name: "startup"; Description: "Start Anchor when Windows starts (recommended)"

[Files]
Source: "..\publish\Anchor.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\Anchor"; Filename: "{app}\Anchor.exe"; Comment: "Puts your windows back where they belong"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Anchor"; \
  ValueData: """{app}\Anchor.exe"""; Tasks: startup; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "Anchor"; \
  ValueType: none; Flags: uninsdeletevalue

[Run]
; Starts Anchor after installing, also after a silent install (so updates keep it running).
Filename: "{app}\Anchor.exe"; Description: "Start Anchor now"; Flags: nowait postinstall

[UninstallDelete]
Type: dirifempty; Name: "{app}"

[Code]
procedure StopAnchor();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im Anchor.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(500);
end;

// Tells Anchor which "start with Windows" choice was made here; Anchor applies it on its next start.
procedure WriteStartupChoice(Enabled: Boolean);
var
  Folder: String;
begin
  Folder := ExpandConstant('{localappdata}\Opifex\Anchor');
  ForceDirectories(Folder);
  if Enabled then
    SaveStringToFile(Folder + '\installer-startup.txt', 'true', False)
  else
    SaveStringToFile(Folder + '\installer-startup.txt', 'false', False);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopAnchor();
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    WriteStartupChoice(WizardIsTaskSelected('startup'));
end;

function InitializeUninstall(): Boolean;
begin
  StopAnchor();
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // Remove the "start as administrator" task if it was set up (needs admin; Windows asks).
    if Exec(ExpandConstant('{sys}\schtasks.exe'), '/query /tn "Opifex\Anchor"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
       and (ResultCode = 0) then
      ShellExec('runas', ExpandConstant('{sys}\schtasks.exe'), '/delete /tn "Opifex\Anchor" /f', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

    DataDir := ExpandConstant('{localappdata}\Opifex\Anchor');
    if DirExists(DataDir) and not UninstallSilent() then
      if MsgBox('Also delete Anchor''s saved window layouts and settings?', mbConfirmation, MB_YESNO) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
