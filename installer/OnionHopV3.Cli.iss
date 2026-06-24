; Inno Setup script for OnionHop CLI
; Build with:
;   ISCC.exe installer\OnionHopV3.Cli.iss /DMyAppVersion=3.0.0 /DPubDir="..."

#define MyAppName "OnionHop CLI"
#define MyAppExeName "OnionHopV3.Cli.exe"
#define MyAppPublisher "center2055"
#define MyAppURL "https://github.com/center2055/OnionHop"

#ifndef MyAppVersion
  #define MyAppVersion "3.4.2"
#endif

#ifndef PubDir
  #define PubDir "..\\OnionHop\\src\\OnionHopV3.Cli\\bin\\Release\\net9.0\\win-x64\\publish"
#endif

[Setup]
SetupIconFile=..\OnionHop\src\OnionHopV3.App\Assets\OnionHop.ico
AppId={{E8B6A29F-4B6E-45D8-A17E-5BB4679A49F4}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=no
OutputDir=output
OutputBaseFilename=OnionHop-CLI-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
ChangesEnvironment=yes
PrivilegesRequired=lowest
CloseApplications=yes
; Include helper binaries so updates can replace tor/vpn files without scheduling reboot.
CloseApplicationsFilter={#MyAppExeName},tor.exe,sing-box.exe,xray.exe,conjure-client.exe,lyrebird.exe,snowflake-client.exe,webtunnel-client.exe
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked
Name: "addtopath"; Description: "Add OnionHop CLI to PATH (run 'onionhop' from terminal)"; GroupDescription: "Environment:"; Flags: checkedonce

[Files]
Source: "{#PubDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs restartreplace
Source: "onionhop.cmd"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{cmd}"; Parameters: "/K ""{app}\{#MyAppExeName}"""; WorkingDir: "{app}"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{cmd}"; Parameters: "/K ""{app}\{#MyAppExeName}"""; WorkingDir: "{app}"; Tasks: desktopicon

[Code]
function NormalizePathValue(const Value: string): string;
begin
  Result := Uppercase(RemoveBackslashUnlessRoot(Trim(Value)));
end;

function PathContainsValue(const PathValue: string; const Expected: string): Boolean;
var
  Remaining: string;
  Segment: string;
  Delimiter: Integer;
begin
  Result := False;
  Remaining := PathValue;
  while Remaining <> '' do
  begin
    Delimiter := Pos(';', Remaining);
    if Delimiter > 0 then
    begin
      Segment := Copy(Remaining, 1, Delimiter - 1);
      Delete(Remaining, 1, Delimiter);
    end
    else
    begin
      Segment := Remaining;
      Remaining := '';
    end;

    if NormalizePathValue(Segment) = NormalizePathValue(Expected) then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

procedure AddToUserPath(const Value: string);
var
  CurrentPath: string;
begin
  if not RegQueryStringValue(HKCU, 'Environment', 'Path', CurrentPath) then
    CurrentPath := '';

  if PathContainsValue(CurrentPath, Value) then
    Exit;

  if CurrentPath = '' then
    CurrentPath := Value
  else if Copy(CurrentPath, Length(CurrentPath), 1) = ';' then
    CurrentPath := CurrentPath + Value
  else
    CurrentPath := CurrentPath + ';' + Value;

  RegWriteExpandStringValue(HKCU, 'Environment', 'Path', CurrentPath);
end;

procedure RemoveFromUserPath(const Value: string);
var
  CurrentPath: string;
  Remaining: string;
  Segment: string;
  NewPath: string;
  Delimiter: Integer;
begin
  if not RegQueryStringValue(HKCU, 'Environment', 'Path', CurrentPath) then
    Exit;

  Remaining := CurrentPath;
  NewPath := '';

  while Remaining <> '' do
  begin
    Delimiter := Pos(';', Remaining);
    if Delimiter > 0 then
    begin
      Segment := Copy(Remaining, 1, Delimiter - 1);
      Delete(Remaining, 1, Delimiter);
    end
    else
    begin
      Segment := Remaining;
      Remaining := '';
    end;

    if NormalizePathValue(Segment) <> NormalizePathValue(Value) then
    begin
      if NewPath = '' then
        NewPath := Segment
      else
        NewPath := NewPath + ';' + Segment;
    end;
  end;

  RegWriteExpandStringValue(HKCU, 'Environment', 'Path', NewPath);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('addtopath') then
    AddToUserPath(ExpandConstant('{app}'));
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RemoveFromUserPath(ExpandConstant('{app}'));
end;
