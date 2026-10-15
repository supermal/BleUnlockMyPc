#Requires -RunAsAdministrator
<#
    Instala AutoUnlock: servicio, ayudante y proveedor de credenciales.
    Es idempotente: se puede volver a ejecutar para actualizar.
#>
param(
    [string]$Configuration = 'Release',
    [string]$SourceDir     = '',
    [string]$InstallDir    = 'C:\Program Files\AutoUnlock',
    [string]$DataDir       = (Join-Path $env:ProgramData 'AutoUnlock')
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

Write-Host "服务输出：$serviceOut"
Write-Host "助手输出：$helperOut"
Write-Host "凭据提供程序：$cpOut"

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

# 助手在交互会话中运行，可能占用安装目录中的文件
Get-Process -Name 'AutoUnlockHelper' -ErrorAction SilentlyContinue | Stop-Process -Force

# ── 2. 目录与文件 ───────────────────────────────────────────────────────────
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $DataDir 'logs') | Out-Null

Write-Host '拷贝服务与助手…'
Copy-Item -Path (Join-Path $serviceOut '*') -Destination $InstallDir -Recurse -Force
Copy-Item -Path (Join-Path $helperOut  '*') -Destination $InstallDir -Recurse -Force

$targetDll = Join-Path $InstallDir 'AutoUnlockCP.dll'
Copy-Item -Path (Join-Path $cpOut 'AutoUnlockCredentialProvider.dll') -Destination $targetDll -Force

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
# 子项不经历空 DACL 窗口；/T 显式覆盖已存在的子项（logs、appsettings.json 等）。
# 不用 /C：逐项失败必须以非零退出码浮出，而不是被吞掉。
icacls $DataDir /inheritance:r /grant:r 'SYSTEM:(OI)(CI)F' /grant:r '*S-1-5-32-544:(OI)(CI)F' /T | Out-Null
if ($LASTEXITCODE -ne 0) { throw "icacls 收紧配置目录权限失败，退出码 $LASTEXITCODE" }

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

# 配置无效时服务会拒绝启动（退出码 1）：与其让 SCM 反复失败，不如在安装时就讲清楚
$configReady = $false
try {
    $config = Get-Content $configPath -Raw | ConvertFrom-Json
    $configReady = -not ([string]::IsNullOrWhiteSpace($config.AutoUnlock.Device.Irk) -or
                         [string]::IsNullOrWhiteSpace($config.AutoUnlock.Credentials.UserName))
} catch {
    Write-Warning "配置文件无法解析：$($_.Exception.Message)"
}
if ($configReady) {
    sc.exe start $serviceName | Out-Null
    Assert-ScOk '启动服务'
    Write-Host "服务已启动。日志目录：$(Join-Path $DataDir 'logs')"
} else {
    Write-Warning '配置尚未填写完整，服务不会启动。'
    Write-Warning "请编辑 $configPath 填入 Device.Irk、Credentials.UserName 与 Password，"
    Write-Warning "然后执行： Start-Service $serviceName"
}
