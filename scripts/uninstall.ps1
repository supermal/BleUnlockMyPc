#Requires -RunAsAdministrator
param(
    [string]$InstallDir = 'C:\Program Files\AutoUnlock',
    [string]$DataDir    = (Join-Path $env:ProgramData 'AutoUnlock'),
    [switch]$KeepData
)

$ErrorActionPreference = 'Continue'
$serviceName = 'AutoUnlockService'

# sc.exe no lanza excepciones: se comprueba el codigo de salida de forma explicita
function Assert-ScOk {
    param([string]$Action, [int[]]$Allowed = @(0))
    if ($Allowed -notcontains $LASTEXITCODE) {
        $msg = (New-Object System.ComponentModel.Win32Exception($LASTEXITCODE)).Message
        throw "$Action 失败，sc.exe 退出码 $LASTEXITCODE（$msg）"
    }
}

$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing) {
    if ($existing.Status -ne 'Stopped') {
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
    Write-Host '服务已删除'
}

if (Test-Path $InstallDir) {
    Remove-Item -Recurse -Force $InstallDir -ErrorAction SilentlyContinue
    if (Test-Path $InstallDir) {
        Write-Error "程序文件删除失败（可能仍被占用）：$InstallDir"
    } else {
        Write-Host '程序文件已删除'
    }
}

if (-not $KeepData) {
    if ((Read-Host "是否删除配置与日志（$DataDir）？输入 yes 确认") -eq 'yes') {
        Remove-Item -Recurse -Force $DataDir -ErrorAction SilentlyContinue
        if (Test-Path $DataDir) {
            Write-Error "配置与日志删除失败：$DataDir"
        } else {
            Write-Host '配置与日志已删除'
        }
    }
}
