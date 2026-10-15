; Inno Setup script for Unbounded Panorama 360. Built by build-installer.ps1:
;   ISCC /DAppVersion=1.0.0 /DSourceDir=<publish folder> installer\UnboundedPanorama360.iss

#define AppName "Unbounded Panorama 360"
#define AppExe "UnboundedPanorama360.exe"
#define TaskName "Unbounded Panorama 360"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\app"
#endif

[Setup]
AppId={{9E3F1C2A-6B7D-4E55-9A21-3C8F0D5B7A61}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Unbounded Technologies
AppPublisherURL=https://github.com/UnboundedTechnologies/Unbounded-Panorama-360
AppSupportURL=https://github.com/UnboundedTechnologies/Unbounded-Panorama-360
; Fixed location: "Start with Windows" and the app's own checks expect Program Files\Unbounded Panorama 360.
DefaultDirName={autopf}\{#AppName}
DisableDirPage=yes
DisableProgramGroupPage=yes
; Administrator rights: the app reads CPU sensors through a kernel driver, and the autostart task runs elevated.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\dist
OutputBaseFilename=UnboundedPanorama360-Setup-{#AppVersion}
SetupIconFile=..\src\Unbounded.Panorama.App\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; The running app is closed through its own quit signal (see PrepareToInstall), not the Restart Manager.
CloseApplications=no

[Tasks]
Name: "autostart"; Description: "Start with Windows (in the tray, no prompt at login)"; GroupDescription: "Startup:"
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "deps.ps1"; DestDir: "{app}\setup"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; runascurrentuser: postinstall entries otherwise run as the non-elevated original user, and the app
; requires administrator rights (CreateProcess then fails with error 740). Setup is already elevated.
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName} now"; Flags: nowait postinstall runascurrentuser

[UninstallRun]
Filename: "{app}\{#AppExe}"; Parameters: "--quit"; Flags: runhidden waituntilterminated; RunOnceId: "QuitApp"
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""{#TaskName}"" /F"; Flags: runhidden; RunOnceId: "DeleteTask"

[UninstallDelete]
Type: filesandordirs; Name: "{app}\setup"

[Code]
const
  QuitScript = 'try { $e = [Threading.EventWaitHandle]::OpenExisting(''UnboundedPanorama360.SingleInstance.Quit''); [void]$e.Set() } catch { } ' +
               'Wait-Process -Name UnboundedPanorama360 -Timeout 30 -ErrorAction SilentlyContinue; ' +
               'if (Get-Process -Name UnboundedPanorama360 -ErrorAction SilentlyContinue) { exit 1 } else { exit 0 }';

function PowerShell(const Args: String; var ResultCode: Integer): Boolean;
begin
  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -ExecutionPolicy Bypass ' + Args, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

{ Close a running copy cleanly (it closes its USB session first) before files are replaced. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Result := '';
  if PowerShell('-Command "' + QuitScript + '"', Code) and (Code <> 0) then
    Result := 'Unbounded Panorama 360 is still running. Right-click its tray icon, choose Exit, then click Back and Next to retry.';
end;

{ Remove the per-user test install that install.ps1 used to create, so nothing piles up. }
procedure RemoveOldUserInstall();
var
  Root: String;
  Code: Integer;
begin
  Root := ExpandConstant('{localappdata}\Programs\UnboundedPanorama360');
  if DirExists(Root) then
  begin
    { rmdir on a junction removes only the link, never its target. }
    Exec(ExpandConstant('{cmd}'), '/c rmdir "' + Root + '\current"', '', SW_HIDE, ewWaitUntilTerminated, Code);
    DelTree(Root, True, True, True);
  end;
  DeleteFile(ExpandConstant('{userprograms}\{#AppName}.lnk'));
end;

procedure SetStatus(const Text: String);
begin
  WizardForm.StatusLabel.Caption := Text;
  WizardForm.Refresh;
end;

function InstallDependency(const Component, Title: String; var Failed: String): Boolean;
var
  Code: Integer;
begin
  SetStatus('Checking ' + Title + '...');
  Result := PowerShell('-File "' + ExpandConstant('{app}\setup\deps.ps1') + '" -Component ' + Component +
                       ' -Log "' + ExpandConstant('{app}\setup\deps.log') + '"', Code) and (Code = 0);
  if not Result then
  begin
    if Code = 2 then
      Failed := Failed + #13#10 + '  - ' + Title + ' (winget is not available on this PC)'
    else
      Failed := Failed + #13#10 + '  - ' + Title;
  end;
end;

procedure ConfigureAutostart();
var
  Code: Integer;
  Exe: String;
begin
  Exe := ExpandConstant('{app}\{#AppExe}');
  if WizardIsTaskSelected('autostart') then
  begin
    SetStatus('Setting up Start with Windows...');
    Exec(ExpandConstant('{sys}\schtasks.exe'),
      '/Create /TN "{#TaskName}" /TR "\"' + Exe + '\" --tray" /SC ONLOGON /RL HIGHEST /F', '', SW_HIDE, ewWaitUntilTerminated, Code);
  end
  else
    Exec(ExpandConstant('{sys}\schtasks.exe'), '/Delete /TN "{#TaskName}" /F', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Failed: String;
begin
  if CurStep = ssInstall then
    RemoveOldUserInstall();
  if CurStep = ssPostInstall then
  begin
    Failed := '';
    InstallDependency('ffmpeg', 'FFmpeg (video conversion)', Failed);
    InstallDependency('ytdlp', 'yt-dlp and Deno (YouTube import)', Failed);
    InstallDependency('pawnio', 'PawnIO driver (CPU temperature, clocks, power)', Failed);
    ConfigureAutostart();
    if Failed <> '' then
      MsgBox('Unbounded Panorama 360 is installed, but these could not be installed automatically:' + Failed + #13#10#13#10 +
             'Check your internet connection and run this setup again. Details: ' + ExpandConstant('{app}\setup\deps.log'),
             mbInformation, MB_OK);
  end;
end;
