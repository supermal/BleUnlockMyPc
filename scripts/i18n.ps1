# Textos de install.ps1 / uninstall.ps1 en chino, inglés y español. El idioma sigue al de Windows del
# usuario (Get-UICulture): zh*, en* y es* tienen el suyo; cualquier otro usa inglés. Se carga con
# «. (Join-Path $PSScriptRoot 'i18n.ps1')». Guardar SIEMPRE en UTF-8 con BOM: PowerShell 5.1 lee los
# scripts sin BOM con la página de códigos del sistema y estropea los caracteres no ASCII.

$script:UiLang = switch ((Get-UICulture).TwoLetterISOLanguageName) {
    'zh' { 'zh' }
    'es' { 'es' }
    default { 'en' }
}

# Nombre del acceso directo en cada idioma: el desinstalador los prueba todos
$script:ShortcutNames = @{
    zh = 'BleUnlock 管理器'
    en = 'BleUnlock Manager'
    es = 'Gestor de BleUnlock'
}

$script:Messages = @{
    ScFailed            = @{ zh = '{0} 失败，sc.exe 退出码 {1}（{2}）'; en = '{0} failed, sc.exe exit code {1} ({2})'; es = '{0} falló, código de salida de sc.exe {1} ({2})' }
    ArtifactMissing     = @{ zh = "未找到 {0} 构建产物（{1}）。已检查：`n  {2}`n请先按 README 构建各项目"; en = "{0} build output not found ({1}). Checked:`n  {2}`nBuild the projects first as described in the README"; es = "No se encontró la compilación de {0} ({1}). Se comprobó:`n  {2}`nCompila primero los proyectos según el README" }
    NameService         = @{ zh = '服务'; en = 'Service'; es = 'Servicio' }
    NameHelper          = @{ zh = '助手'; en = 'Helper'; es = 'Asistente' }
    NameCredProvider    = @{ zh = '凭据提供程序'; en = 'Credential provider'; es = 'Proveedor de credenciales' }
    NameManagerPublish  = @{ zh = '管理端（发布产物）'; en = 'Manager (published output)'; es = 'Gestor (salida publicada)' }
    ServiceOutput       = @{ zh = '服务输出：{0}'; en = 'Service output: {0}'; es = 'Salida del servicio: {0}' }
    HelperOutput        = @{ zh = '助手输出：{0}'; en = 'Helper output: {0}'; es = 'Salida del asistente: {0}' }
    CredProviderOutput  = @{ zh = '凭据提供程序：{0}'; en = 'Credential provider: {0}'; es = 'Proveedor de credenciales: {0}' }
    ManagerOutput       = @{ zh = '管理端发布：{0}'; en = 'Manager output: {0}'; es = 'Salida del Gestor: {0}' }
    StoppingService     = @{ zh = '停止已有服务…'; en = 'Stopping the existing service…'; es = 'Deteniendo el servicio existente…' }
    ActStopService      = @{ zh = '停止服务'; en = 'Stopping the service'; es = 'Detener el servicio' }
    StopTimeout         = @{ zh = '等待服务停止超时（30 秒），当前状态：{0}。请手动停止后重试。'; en = 'Timed out waiting for the service to stop (30 s), current state: {0}. Stop it manually and try again.'; es = 'Se agotó la espera de la detención del servicio (30 s), estado actual: {0}. Detenlo manualmente y vuelve a intentarlo.' }
    ActDeleteService    = @{ zh = '删除服务'; en = 'Deleting the service'; es = 'Eliminar el servicio' }
    DeleteStuck         = @{ zh = '服务已标记为删除但仍未消失（30 秒）。请关闭 services.msc / 事件查看器等占用窗口，或重启后重试。'; en = 'The service is marked for deletion but has not gone away (30 s). Close windows that hold it, such as services.msc or Event Viewer, or restart and try again.'; es = 'El servicio está marcado para eliminarse pero no ha desaparecido (30 s). Cierra las ventanas que lo retienen, como services.msc o el Visor de eventos, o reinicia y vuelve a intentarlo.' }
    ManagerStillRunning = @{ zh = '管理端进程仍未退出（已等待 10 秒），请手动结束后重试。'; en = 'The Manager process is still running (waited 10 s); end it manually and try again.'; es = 'El proceso del Gestor sigue en ejecución (se esperó 10 s); termínalo manualmente y vuelve a intentarlo.' }
    CopyingFiles        = @{ zh = '拷贝服务与助手…'; en = 'Copying the service and helper…'; es = 'Copiando el servicio y el asistente…' }
    OldManagerDirStuck  = @{ zh = '旧管理端目录删除失败：{0}'; en = 'Could not delete the old Manager folder: {0}'; es = 'No se pudo eliminar la carpeta anterior del Gestor: {0}' }
    ManagerDeployed     = @{ zh = '管理端已部署：{0}'; en = 'Manager deployed: {0}'; es = 'Gestor instalado: {0}' }
    ConfigCreated       = @{ zh = '已生成配置模板：{0}'; en = 'Configuration template created: {0}'; es = 'Plantilla de configuración creada: {0}' }
    ConfigKept          = @{ zh = '保留已有配置：{0}'; en = 'Keeping the existing configuration: {0}'; es = 'Se conserva la configuración existente: {0}' }
    TighteningAcl       = @{ zh = '收紧配置目录权限（仅 SYSTEM 与 Administrators）…'; en = 'Restricting the configuration folder (SYSTEM and Administrators only)…'; es = 'Restringiendo la carpeta de configuración (solo SYSTEM y Administradores)…' }
    IcaclsDirFailed     = @{ zh = 'icacls 收紧目录权限失败（{0}），退出码 {1}'; en = 'icacls could not restrict the folder ({0}), exit code {1}'; es = 'icacls no pudo restringir la carpeta ({0}), código de salida {1}' }
    IcaclsFileFailed    = @{ zh = 'icacls 修复文件权限失败（{0}），退出码 {1}'; en = 'icacls could not fix the file permissions ({0}), exit code {1}'; es = 'icacls no pudo corregir los permisos del archivo ({0}), código de salida {1}' }
    AclNotApplied       = @{ zh = '权限收紧未生效（{0}）：SDDL={1}。请以管理员重试；若问题持续请反馈。'; en = 'The permission change did not take effect ({0}): SDDL={1}. Try again as administrator; report it if it persists.'; es = 'El cambio de permisos no surtió efecto ({0}): SDDL={1}. Vuelve a intentarlo como administrador; si persiste, notifícalo.' }
    RegisteringCp       = @{ zh = '注册凭据提供程序…'; en = 'Registering the credential provider…'; es = 'Registrando el proveedor de credenciales…' }
    RegsvrFailed        = @{ zh = 'regsvr32 失败，退出码 {0}'; en = 'regsvr32 failed, exit code {0}'; es = 'regsvr32 falló, código de salida {0}' }
    EventSourceCreated  = @{ zh = '已注册事件日志源 BleUnlock'; en = 'Registered the BleUnlock event log source'; es = 'Se registró el origen de eventos BleUnlock' }
    ActCreateService    = @{ zh = '创建服务'; en = 'Creating the service'; es = 'Crear el servicio' }
    ActSetDescription   = @{ zh = '设置服务描述'; en = 'Setting the service description'; es = 'Establecer la descripción del servicio' }
    ActSetRecovery      = @{ zh = '设置失败恢复策略'; en = 'Setting the recovery policy'; es = 'Establecer la directiva de recuperación' }
    ConfigReadFailed    = @{ zh = '读取或解析配置文件失败：{0}（{1}）。通常是权限异常或文件损坏，请修复后重试。'; en = 'Could not read or parse the configuration file: {0} ({1}). Usually a permission problem or a damaged file; fix it and try again.'; es = 'No se pudo leer o interpretar el archivo de configuración: {0} ({1}). Suele deberse a permisos o a un archivo dañado; corrígelo y vuelve a intentarlo.' }
    ActStartService     = @{ zh = '启动服务'; en = 'Starting the service'; es = 'Iniciar el servicio' }
    ServiceStarted      = @{ zh = '服务已启动。日志目录：{0}'; en = 'Service started. Log folder: {0}'; es = 'Servicio iniciado. Carpeta de registros: {0}' }
    ConfigIncomplete    = @{ zh = '还没有选择解锁设备或填写密码：服务已以「未配置」状态启动，请在管理器的设置向导中完成设置。'; en = 'No unlock device or password has been set yet: the service started in the "not set up" state. Finish the setup in the Manager''s setup wizard.'; es = 'Aún no se ha elegido el dispositivo ni la contraseña: el servicio se ha iniciado en estado «sin configurar». Termina la configuración en el asistente del Gestor.' }
    CreatingShortcut    = @{ zh = '创建开始菜单快捷方式…'; en = 'Creating the Start menu shortcut…'; es = 'Creando el acceso directo del menú Inicio…' }
    ShortcutDescription = @{ zh = 'BleUnlock 管理器（实时监控与配置）'; en = 'BleUnlock Manager (live monitoring and configuration)'; es = 'Gestor de BleUnlock (supervisión en directo y configuración)' }
    RegisteringTask     = @{ zh = '注册计划任务 {0}（用户：{1}；登录自启、最高权限）…'; en = 'Registering scheduled task {0} (user: {1}; starts at sign-in with full rights)…'; es = 'Registrando la tarea programada {0} (usuario: {1}; se inicia al iniciar sesión con permisos completos)…' }
    TaskDescription     = @{ zh = 'BleUnlock 管理器：登录时自动启动（最高权限，无 UAC 弹窗）'; en = 'BleUnlock Manager: starts at sign-in (full rights, no UAC prompt)'; es = 'Gestor de BleUnlock: se inicia al iniciar sesión (permisos completos, sin aviso de UAC)' }
    LegacyFound         = @{ zh = '发现旧版 AutoUnlock，正在移除并迁移数据…'; en = 'Found the previous AutoUnlock version; removing it and migrating its data…'; es = 'Se encontró la versión anterior AutoUnlock; quitándola y migrando sus datos…' }
    LegacyFilesLeft     = @{ zh = '旧版程序目录未能完全删除：{0}（可在重启后手动删除）'; en = 'The old program folder could not be fully deleted: {0} (you can delete it manually after a restart)'; es = 'No se pudo eliminar por completo la carpeta antigua del programa: {0} (puedes borrarla a mano tras reiniciar)' }
    LegacyDataSkipped   = @{ zh = '{1} 中已有配置，以它为准；旧数据目录 {0} 保持不动。'; en = '{1} already has a configuration, which is kept; the old data folder {0} is left untouched.'; es = '{1} ya tiene una configuración, que se conserva; la carpeta de datos antigua {0} no se toca.' }
    LegacyDataMoved     = @{ zh = '已将配置和日志从 {0} 迁移到 {1}'; en = 'Moved the configuration and logs from {0} to {1}'; es = 'Se trasladaron la configuración y los registros de {0} a {1}' }
    LegacyRemoved       = @{ zh = '旧版 AutoUnlock 已移除。'; en = 'The previous AutoUnlock version was removed.'; es = 'Se quitó la versión anterior AutoUnlock.' }
    StartingManager     = @{ zh = '启动管理端…'; en = 'Starting the Manager…'; es = 'Iniciando el Gestor…' }
    ManagerStarted      = @{ zh = '管理端已安装并已启动（见托盘图标）。'; en = 'The Manager is installed and running (see the tray icon).'; es = 'El Gestor está instalado y en ejecución (mira el icono de la bandeja).' }
    ManagerNextLogon    = @{ zh = '管理端将在该用户下次登录时启动（{0}）'; en = 'The Manager will start the next time this user signs in ({0})'; es = 'El Gestor se iniciará la próxima vez que este usuario inicie sesión ({0})' }
    ServiceDeleted      = @{ zh = '服务已删除'; en = 'Service deleted'; es = 'Servicio eliminado' }
    TaskRemoved         = @{ zh = '管理端计划任务已注销'; en = 'Manager scheduled task removed'; es = 'Tarea programada del Gestor eliminada' }
    TaskMissing         = @{ zh = '管理端计划任务不存在（跳过）'; en = 'Manager scheduled task not found (skipped)'; es = 'No existe la tarea programada del Gestor (se omite)' }
    ShortcutRemoved     = @{ zh = '管理端快捷方式已删除'; en = 'Manager shortcut removed'; es = 'Acceso directo del Gestor eliminado' }
    CpUnregistered      = @{ zh = '凭据提供程序已注销'; en = 'Credential provider unregistered'; es = 'Proveedor de credenciales anulado' }
    RegsvrUnregFailed   = @{ zh = 'regsvr32 /u 失败，退出码 {0}。若注销失败，建议重启后再删除程序文件。'; en = 'regsvr32 /u failed, exit code {0}. If unregistering fails, restart before deleting the program files.'; es = 'regsvr32 /u falló, código de salida {0}. Si no se puede anular el registro, reinicia antes de eliminar los archivos del programa.' }
    FilesDeleteFailed   = @{ zh = '程序文件删除失败（可能仍被占用，如 winlogon 持有 DLL）：{0}'; en = 'Could not delete the program files (they may still be in use, e.g. winlogon holding the DLL): {0}'; es = 'No se pudieron eliminar los archivos del programa (puede que sigan en uso, p. ej. winlogon retiene la DLL): {0}' }
    FilesDeleted        = @{ zh = '程序文件已删除'; en = 'Program files deleted'; es = 'Archivos del programa eliminados' }
    ConfirmDeleteData   = @{ zh = '是否删除配置与日志（{0}）？输入 yes 确认'; en = 'Delete the configuration and logs ({0})? Type yes to confirm'; es = '¿Eliminar la configuración y los registros ({0})? Escribe sí para confirmar' }
    DataDeleteFailed    = @{ zh = '配置与日志删除失败：{0}'; en = 'Could not delete the configuration and logs: {0}'; es = 'No se pudieron eliminar la configuración y los registros: {0}' }
    DataDeleted         = @{ zh = '配置与日志已删除'; en = 'Configuration and logs deleted'; es = 'Configuración y registros eliminados' }
    DataKept            = @{ zh = '已保留 {0}'; en = 'Kept {0}'; es = 'Se conservó {0}' }
    UninstallDone       = @{ zh = '卸载完成。建议重启一次，确保 winlogon 释放凭据提供程序 DLL。'; en = 'Uninstall complete. Restart once so winlogon releases the credential provider DLL.'; es = 'Desinstalación completa. Reinicia una vez para que winlogon libere la DLL del proveedor de credenciales.' }
    ManagerSettingsKept = @{ zh = '（管理端界面设置已保留：{0}）'; en = '(Manager interface settings were kept: {0})'; es = '(Se conservaron los ajustes de la interfaz del Gestor: {0})' }
}

# Texto de la clave en el idioma actual, con los argumentos aplicados como en -f
function T([string]$Key, [object[]]$Arguments = @()) {
    $entry = $script:Messages[$Key]
    if ($null -eq $entry) { return "!$Key!" }
    $text = $entry[$script:UiLang]
    if ($Arguments.Count -gt 0) { return $text -f $Arguments }
    return $text
}

# Respuesta afirmativa del desinstalador en cualquiera de los tres idiomas
function Test-Yes([string]$Answer) {
    return @('yes', 'y', '是', 'sí', 'si', 's') -contains $Answer.Trim().ToLowerInvariant()
}
