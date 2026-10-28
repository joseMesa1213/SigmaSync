<#
.SYNOPSIS
  Instala o actualiza el agente SigmaSync de un nodo como servicio Windows (SigmaSync-<NODO>).
.DESCRIPTION
  Copia los binarios publicados a la carpeta de instalación sin tocar la configuración existente
  (appsettings.<NODO>.json / .local.json), valida esquema y configuración y deja el servicio
  automático con reinicio ante fallos. En un servidor con varios nodos (Fábrica + CEDI) se ejecuta
  una vez por nodo: comparten binarios y cada uno tiene su servicio.
  Requiere PowerShell como administrador.
.EXAMPLE
  .\instalar-agente.ps1 -Nodo FAB -Origen .\agente
#>
param(
    [Parameter(Mandatory)] [string]$Nodo,
    [string]$Origen = (Join-Path $PSScriptRoot 'agente'),
    [string]$Destino = 'C:\SigmaSync\agente'
)

$ErrorActionPreference = 'Stop'
$servicio = "SigmaSync-$Nodo"
$exe = Join-Path $Destino 'SigmaSync.Agent.exe'

# Al actualizar se detienen todos los agentes de esta carpeta: comparten el ejecutable.
$enCarpeta = Get-CimInstance Win32_Service | Where-Object { $_.PathName -like "*$exe*" -and $_.State -eq 'Running' }
$enCarpeta | ForEach-Object { Stop-Service $_.Name; Write-Host "Detenido $($_.Name)" }

New-Item -ItemType Directory -Force $Destino | Out-Null
$protegidos = Get-ChildItem $Destino -Filter 'appsettings.*.json' -ErrorAction SilentlyContinue | ForEach-Object Name
Get-ChildItem $Origen -Recurse -File | ForEach-Object {
    $relativo = $_.FullName.Substring((Resolve-Path $Origen).Path.Length).TrimStart('\')
    if ($protegidos -contains $relativo) { Write-Host "  se conserva $relativo"; return }
    $dest = Join-Path $Destino $relativo
    New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
    Copy-Item $_.FullName $dest -Force
}

& $exe validar --nodo $Nodo
if ($LASTEXITCODE -ne 0) {
    Write-Warning "La validación falló. Revise appsettings.$Nodo.json o ejecute: `"$exe`" instalar-esquema --nodo $Nodo"
    $enCarpeta | ForEach-Object { Start-Service $_.Name }
    exit 1
}

if (-not (Get-Service $servicio -ErrorAction SilentlyContinue)) {
    New-Service -Name $servicio -BinaryPathName "`"$exe`" --nodo $Nodo" -DisplayName "SigmaSync agente $Nodo" `
        -Description "Sincronización SAINT (SigmaSync), nodo $Nodo" -StartupType Automatic | Out-Null
    sc.exe failure $servicio reset= 86400 actions= restart/60000/restart/60000/restart/300000 | Out-Null
    Write-Host "Servicio $servicio creado"
}

$enCarpeta | Where-Object Name -ne $servicio | ForEach-Object { Start-Service $_.Name; Write-Host "Reiniciado $($_.Name)" }
Start-Service $servicio
Get-Service $servicio | Format-Table Name, Status, StartType -AutoSize
