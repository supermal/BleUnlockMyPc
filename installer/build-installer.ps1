<#
    Genera installer\out\BleUnlock-Setup-<versión>.exe: un único instalador que lleva todo.
    - Servicio y asistente se publican autocontenidos (no hace falta .NET en el equipo de destino);
      el Gestor ya lo era.
    - El proveedor de credenciales se recompila con MSBuild de Visual Studio si está disponible;
      si no, se usa la última compilación existente.
    - El runtime de Visual C++ se descarga una vez (installer\cache) y va dentro del instalador.
    - La plantilla appsettings.json sale de git (HEAD): nunca lleva credenciales de este equipo.
      Con -TemplateRevision '' se toma del índice (lo preparado para el próximo commit).
    Requiere Inno Setup 6 (ISCC.exe). Uso: .\installer\build-installer.ps1 [-Version 2026.11.1]
#>
param(
    [string]$Version = (Get-Date).ToString('yyyy.M.d'),
    [string]$TemplateRevision = 'HEAD'
)

$ErrorActionPreference = 'Stop'
$here     = $PSScriptRoot
$repo     = Split-Path $here -Parent
$tfm      = 'net10.0-windows10.0.19041.0'
$staging  = Join-Path $here 'staging'
$payload  = Join-Path $staging 'payload'
$cache    = Join-Path $here 'cache'
$outDir   = Join-Path $here 'out'

function Step([string]$Text) { Write-Host "== $Text" -ForegroundColor Cyan }

function Find-Iscc {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    )
    $found = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $found) { throw 'ISCC.exe not found. Install Inno Setup 6: winget install JRSoftware.InnoSetup' }
    return $found
}

function Publish([string]$Project, [string]$Destination) {
    & dotnet publish (Join-Path $repo $Project) -c Release -r win-x64 --self-contained true -nologo -v q -o $Destination
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed: $Project" }
}

$iscc = Find-Iscc

Step 'Clean staging'
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Force $payload, $cache, $outDir | Out-Null

Step 'Publish service and helper (self-contained)'
$serviceOut = Join-Path $payload "src\BleUnlockService\bin\Release\$tfm"
$helperOut  = Join-Path $payload "src\BleUnlockHelper\bin\Release\$tfm"
Publish 'src\BleUnlockService\BleUnlockService.csproj' $serviceOut
Publish 'src\BleUnlockHelper\BleUnlockHelper.csproj' $helperOut

Step 'Publish Manager (self-contained)'
$managerOut = Join-Path $payload "src\BleUnlockManager\bin\Release\$tfm\win-x64\publish"
Publish 'src\BleUnlockManager\BleUnlockManager.csproj' $managerOut

Step 'Configuration template from git'
$template = & git -C $repo show "${TemplateRevision}:src/BleUnlockService/appsettings.json"
if ($LASTEXITCODE -ne 0) { throw 'git show failed for appsettings.json' }
$config = ($template -join "`n") | ConvertFrom-Json
if ($config.BleUnlock.Credentials.UserName -or $config.BleUnlock.Credentials.Password) {
    throw 'The committed appsettings.json contains credentials; refusing to package it.'
}
[IO.File]::WriteAllText((Join-Path $serviceOut 'appsettings.json'), ($template -join "`r`n"), (New-Object Text.UTF8Encoding($false)))

Step 'Credential provider'
$cpOut = Join-Path $repo 'src\BleUnlockCredentialProvider\bin\Release\x64'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$msbuild = if (Test-Path $vswhere) {
    & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
}
if ($msbuild) {
    & $msbuild (Join-Path $repo 'src\BleUnlockCredentialProvider\BleUnlockCredentialProvider.vcxproj') `
        /p:Configuration=Release /p:Platform=x64 /m /nologo /verbosity:minimal
    if ($LASTEXITCODE -ne 0) { Write-Warning 'MSBuild failed; using the existing credential provider build.' }
} else {
    Write-Warning 'MSBuild not found; using the existing credential provider build.'
}
$cpDll = Join-Path $cpOut 'BleUnlockCredentialProvider.dll'
if (-not (Test-Path $cpDll)) { throw "Credential provider not found: $cpDll" }
$cpTarget = Join-Path $payload 'src\BleUnlockCredentialProvider\bin\Release\x64'
New-Item -ItemType Directory -Force $cpTarget | Out-Null
Copy-Item $cpDll $cpTarget
Write-Host "  $cpDll ($((Get-Item $cpDll).LastWriteTime))"

Step 'Scripts'
New-Item -ItemType Directory -Force (Join-Path $payload 'scripts') | Out-Null
Copy-Item (Join-Path $repo 'scripts\install.ps1'), (Join-Path $repo 'scripts\uninstall.ps1'), (Join-Path $repo 'scripts\i18n.ps1') (Join-Path $payload 'scripts')
Copy-Item (Join-Path $here 'run-install.ps1') $payload
Get-ChildItem $payload -Recurse -Filter *.pdb | Remove-Item -Force

Step 'Visual C++ runtime'
$vc = Join-Path $cache 'vc_redist.x64.exe'
if (-not (Test-Path $vc)) {
    Invoke-WebRequest -Uri 'https://aka.ms/vc14/vc_redist.x64.exe' -OutFile $vc -UseBasicParsing
}
$sig = Get-AuthenticodeSignature $vc
if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'Microsoft Corporation') {
    Remove-Item $vc -Force
    throw "vc_redist.x64.exe signature is not a valid Microsoft signature ($($sig.Status))."
}
Copy-Item $vc $staging
Write-Host "  $((Get-Item $vc).VersionInfo.ProductVersion)"

Step "Compile installer $Version"
& $iscc /Q "/DAppVersion=$Version" "/DStagingDir=$staging" "/DOutDir=$outDir" (Join-Path $here 'BleUnlock.iss')
if ($LASTEXITCODE -ne 0) { throw 'ISCC failed' }

$setup = Join-Path $outDir "BleUnlock-Setup-$Version.exe"
Write-Host ''
Write-Host ("Done: {0} ({1:N1} MB)" -f $setup, ((Get-Item $setup).Length / 1MB)) -ForegroundColor Green
