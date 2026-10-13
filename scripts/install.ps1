#Requires -RunAsAdministrator
param(
    [string]$SourceDir  = '',
    [string]$InstallDir = 'C:\Program Files\AutoUnlock',
    [string]$DataDir    = (Join-Path $env:ProgramData 'AutoUnlock')
)

$ErrorActionPreference = 'Stop'
$serviceName = 'AutoUnlockService'

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

if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    Write-Host '停止已有服务…'
    sc.exe stop $serviceName | Out-Null
    Start-Sleep -Seconds 2
    sc.exe delete $serviceName | Out-Null
    Start-Sleep -Seconds 1
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
sc.exe description $serviceName '基于 BLE RSSI 的近距离自动锁屏与解锁服务' | Out-Null
sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
sc.exe start $serviceName | Out-Null

Write-Host "安装完成。日志目录：$(Join-Path $DataDir 'logs')"
