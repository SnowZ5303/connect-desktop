; Instalador do Connect Desktop (Windows x64).
; Compilado por build/publish-windows.ps1 com /DMyAppVersion=... e /DSourceDir=...

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

[Setup]
AppId={{8F3C2A51-6D4B-4E9A-9C7E-2B1A5D0C3E77}
AppName=Connect Desktop
AppVersion={#MyAppVersion}
DefaultDirName={autopf}\Connect Desktop
DefaultGroupName=Connect Desktop
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputBaseFilename=ConnectDesktop-Setup-win-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\ConnectDesktop.exe

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na área de trabalho"; GroupDescription: "Atalhos:"; Flags: unchecked
Name: "startwithwindows"; Description: "Iniciar com o Windows"; GroupDescription: "Opções:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Connect Desktop"; Filename: "{app}\ConnectDesktop.exe"
Name: "{autodesktop}\Connect Desktop"; Filename: "{app}\ConnectDesktop.exe"; Tasks: desktopicon

[Registry]
Root: HKA; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Connect Desktop"; ValueData: """{app}\ConnectDesktop.exe"""; Flags: uninsdeletevalue; Tasks: startwithwindows

[Run]
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""Connect Desktop"""; Flags: runhidden waituntilterminated
Filename: "netsh"; Parameters: "advfirewall firewall add rule name=""Connect Desktop"" dir=in action=allow program=""{app}\ConnectDesktop.exe"" enable=yes profile=any"; Flags: runhidden waituntilterminated
Filename: "{app}\ConnectDesktop.exe"; Description: "Abrir Connect Desktop"; Flags: nowait postinstall skipifsilent
Filename: "{app}\ConnectDesktop.exe"; Flags: nowait runasoriginaluser; Check: WizardSilent

[UninstallRun]
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""Connect Desktop"""; Flags: runhidden waituntilterminated; RunOnceId: "DeleteFirewallRule"
