#define MyAppName "LinkForge 视联网调试工具箱"
#define MyAppShortName "LinkForge"
#define MyAppPublisher "CiallorZ"
#define MyAppUrl "https://github.com/CiallorZ/ChinaTower-Tool"
#define PayloadRoot GetEnv("LINKFORGE_PAYLOAD_ROOT")
#define MyAppVersion GetEnv("LINKFORGE_VERSION")
#define MyFileVersion GetEnv("LINKFORGE_FILE_VERSION")
#define MyAppCommit GetEnv("LINKFORGE_COMMIT")
#define OutputDir GetEnv("LINKFORGE_OUTPUT_DIR")

#if PayloadRoot == ""
  #error LINKFORGE_PAYLOAD_ROOT is required.
#endif

#if MyAppVersion == ""
  #error LINKFORGE_VERSION is required.
#endif

#if MyFileVersion == ""
  #error LINKFORGE_FILE_VERSION is required.
#endif

#if OutputDir == ""
  #error LINKFORGE_OUTPUT_DIR is required.
#endif

[Setup]
AppId={{7D79330C-0D1B-49B9-9A5A-A99A31A18A5B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppUrl}
AppSupportURL={#MyAppUrl}/issues
AppUpdatesURL={#MyAppUrl}/releases
VersionInfoVersion={#MyFileVersion}
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyFileVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=LinkForge 视联网全链路调试工具箱安装程序
VersionInfoCopyright=Copyright (C) 2026 CiallorZ
DefaultDirName={localappdata}\Programs\{#MyAppShortName}
DefaultGroupName={#MyAppShortName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
UsePreviousAppDir=yes
AllowNoIcons=yes
OutputDir={#OutputDir}
OutputBaseFilename={#MyAppShortName}-Setup-{#MyAppVersion}-{#MyAppCommit}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
CloseApplications=yes
RestartApplications=no
RestartIfNeededByRun=no
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\LinkForge.App.exe
MinVersion=10.0
ArchitecturesAllowed=x86compatible

[Languages]
Name: "chinesesimplified"; MessagesFile: "Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式："; Flags: unchecked

[Files]
Source: "{#PayloadRoot}\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: IsX64Install
Source: "{#PayloadRoot}\win-x86\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: not IsX64Install

[Icons]
Name: "{group}\{#MyAppShortName}"; Filename: "{app}\LinkForge.App.exe"; WorkingDir: "{app}"
Name: "{group}\卸载 {#MyAppShortName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppShortName}"; Filename: "{app}\LinkForge.App.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\LinkForge.App.exe"; Description: "启动 {#MyAppShortName}"; Flags: nowait postinstall skipifsilent

[Code]
function IsX64Install: Boolean;
begin
  Result := IsWin64;
end;
