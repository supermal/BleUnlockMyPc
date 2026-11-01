# Lanzado por setup.exe (oculto y elevado): ejecuta scripts\install.ps1 del paquete extraído y deja
# todo lo que imprime en un registro, porque la ventana no se ve. Código de salida 0 = instalación completa.
param([Parameter(Mandatory)][string]$LogPath)

New-Item -ItemType Directory -Force -Path (Split-Path $LogPath) | Out-Null
Start-Transcript -Path $LogPath -Force | Out-Null
$code = 0
try {
    & (Join-Path $PSScriptRoot 'scripts\install.ps1')
}
catch {
    Write-Host $_
    Write-Host $_.ScriptStackTrace
    $code = 1
}
finally {
    Stop-Transcript | Out-Null
}
exit $code
