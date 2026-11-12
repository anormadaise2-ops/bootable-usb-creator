#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

[Setup]
AppId={{5B8C6AE6-3F11-49A8-B989-02B2AF0A57D4}
AppName=Zercraft
AppVersion={#AppVersion}
AppPublisher=Zercraft
DefaultDirName={localappdata}\Programs\Zercraft
DefaultGroupName=Zercraft
UninstallDisplayIcon={app}\Zercraft.exe
OutputDir=..\artifacts
OutputBaseFilename=ZercraftSetup
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=lowest
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
CloseApplications=yes
RestartApplications=no
Uninstallable=yes

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Tasks]
Name: "desktopicon"; Description: "Créer un raccourci sur le Bureau"; GroupDescription: "Raccourcis :"; Flags: unchecked

[Files]
Source: "..\artifacts\publish\Zercraft.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Zercraft"; Filename: "{app}\Zercraft.exe"
Name: "{autodesktop}\Zercraft"; Filename: "{app}\Zercraft.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Zercraft.exe"; Description: "Lancer Zercraft"; Flags: postinstall nowait skipifsilent
