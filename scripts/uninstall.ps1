#Requires -RunAsAdministrator
param(
    [string]$InstallDir = 'C:\Program Files\BleUnlock',
    [string]$DataDir    = (Join-Path $env:ProgramData 'BleUnlock'),
    [switch]$KeepData
)

$ErrorActionPreference = 'Continue'
. (Join-Path $PSScriptRoot 'i18n.ps1')
$serviceName = 'BleUnlockService'

# sc.exe no lanza excepciones: se comprueba el codigo de salida de forma explicita
function Assert-ScOk {
    param([string]$Action, [int[]]$Allowed = @(0))
    if ($Allowed -notcontains $LASTEXITCODE) {
        $msg = (New-Object System.ComponentModel.Win32Exception($LASTEXITCODE)).Message
        throw (T 'ScFailed' $Action, $LASTEXITCODE, $msg)
    }
}

$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing) {
    if ($existing.Status -ne 'Stopped') {
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
    Write-Host (T 'ServiceDeleted')
}

# 助手与管理端在交互会话中运行，可能占用安装目录中的文件
Get-Process -Name 'BleUnlockHelper' -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process -Name 'BleUnlockManager' -ErrorAction SilentlyContinue | Stop-Process -Force
$deadline = (Get-Date).AddSeconds(10)
while ((Get-Process -Name 'BleUnlockManager' -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 250
}

# 管理端：注销计划任务、删除开始菜单快捷方式（程序文件随安装目录一并删除）
$managerTask = 'BleUnlockManager'
if (Get-ScheduledTask -TaskName $managerTask -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $managerTask -Confirm:$false
    Write-Host (T 'TaskRemoved')
} else {
    Write-Host (T 'TaskMissing')
}
# El nombre depende del idioma con el que se instaló: se prueban los tres
foreach ($shortcutName in $script:ShortcutNames.Values) {
    $shortcutPath = Join-Path ([Environment]::GetFolderPath('CommonStartMenu')) "Programs\$shortcutName.lnk"
    if (Test-Path $shortcutPath) {
        Remove-Item $shortcutPath -Force
        Write-Host (T 'ShortcutRemoved')
    }
}

# 先注销 CP 再删除文件：DllUnregisterServer 需要 DLL 还在原路径
$targetDll = Join-Path $InstallDir 'BleUnlockCP.dll'
if (Test-Path $targetDll) {
    $proc = Start-Process regsvr32.exe -ArgumentList "/u /s `"$targetDll`"" -Wait -PassThru -NoNewWindow
    if ($proc.ExitCode -eq 0) {
        Write-Host (T 'CpUnregistered')
    } else {
        Write-Error (T 'RegsvrUnregFailed' $proc.ExitCode)
    }
}

if (Test-Path $InstallDir) {
    Remove-Item -Recurse -Force $InstallDir -ErrorAction SilentlyContinue
    if (Test-Path $InstallDir) {
        Write-Error (T 'FilesDeleteFailed' $InstallDir)
    } else {
        Write-Host (T 'FilesDeleted')
    }
}

if (-not $KeepData) {
    if (Test-Yes (Read-Host (T 'ConfirmDeleteData' $DataDir))) {
        Remove-Item -Recurse -Force $DataDir -ErrorAction SilentlyContinue
        if (Test-Path $DataDir) {
            Write-Error (T 'DataDeleteFailed' $DataDir)
        } else {
            Write-Host (T 'DataDeleted')
        }
    } else {
        Write-Host (T 'DataKept' $DataDir)
    }
}

Write-Host (T 'UninstallDone')
Write-Host (T 'ManagerSettingsKept' "$env:LOCALAPPDATA\BleUnlockManager")
