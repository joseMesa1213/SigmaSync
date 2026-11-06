# Arma una carpeta de prueba de SAINT con su propio SAINT.CFG apuntando a una copia *_SYNCPRB.
# SAINT lee SAINT.CFG de la carpeta "Iniciar en" del acceso directo: el SAINT.CFG de produccion no se modifica
# (el script compara su hash antes y despues). El archivo esta enmascarado con XOR 0xAF byte a byte.
# Pegar la funcion completa en PowerShell (como administrador) y luego la llamada que corresponda (ver abajo).
# Texto sin tildes a proposito: PowerShell 5.1 lee mal los scripts UTF-8 sin BOM.

function New-CarpetaPrueba {
    param(
        [Parameter(Mandatory)] [string] $Nombre,     # carpeta y prefijo de los accesos, ej. FAB_HJ
        [Parameter(Mandatory)] [string] $Base,       # base de prueba, ej. SAINT_ADM_SYNCPRB
        [Parameter(Mandatory)] [string] $Etiqueta,   # nombre visible de la conexion, ej. PRUEBA SYNC FAB HJ
        [string] $Estacion = '',                     # vacio = la misma del SAINT.CFG de produccion
        [string] $Adm = 'C:\Program Files (x86)\Saint Enterprise\Administrativo'
    )
    $src = Join-Path $Adm 'SAINT.CFG'
    $dst = "C:\SigmaSyncPruebas\$Nombre"
    if (-not (Test-Path $src)) { Write-Error "No existe $src. Revisar la ruta de SAINT en esta maquina (-Adm)."; return }
    if (Test-Path "$dst\SAINT.CFG") { Write-Warning "$dst ya tiene SAINT.CFG; no se toca."; return }
    $hashAntes = (Get-FileHash $src).Hash

    New-Item -ItemType Directory -Force $dst | Out-Null
    Get-ChildItem $Adm -File | Where-Object { $_.Name -notlike 'SAINT.CFG*' -and $_.Extension -ne '.exe' } | Copy-Item -Destination $dst
    foreach ($sub in 'Sistema', 'ESTACION') { if (Test-Path "$Adm\$sub") { Copy-Item "$Adm\$sub" -Destination $dst -Recurse -Force } }

    $enc = [System.Text.Encoding]::GetEncoding(28591)   # latin-1: 1 byte = 1 caracter
    $txt = $enc.GetString([byte[]]([System.IO.File]::ReadAllBytes($src) | ForEach-Object { $_ -bxor 0xAF }))
    $txt = $txt -replace '(?m)^DATABASE=[^\r\n]*', "DATABASE=$Base"
    $txt = $txt -replace '(?m)^Name=[^\r\n]*', "Name=$Etiqueta"
    if ($Estacion) { $txt = $txt -replace '(?m)^STATION=[^\r\n]*', "STATION=$Estacion" }
    [System.IO.File]::WriteAllBytes("$dst\SAINT.CFG", [byte[]]($enc.GetBytes($txt) | ForEach-Object { $_ -bxor 0xAF }))

    "== Carpeta $dst (campos sin clave):"
    $txt -split "`r?`n" | Where-Object { $_ -match '^(Name|SERVER|DATABASE|USER|STATION)=' }

    $sh = New-Object -ComObject WScript.Shell
    foreach ($p in @(@('Administrativo', 'Saint.exe'), @('Ventas', 'saintv.exe'), @('Compras', 'saintc.exe'),
                     @('Configurador', 'SaintI.exe'), @('Conexion', 'conexion.exe'))) {
        if (-not (Test-Path "$Adm\$($p[1])")) { continue }
        $l = $sh.CreateShortcut("$env:USERPROFILE\Desktop\PRUEBA $Nombre - $($p[0]).lnk")
        $l.TargetPath = "$Adm\$($p[1])"; $l.WorkingDirectory = $dst; $l.Save()
    }

    $hashDespues = (Get-FileHash $src).Hash
    if ($hashDespues -ne $hashAntes) { Write-Error "ALERTA: el SAINT.CFG de produccion cambio ($hashAntes -> $hashDespues)" }
    else { "Produccion intacta: $hashDespues" }
}

# Quitar los accesos de prueba del Escritorio al terminar (las carpetas y copias se conservan).
function Hide-AccesosPrueba {
    New-Item -ItemType Directory -Force 'C:\SigmaSyncPruebas\Accesos' | Out-Null
    Move-Item "$env:USERPROFILE\Desktop\PRUEBA *.lnk" 'C:\SigmaSyncPruebas\Accesos\' -Force
    Get-ChildItem "$env:USERPROFILE\Desktop" -Filter 'PRUEBA*'   # no debe listar nada
}

# ---------------------------------------------------------------------------
# Llamadas (correr SOLO la que toque):
#
# Servidor de Fabrica, opcion A: misma copia, pero con la estacion de JFM (Venta normal):
#   New-CarpetaPrueba -Nombre FAB_HJ -Base SAINT_ADM_SYNCPRB -Etiqueta 'PRUEBA SYNC FAB HJ' -Estacion 'DESKTOP-HJ'
#
# Servidor de Fabrica, P3 en el CEDI:
#   New-CarpetaPrueba -Nombre CEDI -Base BODEGA1_SYNCPRB -Etiqueta 'PRUEBA SYNC CEDI'
#
# Maquina del PV (despues de restaurar ADM_MAY_SYNCPRB; ajustar -Base si la base tiene otro nombre y -Adm si SAINT
# esta en otra ruta):
#   New-CarpetaPrueba -Nombre PV -Base ADM_MAY_SYNCPRB -Etiqueta 'PRUEBA SYNC PV'
# ---------------------------------------------------------------------------
