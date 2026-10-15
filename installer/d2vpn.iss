; d2vpn installer.
;
; Built by build.ps1, which passes the version in with /DAppVersion so that the number lives in
; Directory.Build.props and nowhere else.
;
; Machine-wide rather than per-user, because the application opens a WinDivert handle and so has
; to run elevated whatever happens. A per-user install would still prompt for elevation on every
; launch, which is the same cost without the shared install.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#define AppName "d2vpn"
#define AppPublisher "szymonoczki1"
#define AppUrl "https://github.com/szymonoczki1/d2vpn"
#define AppExe "d2vpn-ui.exe"

[Setup]
; Stable across versions: this is what lets an install upgrade in place rather than stacking up
; a second copy beside the first.
AppId={{8F3C1A42-5D9E-4B7A-9C2F-6E1D0A4B8C33}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}

DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
AllowNoIcons=yes

; The driver is 64-bit only, so there is no 32-bit install to offer.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

PrivilegesRequired=admin
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
OutputBaseFilename=d2vpn-{#AppVersion}-setup
LicenseFile=..\LICENSE
UninstallDisplayName={#AppName} {#AppVersion}
UninstallDisplayIcon={app}\{#AppExe}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
; The whole self-contained publish. recursesubdirs because a published app may grow folders
; between runtime versions, and a missing one fails at launch rather than at build.
; README and LICENSE are copied into publish by build.ps1, so they arrive with everything else
; and the zip cannot end up without them.
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; runascurrentuser so it inherits the elevation the installer already has, rather than asking a
; second time the moment setup closes.
Filename: "{app}\{#AppExe}"; Description: "Start {#AppName}"; Flags: nowait postinstall skipifsilent runascurrentuser

[UninstallDelete]
; The driver WinDivert extracts beside the executable at runtime. It is not ours to own, so it is
; not in [Files] - but leaving it behind would leave the install folder there after an uninstall.
Type: files; Name: "{app}\WinDivert.dll"
Type: files; Name: "{app}\WinDivert64.sys"
Type: dirifempty; Name: "{app}"

[Messages]
; The config and the API key live in %APPDATA%\d2vpn and are deliberately not touched by the
; uninstaller: keybinds somebody spent time on should survive reinstalling the thing they bind.
ConfirmUninstall=Remove %1?%n%nYour settings in %APPDATA%\d2vpn are kept, so reinstalling restores your keybinds.

[Code]
// WinDivert's driver will not load if another limiter already has it open. Catching that here is
// kinder than letting the window open and report that it could not get a handle.
function InitializeSetup(): Boolean;
var
  Running: Boolean;
begin
  Running := CheckForMutexes('d2vpn-running');
  if Running then
  begin
    MsgBox('d2vpn is running. Close it before installing.', mbError, MB_OK);
    Result := False;
  end
  else
    Result := True;
end;
