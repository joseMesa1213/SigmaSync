-- Reconocimiento en las máquinas del cliente. SOLO LECTURA.
-- Correr en SSMS conectado a la instancia de cada máquina. Guardar la salida (Resultados → Guardar como).
-- Compatible con compat level 100 (sin IIF, TRY_CONVERT, STRING_AGG).

-- 1. Instancia y bases
SELECT @@SERVERNAME AS servidor, SERVERPROPERTY('ProductVersion') AS version_sql, SERVERPROPERTY('Edition') AS edicion;

SELECT d.name, d.compatibility_level, d.recovery_model_desc, d.collation_name, d.create_date,
       (SELECT MAX(b.backup_finish_date) FROM msdb.dbo.backupset b WHERE b.database_name = d.name) AS ultimo_respaldo
FROM sys.databases d WHERE d.database_id > 4 ORDER BY d.name;

-- Quién se conecta a qué base: equipo, programa y usuario SQL (así se ve cómo apunta SAINT)
SELECT RTRIM(p.hostname) equipo, RTRIM(p.program_name) programa, RTRIM(p.loginame) login_sql,
       DB_NAME(p.dbid) base, COUNT(*) conexiones
FROM master.dbo.sysprocesses p WHERE p.spid > 50
GROUP BY p.hostname, p.program_name, p.loginame, p.dbid ORDER BY base, equipo;

-- Tamaño de cada base y espacio libre en sus discos (para saber si caben las copias)
SELECT DB_NAME(f.database_id) base, f.type_desc, f.physical_name, f.size / 128 AS mb
FROM sys.master_files f WHERE f.database_id > 4 ORDER BY base;
SELECT DISTINCT v.volume_mount_point disco, v.total_bytes / 1048576 total_mb, v.available_bytes / 1048576 libre_mb
FROM sys.master_files f CROSS APPLY sys.dm_os_volume_stats(f.database_id, f.file_id) v;

-- 2. Por cada base SAINT: cambiar el nombre en USE y repetir (Fábrica, CEDI, PV)
USE SAINT_ADM;   -- ← cambiar por la base a revisar

SELECT DB_NAME() AS base;
SELECT CodSucu, FieldName, ValueInt FROM SACORRELSIS
WHERE FieldName IN ('PrxPedido', 'PrxOrdenC', 'PrxCompra', 'PrxFact', 'LenCorrel') ORDER BY CodSucu, FieldName;

-- ¿Ya existe el esquema de SigmaSync? (debe salir vacío en producción)
SELECT name FROM sys.schemas WHERE name = 'sync';

-- Colisión de PrxPedido: últimos pedidos y quién los grabó
SELECT TOP 15 NumeroD, FechaE, CodUsua, CodClie, OrdenC, MtoTotal FROM SAFACT WHERE TipoFac = 'E' ORDER BY NumeroD DESC;

-- Usuarios de la integración anterior y usuario SYNC
SELECT CodUsua, COUNT(*) docs, MAX(FechaE) ultimo FROM SAFACT WHERE CodUsua IN ('WORKER', 'SYNC') GROUP BY CodUsua;

-- Clientes del PV (en Fábrica/CEDI) y proveedores Fábrica/CEDI (en el PV)
SELECT CodClie, Descrip FROM SACLIE WHERE CodClie IN ('9012380209') OR Descrip LIKE '%MAYOR%';
SELECT CodProv, Descrip FROM SAPROV WHERE CodProv IN ('890919355-4', '8909193545');

-- Actividad reciente (para saber si la base está viva o es una copia)
SELECT 'SAFACT' tabla, MAX(FechaE) ultima FROM SAFACT
UNION ALL SELECT 'SACOMP', MAX(FechaE) FROM SACOMP;

-- Triggers sobre las tablas que toca SigmaSync (no se tocan, solo se inventarían)
SELECT OBJECT_NAME(parent_id) tabla, name trigger_name, is_disabled FROM sys.triggers
WHERE OBJECT_NAME(parent_id) IN ('SAFACT', 'SAITEMFAC', 'SACOMP', 'SAITEMCOM', 'SAPROD', 'SAEXIS', 'SACORRELSIS');
