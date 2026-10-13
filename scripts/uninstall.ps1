#Requires -RunAsAdministrator
param(
    [string]$InstallDir = 'C:\Program Files\AutoUnlock',
    [string]$DataDir    = (Join-Path $env:ProgramData 'AutoUnlock'),
    [switch]$KeepData
)

$ErrorActionPreference = 'Continue'
$serviceName = 'AutoUnlockService'

if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    sc.exe stop $serviceName | Out-Null
    Start-Sleep -Seconds 2
    sc.exe delete $serviceName | Out-Null
    Write-Host '服务已删除'
}

if (Test-Path $InstallDir) { Remove-Item -Recurse -Force $InstallDir; Write-Host '程序文件已删除' }

if (-not $KeepData) {
    if ((Read-Host "是否删除配置与日志（$DataDir）？输入 yes 确认") -eq 'yes') {
        Remove-Item -Recurse -Force $DataDir
        Write-Host '配置与日志已删除'
    }
}
