; Inno Setup script for OnionHop V2
; Build with:
;   ISCC.exe installer\OnionHopV2.iss /DMyAppVersion=2.4.0 /DPubDir="..."

#define MyAppName "OnionHop V2"
#define MyAppExeName "OnionHopV2.exe"
#define MyAppPublisher "center2055"
#define MyAppURL "https://github.com/center2055/OnionHop"

#ifndef MyAppVersion
  #define MyAppVersion "2.4.0"
#endif

#ifndef PubDir
  #define PubDir "..\\OnionHop\\src\\OnionHopV2.App\\bin\\Release\\net9.0\\win-x64\\publish"
#endif

[Setup]
SetupIconFile=..\OnionHop\src\OnionHopV2.App\Assets\OnionHop.ico
AppId={{6D7E5B7B-2D7E-4F2F-9C2B-8A9B9A3C0C2F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
; Install per-user (matches previous releases), but require elevation so we can reliably close elevated/tray instances during updates.
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=no
OutputDir=output
OutputBaseFilename=OnionHop-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
AppMutex=OnionHopV2.SingleInstance
CloseApplications=yes
RestartApplications=yes
; Include helper binaries so updates can replace tor/vpn files without scheduling reboot.
CloseApplicationsFilter={#MyAppExeName},tor.exe,sing-box.exe,xray.exe,conjure-client.exe,lyrebird.exe,snowflake-client.exe,webtunnel-client.exe
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked

[Files]
Source: "{#PubDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs restartreplace

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
function FindExistingInstallDir(): string;
var
  uninstallKey: string;
  installDir: string;
begin
  Result := '';
  uninstallKey := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{6D7E5B7B-2D7E-4F2F-9C2B-8A9B9A3C0C2F}_is1';

  installDir := '';
  if RegQueryStringValue(HKCU, uninstallKey, 'InstallLocation', installDir) then
  begin
    if installDir <> '' then Result := installDir;
    exit;
  end;
  if RegQueryStringValue(HKCU, uninstallKey, 'Inno Setup: App Path', installDir) then
  begin
    if installDir <> '' then Result := installDir;
    exit;
  end;

  installDir := '';
  if RegQueryStringValue(HKLM, uninstallKey, 'InstallLocation', installDir) then
  begin
    if installDir <> '' then Result := installDir;
    exit;
  end;
  if RegQueryStringValue(HKLM, uninstallKey, 'Inno Setup: App Path', installDir) then
  begin
    if installDir <> '' then Result := installDir;
    exit;
  end;
end;

function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
  existingDir: string;
  existingExe: string;
  i: Integer;
begin
  Result := True;
  try
    existingDir := FindExistingInstallDir();
    if existingDir <> '' then
    begin
      existingExe := AddBackslash(existingDir) + '{#MyAppExeName}';
      if FileExists(existingExe) then
      begin
        Exec(existingExe, '--shutdown-existing', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
        // Give the app a moment to release file locks (tray-hidden instances included).
        for i := 0 to 40 do
        begin
          if not CheckForMutexes('OnionHopV2.SingleInstance') then break;
          Sleep(250);
        end;

        // Last resort: force close (covers old builds that don't implement IPC shutdown or when mutex isn't readable).
        Exec('taskkill', '/im {#MyAppExeName} /t /f', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
        for i := 0 to 20 do
        begin
          if not CheckForMutexes('OnionHopV2.SingleInstance') then break;
          Sleep(250);
        end;
      end;
    end;
  except
    // ignore
  end;
end;
