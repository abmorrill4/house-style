; HouseStyle-Setup.exe: a thin wrapper around the signed FlareAI folder and the signed Install.ps1.
; It unpacks them to a temporary folder and runs Install.ps1, which does all the real work (folder layout, rollback copy,
; the one administrator step, turning the plugin on). Build: ISCC.exe /DVersion=x.y.z installer\HouseStyle.iss
; The stage (dist\stage) must already be signed.
#ifndef Version
  #error Pass /DVersion=x.y.z
#endif

[Setup]
AppId={{6E0A2B52-5C0B-4C55-9E55-4D6F0B1A7C11}
AppName=House Style
AppVersion={#Version}
AppPublisher=Arthur Morrill
AppPublisherURL=https://gethousestyle.com
VersionInfoVersion={#Version}
; Nothing is installed by Setup itself, so there is no install folder, no uninstaller and no Start menu entry.
CreateAppDir=no
Uninstallable=no
DisableProgramGroupPage=yes
DisableWelcomePage=no
DisableReadyPage=yes
DisableFinishedPage=yes
; Per user. Install.ps1 asks Windows for administrator rights itself, once, for the Flare Plugins link.
PrivilegesRequired=lowest
; 32-bit PowerShell would not find Flare under Program Files.
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=HouseStyle-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Files]
Source: "..\dist\stage\FlareAI\*"; DestDir: "{tmp}\HouseStyleSetup\FlareAI"; Flags: recursesubdirs ignoreversion deleteafterinstall
Source: "..\dist\stage\Install.ps1"; DestDir: "{tmp}\HouseStyleSetup"; Flags: ignoreversion deleteafterinstall

[Messages]
WelcomeLabel1=House Style {#Version}
WelcomeLabel2=This installs House Style for MadCap Flare for your Windows account.%n%nClose Flare first. Windows asks for administrator approval once, the first time.

[Code]
// Inno has no way to set an exit code from Pascal Script (Abort exits 0), so a failed install ends the process itself.
procedure ExitProcess(ExitCode: Cardinal); external 'ExitProcess@kernel32.dll stdcall';

procedure CurStepChanged(CurStep: TSetupStep);
var
  Ps, Args: String;
  Code: Integer;
begin
  if CurStep <> ssPostInstall then Exit;
  Ps := ExpandConstant('{sys}\cmd.exe');
  // The console stays open while Install.ps1 runs and pauses only if it fails, so its message can be read.
  Args := '/c ""' + ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe') + '" -NoProfile -ExecutionPolicy Bypass -File "' +
          ExpandConstant('{tmp}\HouseStyleSetup\Install.ps1') + '" -Silent || (pause & exit /b 1)"';
  if Exec(Ps, Args, ExpandConstant('{tmp}\HouseStyleSetup'), SW_SHOW, ewWaitUntilTerminated, Code) and (Code = 0) then
    SuppressibleMsgBox('House Style is installed. Start Flare and look for the House Style tab.', mbInformation, MB_OK, IDOK)
  else
  begin
    SuppressibleMsgBox('House Style was not installed. The message in the console window says why. Close Flare and run Setup again.', mbError, MB_OK, IDOK);
    DelTree(ExpandConstant('{tmp}\HouseStyleSetup'), True, True, True);
    ExitProcess(4);   // 4 = a fatal error during installation, so a silent run does not report success
  end;
end;
