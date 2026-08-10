; Inno Setup script for the AirPlay Receiver installer (AirPlayReceiverSetup.exe).
;
; Packages the self-contained `dotnet publish` output into a single installer, so
; users get a Start-menu entry and an uninstaller instead of a loose folder of
; DLLs. Built automatically by .github/workflows/release.yml; to build by hand:
;
;   dotnet publish src/AirPlayReceiver/AirPlayReceiver.csproj -c Release -r win-x64 ^
;     --self-contained true -p:Platform=x64 -p:WindowsPackageType=None -o publish
;   iscc /DAppVersion=1.2.0 installer\AirPlayReceiver.iss
;
; Defines (all optional, defaults suit a repo-root build):
;   AppVersion  — version shown in Programs & Features   (default 0.0.0)
;   PublishDir  — folder holding the published app       (default ..\publish)
;   OutputDir   — where the setup .exe is written        (default ..)

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish"
#endif
#ifndef OutputDir
  #define OutputDir ".."
#endif

#define AppName "AirPlay Receiver"
#define AppExe  "AirPlayReceiver.exe"

[Setup]
; Keep this GUID stable — it's how Windows recognises an upgrade of an existing
; install rather than a second copy.
AppId={{8F3A6C21-4B7E-4E2A-9C55-1D0E5A7B93C4}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=smelvintime
AppSupportURL=https://github.com/smelvintime/airplay-receiver-windows
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
OutputDir={#OutputDir}
OutputBaseFilename=AirPlayReceiverSetup
SetupIconFile=..\src\AirPlayReceiver\Assets\AppIcon.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; The app is x64-only (see the csproj's <Platforms>) and targets Windows 10 1809+.
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
MinVersion=10.0.17763
; Admin rights let us add the firewall rules below, which is what makes the
; receiver discoverable without the user having to answer the firewall prompt.
PrivilegesRequired=admin
; The receiver normally sits in the system tray, so an upgrade has to close the
; running copy before its files can be replaced.
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; The app adds this itself on first launch (Services/StartupManager.cs) so it
; keeps advertising in the background. Declared here only so uninstalling
; removes it rather than leaving a Run entry pointing at a deleted exe.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "AirPlayReceiver"; Flags: dontcreatekey uninsdeletevalue

[Run]
; Pre-authorise the receiver in Windows Firewall. Without this the user gets the
; standard prompt on first launch and the PC never shows up in Screen Mirroring
; if they dismiss it. The rules are program-scoped rather than port-scoped
; because AirPlay negotiates extra media ports per session; delete-then-add keeps
; a repeat install from stacking duplicate rules.
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#AppName} (TCP-In)"""; Flags: runhidden; StatusMsg: "Adding Windows Firewall rules..."
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#AppName} (UDP-In)"""; Flags: runhidden
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""{#AppName} (TCP-In)"" dir=in action=allow program=""{app}\{#AppExe}"" enable=yes profile=private,domain protocol=TCP"; Flags: runhidden
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""{#AppName} (UDP-In)"" dir=in action=allow program=""{app}\{#AppExe}"" enable=yes profile=private,domain protocol=UDP"; Flags: runhidden
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#AppName} (TCP-In)"""; Flags: runhidden; RunOnceId: "DelFwTcp"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#AppName} (UDP-In)"""; Flags: runhidden; RunOnceId: "DelFwUdp"

[UninstallDelete]
; Written next to the exe by App.xaml.cs's crash logger.
Type: files; Name: "{app}\airplay-crash.log"
