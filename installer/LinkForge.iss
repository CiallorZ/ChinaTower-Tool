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
var
  UpgradeBackupDirectory: String;
  UpgradeBackupCreated: Boolean;
  UpgradeInstallSucceeded: Boolean;
  UpgradeRestoreSucceeded: Boolean;

function IsDirectoryEntry(const FindRec: TFindRec): Boolean;
begin
  Result := (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0;
end;

function CopyDirectoryTree(const SourceDir, DestinationDir: String): Boolean;
var
  FindRec: TFindRec;
  SourcePath: String;
  DestinationPath: String;
begin
  Result := True;

  if not DirExists(SourceDir) then
    Exit;

  if not ForceDirectories(DestinationDir) then
  begin
    Result := False;
    Exit;
  end;

  if FindFirst(AddBackslash(SourceDir) + '*', FindRec) then
  begin
    repeat
      if (FindRec.Name <> '.') and (FindRec.Name <> '..') then
      begin
        SourcePath := AddBackslash(SourceDir) + FindRec.Name;
        DestinationPath := AddBackslash(DestinationDir) + FindRec.Name;

        if IsDirectoryEntry(FindRec) then
        begin
          if not CopyDirectoryTree(SourcePath, DestinationPath) then
            Result := False;
        end
        else
        begin
          if not FileCopy(SourcePath, DestinationPath, False) then
            Result := False;
        end;
      end;
    until not FindNext(FindRec);
    FindClose(FindRec);
  end;
end;

function DeleteDirectoryTree(const Directory: String): Boolean;
var
  FindRec: TFindRec;
  EntryPath: String;
begin
  Result := True;

  if not DirExists(Directory) then
    Exit;

  if FindFirst(AddBackslash(Directory) + '*', FindRec) then
  begin
    repeat
      if (FindRec.Name <> '.') and (FindRec.Name <> '..') then
      begin
        EntryPath := AddBackslash(Directory) + FindRec.Name;
        if IsDirectoryEntry(FindRec) then
        begin
          if not DeleteDirectoryTree(EntryPath) then
            Result := False;
        end
        else
        begin
          if not DeleteFile(EntryPath) then
            Result := False;
        end;
      end;
    until not FindNext(FindRec);
    FindClose(FindRec);
  end;

  if not RemoveDir(Directory) then
    Result := False;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  AppDirectory: String;
begin
  Result := '';
  UpgradeBackupCreated := False;
  UpgradeInstallSucceeded := False;
  UpgradeRestoreSucceeded := False;
  UpgradeBackupDirectory := ExpandConstant('{localappdata}\LinkForge\upgrade-backup');
  AppDirectory := ExpandConstant('{app}');

  if DirExists(UpgradeBackupDirectory) then
  begin
    if not DeleteDirectoryTree(UpgradeBackupDirectory) then
    begin
      Log('Could not remove stale upgrade backup: ' + UpgradeBackupDirectory);
      Result := '无法清理上一次升级备份目录：' + UpgradeBackupDirectory;
      Exit;
    end;
  end;

  // Only existing installations need an upgrade backup. A fresh install can
  // use Inno's normal rollback behavior without a previous copy.
  if not FileExists(AddBackslash(AppDirectory) + 'LinkForge.App.exe') then
    Exit;

  Log('Creating upgrade backup: ' + UpgradeBackupDirectory);
  if not CopyDirectoryTree(AppDirectory, UpgradeBackupDirectory) then
  begin
    Log('Upgrade backup failed: ' + UpgradeBackupDirectory);
    DeleteDirectoryTree(UpgradeBackupDirectory);
    Result := '无法创建升级备份目录：' + UpgradeBackupDirectory;
    Exit;
  end;

  UpgradeBackupCreated := True;
  Log('Upgrade backup created: ' + UpgradeBackupDirectory);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    UpgradeInstallSucceeded := True;
end;

procedure DeinitializeSetup;
var
  AppDirectory: String;
begin
  if not UpgradeBackupCreated then
    Exit;

  AppDirectory := ExpandConstant('{app}');
  if not UpgradeInstallSucceeded then
  begin
    // Inno may have rolled back a failed upgrade by uninstalling the old
    // installation. Restore the complete program directory before setup exits.
    // Locked files may make the initial delete fail; the copy still restores
    // every file that it can access.
    Log('Upgrade did not complete; restoring previous installation.');
    DeleteDirectoryTree(AppDirectory);
    UpgradeRestoreSucceeded := CopyDirectoryTree(UpgradeBackupDirectory, AppDirectory);
    if UpgradeRestoreSucceeded then
      Log('Previous installation restored.')
    else
      Log('Previous installation restore was incomplete; backup retained: ' + UpgradeBackupDirectory);
  end;

  if UpgradeInstallSucceeded or UpgradeRestoreSucceeded then
    DeleteDirectoryTree(UpgradeBackupDirectory);
end;

function IsX64Install: Boolean;
begin
  Result := IsWin64;
end;
