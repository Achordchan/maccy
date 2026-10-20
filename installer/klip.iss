#define MyAppName "Klip"
#define MyAppPublisher "Achord Chan"
#define MyAppExeName "maccy.exe"
#ifndef MyPublishDir
  #define MyPublishDir SourcePath + "..\artifacts\publish\win-x64"
#endif
#define MyExePath MyPublishDir + "\\" + MyAppExeName
#if !FileExists(MyExePath)
  #error "Published EXE not found: " + MyExePath + " (run: python tools/release_go.py build --version <x.y.z>)"
#endif
#define MyAppVersionShort "1.0.11"
#define MyAppId "{{4F6E38C4-29B6-4C3C-9D36-9B0D2F38C4F1}}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersionShort}
AppMutex=maccy_mutex
CloseApplications=yes
RestartApplications=no
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\{#MyAppName}
DefaultGroupName={#MyAppName}
OutputDir=..\artifacts\installer
OutputBaseFilename=maccy-{#MyAppVersionShort}-setup
SetupIconFile=..\klip\Assets\avalonia-logo.ico
Compression=lzma
SolidCompression=yes
PrivilegesRequired=lowest
DisableDirPage=no
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
WizardStyle=modern

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务"; Flags: checkedonce

[Files]
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Code]
// 1.0.11 is a Go program: what the .NET program of 1.0.10 and older left in
// the folder is of no use. It is removed once everything is installed, not
// before: Inno Setup does not bring back what [InstallDelete] took if the
// installation fails, and the old program must still run then. Only files that
// the earlier program is known to have had are named, in legacy-delete.iss,
// which the release tool writes from the file list of the earlier package
// (--legacy-from) without the files the new package has itself: other DLLs or a
// folder of the user's own in the same place are not touched, nor is the user's
// data (settings.json, history.json, images, files, blobs, sync, updates).
// Only release_go.py passes /DGoBuild: the list belongs to the Go payload, and a
// build of the C# program from the same script (whose files are the ones named)
// must never run it.
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
#ifdef GoBuild
#if FileExists(SourcePath + "legacy-delete.iss")
    #include "legacy-delete.iss"
#endif
#endif
    // the shortcuts that were called Maccy before the program was renamed Klip
    DeleteFile(ExpandConstant('{userprograms}\Maccy.lnk'));
    DeleteFile(ExpandConstant('{userdesktop}\Maccy.lnk'));
  end;
end;

[Icons]
Name: "{userprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "运行 {#MyAppName}"; Flags: nowait postinstall skipifsilent
