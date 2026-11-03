#Requires -RunAsAdministrator
<#
    Instala BleUnlock: servicio, asistente, proveedor de credenciales y Gestor.
    Idempotente: se puede volver a ejecutar para actualizar. El Gestor se inicia con la sesión mediante una
    tarea programada (permisos completos, sin aviso de UAC). Los mensajes siguen el idioma de Windows (i18n.ps1).
#>
param(
    [string]$Configuration     = 'Release',
    [string]$SourceDir         = '',
    [string]$ManagerSourceDir  = '',
    [string]$InstallDir        = 'C:\Program Files\BleUnlock',
    [string]$DataDir           = (Join-Path $env:ProgramData 'BleUnlock'),
    [string]$ManagerUser       = ''
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'i18n.ps1')
$serviceName = 'BleUnlockService'
$repoRoot    = Split-Path $PSScriptRoot -Parent
$tfm         = 'net10.0-windows10.0.19041.0'

# sc.exe no lanza excepciones: se comprueba el codigo de salida de forma explicita
function Assert-ScOk {
    param([string]$Action, [int[]]$Allowed = @(0))
    if ($Allowed -notcontains $LASTEXITCODE) {
        $msg = (New-Object System.ComponentModel.Win32Exception($LASTEXITCODE)).Message
        throw (T 'ScFailed' $Action, $LASTEXITCODE, $msg)
    }
}

# 在候选目录中挑选含目标文件的产物目录；多个命中时取最新构建的那个。
# bin\x64\... 与 bin\... 都可能存在：逐项目构建与通过 .sln 构建的输出布局不同
function Resolve-Artifact {
    param([string]$Name, [string[]]$Candidates, [string]$Leaf)
    $best = $null
    $bestTime = [datetime]::MinValue
    foreach ($dir in $Candidates) {
        $leafPath = Join-Path $dir $Leaf
        if (Test-Path $leafPath) {
            $time = (Get-Item $leafPath).LastWriteTimeUtc
            if ($time -gt $bestTime) { $best = $dir; $bestTime = $time }
        }
    }
    if (-not $best) {
        throw (T 'ArtifactMissing' $Name, $Leaf, ($Candidates -join "`n  "))
    }
    return $best
}

$serviceCandidates = if ($SourceDir) { @($SourceDir) } else { @(
    (Join-Path $repoRoot "src\BleUnlockService\bin\x64\$Configuration\$tfm"),
    (Join-Path $repoRoot "src\BleUnlockService\bin\$Configuration\$tfm")
) }
$serviceOut = Resolve-Artifact (T 'NameService') $serviceCandidates 'BleUnlockService.exe'
$helperOut  = Resolve-Artifact (T 'NameHelper') @(
    (Join-Path $repoRoot "src\BleUnlockHelper\bin\x64\$Configuration\$tfm"),
    (Join-Path $repoRoot "src\BleUnlockHelper\bin\$Configuration\$tfm")
) 'BleUnlockHelper.exe'
$cpOut      = Resolve-Artifact (T 'NameCredProvider') @(
    (Join-Path $repoRoot "src\BleUnlockCredentialProvider\bin\$Configuration\x64")
) 'BleUnlockCredentialProvider.dll'

# 管理端是自包含发布产物：以 win-x64 publish 目录整体分发
$managerCandidates = if ($ManagerSourceDir) { @($ManagerSourceDir) } else { @(
    (Join-Path $repoRoot "src\BleUnlockManager\bin\$Configuration\net10.0-windows10.0.19041.0\win-x64\publish")
) }
$managerOut = Resolve-Artifact (T 'NameManagerPublish') $managerCandidates 'BleUnlockManager.exe'

Write-Host (T 'ServiceOutput' $serviceOut)
Write-Host (T 'HelperOutput' $helperOut)
Write-Host (T 'CredProviderOutput' $cpOut)
Write-Host (T 'ManagerOutput' $managerOut)

# ── 0. Migración desde AutoUnlock (nombre anterior del producto) ──────────────
# Se quita la instalación antigua con sus nombres de entonces y su carpeta de datos se traslada entera
# a la nueva, de modo que el dispositivo, los umbrales, la contraseña y el idioma se conservan.
# El CLSID del proveedor de credenciales no cambió: el registro de abajo sobrescribe el anterior.
function Invoke-LegacyMigration {
    $legacyService = 'AutoUnlockService'
    $legacyInstall = 'C:\Program Files\AutoUnlock'
    $legacyData    = Join-Path $env:ProgramData 'AutoUnlock'

    $svc = Get-Service -Name $legacyService -ErrorAction SilentlyContinue
    if (-not $svc -and -not (Test-Path $legacyInstall) -and -not (Test-Path $legacyData)) { return }
    Write-Host (T 'LegacyFound')

    if ($svc) {
        if ($svc.Status -ne 'Stopped') {
            sc.exe stop $legacyService | Out-Null
            Assert-ScOk (T 'ActStopService') -Allowed 0, 1061, 1062
            try {
                $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
            } catch [System.ServiceProcess.TimeoutException] {
                throw (T 'StopTimeout' (Get-Service -Name $legacyService).Status)
            }
        }
        sc.exe delete $legacyService | Out-Null
        Assert-ScOk (T 'ActDeleteService')
        $deadline = (Get-Date).AddSeconds(30)
        while ((Get-Service -Name $legacyService -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 500
        }
        if (Get-Service -Name $legacyService -ErrorAction SilentlyContinue) { throw (T 'DeleteStuck') }
    }

    Get-Process -Name 'AutoUnlockHelper', 'AutoUnlockManager' -ErrorAction SilentlyContinue | Stop-Process -Force
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Process -Name 'AutoUnlockHelper', 'AutoUnlockManager' -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 250
    }

    if (Get-ScheduledTask -TaskName 'AutoUnlockManager' -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName 'AutoUnlockManager' -Confirm:$false
    }
    foreach ($name in 'AutoUnlock 管理器', 'AutoUnlock Manager', 'Gestor de AutoUnlock') {
        $lnk = Join-Path ([Environment]::GetFolderPath('CommonStartMenu')) "Programs\$name.lnk"
        if (Test-Path $lnk) { Remove-Item $lnk -Force }
    }

    $legacyDll = Join-Path $legacyInstall 'AutoUnlockCP.dll'
    if (Test-Path $legacyDll) {
        Start-Process regsvr32.exe -ArgumentList "/u /s `"$legacyDll`"" -Wait -NoNewWindow
    }
    if (Test-Path $legacyInstall) {
        Remove-Item -Recurse -Force $legacyInstall -ErrorAction SilentlyContinue
        if (Test-Path $legacyInstall) { Write-Warning (T 'LegacyFilesLeft' $legacyInstall) }
    }

    if ([System.Diagnostics.EventLog]::SourceExists('AutoUnlock')) {
        Remove-EventLog -Source 'AutoUnlock' -ErrorAction SilentlyContinue
    }

    # Datos por usuario (solo los de la cuenta que instala)
    foreach ($pair in @(@('AutoUnlock', 'BleUnlock'), @('AutoUnlockManager', 'BleUnlockManager'))) {
        $old = Join-Path $env:LOCALAPPDATA $pair[0]
        $new = Join-Path $env:LOCALAPPDATA $pair[1]
        if ((Test-Path $old) -and -not (Test-Path $new)) { Move-Item $old $new -ErrorAction SilentlyContinue }
    }

    if (Test-Path $legacyData) {
        if (Test-Path (Join-Path $DataDir 'appsettings.json')) {
            # Ya hay una configuración nueva: manda ella y la carpeta antigua se deja intacta
            Write-Warning (T 'LegacyDataSkipped' $legacyData, $DataDir)
        } else {
            # La carpeta nueva puede existir ya (setup.exe abre ahí setup.log antes de llegar aquí):
            # se mueve el contenido elemento a elemento y lo que ya exista en la nueva se queda
            New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
            foreach ($item in Get-ChildItem $legacyData -Force) {
                $dest = Join-Path $DataDir $item.Name
                if (-not (Test-Path $dest)) { Move-Item $item.FullName $dest }
            }
            Remove-Item -Recurse -Force $legacyData -ErrorAction SilentlyContinue
            # La sección de configuración cambió de nombre; también en las copias de seguridad
            foreach ($json in Get-ChildItem $DataDir -Filter 'appsettings*.json*' -File) {
                $text = [IO.File]::ReadAllText($json.FullName)
                $fixed = [regex]::Replace($text, '"AutoUnlock"(\s*:)', '"BleUnlock"$1', 'IgnoreCase')
                if ($fixed -ne $text) { [IO.File]::WriteAllText($json.FullName, $fixed, (New-Object Text.UTF8Encoding($false))) }
            }
            # El Gestor solo lista registros bleunlock-*.log
            $logs = Join-Path $DataDir 'logs'
            if (Test-Path $logs) {
                foreach ($log in Get-ChildItem $logs -Filter 'autounlock-*' -File) {
                    $target = Join-Path $logs ($log.Name -replace '^autounlock-', 'bleunlock-')
                    if (-not (Test-Path $target)) { Rename-Item $log.FullName $target }
                }
            }
            Write-Host (T 'LegacyDataMoved' $legacyData, $DataDir)
        }
    }
    Write-Host (T 'LegacyRemoved')
}
Invoke-LegacyMigration

# ── 1. 停止旧服务 ────────────────────────────────────────────────────────────
$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host (T 'StoppingService')
    if ($existing.Status -ne 'Stopped') {
        # 1061 = 正在停止/启动中，1062 = 未运行：都可继续等待实际状态
        sc.exe stop $serviceName | Out-Null
        Assert-ScOk (T 'ActStopService') -Allowed 0, 1061, 1062
        try {
            $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
        } catch [System.ServiceProcess.TimeoutException] {
            throw (T 'StopTimeout' (Get-Service -Name $serviceName).Status)
        }
    }
    sc.exe delete $serviceName | Out-Null
    Assert-ScOk (T 'ActDeleteService')
    # El servicio puede quedar "marcado para eliminar" hasta liberar los handles: se espera a que desaparezca
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Service -Name $serviceName -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
    }
    if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
        throw (T 'DeleteStuck')
    }
}

# 助手与管理端在交互会话中运行，可能占用安装目录中的文件
Get-Process -Name 'BleUnlockHelper' -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process -Name 'BleUnlockManager' -ErrorAction SilentlyContinue | Stop-Process -Force
# 结束是异步的：等旧进程完全退出，避免随后替换文件时被占用
$deadline = (Get-Date).AddSeconds(10)
while ((Get-Process -Name 'BleUnlockManager' -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 250
}
if (Get-Process -Name 'BleUnlockManager' -ErrorAction SilentlyContinue) {
    throw (T 'ManagerStillRunning')
}

# ── 2. 目录与文件 ───────────────────────────────────────────────────────────
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $DataDir 'logs') | Out-Null

Write-Host (T 'CopyingFiles')
Copy-Item -Path (Join-Path $serviceOut '*') -Destination $InstallDir -Recurse -Force
Copy-Item -Path (Join-Path $helperOut  '*') -Destination $InstallDir -Recurse -Force

$targetDll = Join-Path $InstallDir 'BleUnlockCP.dll'
# Restos de una sustitución anterior (ya liberados si LogonUI se cerró desde entonces)
Get-ChildItem -Path $InstallDir -Filter 'BleUnlockCP.dll.*.old' -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue
try {
    Copy-Item -Path (Join-Path $cpOut 'BleUnlockCredentialProvider.dll') -Destination $targetDll -Force -ErrorAction Stop
}
catch [System.IO.IOException] {
    # La DLL está cargada (pantalla de bloqueo o una petición de credenciales abierta). Windows deja
    # renombrar un fichero en uso: la antigua se aparta y la nueva se carga en el siguiente bloqueo,
    # sin reiniciar. El resto se borra en la próxima instalación
    $aside = '{0}.{1:yyyyMMddHHmmss}.old' -f $targetDll, (Get-Date)
    Move-Item -Path $targetDll -Destination $aside -Force
    Copy-Item -Path (Join-Path $cpOut 'BleUnlockCredentialProvider.dll') -Destination $targetDll -Force
    Write-Host (T 'CpDllInUse' $aside)
}

# 管理端：整体替换 Manager 子目录（干净升级，避免上一版本残留文件）
$managerDir = Join-Path $InstallDir 'Manager'
if (Test-Path $managerDir) {
    Remove-Item -Recurse -Force $managerDir
    if (Test-Path $managerDir) { throw (T 'OldManagerDirStuck' $managerDir) }
}
New-Item -ItemType Directory -Force -Path $managerDir | Out-Null
Copy-Item -Path (Join-Path $managerOut '*') -Destination $managerDir -Recurse -Force
Write-Host (T 'ManagerDeployed' $managerDir)

# ── 3. 配置文件与权限 ───────────────────────────────────────────────────────
$configPath = Join-Path $DataDir 'appsettings.json'
if (-not (Test-Path $configPath)) {
    Copy-Item -Path (Join-Path $serviceOut 'appsettings.json') -Destination $configPath
    Write-Host (T 'ConfigCreated' $configPath)
} else {
    Write-Host (T 'ConfigKept' $configPath)
}
# El fichero de servicio no debe quedarse: el que manda es el de ProgramData
Remove-Item (Join-Path $InstallDir 'appsettings.json') -ErrorAction SilentlyContinue

Write-Host (T 'TighteningAcl')
# Administrators 必须用 SID（*S-1-5-32-544）：组名随系统语言本地化（如西班牙语为 Administradores），
# 英文名会导致 icacls 解析失败（exit 1332）。/inheritance:r 与授权放在同一条命令里完成，
# 子项不经历空 DACL 窗口。
# 注意（Task 19 现场缺陷）：不能对「已存在的文件」用带 /T 的收紧——会把文件清成空 DACL（D:PAI）
# 且 icacls 静默退出 0。这里改为：目录用 /inheritance:r + OI/CI（未来新文件自动继承），
# 已存在的文件逐个显式授权；完成后回读 SDDL 断言，权限静默丢失时立即失败。
$grantDir  = @('/grant:r', 'SYSTEM:(OI)(CI)F', '/grant:r', '*S-1-5-32-544:(OI)(CI)F')
$grantFile = @('/grant:r', 'SYSTEM:F',         '/grant:r', '*S-1-5-32-544:F')
$dirTargets = @($DataDir) + @(Get-ChildItem -Path $DataDir -Directory -Recurse | ForEach-Object { $_.FullName })
foreach ($dir in $dirTargets) {
    icacls $dir /inheritance:r @grantDir | Out-Null
    if ($LASTEXITCODE -ne 0) { throw (T 'IcaclsDirFailed' $dir, $LASTEXITCODE) }
}
foreach ($file in @(Get-ChildItem -Path $DataDir -File -Recurse | ForEach-Object { $_.FullName })) {
    icacls $file @grantFile | Out-Null
    if ($LASTEXITCODE -ne 0) { throw (T 'IcaclsFileFailed' $file, $LASTEXITCODE) }
}

# 回读断言（SDDL 须含 SYSTEM/Administrators 的 FA 授权）
function Assert-AclHasFullControl {
    param([string]$Path)
    $sddl = (Get-Acl -Path $Path).Sddl
    if ($sddl -notmatch ';FA;;;SY' -or $sddl -notmatch ';FA;;;BA') {
        throw (T 'AclNotApplied' $Path, $sddl)
    }
}
foreach ($dir in $dirTargets) { Assert-AclHasFullControl $dir }
foreach ($file in @(Get-ChildItem -Path $DataDir -File -Recurse | ForEach-Object { $_.FullName })) {
    Assert-AclHasFullControl $file
}

# ── 4. 注册凭据提供程序 ─────────────────────────────────────────────────────
Write-Host (T 'RegisteringCp')
$proc = Start-Process regsvr32.exe -ArgumentList "/s `"$targetDll`"" -Wait -PassThru -NoNewWindow
if ($proc.ExitCode -ne 0) { throw (T 'RegsvrFailed' $proc.ExitCode) }

# ── 5. 事件日志源 ───────────────────────────────────────────────────────────
if (-not [System.Diagnostics.EventLog]::SourceExists('BleUnlock')) {
    New-EventLog -LogName Application -Source 'BleUnlock'
    Write-Host (T 'EventSourceCreated')
}

# ── 6. 创建并启动服务 ───────────────────────────────────────────────────────
$exePath = Join-Path $InstallDir 'BleUnlockService.exe'
sc.exe create $serviceName binPath= "`"$exePath`"" start= delayed-auto DisplayName= 'BleUnlock Proximity Unlock' | Out-Null
Assert-ScOk (T 'ActCreateService')
sc.exe description $serviceName 'Locks and unlocks Windows based on the Bluetooth signal of your phone.' | Out-Null
Assert-ScOk (T 'ActSetDescription')
sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
Assert-ScOk (T 'ActSetRecovery')

# 配置无效时服务会拒绝启动（退出码 1）：与其让 SCM 反复失败，不如在安装时就讲清楚。
# 读取/解析失败必须 throw（Task 19 教训：ACL 静默丢失时此处曾只告警并继续，留下坏状态）。
try {
    $config = Get-Content $configPath -Raw | ConvertFrom-Json
} catch {
    throw (T 'ConfigReadFailed' $configPath, $_.Exception.Message)
}
# 配置不完整（新电脑：没有设备、没有密码）时服务照常启动：选设备要用的配对设备列表正是服务提供的。
# 服务处于「未配置」状态，不监测任何设备；管理器启动后会自动打开设置向导
$configReady = -not ([string]::IsNullOrWhiteSpace($config.BleUnlock.Device.Irk) -or
                     [string]::IsNullOrWhiteSpace($config.BleUnlock.Credentials.UserName))
sc.exe start $serviceName | Out-Null
Assert-ScOk (T 'ActStartService')
Write-Host (T 'ServiceStarted' (Join-Path $DataDir 'logs'))
if (-not $configReady) {
    Write-Warning (T 'ConfigIncomplete')
}

# ── 7. 管理端：开始菜单快捷方式与登录自启计划任务 ─────────────────────────
$managerExe = Join-Path $managerDir 'BleUnlockManager.exe'
$taskName   = 'BleUnlockManager'

Write-Host (T 'CreatingShortcut')
$shortcutPath = Join-Path ([Environment]::GetFolderPath('CommonStartMenu')) "Programs\$($script:ShortcutNames[$script:UiLang]).lnk"
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcutPath)
$link.TargetPath = $managerExe
$link.WorkingDirectory = $managerDir
$link.IconLocation = "$managerExe,0"
$link.Description = (T 'ShortcutDescription')
$link.Save()
[void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell)

if (-not $ManagerUser) {
    # 默认=执行本安装的账户（提权 PowerShell 的当前身份）
    $ManagerUser = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
}
Write-Host (T 'RegisteringTask' $taskName, $ManagerUser)
$action    = New-ScheduledTaskAction -Execute $managerExe -WorkingDirectory $managerDir
$trigger   = New-ScheduledTaskTrigger -AtLogOn -User $ManagerUser
$principal = New-ScheduledTaskPrincipal -UserId $ManagerUser -LogonType Interactive -RunLevel Highest
$settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal `
    -Settings $settings -Description (T 'TaskDescription') -Force | Out-Null

Write-Host (T 'StartingManager')
try {
    Start-ScheduledTask -TaskName $taskName
    Write-Host (T 'ManagerStarted')
} catch {
    Write-Warning (T 'ManagerNextLogon' $_.Exception.Message)
}
