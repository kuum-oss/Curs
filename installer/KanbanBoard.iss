#ifndef AppVersion
  #error AppVersion must be supplied by the release packaging script.
#endif
#ifndef PublishDir
  #error PublishDir must be supplied by the release packaging script.
#endif
#ifndef PackageDir
  #error PackageDir must be supplied by the release packaging script.
#endif
#ifndef IconFile
  #error IconFile must be supplied by the release packaging script.
#endif

[Setup]
AppId={{FF703EA6-78B7-4E66-976D-6CE028174DA4}
AppName=KanbanBoard
AppVersion={#AppVersion}
AppPublisher=KanbanBoard
AppPublisherURL=https://github.com/kuum-oss/Curs
DefaultDirName={localappdata}\Programs\KanbanBoard
DefaultGroupName=KanbanBoard
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir={#PackageDir}
OutputBaseFilename=KanbanBoard-{#AppVersion}-windows-x64-setup
SetupIconFile={#IconFile}
UninstallDisplayIcon={app}\KanbanBoard.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "ukrainian"; MessagesFile: "compiler:Languages\Ukrainian.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\KanbanBoard"; Filename: "{app}\KanbanBoard.exe"
Name: "{autodesktop}\KanbanBoard"; Filename: "{app}\KanbanBoard.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\KanbanBoard.exe"; Description: "{cm:LaunchProgram,KanbanBoard}"; Flags: nowait postinstall skipifsilent

; Application data and Docker volumes belong to the application, not the
; installer. An uninstall removes only program files and its shortcuts.
