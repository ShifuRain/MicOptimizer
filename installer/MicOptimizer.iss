; Inno Setup script for Mic Optimizer. Build with build-installer.ps1 (it publishes the app first).
; Installs per user (no administrator rights needed) into %LOCALAPPDATA%\Programs\Mic Optimizer.
;
; Compile with /DTESTBUILD=1 to make an installer that cannot disturb a real installation: it uses another installer
; ID, another name and another startup-entry name. build-installer.ps1 never uses this; it exists for testing.

#define MyAppName "Mic Optimizer"
#define MyAppVersion "1.2.1"
#define MyAppExe "MicOptimizer.exe"
#define PublishDir "..\artifacts\publish"

#ifdef TESTBUILD
  #define AppIdGuid "{{0B8C3E71-9A22-4F6D-8E55-1D7C4A9B6F02}"
  #define ShownName "Mic Optimizer (installer test)"
  #define RunValueName "MicOptimizerInstallerTest"
  #define OutputFolder "..\artifacts\installer-test"
  #define OutputName "MicOptimizer-InstallerTest"
#else
  #define AppIdGuid "{{6F1D2C5E-3B7A-4E58-9C1D-4A8B0E2F7D31}"
  #define ShownName MyAppName
  #define RunValueName "MicOptimizer"
  #define OutputFolder "..\artifacts\installer"
  #define OutputName "MicOptimizer-Setup-" + MyAppVersion
#endif

[Setup]
AppId={#AppIdGuid}
AppName={#ShownName}
AppVersion={#MyAppVersion}
AppVerName={#ShownName} {#MyAppVersion}
DefaultDirName={autopf}\{#ShownName}
DefaultGroupName={#ShownName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputFolder}
OutputBaseFilename={#OutputName}
SetupIconFile=..\src\MicOptimizer.App\Assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExe}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
InfoAfterFile=after-install.txt
; Adding to PATH changes the user's environment; this tells Windows to tell running programs.
ChangesEnvironment=yes
#ifndef TESTBUILD
; The app holds this mutex while it runs, so setup asks to close it before replacing files.
AppMutex=Local\MicOptimizer.SingleInstance
#endif

[Tasks]
Name: "startup"; Description: "Start Mic Optimizer hidden in the tray when I sign in to Windows"; GroupDescription: "Startup:"
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked
Name: "addtopath"; Description: "Add Mic Optimizer to my PATH, so I can start it by typing MicOptimizer in a terminal"; GroupDescription: "Command line:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{autoprograms}\{#ShownName}"; Filename: "{app}\{#MyAppExe}"
Name: "{autodesktop}\{#ShownName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Registry]
; Same value the app's own "Start with Windows" switch writes, so the two stay in step.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#RunValueName}"; \
    ValueData: """{app}\{#MyAppExe}"" --background"; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "Open {#ShownName} now"; Flags: nowait postinstall skipifsilent

[Code]
const
  EnvironmentKey = 'Environment';

{ Compares folders the way Windows does: ignoring case, surrounding quotes and a trailing slash. }
function NormalizeFolder(const Folder: string): string;
begin
  Result := Lowercase(Trim(Folder));
  if (Length(Result) > 1) and (Result[1] = '"') and (Result[Length(Result)] = '"') then
    Result := Copy(Result, 2, Length(Result) - 2);
  while (Length(Result) > 3) and ((Result[Length(Result)] = '\') or (Result[Length(Result)] = '/')) do
    Delete(Result, Length(Result), 1);
end;

{ Returns the PATH without any entry for Folder; Found says whether there was one. Other entries are kept as written. }
function PathWithout(const Path, Folder: string; var Found: Boolean): string;
var
  Rest, Item, Target: string;
  P: Integer;
begin
  Result := '';
  Found := False;
  Rest := Path;
  Target := NormalizeFolder(Folder);
  while Rest <> '' do
  begin
    P := Pos(';', Rest);
    if P = 0 then
    begin
      Item := Rest;
      Rest := '';
    end
    else
    begin
      Item := Copy(Rest, 1, P - 1);
      Rest := Copy(Rest, P + 1, Length(Rest));
    end;
    Item := Trim(Item);
    if Item <> '' then
    begin
      if NormalizeFolder(Item) = Target then
        Found := True
      else
      begin
        if Result <> '' then Result := Result + ';';
        Result := Result + Item;
      end;
    end;
  end;
end;

procedure AddAppToPath;
var
  Current, Ignored: string;
  Found: Boolean;
begin
  if not RegQueryStringValue(HKCU, EnvironmentKey, 'Path', Current) then
    Current := '';
  Ignored := PathWithout(Current, ExpandConstant('{app}'), Found);
  if Found then Exit;                       { already listed: change nothing }
  if (Current <> '') and (Current[Length(Current)] <> ';') then
    Current := Current + ';';
  RegWriteExpandStringValue(HKCU, EnvironmentKey, 'Path', Current + ExpandConstant('{app}'));
end;

procedure RemoveAppFromPath;
var
  Current, Updated: string;
  Found: Boolean;
begin
  if not RegQueryStringValue(HKCU, EnvironmentKey, 'Path', Current) then Exit;
  Updated := PathWithout(Current, ExpandConstant('{app}'), Found);
  if Found then
    RegWriteExpandStringValue(HKCU, EnvironmentKey, 'Path', Updated);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('addtopath') then
    AddAppToPath;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  { Always clean up, whether the entry came from this installer or from the app's tray menu. }
  if CurUninstallStep = usPostUninstall then
    RemoveAppFromPath;
end;
