#Requires -RunAsAdministrator
param(
    [string]$SourceDir  = '',
    [string]$InstallDir = 'C:\Program Files\AutoUnlock',
    [string]$DataDir    = (Join-Path $env:ProgramData 'AutoUnlock')
)

$ErrorActionPreference = 'Stop'
$serviceName = 'AutoUnlockService'

# sc.exe no lanza excepciones: se comprueba el codigo de salida de forma explicita
function Assert-ScOk {
    param([string]$Action, [int[]]$Allowed = @(0))
    if ($Allowed -notcontains $LASTEXITCODE) {
        $msg = (New-Object System.ComponentModel.Win32Exception($LASTEXITCODE)).Message
        throw "$Action 失败，sc.exe 退出码 $LASTEXITCODE（$msg）"
    }
}

# Sondea ambas disposiciones de salida: con y sin -p:Platform=x64
if (-not $SourceDir) {
    $tfm  = 'net10.0-windows10.0.19041.0'
    $base = Join-Path $PSScriptRoot '..\src\AutoUnlockService\bin'
    $candidates = @(
        (Join-Path $base "x64\Release\$tfm"),
        (Join-Path $base "Release\$tfm")
    )
    $SourceDir = $candidates | Where-Object { Test-Path (Join-Path $_ 'AutoUnlockService.exe') } | Select-Object -First 1
    if (-not $SourceDir) {
        throw "未找到编译输出。已检查：`n  $($candidates -join "`n  ")`n请先运行：dotnet build src/AutoUnlockService/AutoUnlockService.csproj -c Release"
    }
}
if (-not (Test-Path (Join-Path $SourceDir 'AutoUnlockService.exe'))) {
    throw "SourceDir 中没有 AutoUnlockService.exe：$SourceDir"
}

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

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $DataDir 'logs') | Out-Null

Write-Host "拷贝文件：$SourceDir -> $InstallDir"
Copy-Item -Path (Join-Path $SourceDir '*') -Destination $InstallDir -Recurse -Force

$configPath = Join-Path $DataDir 'appsettings.json'
if (-not (Test-Path $configPath)) {
    Copy-Item -Path (Join-Path $SourceDir 'appsettings.json') -Destination $configPath
    Write-Host "已生成配置模板：$configPath"
} else {
    Write-Host "保留已有配置：$configPath"
}

if (-not [System.Diagnostics.EventLog]::SourceExists('AutoUnlock')) {
    New-EventLog -LogName Application -Source 'AutoUnlock'
    Write-Host '已注册事件日志源 AutoUnlock'
}

$exePath = Join-Path $InstallDir 'AutoUnlockService.exe'
sc.exe create $serviceName binPath= "`"$exePath`"" start= delayed-auto DisplayName= 'AutoUnlock 近距离自动解锁' | Out-Null
Assert-ScOk '创建服务'
sc.exe description $serviceName '基于 BLE RSSI 的近距离自动锁屏与解锁服务' | Out-Null
Assert-ScOk '设置服务描述'
sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
Assert-ScOk '设置失败恢复策略'
sc.exe start $serviceName | Out-Null
Assert-ScOk '启动服务'

Write-Host "安装完成。日志目录：$(Join-Path $DataDir 'logs')"
