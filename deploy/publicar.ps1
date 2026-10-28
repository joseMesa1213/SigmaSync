<#
.SYNOPSIS
  Publica el agente y el hub como ejecutables autocontenidos (win-x64): los servidores no necesitan .NET instalado.
.EXAMPLE
  .\deploy\publicar.ps1                 # deja publish\agente y publish\hub
#>
param([string]$Salida = (Join-Path $PSScriptRoot '..\publish'))

$ErrorActionPreference = 'Stop'
$raiz = Resolve-Path (Join-Path $PSScriptRoot '..')
$comunes = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-nologo')

dotnet publish "$raiz\src\SigmaSync.Agent" @comunes -o "$Salida\agente"
if ($LASTEXITCODE -ne 0) { throw 'Falló la publicación del agente' }
dotnet publish "$raiz\src\SigmaSync.Hub" @comunes -o "$Salida\hub"
if ($LASTEXITCODE -ne 0) { throw 'Falló la publicación del hub' }

# No se distribuyen credenciales locales de desarrollo.
Get-ChildItem $Salida -Recurse -Filter '*.local.json' | Remove-Item -Force
Copy-Item "$PSScriptRoot\instalar-agente.ps1", "$PSScriptRoot\instalar-hub.ps1" $Salida -Force
Write-Host "Publicado en $Salida"
