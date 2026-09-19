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
Source: "Redist\vc_redist.x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall
Source: "Redist\VulkanRT-Installer.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Icons]
Name: "{group}\StreamBox"; Filename: "{app}\StreamBox.exe"; IconFilename: "{app}\StreamBox.exe"
Name: "{group}\Uninstall StreamBox"; Filename: "{uninstallexe}"
Name: "{autodesktop}\StreamBox"; Filename: "{app}\StreamBox.exe"; IconFilename: "{app}\StreamBox.exe"

[Run]
Filename: "{app}\StreamBox.exe"; Description: "Launch StreamBox now"; Flags: postinstall nowait skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
function VCRedistNeeded(): Boolean;
var
  Installed: Cardinal;
begin
  Result := True;
  if RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64', 'Installed', Installed) then
  begin
    if Installed = 1 then
      Result := False;
  end;
end;

function VulkanRuntimeNeeded(): Boolean;
begin
  Result := not FileExists(ExpandConstant('{sys}\vulkan-1.dll'));
end;

procedure InstallRedistributables();
var
  ResultCode: Integer;
  TempDir: String;
begin
  TempDir := ExpandConstant('{tmp}');

  if VCRedistNeeded() then
  begin
    if not ShellExec('runas', TempDir + '\vc_redist.x64.exe', '/install /quiet /norestart', '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
      MsgBox('Visual C++ Runtime install failed or was cancelled. StreamBox may not run correctly until it''s installed manually.', mbError, MB_OK);
  end;

  if VulkanRuntimeNeeded() then
  begin
    if not ShellExec('runas', TempDir + '\VulkanRT-Installer.exe', '/S', '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
      MsgBox('Vulkan Runtime install failed or was cancelled. StreamBox may not run correctly until it''s installed manually.', mbError, MB_OK);
  end;
end;

// Kill StreamBox.exe before install (in case it's already running).
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssInstall then
  begin
    Exec('taskkill', '/F /IM StreamBox.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
  if CurStep = ssPostInstall then
  begin
    InstallRedistributables();
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
