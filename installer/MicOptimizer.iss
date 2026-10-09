; Inno Setup script for Mic Optimizer. Build with build-installer.ps1 (it publishes the app first).
; Installs per user (no administrator rights needed) into %LOCALAPPDATA%\Programs\Mic Optimizer.

#define MyAppName "Mic Optimizer"
#define MyAppVersion "1.1.0"
#define MyAppExe "MicOptimizer.exe"
#define PublishDir "..\artifacts\publish"

[Setup]
AppId={{6F1D2C5E-3B7A-4E58-9C1D-4A8B0E2F7D31}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts\installer
OutputBaseFilename=MicOptimizer-Setup-{#MyAppVersion}
SetupIconFile=..\src\MicOptimizer.App\Assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExe}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
InfoAfterFile=after-install.txt
; The app holds this mutex while it runs, so setup asks to close it before replacing files.
AppMutex=Local\MicOptimizer.SingleInstance

[Tasks]
Name: "startup"; Description: "Start Mic Optimizer hidden in the tray when I sign in to Windows"; GroupDescription: "Startup:"
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Registry]
; Same value the app's own "Start with Windows" switch writes, so the two stay in step.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "MicOptimizer"; \
    ValueData: """{app}\{#MyAppExe}"" --background"; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "Open {#MyAppName} now"; Flags: nowait postinstall skipifsilent
