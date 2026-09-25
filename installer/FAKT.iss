; Установщик FAKT (Inno Setup 6). Собирается build\Build-Release.ps1:
;   ISCC.exe /DAppVersion=<версия> /DSourceDir=<dist\FAKT> /O<dist> installer\FAKT.iss
; Установщик ничего не загружает из интернета: .NET Framework 4.8 должен быть установлен заранее
; (офлайн-установщик NDP48-x86-x64-AllOS-ENU.exe), обновления Windows 7 — см. docs\SETUP.md.
; Статус: сценарий не компилировался на машине разработки (Inno Setup не установлен) — требует проверки.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\FAKT"
#endif

[Setup]
AppId={{6F3B7A52-9C1E-4D8B-A0F4-2E7C51D9B6A3}
AppName=FAKT
AppVersion={#AppVersion}
AppVerName=FAKT {#AppVersion}
AppPublisher=FAKT
DefaultDirName={autopf}\FAKT
DefaultGroupName=FAKT
DisableProgramGroupPage=yes
OutputBaseFilename=FAKT-{#AppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
MinVersion=6.1sp1
PrivilegesRequired=admin
WizardStyle=modern
SetupIconFile=..\src\Fakt.Desktop\Assets\fakt.ico
UninstallDisplayIcon={app}\FAKT.exe

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать значок на рабочем столе"; Flags: unchecked
Name: "tls12"; Description: "Включить TLS 1.2 для клиентских соединений (Windows 7: нужно для SQL Server и HTTPS)"; Check: IsWindows7

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\FAKT"; Filename: "{app}\FAKT.exe"
Name: "{group}\Руководство по установке"; Filename: "{app}\docs\SETUP.md"
Name: "{autodesktop}\FAKT"; Filename: "{app}\FAKT.exe"; Tasks: desktopicon

[Run]
Filename: "{win}\regedit.exe"; Parameters: "/s ""{app}\windows7\tls12-client.reg"""; Tasks: tls12; Flags: runhidden waituntilterminated
Filename: "{app}\FAKT.exe"; Description: "Запустить FAKT"; Flags: nowait postinstall skipifsilent

[Code]
{ .NET Framework 4.8: значение Release не меньше 528040. }
function IsDotNet48Installed(): Boolean;
var
  Release: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release) and (Release >= 528040);
end;

function IsWindows7(): Boolean;
var
  Version: TWindowsVersion;
begin
  GetWindowsVersionEx(Version);
  Result := (Version.Major = 6) and (Version.Minor = 1);
end;

function InitializeSetup(): Boolean;
begin
  Result := IsDotNet48Installed();
  if not Result then
    MsgBox('Для FAKT нужен .NET Framework 4.8 (не 4.8.1). Установите его офлайн-установщиком ' +
      'NDP48-x86-x64-AllOS-ENU.exe и запустите установку FAKT снова. Подробности — docs\SETUP.md.', mbError, MB_OK);
end;
