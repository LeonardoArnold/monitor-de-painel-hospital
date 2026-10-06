; Instalador do G-HOSP Panel Watchdog (Inno Setup 6)
; - Instalação por usuário, sem administrador.
; - NÃO configura inicialização automática com o Windows (fase de testes).

#define AppName "G-HOSP Panel Watchdog"
#define AppVersion "1.0.0"
#define AppExe "G-HOSP Panel Watchdog.exe"
#define Src "..\dist\app"

[Setup]
AppId={{6B0E3A57-2C1D-4C7B-9F0E-5D1A8B6C4E21}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppName}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputBaseFilename={#AppName} Setup
SetupIconFile=..\assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
DisableProgramGroupPage=yes
CloseApplications=no

[Languages]
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na Área de Trabalho"; Flags: unchecked

[Files]
Source: "{#Src}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Src}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Src}\docs\*.md"; DestDir: "{app}\docs"; Flags: ignoreversion

[Dirs]
Name: "{localappdata}\GHospPanelWatchdog"
Name: "{localappdata}\GHospPanelWatchdog\logs"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Configurações do painel"; Filename: "{app}\{#AppExe}"; Parameters: "--config"
Name: "{group}\Diagnóstico do painel"; Filename: "{app}\{#AppExe}"; Parameters: "--diag"
Name: "{group}\Pasta de logs"; Filename: "{localappdata}\GHospPanelWatchdog\logs"
Name: "{group}\Leia-me"; Filename: "{app}\README.md"
Name: "{group}\Desinstalar {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon
; Propositalmente SEM atalho em Inicializar/Startup e SEM entrada no registro Run.

[Run]
Filename: "{app}\{#AppExe}"; Parameters: "--config"; Description: "Iniciar agora e abrir as configurações (monitor, áudio, credenciais)"; Flags: postinstall nowait skipifsilent

[UninstallRun]
Filename: "{app}\{#AppExe}"; Parameters: "--stop-all"; RunOnceId: "StopWatchdog"; Flags: runhidden waituntilterminated skipifdoesntexist

[Code]
var
  PurgeData: Boolean;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
  Exe: String;
begin
  { Atualização: encerra a instância em execução (o painel continua aberto). }
  Exe := ExpandConstant('{app}\{#AppExe}');
  if FileExists(Exe) then
    Exec(Exe, '--stop', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  PurgeData := MsgBox('Remover também as configurações, as credenciais do G-HOSP (Gerenciador de Credenciais), ' +
    'o perfil exclusivo do Edge do painel e os LOGS?' + #13#10#13#10 +
    'Escolha "Não" para mantê-los (úteis para diagnóstico ou reinstalação).',
    mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Code: Integer;
  Exe: String;
begin
  if (CurUninstallStep = usUninstall) and PurgeData then
  begin
    Exe := ExpandConstant('{app}\{#AppExe}');
    if FileExists(Exe) then
      Exec(Exe, '--purge-data', '', SW_HIDE, ewWaitUntilTerminated, Code);
  end;
end;
