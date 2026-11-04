; -----------------------------------------------------------------------------
;  Lumen - conventional installer (Inno Setup 6)
;  Build:  ./scripts/publish.ps1        (calls ISCC with AppVersion and SourceDir)
;  Manual: ISCC /DAppVersion=0.1.0 /DSourceDir=..\..\artifacts\publish\Lumen Lumen.iss
;
;  Per-user install by default (no admin prompt): everything Lumen touches is per-user,
;  and the optional autostart entry belongs in the signed-in user's HKCU.
; -----------------------------------------------------------------------------

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\artifacts\publish\Lumen"
#endif

[Setup]
AppId={{7C3E5A51-2F6B-4B7E-9F8E-6A2D1C0B4E11}
AppName=Lumen
AppVersion={#AppVersion}
AppPublisher=Lumen
AppVerName=Lumen {#AppVersion}
DefaultDirName={autopf}\Lumen
DefaultGroupName=Lumen
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\..\artifacts\installer
OutputBaseFilename=Lumen-Setup-{#AppVersion}-x64
SetupIconFile=..\..\src\Lumen.App\Assets\Lumen.ico
UninstallDisplayIcon={app}\Lumen.exe
UninstallDisplayName=Lumen
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
; To sign the installer from ISCC directly, configure a SignTool in the Inno IDE and uncomment:
; SignTool=signtool

[Tasks]
Name: "autostart"; Description: "Start Lumen when I sign in (console mode - you can change this later in Lumen Settings)"; Flags: unchecked
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion createallsubdirs

[Icons]
Name: "{group}\Lumen"; Filename: "{app}\Lumen.exe"
Name: "{group}\Lumen (safe mode)"; Filename: "{app}\Lumen.exe"; Parameters: "--safe-mode"
Name: "{group}\Lumen Recovery"; Filename: "{app}\Lumen.Recovery.exe"
Name: "{group}\Restore normal Windows startup"; Filename: "{app}\RestoreWindows.cmd"
Name: "{autodesktop}\Lumen"; Filename: "{app}\Lumen.exe"; Tasks: desktopicon

[Registry]
; Same value Lumen writes itself (Settings > Start Lumen when I sign in) - removed on uninstall.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Lumen"; \
  ValueData: """{app}\Lumen.exe"" --autostart"; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\Lumen.exe"; Parameters: "--windowed"; Description: "Open Lumen now (windowed)"; Flags: nowait postinstall skipifsilent
; In-app update (Lumen starts the installer with /VERYSILENT /LUMENUPDATE=1 and exits): start Lumen again.
Filename: "{app}\Lumen.exe"; Parameters: "--after-update"; Flags: nowait; Check: IsLumenUpdate

[UninstallRun]
Filename: "{cmd}"; Parameters: "/C taskkill /IM Lumen.exe /F"; Flags: runhidden; RunOnceId: "StopLumen"

[Code]
function IsLumenUpdate: Boolean;
begin
  Result := ExpandConstant('{param:LUMENUPDATE|0}') = '1';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    { Always remove the autostart entry, even if Lumen created it (not the installer). }
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Lumen');
  end;
  if CurUninstallStep = usPostUninstall then
  begin
    if MsgBox('Also delete your Lumen library, settings and logs (%LOCALAPPDATA%\Lumen)?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(ExpandConstant('{localappdata}\Lumen'), True, True, True);
  end;
end;
