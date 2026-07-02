; Inno Setup script for DragThrough.
;
; Per-user install (no admin rights needed) into %LocalAppData%\Programs\DragThrough.
; This matches the app: it runs as the signed-in user (asInvoker) and its built-in
; auto-updater swaps the .exe in place, which only works in a writable folder.
;
; Build it with installer\build.ps1 (publishes the app, then compiles this script),
; or by hand:
;     ISCC.exe /DMyAppVersion=1.0.0.8 /DPublishDir=<path-to-publish> DragThrough.iss

#define MyAppName "DragThrough"
; The published assembly is still named ZombieBar.exe; keep it so the app's own paths/updater
; assumptions are untouched. The user-facing name everywhere is "DragThrough".
#define MyAppExeName "ZombieBar.exe"
#define MyAppPublisher "Redozubov"
#define MyAppURL "https://buymeacoffee.com/redozubov"

; Version: pass /DMyAppVersion=x.y.z.w on the ISCC command line; fallback below.
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0.0"
#endif

; Folder that holds the published single-file exe. Override with /DPublishDir=...
#ifndef PublishDir
  #define PublishDir "..\ZombieBar\bin\Release\net10.0-windows\win-x64\publish"
#endif

[Setup]
; A stable, unique GUID identifies the app for upgrades/uninstall. Do not change it.
AppId={{B6D3A7E2-3C4F-4A1E-9E2B-7F5A1C9D0E84}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
; Per-user install: no UAC prompt, installs under the user's profile.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\{#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=yes
OutputDir=Output
OutputBaseFilename=DragThrough-Setup-{#MyAppVersion}
SetupIconFile=..\ZombieBar\Resources\zombiebar.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
; Detect and close a running instance so the exe can be replaced on upgrade.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
; Checked by default: start automatically when the user signs in.
Name: "autostart"; Description: "{cm:AutoStartDescription}"; GroupDescription: "{cm:AutoStartGroup}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Files]
; The self-contained exe plus the loose data the app reads at runtime: the Languages and Themes
; folders (enumerated from disk) and the Resources sub-assets. Everything except debug symbols.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; \
    Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Autostart for the current user. Removed on uninstall (and if the task is unchecked).
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "{#MyAppName}"; ValueData: """{app}\{#MyAppExeName}"""; \
    Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; \
    Flags: nowait postinstall skipifsilent

[UninstallRun]
; Make sure the app isn't running while we uninstall, so the exe can be removed.
Filename: "{cmd}"; Parameters: "/C taskkill /IM ""{#MyAppExeName}"" /F"; \
    Flags: runhidden; RunOnceId: "KillApp"

[CustomMessages]
english.AutoStartGroup=Startup:
english.AutoStartDescription=Start {#MyAppName} automatically when I sign in to Windows
russian.AutoStartGroup=Автозапуск:
russian.AutoStartDescription=Запускать {#MyAppName} автоматически при входе в Windows

[Code]
// Belt-and-braces: if the app is still running when files get installed (the tray app
// may not respond to the Restart Manager's close request), terminate it first.
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssInstall then
    Exec(ExpandConstant('{cmd}'), '/C taskkill /IM "{#MyAppExeName}" /F',
         '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;
