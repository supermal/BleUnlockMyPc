; Instalador de BleUnlock (Inno Setup 6). No contiene lógica de instalación propia: extrae el paquete
; y ejecuta scripts\install.ps1, el mismo que se usa a mano; la desinstalación ejecuta uninstall.ps1.
; Lo genera build-installer.ps1, que define AppVersion, StagingDir y OutDir con /D.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef StagingDir
  #define StagingDir "staging"
#endif
#ifndef OutDir
  #define OutDir "out"
#endif

[Setup]
AppId={{8F3C7B5E-2A41-4D6B-9E07-5B1C3A9D2F64}
AppName=BleUnlock
AppVersion={#AppVersion}
AppVerName=BleUnlock {#AppVersion}
AppPublisher=BleUnlock
; Debe coincidir con InstallDir de install.ps1 / uninstall.ps1
DefaultDirName={commonpf64}\BleUnlock
DisableDirPage=yes
; Mismo AppId que la versión anterior (AutoUnlock): se trata como actualización y su entrada de
; «Aplicaciones» se sustituye. La carpeta antigua no se reutiliza; install.ps1 la elimina
UsePreviousAppDir=no
DisableProgramGroupPage=yes
DisableReadyPage=no
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutDir}
OutputBaseFilename=BleUnlock-Setup-{#AppVersion}
SetupIconFile=..\src\BleUnlockManager\Assets\AppIcon.ico
UninstallDisplayIcon={app}\Manager\BleUnlockManager.exe
UninstallDisplayName=BleUnlock
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
LanguageDetectionMethod=uilanguage
ShowLanguageDialog=auto
; Los procesos (servicio, gestor) los detiene install.ps1
CloseApplications=no
RestartApplications=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "zh"; MessagesFile: "Languages\ChineseSimplified.isl"
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"

[CustomMessages]
en.InstallingVc=Installing the Visual C++ runtime…
zh.InstallingVc=正在安装 Visual C++ 运行库…
es.InstallingVc=Instalando el runtime de Visual C++…
en.InstallingApp=Installing the service, helper, credential provider and Manager…
zh.InstallingApp=正在安装服务、提示助手、凭据提供程序和管理器…
es.InstallingApp=Instalando el servicio, el asistente, el proveedor de credenciales y el Gestor…
en.VcFailed=The Visual C++ runtime could not be installed (code %1). Automatic unlock on the lock screen may not work until it is installed.
zh.VcFailed=Visual C++ 运行库安装失败（代码 %1）。在装好之前，锁屏界面的自动解锁可能无法使用。
es.VcFailed=No se pudo instalar el runtime de Visual C++ (código %1). El desbloqueo automático en la pantalla de bloqueo puede no funcionar hasta instalarlo.
en.InstallFailed=Installation did not complete. The details are in:%n%n%1
zh.InstallFailed=安装没有完成。详细信息见：%n%n%1
es.InstallFailed=La instalación no se completó. Los detalles están en:%n%n%1
en.Removing=Removing the service, helper, credential provider and Manager…
zh.Removing=正在移除服务、提示助手、凭据提供程序和管理器…
es.Removing=Quitando el servicio, el asistente, el proveedor de credenciales y el Gestor…
en.DeleteData=Also delete the configuration and logs?%n%n%1%n%nChoose No to keep them for a later reinstall.
zh.DeleteData=同时删除配置和日志吗？%n%n%1%n%n选「否」会保留它们，以后重新安装时继续使用。
es.DeleteData=¿Eliminar también la configuración y los registros?%n%n%1%n%nElige No para conservarlos para una reinstalación posterior.

[Files]
; Paquete con la misma estructura que el repositorio: install.ps1 encuentra cada programa en su sitio
Source: "{#StagingDir}\payload\*"; DestDir: "{tmp}\payload"; Flags: recursesubdirs createallsubdirs ignoreversion deleteafterinstall
Source: "{#StagingDir}\vc_redist.x64.exe"; DestDir: "{tmp}"; Flags: ignoreversion deleteafterinstall
; Lo único que queda registrado por Inno: los scripts para desinstalar
Source: "{#StagingDir}\payload\scripts\uninstall.ps1"; DestDir: "{app}\setup"; Flags: ignoreversion
Source: "{#StagingDir}\payload\scripts\i18n.ps1"; DestDir: "{app}\setup"; Flags: ignoreversion

[Code]
function PowerShellExe(): String;
begin
  Result := ExpandConstant('{sysnative}\WindowsPowerShell\v1.0\powershell.exe');
end;

function RunScript(const Script, Args: String; var Code: Integer): Boolean;
begin
  Result := Exec(PowerShellExe(), '-NoProfile -ExecutionPolicy Bypass -File "' + Script + '" ' + Args,
    '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
  LogPath: String;
begin
  if CurStep <> ssPostInstall then
    Exit;

  // El instalador de Microsoft no hace nada si ya hay una versión igual o más nueva (1638)
  WizardForm.StatusLabel.Caption := CustomMessage('InstallingVc');
  if not Exec(ExpandConstant('{tmp}\vc_redist.x64.exe'), '/install /quiet /norestart', '', SW_HIDE,
              ewWaitUntilTerminated, Code) then
    Code := -1;
  if (Code <> 0) and (Code <> 1638) and (Code <> 3010) then
    MsgBox(FmtMessage(CustomMessage('VcFailed'), [IntToStr(Code)]), mbError, MB_OK);

  WizardForm.StatusLabel.Caption := CustomMessage('InstallingApp');
  LogPath := ExpandConstant('{commonappdata}\BleUnlock\setup.log');
  if (not RunScript(ExpandConstant('{tmp}\payload\run-install.ps1'), '-LogPath "' + LogPath + '"', Code)) or (Code <> 0) then
    MsgBox(FmtMessage(CustomMessage('InstallFailed'), [LogPath]), mbError, MB_OK);
end;

// El proveedor de credenciales solo se carga en la pantalla de bloqueo tras reiniciar
function NeedRestart(): Boolean;
begin
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Code: Integer;
  TempDir, DataDir: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    // uninstall.ps1 borra la carpeta de instalación entera, incluido él mismo: se ejecuta una copia
    TempDir := AddBackslash(GetTempDir()) + 'BleUnlockUninstall';
    ForceDirectories(TempDir);
    FileCopy(ExpandConstant('{app}\setup\uninstall.ps1'), TempDir + '\uninstall.ps1', False);
    FileCopy(ExpandConstant('{app}\setup\i18n.ps1'), TempDir + '\i18n.ps1', False);
    UninstallProgressForm.StatusLabel.Caption := CustomMessage('Removing');
    RunScript(TempDir + '\uninstall.ps1', '-KeepData', Code);
    DelTree(TempDir, True, True, True);
  end;

  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{commonappdata}\BleUnlock');
    if DirExists(DataDir) and (not UninstallSilent()) and
       (MsgBox(FmtMessage(CustomMessage('DeleteData'), [DataDir]), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
      DelTree(DataDir, True, True, True);
  end;
end;
