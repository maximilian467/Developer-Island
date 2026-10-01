; Developer Island installer (Inno Setup 6).
; Built by tools/build-release.ps1, which passes AppVersion, SourceDir and OutputDir.
; Per-user install: no administrator rights, no Visual Studio, no .NET runtime needed
; (the app is self-contained, including the Windows App SDK).

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

[Setup]
AppId={{6F1C2A5E-8B4D-4E7A-9C3B-2D5E7F9A1B3C}
AppName=Developer Island
AppVersion={#AppVersion}
AppVerName=Developer Island {#AppVersion}
AppPublisher=Developer Island
AppPublisherURL=https://github.com/maximilian467/Developer-Island
AppSupportURL=https://github.com/maximilian467/Developer-Island/issues
DefaultDirName={localappdata}\Programs\Developer Island
DefaultGroupName=Developer Island
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
OutputDir={#OutputDir}
OutputBaseFilename=DeveloperIsland-Setup-{#AppVersion}
LicenseFile=..\LICENSE
SetupIconFile=..\src\DeveloperIsland\Assets\DeveloperIsland.ico
UninstallDisplayIcon={app}\DeveloperIsland.exe
UninstallDisplayName=Developer Island
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
CloseApplications=force
RestartApplications=no

[Tasks]
Name: "autostart"; Description: "Start Developer Island when I sign in"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autoprograms}\Developer Island"; Filename: "{app}\DeveloperIsland.exe"
Name: "{autoprograms}\Developer Island (Demo)"; Filename: "{app}\DeveloperIsland.exe"; Parameters: "--demo"

[Registry]
; Same entry the app writes for "Start with Windows"; the app adopts it on first launch.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "DeveloperIsland"; \
    ValueData: """{app}\DeveloperIsland.exe"" --autostart"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\DeveloperIsland.exe"; Description: "Open Developer Island"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM DeveloperIsland.exe /F"; Flags: runhidden; RunOnceId: "StopDeveloperIsland"
Filename: "{sys}\reg.exe"; Parameters: "delete HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v DeveloperIsland /f"; Flags: runhidden; RunOnceId: "RemoveAutostart"

; Usage history and settings in %LOCALAPPDATA%\DeveloperIsland are kept on uninstall,
; so a reinstall continues where it left off. Delete that folder to remove them.
