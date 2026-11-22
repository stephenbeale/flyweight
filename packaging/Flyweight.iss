; Inno Setup script for the Flyweight installer. Built by packaging\package-preview.ps1, which passes:
;   /DAppVersion=0.2.0-preview  /DSourceDir=<published folder>  /DOutputDir=<artifacts>  /DRepoDir=<repo root>
; Per-user install (no admin prompt), Start menu + optional desktop shortcut, .flyweight files open in Flyweight,
; and a ticked "Launch Flyweight" box at the end.

#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif

[Setup]
AppId={{6E7D3C2A-8F14-4B5E-9C61-F1A3E0B7D249}
AppName=Flyweight
AppVersion={#AppVersion}
AppVerName=Flyweight {#AppVersion}
AppPublisher=Flyweight
AppPublisherURL=https://github.com/stephenbeale/flyweight
AppSupportURL=https://github.com/stephenbeale/flyweight/issues
DefaultDirName={autopf}\Flyweight
DefaultGroupName=Flyweight
DisableProgramGroupPage=yes
; Per-user: installs to %LOCALAPPDATA%\Programs\Flyweight without asking for admin rights.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename=Flyweight-{#AppVersion}-Setup
SetupIconFile={#RepoDir}\src\Flyweight.App\flyweight.ico
UninstallDisplayIcon={app}\Flyweight.exe
UninstallDisplayName=Flyweight
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; If Flyweight is open (e.g. when updating), offer to close it first.
CloseApplications=yes
ChangesAssociations=yes

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "fileassoc"; Description: "Open .flyweight &project files with Flyweight"; GroupDescription: "Files:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Flyweight"; Filename: "{app}\Flyweight.exe"
Name: "{group}\Flyweight README"; Filename: "{app}\About\README.txt"
Name: "{autodesktop}\Flyweight"; Filename: "{app}\Flyweight.exe"; Tasks: desktopicon

[Registry]
; .flyweight → Flyweight (per-user, under HKCU\Software\Classes; removed on uninstall).
Root: HKA; Subkey: "Software\Classes\.flyweight"; ValueType: string; ValueName: ""; ValueData: "Flyweight.Project"; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Flyweight.Project"; ValueType: string; ValueName: ""; ValueData: "Flyweight project"; Flags: uninsdeletekey; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Flyweight.Project\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\Flyweight.exe,0"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Flyweight.Project\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Flyweight.exe"" ""%1"""; Tasks: fileassoc

[Run]
Filename: "{app}\Flyweight.exe"; Description: "Launch Flyweight"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Settings, autosave and the editing-copy cache live in %LOCALAPPDATA%\Flyweight. Projects and videos are the
; user's own files and are never touched.
Type: filesandordirs; Name: "{localappdata}\Flyweight\cache"
