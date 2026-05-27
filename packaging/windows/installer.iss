; Inno Setup script for Game Master Sound Board
; Compiled by ISCC.exe (Inno Setup Compiler) in CI.
;
; Expected variables (passed via /D on the command line from the workflow):
;   /DAppVersion=1.2.3
;   /DSourceDir=..\..\publish\win-x64        (relative to this .iss file)
;   /DOutputDir=..\..\artifacts              (where the installer .exe lands)

#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\publish\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\artifacts"
#endif

#define AppName       "Game Master Sound Board"
#define AppPublisher  "Game Master Sound Board Project"
#define AppURL        "https://github.com/DevinSanders/game-master-soundboard"
#define AppExeName    "SoundBoard.Desktop.exe"
; AppId is the upgrade key Inno Setup uses to detect previous installs.
; Doubled outer braces produce the literal "{GUID}" form Inno expects.
; This GUID is fixed across releases — changing it will create a
; side-by-side install instead of upgrading. Generated with
; [guid]::NewGuid() before first public release.
#define AppId         "{{6CE80D9F-FB67-4C69-ADAC-0EEBF5B1986A}"

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}/issues
AppUpdatesURL={#AppURL}/releases
DefaultDirName={autopf}\GameMasterSoundBoard
DefaultGroupName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
OutputBaseFilename=GameMasterSoundBoard-Setup-{#AppVersion}
OutputDir={#OutputDir}
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
DisableProgramGroupPage=yes
LicenseFile=..\..\LICENSE
SetupIconFile=..\..\SoundBoard.UI\Assets\app-icon.ico
; SignTool=signpath_signtool       ; Uncomment + configure when SignPath signing is wired up.

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

; Register the gmsound:// URI scheme.
; HKA = HKEY_CURRENT_USER when installing per-user, HKEY_LOCAL_MACHINE when installing for all users.
[Registry]
Root: HKA; Subkey: "Software\Classes\gmsound"; ValueType: string; ValueName: ""; ValueData: "URL:Game Master Sound Board Protocol"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\gmsound"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\gmsound\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExeName},0"
Root: HKA; Subkey: "Software\Classes\gmsound\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
