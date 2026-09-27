; WallpaperFlow Pro — Inno Setup 6.2+ script
; Build: build\build.ps1 -Installer   (or: iscc /DAppVersion=1.0.0 installer\WallpaperFlow.iss)
; Per-user install (no admin), autostart via HKCU Run, keeps a copy of itself for one-click rollback.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef AppURL
  #define AppURL "https://github.com"
#endif
#define AppName "WallpaperFlow Pro"
#define AppPublisher "Tnsk.exe"
#define AppExe "WallpaperFlowPro.exe"
#define PublishDir "..\artifacts\publish"

[Setup]
AppId={{9C1D4E2A-5B7F-4A31-9E0C-6F2B8D1A7E55}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}/issues
LicenseFile=..\LICENSE
VersionInfoVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\WallpaperFlow Pro
DisableDirPage=auto
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\artifacts\installer
OutputBaseFilename=WallpaperFlowPro-Setup-{#AppVersion}
SetupIconFile=..\src\WallpaperFlow.App\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
; lzma2/max in a separate compressor process: small installer without exhausting the 32-bit compiler's memory.
Compression=lzma2/max
SolidCompression=yes
LZMAUseSeparateProcess=yes
WizardStyle=modern
CloseApplications=force
RestartApplications=no
SetupLogging=yes
; Uncomment and configure to sign installer and uninstaller (updates refuse unsigned installers by default):
; SignTool=signtool sign /fd sha256 /tr http://timestamp.digicert.com /td sha256 /a $f
; SignedUninstaller=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startup"; Description: "Start WallpaperFlow Pro with Windows"; GroupDescription: "Startup:"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Keep this installer for rollback (UpdateService.RetainedInstallers looks for "*Setup-<version>.exe" here).
Source: "{srcexe}"; DestDir: "{localappdata}\WallpaperFlow\installers"; DestName: "WallpaperFlowPro-Setup-{#AppVersion}.exe"; Flags: external ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "WallpaperFlowPro"; ValueData: """{app}\{#AppExe}"" --silent"; Tasks: startup; Flags: uninsdeletevalue

[Run]
; Interactive install: offer to launch.
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
; Silent install (auto-update / rollback): relaunch quietly in the tray.
Filename: "{app}\{#AppExe}"; Parameters: "--silent"; Flags: nowait; Check: WizardSilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM {#AppExe} /T"; Flags: runhidden; RunOnceId: "StopApp"

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\WallpaperFlow\installers"
Type: filesandordirs; Name: "{app}"

[Code]
const
  WebView2ClientKey = 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WebView2ClientKeyUser = 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WebView2Bootstrapper = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703';

function WebView2Installed(): Boolean;
var
  V: String;
begin
  Result := (RegQueryStringValue(HKLM, WebView2ClientKey, 'pv', V) and (V <> '') and (V <> '0.0.0.0')) or
            (RegQueryStringValue(HKCU, WebView2ClientKeyUser, 'pv', V) and (V <> '') and (V <> '0.0.0.0'));
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  Path: String;
begin
  if (CurStep = ssPostInstall) and (not WebView2Installed()) then
  begin
    // Windows 11 ships the runtime; some Windows 10 installs don't. Web, shader and clock wallpapers need it.
    try
      DownloadTemporaryFile(WebView2Bootstrapper, 'MicrosoftEdgeWebview2Setup.exe', '', nil);
      Path := ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe');
      Exec(Path, '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    except
      Log('WebView2 runtime download failed: ' + GetExceptionMessage);
      if not WizardSilent then
        MsgBox('The Microsoft Edge WebView2 Runtime could not be installed automatically. Video wallpapers work without it; ' +
               'web, shader and clock wallpapers need it. You can install it later from https://developer.microsoft.com/microsoft-edge/webview2/.',
               mbInformation, MB_OK);
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    if (not UninstallSilent) and
       (MsgBox('Also delete your wallpaper library, settings and backups?' #13#10 +
               '(' + ExpandConstant('{localappdata}\WallpaperFlow') + ')', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
      DelTree(ExpandConstant('{localappdata}\WallpaperFlow'), True, True, True);
  end;
end;
