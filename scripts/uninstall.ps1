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
    # El servicio puede quedar "marcado para eliminar" hasta liberar los handles: se espera a que desaparezca
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Service -Name $serviceName -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
    }
    if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
        throw '服务已标记为删除但仍未消失（30 秒）。请关闭 services.msc / 事件查看器等占用窗口，或重启后重试。'
    }
    Write-Host '服务已删除'
}

# 助手与管理端在交互会话中运行，可能占用安装目录中的文件
Get-Process -Name 'AutoUnlockHelper' -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process -Name 'AutoUnlockManager' -ErrorAction SilentlyContinue | Stop-Process -Force
$deadline = (Get-Date).AddSeconds(10)
while ((Get-Process -Name 'AutoUnlockManager' -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 250
}

# 管理端：注销计划任务、删除开始菜单快捷方式（程序文件随安装目录一并删除）
$managerTask = 'AutoUnlockManager'
if (Get-ScheduledTask -TaskName $managerTask -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $managerTask -Confirm:$false
    Write-Host '管理端计划任务已注销'
} else {
    Write-Host '管理端计划任务不存在（跳过）'
}
$shortcutPath = Join-Path ([Environment]::GetFolderPath('CommonStartMenu')) 'Programs\AutoUnlock 管理器.lnk'
if (Test-Path $shortcutPath) {
    Remove-Item $shortcutPath -Force
    Write-Host '管理端快捷方式已删除'
}

# 先注销 CP 再删除文件：DllUnregisterServer 需要 DLL 还在原路径
$targetDll = Join-Path $InstallDir 'AutoUnlockCP.dll'
if (Test-Path $targetDll) {
    $proc = Start-Process regsvr32.exe -ArgumentList "/u /s `"$targetDll`"" -Wait -PassThru -NoNewWindow
    if ($proc.ExitCode -eq 0) {
        Write-Host '凭据提供程序已注销'
    } else {
        Write-Error "regsvr32 /u 失败，退出码 $($proc.ExitCode)。若注销失败，建议重启后再删除程序文件。"
    }
}

if (Test-Path $InstallDir) {
    Remove-Item -Recurse -Force $InstallDir -ErrorAction SilentlyContinue
    if (Test-Path $InstallDir) {
        Write-Error "程序文件删除失败（可能仍被占用，如 winlogon 持有 DLL）：$InstallDir"
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
    } else {
        Write-Host "已保留 $DataDir"
    }
}

Write-Host '卸载完成。建议重启一次，确保 winlogon 释放凭据提供程序 DLL。'
Write-Host "（管理端界面设置已保留：$env:LOCALAPPDATA\AutoUnlockManager）"
