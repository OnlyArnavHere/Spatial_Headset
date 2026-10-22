; Inno Setup script. Build: iscc /DAppVersion=0.1.0 packaging\installer.iss  (run from Tools\LaptopAgent)
; Per-user install, no admin.
#ifndef AppVersion
  #error Pass the version: iscc /DAppVersion=x.y.z installer.iss
#endif

[Setup]
AppId={{6F0B3C52-5A0E-4D3B-9C61-2E8C1A7D9B14}
AppName=Spatial Launcher Agent
AppVersion={#AppVersion}
AppPublisher=Spatial Launcher
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\Spatial Launcher Agent
DefaultGroupName=Spatial Launcher Agent
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=SpatialLauncherAgent-{#AppVersion}-setup
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\SpatialLauncherAgent.exe

[Files]
Source: "..\dist\SpatialLauncherAgent\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{userprograms}\Spatial Launcher Agent"; Filename: "{app}\SpatialLauncherAgent.exe"

[Run]
Filename: "{app}\SpatialLauncherAgent.exe"; Description: "Start Spatial Launcher Agent"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM SpatialLauncherAgent.exe /F"; Flags: runhidden; RunOnceId: "StopAgent"
Filename: "{sys}\reg.exe"; Parameters: "delete ""HKCU\Software\Microsoft\Windows\CurrentVersion\Run"" /v SpatialLauncherAgent /f"; Flags: runhidden; RunOnceId: "RemoveAutostart"

[Code]
// Stop a running copy so its files can be replaced (an update installed over a running agent).
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM SpatialLauncherAgent.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;
