#Requires -RunAsAdministrator
<#
    Instala AutoUnlock: servicio, asistente, proveedor de credenciales y Gestor.
    Idempotente: se puede volver a ejecutar para actualizar. El Gestor se inicia con la sesión mediante una
    tarea programada (permisos completos, sin aviso de UAC). Los mensajes siguen el idioma de Windows (i18n.ps1).
#>
param(
    [string]$Configuration     = 'Release',
    [string]$SourceDir         = '',
    [string]$ManagerSourceDir  = '',
    [string]$InstallDir        = 'C:\Program Files\AutoUnlock',
    [string]$DataDir           = (Join-Path $env:ProgramData 'AutoUnlock'),
    [string]$ManagerUser       = ''
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'i18n.ps1')
$serviceName = 'AutoUnlockService'
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
    (Join-Path $repoRoot "src\AutoUnlockService\bin\x64\$Configuration\$tfm"),
    (Join-Path $repoRoot "src\AutoUnlockService\bin\$Configuration\$tfm")
) }
$serviceOut = Resolve-Artifact (T 'NameService') $serviceCandidates 'AutoUnlockService.exe'
$helperOut  = Resolve-Artifact (T 'NameHelper') @(
    (Join-Path $repoRoot "src\AutoUnlockHelper\bin\x64\$Configuration\$tfm"),
    (Join-Path $repoRoot "src\AutoUnlockHelper\bin\$Configuration\$tfm")
) 'AutoUnlockHelper.exe'
$cpOut      = Resolve-Artifact (T 'NameCredProvider') @(
    (Join-Path $repoRoot "src\AutoUnlockCredentialProvider\bin\$Configuration\x64")
) 'AutoUnlockCredentialProvider.dll'

# 管理端是自包含发布产物：以 win-x64 publish 目录整体分发
$managerCandidates = if ($ManagerSourceDir) { @($ManagerSourceDir) } else { @(
    (Join-Path $repoRoot "src\AutoUnlockManager\bin\$Configuration\net10.0-windows10.0.19041.0\win-x64\publish")
) }
$managerOut = Resolve-Artifact (T 'NameManagerPublish') $managerCandidates 'AutoUnlockManager.exe'

Write-Host (T 'ServiceOutput' $serviceOut)
Write-Host (T 'HelperOutput' $helperOut)
Write-Host (T 'CredProviderOutput' $cpOut)
Write-Host (T 'ManagerOutput' $managerOut)

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
Get-Process -Name 'AutoUnlockHelper' -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process -Name 'AutoUnlockManager' -ErrorAction SilentlyContinue | Stop-Process -Force
# 结束是异步的：等旧进程完全退出，避免随后替换文件时被占用
$deadline = (Get-Date).AddSeconds(10)
while ((Get-Process -Name 'AutoUnlockManager' -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 250
}
if (Get-Process -Name 'AutoUnlockManager' -ErrorAction SilentlyContinue) {
    throw (T 'ManagerStillRunning')
}

# ── 2. 目录与文件 ───────────────────────────────────────────────────────────
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $DataDir 'logs') | Out-Null

Write-Host (T 'CopyingFiles')
Copy-Item -Path (Join-Path $serviceOut '*') -Destination $InstallDir -Recurse -Force
Copy-Item -Path (Join-Path $helperOut  '*') -Destination $InstallDir -Recurse -Force

$targetDll = Join-Path $InstallDir 'AutoUnlockCP.dll'
Copy-Item -Path (Join-Path $cpOut 'AutoUnlockCredentialProvider.dll') -Destination $targetDll -Force

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
if (-not [System.Diagnostics.EventLog]::SourceExists('AutoUnlock')) {
    New-EventLog -LogName Application -Source 'AutoUnlock'
    Write-Host (T 'EventSourceCreated')
}

# ── 6. 创建并启动服务 ───────────────────────────────────────────────────────
$exePath = Join-Path $InstallDir 'AutoUnlockService.exe'
sc.exe create $serviceName binPath= "`"$exePath`"" start= delayed-auto DisplayName= 'AutoUnlock Proximity Unlock' | Out-Null
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
$configReady = -not ([string]::IsNullOrWhiteSpace($config.AutoUnlock.Device.Irk) -or
                     [string]::IsNullOrWhiteSpace($config.AutoUnlock.Credentials.UserName))
if ($configReady) {
    sc.exe start $serviceName | Out-Null
    Assert-ScOk (T 'ActStartService')
    Write-Host (T 'ServiceStarted' (Join-Path $DataDir 'logs'))
} else {
    Write-Warning (T 'ConfigIncomplete')
    Write-Warning (T 'ConfigFillIn' $configPath)
    Write-Warning (T 'ConfigThenStart' $serviceName)
}

# ── 7. 管理端：开始菜单快捷方式与登录自启计划任务 ─────────────────────────
$managerExe = Join-Path $managerDir 'AutoUnlockManager.exe'
$taskName   = 'AutoUnlockManager'

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
