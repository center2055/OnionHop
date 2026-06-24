; Inno Setup script for OnionHop V3
; Build with:
;   ISCC.exe installer\OnionHopV3.iss /DMyAppVersion=3.0.0 /DPubDir="..."

#define MyAppName "OnionHop V3"
#define MyAppExeName "OnionHopV3.exe"
#define V2AppExeName "OnionHopV2.exe"
#define MyAppPublisher "center2055"
#define MyAppURL "https://github.com/center2055/OnionHop"

#ifndef MyAppVersion
  #define MyAppVersion "3.4.2"
#endif

#ifndef PubDir
  #define PubDir "..\\OnionHop\\src\\OnionHopV3.App\\bin\\Release\\net9.0\\win-x64\\publish"
#endif

[Setup]
SetupIconFile=..\OnionHop\src\OnionHopV3.App\Assets\OnionHop.ico
AppId={{A7E27E1E-1598-41B3-B1C2-8A7F2F4F0D33}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
; Install per-user (matches previous releases), but require elevation so we can reliably close elevated/tray instances during updates.
DefaultDirName={localappdata}\Programs\OnionHop V3
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=no
OutputDir=output
OutputBaseFilename=OnionHop-Setup-v3
Compression=lzma2/fast
SolidCompression=no
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
; Do not use AppMutex here: it can make setup report "V3 is running" from stale/dev mutex state.
CloseApplications=yes
RestartApplications=no
RestartIfNeededByRun=no
; Include helper binaries so updates can replace tor/vpn files without scheduling reboot.
CloseApplicationsFilter={#MyAppExeName},{#V2AppExeName},tor.exe,sing-box.exe,xray.exe,conjure-client.exe,lyrebird.exe,snowflake-client.exe,webtunnel-client.exe,snowflake-proxy.exe,artihop.exe,arti.exe
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked

[Files]
Source: "{#PubDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "powershell"; Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Get-ScheduledTask -TaskName 'OnionHop Persistent Admin Helper *' -ErrorAction SilentlyContinue | Stop-ScheduledTask -ErrorAction SilentlyContinue; Get-ScheduledTask -TaskName 'OnionHop Persistent Admin Helper *' -ErrorAction SilentlyContinue | Unregister-ScheduledTask -Confirm:$false -ErrorAction SilentlyContinue"""; Flags: runhidden

[Code]
function FindExistingV3InstallDir(): string;
var
  uninstallKey: string;
  installDir: string;
begin
  Result := '';
  uninstallKey := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{A7E27E1E-1598-41B3-B1C2-8A7F2F4F0D33}_is1';

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

function FindExistingV2InstallDir(): string;
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

function RemoveExistingV2(existingDir: string): Boolean;
var
  resultCode: Integer;
  uninstaller: string;
begin
  Result := True;
  uninstaller := AddBackslash(existingDir) + 'unins000.exe';

  if FileExists(uninstaller) then
  begin
    Result := Exec(uninstaller, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_SHOW, ewWaitUntilTerminated, resultCode);
    if Result then Result := resultCode = 0;
  end;

  if DirExists(existingDir) then
  begin
    if FileExists(AddBackslash(existingDir) + '{#V2AppExeName}') then
    begin
      Result := DelTree(existingDir, True, True, True) and Result;
    end;
  end;
end;

procedure StopExistingV3(existingDir: string);
var
  resultCode: Integer;
  existingExe: string;
  i: Integer;
begin
  existingExe := AddBackslash(existingDir) + '{#MyAppExeName}';
  if FileExists(existingExe) then
  begin
    Exec(existingExe, '--shutdown-existing', '', SW_HIDE, ewWaitUntilTerminated, resultCode);
    for i := 0 to 16 do
    begin
      if not CheckForMutexes('OnionHopV3.SingleInstance') then break;
      Sleep(250);
    end;

    if CheckForMutexes('OnionHopV3.SingleInstance') then
    begin
      Exec('taskkill', '/im {#MyAppExeName} /t /f', '', SW_HIDE, ewWaitUntilTerminated, resultCode);
      for i := 0 to 8 do
      begin
        if not CheckForMutexes('OnionHopV3.SingleInstance') then break;
        Sleep(250);
      end;
    end;
  end;
end;

procedure StopExistingV2(existingDir: string);
var
  resultCode: Integer;
  existingExe: string;
  i: Integer;
begin
  existingExe := AddBackslash(existingDir) + '{#V2AppExeName}';
  if FileExists(existingExe) then
  begin
    Exec(existingExe, '--shutdown-existing', '', SW_HIDE, ewWaitUntilTerminated, resultCode);
    for i := 0 to 16 do
    begin
      if not CheckForMutexes('OnionHopV2.SingleInstance') then break;
      Sleep(250);
    end;

    if CheckForMutexes('OnionHopV2.SingleInstance') then
    begin
      Exec('taskkill', '/im {#V2AppExeName} /t /f', '', SW_HIDE, ewWaitUntilTerminated, resultCode);
      for i := 0 to 8 do
      begin
        if not CheckForMutexes('OnionHopV2.SingleInstance') then break;
        Sleep(250);
      end;
    end;
  end;
end;

function InitializeSetup(): Boolean;
var
  existingV3Dir: string;
  existingV2Dir: string;
  promptResult: Integer;
  resultCode: Integer;
begin
  Result := True;
  try
    // Force-terminate any running OnionHop V3 process (including an elevated/wedged instance) before
    // copying files. Setup runs elevated (PrivilegesRequired=admin), so it can kill an admin instance.
    // Without this, a running app keeps its DLLs (e.g. Avalonia.Base.dll) locked and the install
    // aborts or only partially updates, leaving stale files behind.
    Exec('taskkill', '/F /IM {#MyAppExeName} /T', '', SW_HIDE, ewWaitUntilTerminated, resultCode);
    Sleep(800);

    existingV3Dir := FindExistingV3InstallDir();
    if existingV3Dir <> '' then
    begin
      StopExistingV3(existingV3Dir);
    end;

    existingV2Dir := FindExistingV2InstallDir();
    if existingV2Dir <> '' then
    begin
      promptResult := MsgBox(
        'An existing OnionHop V2 installation was found:' + #13#10 + #13#10 +
        existingV2Dir + #13#10 + #13#10 +
        'Remove OnionHop V2 before installing OnionHop V3?' + #13#10 + #13#10 +
        'Choose Yes to remove V2 first, No to keep it and install V3 side-by-side, or Cancel to stop setup.',
        mbConfirmation,
        MB_YESNOCANCEL);

      if promptResult = IDCANCEL then
      begin
        Result := False;
        exit;
      end;

      StopExistingV2(existingV2Dir);

      if promptResult = IDYES then
      begin
        if not RemoveExistingV2(existingV2Dir) then
        begin
          if MsgBox('OnionHop V2 could not be fully removed. Continue installing OnionHop V3 anyway?', mbError, MB_YESNO) = IDNO then
          begin
            Result := False;
            exit;
          end;
        end;
      end;
    end;
  except
    // ignore
  end;
end;
