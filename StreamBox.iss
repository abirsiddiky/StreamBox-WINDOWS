[Setup]
AppName=StreamBox
AppVersion=1.0.0
AppPublisher=StreamBox
DefaultDirName={autopf}\StreamBox
DefaultGroupName=StreamBox
OutputDir=Output
OutputBaseFilename=StreamBox-Setup.exe
Compression=lzma2
SolidCompression=yes
SetupIconFile=Assets\app-icon.ico
UninstallDisplayIcon={app}\StreamBox.exe
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DisableProgramGroupPage=yes
DisableWelcomePage=yes
DisableReadyPage=no
DisableDirPage=no
MinVersion=10.0.17763

[Files]
Source: "bin\Release\net8.0\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\StreamBox"; Filename: "{app}\StreamBox.exe"; IconFilename: "{app}\StreamBox.exe"
Name: "{group}\Uninstall StreamBox"; Filename: "{uninstallexe}"
Name: "{autodesktop}\StreamBox"; Filename: "{app}\StreamBox.exe"; IconFilename: "{app}\StreamBox.exe"

[Run]
Filename: "{app}\StreamBox.exe"; Description: "Launch StreamBox now"; Flags: postinstall nowait skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
// Kill StreamBox.exe before install (in case it's already running).
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssInstall then
  begin
    Exec('taskkill', '/F /IM StreamBox.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

// Kill StreamBox.exe before uninstall (previous bug: uninstaller couldn't delete locked files).
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    Exec('taskkill', '/F /IM StreamBox.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;
