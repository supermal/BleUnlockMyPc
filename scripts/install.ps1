#Requires -RunAsAdministrator
<#
    安装 AutoUnlock：服务、助手、凭据提供程序与管理端。
    幂等：可重复运行以升级。管理端以计划任务方式随登录自启（最高权限，无 UAC 弹窗）。
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
$serviceName = 'AutoUnlockService'
$repoRoot    = Split-Path $PSScriptRoot -Parent
$tfm         = 'net10.0-windows10.0.19041.0'

# sc.exe no lanza excepciones: se comprueba el codigo de salida de forma explicita
function Assert-ScOk {
    param([string]$Action, [int[]]$Allowed = @(0))
    if ($Allowed -notcontains $LASTEXITCODE) {
        $msg = (New-Object System.ComponentModel.Win32Exception($LASTEXITCODE)).Message
        throw "$Action 失败，sc.exe 退出码 $LASTEXITCODE（$msg）"
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
        throw "未找到 $Name 构建产物（$Leaf）。已检查：`n  $($Candidates -join "`n  ")`n请先按 README 构建各项目"
    }
    return $best
}

$serviceCandidates = if ($SourceDir) { @($SourceDir) } else { @(
    (Join-Path $repoRoot "src\AutoUnlockService\bin\x64\$Configuration\$tfm"),
    (Join-Path $repoRoot "src\AutoUnlockService\bin\$Configuration\$tfm")
) }
$serviceOut = Resolve-Artifact '服务' $serviceCandidates 'AutoUnlockService.exe'
$helperOut  = Resolve-Artifact '助手' @(
    (Join-Path $repoRoot "src\AutoUnlockHelper\bin\x64\$Configuration\$tfm"),
    (Join-Path $repoRoot "src\AutoUnlockHelper\bin\$Configuration\$tfm")
) 'AutoUnlockHelper.exe'
$cpOut      = Resolve-Artifact '凭据提供程序' @(
    (Join-Path $repoRoot "src\AutoUnlockCredentialProvider\bin\$Configuration\x64")
) 'AutoUnlockCredentialProvider.dll'

# 管理端是自包含发布产物：以 win-x64 publish 目录整体分发
$managerCandidates = if ($ManagerSourceDir) { @($ManagerSourceDir) } else { @(
    (Join-Path $repoRoot "src\AutoUnlockManager\bin\$Configuration\net10.0-windows10.0.19041.0\win-x64\publish")
) }
$managerOut = Resolve-Artifact '管理端（发布产物）' $managerCandidates 'AutoUnlockManager.exe'

Write-Host "服务输出：$serviceOut"
Write-Host "助手输出：$helperOut"
Write-Host "凭据提供程序：$cpOut"
Write-Host "管理端发布：$managerOut"

# ── 1. 停止旧服务 ────────────────────────────────────────────────────────────
$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host '停止已有服务…'
    if ($existing.Status -ne 'Stopped') {
        # 1061 = 正在停止/启动中，1062 = 未运行：都可继续等待实际状态
        sc.exe stop $serviceName | Out-Null
        Assert-ScOk '停止服务' -Allowed 0, 1061, 1062
        try {
            $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
        } catch [System.ServiceProcess.TimeoutException] {
            throw "等待服务停止超时（30 秒），当前状态：$((Get-Service -Name $serviceName).Status)。请手动停止后重试。"
        }
    }
    sc.exe delete $serviceName | Out-Null
    Assert-ScOk '删除服务'
    # El servicio puede quedar "marcado para eliminar" hasta liberar los handles: se espera a que desaparezca
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Service -Name $serviceName -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
    }
    if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
        throw '服务已标记为删除但仍未消失（30 秒）。请关闭 services.msc / 事件查看器等占用窗口，或重启后重试。'
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
    throw '管理端进程仍未退出（已等待 10 秒），请手动结束后重试。'
}

# ── 2. 目录与文件 ───────────────────────────────────────────────────────────
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $DataDir 'logs') | Out-Null

Write-Host '拷贝服务与助手…'
Copy-Item -Path (Join-Path $serviceOut '*') -Destination $InstallDir -Recurse -Force
Copy-Item -Path (Join-Path $helperOut  '*') -Destination $InstallDir -Recurse -Force

$targetDll = Join-Path $InstallDir 'AutoUnlockCP.dll'
Copy-Item -Path (Join-Path $cpOut 'AutoUnlockCredentialProvider.dll') -Destination $targetDll -Force

# 管理端：整体替换 Manager 子目录（干净升级，避免上一版本残留文件）
$managerDir = Join-Path $InstallDir 'Manager'
if (Test-Path $managerDir) {
    Remove-Item -Recurse -Force $managerDir
    if (Test-Path $managerDir) { throw "旧管理端目录删除失败：$managerDir" }
}
New-Item -ItemType Directory -Force -Path $managerDir | Out-Null
Copy-Item -Path (Join-Path $managerOut '*') -Destination $managerDir -Recurse -Force
Write-Host "管理端已部署：$managerDir"

# ── 3. 配置文件与权限 ───────────────────────────────────────────────────────
$configPath = Join-Path $DataDir 'appsettings.json'
if (-not (Test-Path $configPath)) {
    Copy-Item -Path (Join-Path $serviceOut 'appsettings.json') -Destination $configPath
    Write-Host "已生成配置模板：$configPath"
} else {
    Write-Host "保留已有配置：$configPath"
}
# El fichero de servicio no debe quedarse: el que manda es el de ProgramData
Remove-Item (Join-Path $InstallDir 'appsettings.json') -ErrorAction SilentlyContinue

Write-Host '收紧配置目录权限（仅 SYSTEM 与 Administrators）…'
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
    if ($LASTEXITCODE -ne 0) { throw "icacls 收紧目录权限失败（$dir），退出码 $LASTEXITCODE" }
}
foreach ($file in @(Get-ChildItem -Path $DataDir -File -Recurse | ForEach-Object { $_.FullName })) {
    icacls $file @grantFile | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls 修复文件权限失败（$file），退出码 $LASTEXITCODE" }
}

# 回读断言（SDDL 须含 SYSTEM/Administrators 的 FA 授权）
function Assert-AclHasFullControl {
    param([string]$Path)
    $sddl = (Get-Acl -Path $Path).Sddl
    if ($sddl -notmatch ';FA;;;SY' -or $sddl -notmatch ';FA;;;BA') {
        throw "权限收紧未生效（$Path）：SDDL=$sddl。请以管理员重试；若问题持续请反馈。"
    }
}
foreach ($dir in $dirTargets) { Assert-AclHasFullControl $dir }
foreach ($file in @(Get-ChildItem -Path $DataDir -File -Recurse | ForEach-Object { $_.FullName })) {
    Assert-AclHasFullControl $file
}

# ── 4. 注册凭据提供程序 ─────────────────────────────────────────────────────
Write-Host '注册凭据提供程序…'
$proc = Start-Process regsvr32.exe -ArgumentList "/s `"$targetDll`"" -Wait -PassThru -NoNewWindow
if ($proc.ExitCode -ne 0) { throw "regsvr32 失败，退出码 $($proc.ExitCode)" }

# ── 5. 事件日志源 ───────────────────────────────────────────────────────────
if (-not [System.Diagnostics.EventLog]::SourceExists('AutoUnlock')) {
    New-EventLog -LogName Application -Source 'AutoUnlock'
    Write-Host '已注册事件日志源 AutoUnlock'
}

# ── 6. 创建并启动服务 ───────────────────────────────────────────────────────
$exePath = Join-Path $InstallDir 'AutoUnlockService.exe'
sc.exe create $serviceName binPath= "`"$exePath`"" start= delayed-auto DisplayName= 'AutoUnlock 近距离自动解锁' | Out-Null
Assert-ScOk '创建服务'
sc.exe description $serviceName '基于 BLE RSSI 的近距离自动锁屏与解锁服务' | Out-Null
Assert-ScOk '设置服务描述'
sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
Assert-ScOk '设置失败恢复策略'

# 配置无效时服务会拒绝启动（退出码 1）：与其让 SCM 反复失败，不如在安装时就讲清楚。
# 读取/解析失败必须 throw（Task 19 教训：ACL 静默丢失时此处曾只告警并继续，留下坏状态）。
try {
    $config = Get-Content $configPath -Raw | ConvertFrom-Json
} catch {
    throw "读取或解析配置文件失败：$configPath（$($_.Exception.Message)）。通常是权限异常或文件损坏，请修复后重试。"
}
$configReady = -not ([string]::IsNullOrWhiteSpace($config.AutoUnlock.Device.Irk) -or
                     [string]::IsNullOrWhiteSpace($config.AutoUnlock.Credentials.UserName))
if ($configReady) {
    sc.exe start $serviceName | Out-Null
    Assert-ScOk '启动服务'
    Write-Host "服务已启动。日志目录：$(Join-Path $DataDir 'logs')"
} else {
    Write-Warning '配置尚未填写完整，服务不会启动。'
    Write-Warning "请编辑 $configPath 填入 Device.Irk、Credentials.UserName 与 Password，"
    Write-Warning "然后执行： Start-Service $serviceName"
}

# ── 7. 管理端：开始菜单快捷方式与登录自启计划任务 ─────────────────────────
$managerExe = Join-Path $managerDir 'AutoUnlockManager.exe'
$taskName   = 'AutoUnlockManager'

Write-Host '创建开始菜单快捷方式…'
$shortcutPath = Join-Path ([Environment]::GetFolderPath('CommonStartMenu')) 'Programs\AutoUnlock 管理器.lnk'
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcutPath)
$link.TargetPath = $managerExe
$link.WorkingDirectory = $managerDir
$link.IconLocation = "$managerExe,0"
$link.Description = 'AutoUnlock 管理器（实时监控与配置）'
$link.Save()
[void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell)

if (-not $ManagerUser) {
    # 默认=执行本安装的账户（提权 PowerShell 的当前身份）
    $ManagerUser = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
}
Write-Host "注册计划任务 $taskName（用户：$ManagerUser；登录自启、最高权限）…"
$action    = New-ScheduledTaskAction -Execute $managerExe -WorkingDirectory $managerDir
$trigger   = New-ScheduledTaskTrigger -AtLogOn -User $ManagerUser
$principal = New-ScheduledTaskPrincipal -UserId $ManagerUser -LogonType Interactive -RunLevel Highest
$settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal `
    -Settings $settings -Description 'AutoUnlock 管理器：登录时自动启动（最高权限，无 UAC 弹窗）' -Force | Out-Null

Write-Host '启动管理端…'
try {
    Start-ScheduledTask -TaskName $taskName
    Write-Host '管理端已安装并已启动（见托盘图标）。'
} catch {
    Write-Warning "管理端将在该用户下次登录时启动（$($_.Exception.Message)）"
}
