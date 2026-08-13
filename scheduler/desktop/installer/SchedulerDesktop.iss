; All values below are supplied by scripts/build-installer.ps1. Keep this
; installer independent from the developer machine and from network downloads.
#ifndef AppVersion
  #error AppVersion must be supplied by the build script.
#endif

#ifndef SourceDir
  #error SourceDir must be supplied by the build script.
#endif

#ifndef OutputDir
  #error OutputDir must be supplied by the build script.
#endif

#ifndef IncludeWebView2
  #define IncludeWebView2 0
#endif

#if IncludeWebView2 == "1"
  #ifndef WebView2Installer
    #error WebView2Installer is required when IncludeWebView2 is enabled.
  #endif
#endif

#define AppName "VNU-UET Custom Timetable Scheduler"
#define AppPublisher "VNU-UET"
#define AppExeName "Scheduler.Desktop.exe"
#define AppId "{{E0DFD8A4-3C3E-4DC7-BB99-7EA58D8E2A5F}"
#define WebView2ClientId "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"
#define WebView2InstallerName "MicrosoftEdgeWebView2Setup.exe"
#define AppIconFile "{#SourceDir}\app.ico"

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=VNU-UET-Custom-Timetable-Scheduler-{#AppVersion}-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile={#AppIconFile}
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
LicenseFile={#SourceDir}\THIRD_PARTY_NOTICES.md

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
#if IncludeWebView2 == "1"
Source: "{#WebView2Installer}"; DestDir: "{tmp}"; DestName: "{#WebView2InstallerName}"; Flags: dontcopy ignoreversion
#endif

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Code]
function IsWebView2RuntimeInstalled(): Boolean;
var
  Version: String;
begin
  Result :=
    RegQueryStringValue(HKLM64, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{#WebView2ClientId}', 'pv', Version) or
    RegQueryStringValue(HKLM32, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{#WebView2ClientId}', 'pv', Version) or
    RegQueryStringValue(HKCU64, 'Software\Microsoft\EdgeUpdate\Clients\{#WebView2ClientId}', 'pv', Version) or
    RegQueryStringValue(HKCU32, 'Software\Microsoft\EdgeUpdate\Clients\{#WebView2ClientId}', 'pv', Version);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';

  if IsWebView2RuntimeInstalled() then
    exit;

#if IncludeWebView2 == "1"
  ExtractTemporaryFile('{#WebView2InstallerName}');
  if not Exec(
    ExpandConstant('{tmp}\{#WebView2InstallerName}'),
    '/silent /install',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode
  ) then begin
    Result := 'Microsoft Edge WebView2 Runtime could not be started. Install the supplied runtime manually, then run setup again.';
    exit;
  end;

  if ResultCode <> 0 then begin
    Result := Format('Microsoft Edge WebView2 Runtime installation failed with exit code %d.', [ResultCode]);
    exit;
  end;

  if not IsWebView2RuntimeInstalled() then
    Result := 'Microsoft Edge WebView2 Runtime was not detected after installation. Install it manually, then run setup again.';
#else
  Result := 'Microsoft Edge WebView2 Runtime is required. Install the Evergreen WebView2 Runtime, then run this setup again.';
#endif
end;
