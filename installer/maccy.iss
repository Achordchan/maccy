#define MyAppName "Maccy"
#define MyAppPublisher "Achord Chan"
#define MyAppExeName "maccy.exe"
#define MyPublishDir SourcePath + "..\artifacts\publish\win-x64"
#define MyExePath MyPublishDir + "\\" + MyAppExeName
#if !FileExists(MyExePath)
  #error "Published EXE not found: " + MyExePath + " (run: dotnet publish -c Release -r win-x64 -o artifacts\\publish\\win-x64)"
#endif
#define MyAppVersionShort "1.0.7"
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
SetupIconFile=..\maccy\Assets\avalonia-logo.ico
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

[Icons]
Name: "{userprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "运行 {#MyAppName}"; Flags: nowait postinstall skipifsilent
