; Instalador sencillo de Umbra (Inno Setup 6). Se compila con installer\build.ps1
#ifndef AppVersion
  #define AppVersion "1.1.0"
#endif

[Setup]
AppId={{B7A1C3D2-5E64-4F0A-9C21-0A1B2C3D4E5F}
AppName=Umbra
AppVersion={#AppVersion}
AppPublisher=Nogui23
DefaultDirName={autopf}\Umbra
DefaultGroupName=Umbra
UninstallDisplayIcon={app}\Umbra.exe
SetupIconFile=..\src\Umbra.App\Assets\app.ico
OutputDir=output
OutputBaseFilename=Umbra-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
WizardStyle=modern

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\Umbra"; Filename: "{app}\Umbra.exe"
Name: "{autodesktop}\Umbra"; Filename: "{app}\Umbra.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Umbra.exe"; Description: "{cm:LaunchProgram,Umbra}"; Flags: nowait postinstall skipifsilent
