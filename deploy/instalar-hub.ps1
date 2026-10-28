<#
.SYNOPSIS
  Instala o actualiza el SyncHub como servicio Windows (SigmaSync-Hub) y crea o actualiza su base.
.DESCRIPTION
  Conserva appsettings.json / appsettings.local.json si ya existen en el destino. La exposición a
  internet la hace cloudflared (ver deploy\cloudflared\LEEME.md); el hub solo escucha en localhost.
  Requiere PowerShell como administrador.
.EXAMPLE
  .\instalar-hub.ps1 -Origen .\hub
#>
param(
    [string]$Origen = (Join-Path $PSScriptRoot 'hub'),
    [string]$Destino = 'C:\SigmaSync\hub'
)

$ErrorActionPreference = 'Stop'
$servicio = 'SigmaSync-Hub'
$exe = Join-Path $Destino 'SigmaSync.Hub.exe'

if (Get-Service $servicio -ErrorAction SilentlyContinue) { Stop-Service $servicio }

New-Item -ItemType Directory -Force $Destino | Out-Null
$protegidos = Get-ChildItem $Destino -Filter 'appsettings*.json' -ErrorAction SilentlyContinue | ForEach-Object Name
Get-ChildItem $Origen -Recurse -File | ForEach-Object {
    $relativo = $_.FullName.Substring((Resolve-Path $Origen).Path.Length).TrimStart('\')
    if ($protegidos -contains $relativo) { Write-Host "  se conserva $relativo"; return }
    $dest = Join-Path $Destino $relativo
    New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
    Copy-Item $_.FullName $dest -Force
}

& $exe instalar-bd
if ($LASTEXITCODE -ne 0) { throw 'Falló instalar-bd: revise Hub:ConnectionString en appsettings.json' }

if (-not (Get-Service $servicio -ErrorAction SilentlyContinue)) {
    New-Service -Name $servicio -BinaryPathName "`"$exe`"" -DisplayName 'SigmaSync hub' `
        -Description 'Cola de mensajes de la sincronización SAINT (SigmaSync)' -StartupType Automatic | Out-Null
    sc.exe failure $servicio reset= 86400 actions= restart/60000/restart/60000/restart/300000 | Out-Null
    Write-Host "Servicio $servicio creado"
}
Start-Service $servicio
Get-Service $servicio | Format-Table Name, Status, StartType -AutoSize
Write-Host "Registre cada nodo con: `"$exe`" registrar-nodo NODO `"Descripción`""
