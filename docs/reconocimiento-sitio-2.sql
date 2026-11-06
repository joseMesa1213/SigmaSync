-- Segunda vuelta en SERVIDOR\SQLEXPRESS (Fábrica + CEDI), 2026-11-04.
-- PARTE A: solo lectura. PARTE B: respaldo COPY_ONLY y restauración con OTRO nombre (no toca las bases de producción).
-- Compatible con compat level 100.

------------------------------------------------------------------------------
-- PARTE A.1  ¿Qué es "serviceworker"? (5 conexiones .NET abiertas contra SAINT_ADM)
------------------------------------------------------------------------------
SELECT s.session_id, s.host_name, s.program_name, s.host_process_id, s.login_time, s.last_request_end_time,
       DB_NAME(s.database_id) base, LEFT(t.text, 400) ultima_sql
FROM sys.dm_exec_sessions s
LEFT JOIN sys.dm_exec_connections c ON c.session_id = s.session_id
OUTER APPLY sys.dm_exec_sql_text(c.most_recent_sql_handle) t
WHERE s.login_name = 'serviceworker';

SELECT name, type_desc, create_date, modify_date, is_disabled, default_database_name
FROM sys.server_principals WHERE name IN ('serviceworker', 'sa', 'sigmasync');

------------------------------------------------------------------------------
-- PARTE A.2  Fábrica (SAINT_ADM): pedidos recientes de la sucursal 00000 y documentos de WORKER
------------------------------------------------------------------------------
USE SAINT_ADM;

SELECT TOP 15 NroUnico, NumeroD, CodSucu, FechaE, CodUsua, CodEsta, CodClie, OrdenC, MtoTotal
FROM SAFACT WHERE TipoFac = 'E' AND CodSucu = '00000' ORDER BY NroUnico DESC;

SELECT TipoFac, CodSucu, MIN(NumeroD) desde, MAX(NumeroD) hasta, COUNT(*) docs, MAX(NroUnico) ultimo_nrounico
FROM SAFACT WHERE CodUsua = 'WORKER' GROUP BY TipoFac, CodSucu;

-- Últimos 20 documentos de venta de cualquier tipo: ¿alguien escribe aparte de los usuarios de SAINT?
SELECT TOP 20 NroUnico, TipoFac, NumeroD, CodSucu, CodUsua, CodEsta, FechaE, CodClie, OrdenC
FROM SAFACT ORDER BY NroUnico DESC;

-- Cliente del PV Mayorista en Fábrica (el diseño usa 2853060)
SELECT CodClie, Descrip FROM SACLIE WHERE CodClie IN ('2853060', '9012380209', '01515');

-- ¿Los objetos de la base hacen referencia a otras bases? (si sale algo, las pruebas en la copia podrían escribir en producción)
SELECT OBJECT_NAME(referencing_id) objeto, referenced_server_name, referenced_database_name, referenced_entity_name
FROM sys.sql_expression_dependencies WHERE referenced_database_name IS NOT NULL;

------------------------------------------------------------------------------
-- PARTE A.3  CEDI (BODEGA1): lo mismo
------------------------------------------------------------------------------
USE BODEGA1;

SELECT TOP 20 NroUnico, TipoFac, NumeroD, CodSucu, CodUsua, CodEsta, FechaE, CodClie, OrdenC
FROM SAFACT ORDER BY NroUnico DESC;

SELECT OBJECT_NAME(referencing_id) objeto, referenced_server_name, referenced_database_name, referenced_entity_name
FROM sys.sql_expression_dependencies WHERE referenced_database_name IS NOT NULL;

-- Depósitos y vendedores que usa el CEDI al facturar al PV (para confirmar CodUbic 100 / CodVend 9810)
SELECT TOP 10 f.CodVend, i.CodUbic, COUNT(*) lineas, MAX(f.FechaE) ultima
FROM SAFACT f JOIN SAITEMFAC i ON i.TipoFac = f.TipoFac AND i.NumeroD = f.NumeroD
WHERE f.TipoFac = 'A' AND f.CodClie = '9012380209'
GROUP BY f.CodVend, i.CodUbic ORDER BY lineas DESC;

------------------------------------------------------------------------------
-- PARTE B  Copias de prueba. Correr SOLO después de revisar la parte A.
-- COPY_ONLY: no altera la cadena de respaldos de producción (SAINT_ADM está en FULL).
-- La restauración crea bases NUEVAS (*_SYNCPRB) con archivos NUEVOS. No toca SAINT_ADM ni BODEGA1.
------------------------------------------------------------------------------
/*
USE master;

BACKUP DATABASE SAINT_ADM TO DISK = 'C:\Program Files\Microsoft SQL Server\MSSQL14.SQLEXPRESS\MSSQL\Backup\SAINT_ADM_syncprb.bak'
  WITH COPY_ONLY, INIT, CHECKSUM, STATS = 10;
BACKUP DATABASE BODEGA1 TO DISK = 'C:\Program Files\Microsoft SQL Server\MSSQL14.SQLEXPRESS\MSSQL\Backup\BODEGA1_syncprb.bak'
  WITH COPY_ONLY, INIT, CHECKSUM, STATS = 10;

-- Restaura con MOVE a archivos *_SYNCPRB; los nombres lógicos se leen de la base origen.
DECLARE @dir nvarchar(260) = N'C:\Program Files\Microsoft SQL Server\MSSQL14.SQLEXPRESS\MSSQL\DATA\';
DECLARE @sql nvarchar(max), @origen sysname, @destino sysname, @datos sysname, @log sysname;

DECLARE c CURSOR LOCAL FAST_FORWARD FOR SELECT N'SAINT_ADM', N'SAINT_ADM_SYNCPRB' UNION ALL SELECT N'BODEGA1', N'BODEGA1_SYNCPRB';
OPEN c; FETCH NEXT FROM c INTO @origen, @destino;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF DB_ID(@destino) IS NOT NULL BEGIN RAISERROR('Ya existe %s. No se restaura.', 16, 1, @destino); RETURN; END
    SELECT @datos = name FROM sys.master_files WHERE database_id = DB_ID(@origen) AND type_desc = 'ROWS';
    SELECT @log   = name FROM sys.master_files WHERE database_id = DB_ID(@origen) AND type_desc = 'LOG';
    SET @sql = N'RESTORE DATABASE ' + QUOTENAME(@destino)
             + N' FROM DISK = N''C:\Program Files\Microsoft SQL Server\MSSQL14.SQLEXPRESS\MSSQL\Backup\' + @origen + N'_syncprb.bak'''
             + N' WITH MOVE N''' + @datos + N''' TO N''' + @dir + @destino + N'.mdf'','
             + N' MOVE N''' + @log + N''' TO N''' + @dir + @destino + N'_log.ldf'', RECOVERY, STATS = 10;';
    PRINT @sql;
    EXEC (@sql);
    SET @sql = N'ALTER DATABASE ' + QUOTENAME(@destino) + N' SET RECOVERY SIMPLE;';
    EXEC (@sql);
    FETCH NEXT FROM c INTO @origen, @destino;
END
CLOSE c; DEALLOCATE c;

SELECT name, create_date, recovery_model_desc FROM sys.databases WHERE name LIKE '%SYNCPRB';
*/

------------------------------------------------------------------------------
-- LIMPIEZA al terminar el día (no correr hasta cerrar las pruebas)
------------------------------------------------------------------------------
/*
USE master;
ALTER DATABASE SAINT_ADM_SYNCPRB SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE SAINT_ADM_SYNCPRB;
ALTER DATABASE BODEGA1_SYNCPRB   SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE BODEGA1_SYNCPRB;
-- y borrar a mano los dos .bak de la carpeta Backup
*/
