; Build with Inno Setup 6: ISCC.exe installer\VolumeDock.iss
; Silent isolated package check: /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS="" /DIR="<test-directory>"
; The startup task is selected for normal installs. Silent installs never launch the app.
#define AppName "VolumeDock"
#define AppVersion "1.0.1"
#define AppExe "VolumeDock.exe"

[Setup]
AppId={{2A6E6F1D-26A7-49CB-B948-D991E0E8A460}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=VolumeDock
AppPublisherURL=https://github.com/AndreMontz/VolumeDock
AppSupportURL=https://github.com/AndreMontz/VolumeDock/issues
AppUpdatesURL=https://github.com/AndreMontz/VolumeDock/releases
DefaultDirName={localappdata}\Programs\VolumeDock
DefaultGroupName=VolumeDock
PrivilegesRequired=lowest
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
MinVersion=10.0.17763
DisableProgramGroupPage=yes
DisableDirPage=auto
UsePreviousTasks=yes
LanguageDetectionMethod=uilanguage
ShowLanguageDialog=yes
UsePreviousLanguage=no
CloseApplications=no
RestartApplications=no
RestartIfNeededByRun=no
UninstallDisplayName=VolumeDock
UninstallDisplayIcon={app}\{#AppExe}
OutputDir=..\dist
OutputBaseFilename=VolumeDock-{#AppVersion}-Setup-x64
SetupIconFile=..\assets\VolumeDock.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
VersionInfoVersion={#AppVersion}.0
VersionInfoProductName=VolumeDock
VersionInfoDescription=VolumeDock per-user installer

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "startup"; Description: "{cm:StartupTask}"; GroupDescription: "{cm:StartupGroup}"; Flags: checkedonce

[CustomMessages]
english.StartupTask=Start VolumeDock when I sign in to Windows
english.StartupGroup=Startup:
english.StopShortcut=Stop VolumeDock
english.UninstallShortcut=Uninstall VolumeDock
english.LaunchTask=Start VolumeDock now
english.NetRequired=VolumeDock requires Microsoft .NET Framework 4.8 or later.%nInstall it from https://dotnet.microsoft.com/download/dotnet-framework/net48 and run this installer again.
english.StopFailed=Could not stop VolumeDock: %1
english.StopCommandFailed=The command to stop VolumeDock failed (code %1).
english.StillRunning=VolumeDock is still running. Use the "Stop VolumeDock" shortcut and try again.
english.RunningExeMissing=VolumeDock is running, but its executable could not be found. Stop the application and try again.
brazilianportuguese.StartupTask=Iniciar o VolumeDock ao entrar no Windows
brazilianportuguese.StartupGroup=Inicialização:
brazilianportuguese.StopShortcut=Parar VolumeDock
brazilianportuguese.UninstallShortcut=Desinstalar VolumeDock
brazilianportuguese.LaunchTask=Iniciar o VolumeDock agora
brazilianportuguese.NetRequired=O VolumeDock precisa do Microsoft .NET Framework 4.8 ou posterior.%nInstale-o em https://dotnet.microsoft.com/download/dotnet-framework/net48 e execute este instalador novamente.
brazilianportuguese.StopFailed=Não foi possível encerrar o VolumeDock: %1
brazilianportuguese.StopCommandFailed=O comando para encerrar o VolumeDock falhou (código %1).
brazilianportuguese.StillRunning=O VolumeDock ainda está em execução. Use o atalho "Parar VolumeDock" e tente novamente.
brazilianportuguese.RunningExeMissing=O VolumeDock está em execução, mas seu executável não foi encontrado. Encerre o aplicativo e tente novamente.

[Files]
Source: "..\build\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
; The freshly extracted executable can signal the prior shared IPC endpoint even
; when an installed executable is locked, so upgrades need no process killing.
Source: "..\build\{#AppExe}"; DestDir: "{tmp}"; DestName: "VolumeDock-stop.exe"; Flags: dontcopy
Source: "..\build\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\build\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\build\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\build\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\VolumeDock"; Filename: "{app}\{#AppExe}"; Parameters: "--toggle"; WorkingDir: "{app}"
Name: "{group}\{cm:StopShortcut}"; Filename: "{app}\{#AppExe}"; Parameters: "--quit"; WorkingDir: "{app}"
Name: "{group}\{cm:UninstallShortcut}"; Filename: "{uninstallexe}"
Name: "{userstartup}\VolumeDock"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: startup

[InstallDelete]
; Honor an explicitly deselected startup task during upgrades as well.
Type: files; Name: "{userstartup}\VolumeDock.lnk"; Tasks: not startup; Check: IsCurrentInstallStartupLink

[Run]
Filename: "{app}\{#AppExe}"; Parameters: "{code:GetLaunchParameters}"; Description: "{cm:LaunchTask}"; Flags: postinstall nowait skipifsilent

[Code]
const
  { Kept compatible with the original customized panel for clean migration. }
  PanelMutexes = 'Local\Win10AudioPanel.Instance,Local\VolumeDock.Instance';
  OwnUninstallKey = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{2A6E6F1D-26A7-49CB-B948-D991E0E8A460}_is1';

var
  IsUpgrade: Boolean;

function GetLaunchParameters(Param: String): String;
var
  SelectedAppLanguage, SystemAppLanguage: String;
begin
  { The application keeps its own language preference during upgrades. On the
    first interactive install, preserve automatic language unless the selected
    setup language differs from the language the app derives from Windows.
    Silent installs skip the Run entry and never change app preferences. }
  Result := '';
  if not IsUpgrade then
  begin
    if ActiveLanguage = 'brazilianportuguese' then
      SelectedAppLanguage := 'pt-BR'
    else
      SelectedAppLanguage := 'en';
    if (GetUILanguage and $3FF) = $16 then
      SystemAppLanguage := 'pt-BR'
    else
      SystemAppLanguage := 'en';
    if SelectedAppLanguage <> SystemAppLanguage then
      Result := '--language=' + SelectedAppLanguage;
  end;
end;

function IsCurrentInstallStartupLink: Boolean;
var
  Shell, Shortcut: Variant;
  LinkPath: String;
begin
  Result := False;
  LinkPath := ExpandConstant('{userstartup}\VolumeDock.lnk');
  if not FileExists(LinkPath) then
    Exit;
  try
    Shell := CreateOleObject('WScript.Shell');
    Shortcut := Shell.CreateShortcut(LinkPath);
    Result := CompareText(Shortcut.TargetPath, ExpandConstant('{app}\{#AppExe}')) = 0;
  except
    Log('Could not inspect the existing startup shortcut; leaving it unchanged.');
  end;
end;

function HasNetFramework48: Boolean;
var
  Release: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM64,
    'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release) and
    (Release >= 528040);
end;

function InitializeSetup: Boolean;
begin
  IsUpgrade := RegKeyExists(HKCU64, OwnUninstallKey) or RegKeyExists(HKCU32, OwnUninstallKey);
  Result := HasNetFramework48;
  if not Result then
    SuppressibleMsgBox(CustomMessage('NetRequired'),
      mbError, MB_OK, IDOK);
end;

function StopRunningPanel(const Executable: String): String;
var
  ExitCode, Attempt: Integer;
begin
  Result := '';
  if not CheckForMutexes(PanelMutexes) then
    Exit;
  Log('Stopping VolumeDock through its quit IPC endpoint: ' + Executable);
  if not Exec(Executable, '--quit', '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
  begin
    Result := FmtMessage(CustomMessage('StopFailed'), [SysErrorMessage(ExitCode)]);
    Exit;
  end;
  if ExitCode <> 0 then
  begin
    Result := FmtMessage(CustomMessage('StopCommandFailed'), [IntToStr(ExitCode)]);
    Exit;
  end;
  { Exec waits for the signal-sending process, then separately waits for the
    resident process to release its mutex and remove both low-level hooks. }
  for Attempt := 1 to 100 do
  begin
    if not CheckForMutexes(PanelMutexes) then
      Exit;
    Sleep(50);
  end;
  Result := CustomMessage('StillRunning');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  NeedsRestart := False;
  Result := '';
  if CheckForMutexes(PanelMutexes) then
  begin
    ExtractTemporaryFile('VolumeDock-stop.exe');
    Result := StopRunningPanel(ExpandConstant('{tmp}\VolumeDock-stop.exe'));
  end;
end;

function InitializeUninstall: Boolean;
var
  StopError: String;
begin
  StopError := '';
  if CheckForMutexes(PanelMutexes) then
  begin
    if FileExists(ExpandConstant('{app}\{#AppExe}')) then
      StopError := StopRunningPanel(ExpandConstant('{app}\{#AppExe}'))
    else
      StopError := CustomMessage('RunningExeMissing');
  end;
  Result := StopError = '';
  if not Result then
    SuppressibleMsgBox(StopError, mbError, MB_OK, IDOK);
end;
