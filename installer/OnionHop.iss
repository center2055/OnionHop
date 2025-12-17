; Inno Setup script for OnionHop
; Build with:
;   ISCC.exe installer\OnionHop.iss /DMyAppVersion=1.0.0 /DPubDir="..."

#define MyAppName "OnionHop"
#define MyAppPublisher "center2055"
#define MyAppURL "https://github.com/center2055/OnionHop"

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

#ifndef PubDir
  #define PubDir "..\\OnionHop\\bin\\Release\\net9.0-windows\\win-x64\\publish"
#endif

[Setup]
AppId={{A3C8E2B1-0A70-4F41-8F21-5A9D5A9F0E27}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=output
OutputBaseFilename={#MyAppName}-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=lowest
UninstallDisplayIcon={app}\\OnionHop.exe

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked

[Files]
Source: "{#PubDir}\\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\\{#MyAppName}"; Filename: "{app}\\OnionHop.exe"
Name: "{userdesktop}\\{#MyAppName}"; Filename: "{app}\\OnionHop.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\\OnionHop.exe"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
