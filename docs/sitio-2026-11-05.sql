-- Sesión en sitio 2026-11-05. Plan y orden en docs/plan-sitio-2026-11-05.md.
-- Todo es SELECT salvo donde se indica ESCRIBE (y entonces solo en una copia *_SYNCPRB, con guarda DB_NAME()).
-- Compatible con compat level 100. Las fotos antes/después de la factura en Fábrica están en docs/pruebas-sitio-2026-11-04.sql (secciones 1 y 2).

------------------------------------------------------------------------------
-- F.0  Fábrica, PRODUCCIÓN, solo lectura: histórico de facturas cargadas desde pedidos (confirma lo visto en la copia de octubre)
------------------------------------------------------------------------------
USE SAINT_ADM;
WITH fp AS (SELECT i.NumeroD fac, i.ONumero ped, i.CodItem, SUM(i.Cantidad) cant_fac, MAX(i.FechaE) fecha
            FROM SAITEMFAC i WHERE i.TipoFac = 'A' AND i.OTipo = 'E' AND i.FechaE >= '2025-01-01'
            GROUP BY i.NumeroD, i.ONumero, i.CodItem)
SELECT fp.fac, CONVERT(varchar(10), fp.fecha, 120) fecha, fp.ped,
       CASE WHEN p.NumeroD IS NULL THEN 'BORRADO' ELSE 'SIGUE' END pedido,
       fp.CodItem, fp.cant_fac, pi.Cantidad cant_ped_hoy
FROM fp
LEFT JOIN SAFACT p     ON p.TipoFac = 'E'  AND p.NumeroD = fp.ped
LEFT JOIN SAITEMFAC pi ON pi.TipoFac = 'E' AND pi.NumeroD = fp.ped AND pi.CodItem = fp.CodItem
ORDER BY fp.fecha DESC;

------------------------------------------------------------------------------
-- F.1  Fábrica, copia, solo lectura: ¿qué estación usa el SAINT de prueba y cómo está configurada?
--      Correr con "PRUEBA FAB_HJ - Ventas" abierto (opción A).
------------------------------------------------------------------------------
USE SAINT_ADM_SYNCPRB;
SELECT DB_NAME() AS base_actual;
SELECT CodEsta, CodSucu, CodUsua, Descrip FROM SAESTA WHERE CodEsta IN ('SERVIDOR', 'DESKTOP-HJ');

------------------------------------------------------------------------------
-- F.2  Fábrica, copia (opción B): parámetros de ventas de GROUPIT vs. JFM.
--      SAINT guarda la configuración de cada usuario en SSPARM (Activo/Habilitado) y SSPARD (valores),
--      con CodParm = '_U_<usuario>' y un Modulo por pantalla (101, 102, 108, …; _N_xx son los niveles).
--      F.2a es solo lectura. F.2b ESCRIBE en la copia: clona a GROUPIT la configuración de JFM de los módulos de ventas.
------------------------------------------------------------------------------
USE SAINT_ADM_SYNCPRB;
SELECT DB_NAME() AS base_actual;   -- debe decir SAINT_ADM_SYNCPRB

-- F.2a Módulos que tiene cada uno
SELECT CodParm, Modulo, COUNT(*) parametros, SUM(Activo) activos, SUM(Habilitado) habilitados
FROM SSPARM WHERE CodParm IN ('_U_GROUPIT', '_U_JFM') GROUP BY CodParm, Modulo ORDER BY Modulo, CodParm;

-- Parámetros de ventas que difieren (módulos 1xx)
SELECT COALESCE(j.Modulo, g.Modulo) Modulo, COALESCE(j.Parametro, g.Parametro) Parametro,
       j.Activo jfm_activo, g.Activo groupit_activo, j.Habilitado jfm_hab, g.Habilitado groupit_hab
FROM (SELECT * FROM SSPARM WHERE CodParm = '_U_JFM' AND Modulo BETWEEN 100 AND 199) j
FULL JOIN (SELECT * FROM SSPARM WHERE CodParm = '_U_GROUPIT' AND Modulo BETWEEN 100 AND 199) g
     ON g.Modulo = j.Modulo AND g.Parametro = j.Parametro
WHERE ISNULL(j.Activo, -1) <> ISNULL(g.Activo, -1) OR ISNULL(j.Habilitado, -1) <> ISNULL(g.Habilitado, -1)
ORDER BY 1, 2;

-- F.2b ESCRIBE (solo la copia): GROUPIT queda con los mismos parámetros de ventas (módulos 1xx) que JFM.
--      Cerrar antes todo SAINT de prueba; abrir de nuevo después.
/*
IF DB_NAME() <> 'SAINT_ADM_SYNCPRB' BEGIN RAISERROR('No es la copia. No se hace nada.', 16, 1); RETURN; END
BEGIN TRAN;
DELETE FROM SSPARD WHERE CodParm = '_U_GROUPIT' AND Modulo BETWEEN 100 AND 199;
DELETE FROM SSPARM WHERE CodParm = '_U_GROUPIT' AND Modulo BETWEEN 100 AND 199;
INSERT SSPARM (CodParm, Modulo, Parametro, Activo, Clave, Habilitado, SSFld)
SELECT '_U_GROUPIT', Modulo, Parametro, Activo, Clave, Habilitado, SSFld FROM SSPARM WHERE CodParm = '_U_JFM' AND Modulo BETWEEN 100 AND 199;
INSERT SSPARD (CodParm, Modulo, Parametro, NoData, TipoData, IData, SData, FData, SSFld)
SELECT '_U_GROUPIT', Modulo, Parametro, NoData, TipoData, IData, SData, FData, SSFld FROM SSPARD WHERE CodParm = '_U_JFM' AND Modulo BETWEEN 100 AND 199;
SELECT CodParm, Modulo, COUNT(*) n FROM SSPARM WHERE CodParm IN ('_U_GROUPIT', '_U_JFM') AND Modulo BETWEEN 100 AND 199 GROUP BY CodParm, Modulo;
COMMIT;
*/

------------------------------------------------------------------------------
-- PV.0  Máquina del PV, solo lectura: reconocimiento. Ajustar el nombre de la base en USE si no es ADM_MAY.
------------------------------------------------------------------------------
SELECT @@SERVERNAME AS servidor, SERVERPROPERTY('ProductVersion') AS version_sql, SERVERPROPERTY('Edition') AS edicion;
SELECT d.name, d.compatibility_level, d.recovery_model_desc, d.create_date,
       (SELECT MAX(b.backup_finish_date) FROM msdb.dbo.backupset b WHERE b.database_name = d.name) AS ultimo_respaldo
FROM sys.databases d WHERE d.database_id > 4 ORDER BY d.name;
SELECT RTRIM(p.hostname) equipo, RTRIM(p.program_name) programa, RTRIM(p.loginame) login_sql, DB_NAME(p.dbid) base, COUNT(*) conexiones
FROM master.dbo.sysprocesses p WHERE p.spid > 50
GROUP BY p.hostname, p.program_name, p.loginame, p.dbid ORDER BY base, equipo;
SELECT DB_NAME(f.database_id) base, f.type_desc, f.name nombre_logico, f.physical_name, f.size / 128 AS mb
FROM sys.master_files f WHERE f.database_id > 4 ORDER BY base;

USE ADM_MAY;   -- ← ajustar
SELECT CodSucu, FieldName, ValueInt FROM SACORRELSIS WHERE FieldName IN ('PrxOrdenC', 'PrxCompra', 'LenCorrel') ORDER BY CodSucu, FieldName;
SELECT name FROM sys.schemas WHERE name = 'sync';
SELECT CodProv, Descrip FROM SAPROV WHERE CodProv IN ('890919355-4', '8909193545');
SELECT 'OC (L)' tipo, MAX(FechaE) ultima, COUNT(*) docs FROM SACOMP WHERE TipoCom = 'L'
UNION ALL SELECT 'Compra (H)', MAX(FechaE), COUNT(*) FROM SACOMP WHERE TipoCom = 'H';
-- Compras que cargaron una OC (en la copia de octubre: ninguna)
SELECT COUNT(*) lineas_con_oc FROM SAITEMCOM WHERE TipoCom = 'H' AND OTipo = 'L';
-- OC recientes a Fábrica con 2+ ítems (elegir una para P1)
SELECT TOP 5 c.NumeroD, c.FechaE, COUNT(*) items, c.MtoTotal
FROM SACOMP c JOIN SAITEMCOM i ON i.TipoCom = c.TipoCom AND i.NumeroD = c.NumeroD AND i.CodProv = c.CodProv
WHERE c.TipoCom = 'L' AND c.CodProv = '890919355-4'
GROUP BY c.NumeroD, c.FechaE, c.MtoTotal HAVING COUNT(*) >= 2 ORDER BY c.FechaE DESC;
SELECT OBJECT_NAME(referencing_id) objeto, referenced_database_name, referenced_entity_name
FROM sys.sql_expression_dependencies WHERE referenced_database_name IS NOT NULL;
SELECT OBJECT_NAME(parent_id) tabla, name trigger_name, is_disabled FROM sys.triggers
WHERE OBJECT_NAME(parent_id) IN ('SACOMP', 'SAITEMCOM', 'SAPROD', 'SAEXIS', 'SACORRELSIS');

------------------------------------------------------------------------------
-- PV.0b Máquina del PV, solo lectura: trigger de SACOMP, usuario que compra y la OC elegida para P1.
--       Resultados en docs/resultados_pv/resultado_PV0b.md. Para ver el texto completo del trigger:
--       Ctrl+T (resultados en texto) y Herramientas > Opciones > Resultados de consultas > Texto > máx. 8192.
------------------------------------------------------------------------------
USE ADM_MAY;
-- 1) Trigger: definición completa y fechas
SELECT name, create_date, modify_date, is_disabled, is_instead_of_trigger
FROM sys.triggers WHERE name = 'TR_ADM_INSERTDOCUMENTOCOMPRA';
EXEC sp_helptext 'TR_ADM_INSERTDOCUMENTOCOMPRA';
-- Objetos que usa el trigger (tablas, procedimientos, bases externas)
SELECT referenced_server_name, referenced_database_name, referenced_schema_name, referenced_entity_name
FROM sys.sql_expression_dependencies WHERE referencing_id = OBJECT_ID('TR_ADM_INSERTDOCUMENTOCOMPRA');
-- Servidores vinculados y otros triggers/procedimientos no estándar de la base
SELECT name, product, provider, data_source FROM sys.servers WHERE server_id > 0;
SELECT o.type_desc, o.name, o.create_date, o.modify_date FROM sys.objects o
WHERE o.type IN ('TR', 'P', 'FN', 'IF', 'TF') AND o.is_ms_shipped = 0 ORDER BY o.create_date DESC;

-- 2) Quién graba compras y OC en el PV (usuario y estación), últimos 90 días
SELECT TipoCom, CodUsua, CodEsta, COUNT(*) docs, MAX(FechaE) ultima
FROM SACOMP WHERE TipoCom IN ('H', 'L') AND FechaE >= DATEADD(DAY, -90, GETDATE())
GROUP BY TipoCom, CodUsua, CodEsta ORDER BY TipoCom, docs DESC;
-- Parámetros de usuario existentes (para saber si ese usuario tiene configuración propia)
SELECT CodParm, COUNT(DISTINCT Modulo) modulos FROM SSPARM WHERE CodParm LIKE '[_]U[_]%' GROUP BY CodParm ORDER BY CodParm;

-- 3) OC 000003567 (candidata para P1): encabezado y líneas
SELECT TipoCom, NumeroD, CodProv, FechaE, CodUsua, CodEsta, CodUbic, OrdenC, Monto, MtoTax, MtoTotal, Notas1
FROM SACOMP WHERE TipoCom = 'L' AND NumeroD = '000003567' AND CodProv = '890919355-4';
SELECT NroLinea, CodItem, Descrip1, CodUbic, Cantidad, Costo, TotalItem
FROM SAITEMCOM WHERE TipoCom = 'L' AND NumeroD = '000003567' AND CodProv = '890919355-4' ORDER BY NroLinea;

------------------------------------------------------------------------------
-- PV.1  ESCRIBE (solo crea una base nueva): respaldo COPY_ONLY + restauración como <origen>_SYNCPRB.
--       Correr después de revisar PV.0. Los archivos van en la misma carpeta de los de la base origen.
------------------------------------------------------------------------------
/*
USE master;
DECLARE @origen sysname = N'ADM_MAY';   -- ← ajustar
DECLARE @destino sysname = @origen + N'_SYNCPRB';
DECLARE @dir nvarchar(260), @datos sysname, @log sysname, @bak nvarchar(400), @q nvarchar(max);

IF DB_ID(@origen) IS NULL BEGIN RAISERROR('No existe la base %s.', 16, 1, @origen); RETURN; END
IF DB_ID(@destino) IS NOT NULL BEGIN RAISERROR('Ya existe %s. No se restaura.', 16, 1, @destino); RETURN; END
SELECT @datos = name, @dir = LEFT(physical_name, LEN(physical_name) - CHARINDEX('\', REVERSE(physical_name)) + 1)
FROM sys.master_files WHERE database_id = DB_ID(@origen) AND type_desc = 'ROWS';
SELECT @log = name FROM sys.master_files WHERE database_id = DB_ID(@origen) AND type_desc = 'LOG';
SET @bak = @dir + @origen + N'_syncprb.bak';

SET @q = N'BACKUP DATABASE ' + QUOTENAME(@origen) + N' TO DISK = N''' + @bak + N''' WITH COPY_ONLY, INIT, CHECKSUM, STATS = 10;';
PRINT @q; EXEC (@q);
SET @q = N'RESTORE DATABASE ' + QUOTENAME(@destino) + N' FROM DISK = N''' + @bak + N''''
         + N' WITH MOVE N''' + @datos + N''' TO N''' + @dir + @destino + N'.mdf'','
         + N' MOVE N''' + @log + N''' TO N''' + @dir + @destino + N'_log.ldf'', RECOVERY, STATS = 10;';
PRINT @q; EXEC (@q);
SET @q = N'ALTER DATABASE ' + QUOTENAME(@destino) + N' SET RECOVERY SIMPLE;';
EXEC (@q);
SELECT name, create_date, recovery_model_desc FROM sys.databases WHERE name = @destino;
-- Luego borrar el .bak a mano (ruta en el PRINT).
*/

------------------------------------------------------------------------------
-- PV.2  P1, copia del PV, solo lectura: FOTO antes/después de grabar una compra cargando la OC.
--       Poner en @oc la OC elegida en PV.0 y en @compra el número de factura de prueba (PRUEBA-P1A, PRUEBA-P1B).
------------------------------------------------------------------------------
USE ADM_MAY_SYNCPRB;   -- ← ajustar
SELECT DB_NAME() AS base_actual;   -- debe terminar en _SYNCPRB
DECLARE @oc varchar(20) = '<oc>', @compra varchar(20) = 'PRUEBA-P1A';
SELECT FieldName, ValueInt FROM SACORRELSIS WHERE FieldName IN ('PrxOrdenC', 'PrxCompra');
SELECT TipoCom, NumeroD, CodProv, FechaE, OTipo, ONumero, MtoTotal FROM SACOMP WHERE TipoCom = 'L' AND NumeroD = @oc;
SELECT NroLinea, CodItem, CodUbic, Cantidad, OTipo, ONumero FROM SAITEMCOM WHERE TipoCom = 'L' AND NumeroD = @oc ORDER BY NroLinea;
SELECT i.CodItem, p.Pedido, p.Existen, e.CodUbic, e.CantPed, e.Existen AS ExistenDep
FROM SAITEMCOM i JOIN SAPROD p ON p.CodProd = i.CodItem
LEFT JOIN SAEXIS e ON e.CodProd = i.CodItem AND e.CodUbic = i.CodUbic
WHERE i.TipoCom = 'L' AND i.NumeroD = @oc;
SELECT TipoCom, NumeroD, CodProv, FechaE, OTipo, ONumero, MtoTotal FROM SACOMP WHERE TipoCom = 'H' AND NumeroD = @compra;
SELECT NroLinea, CodItem, Cantidad, OTipo, ONumero FROM SAITEMCOM WHERE TipoCom = 'H' AND NumeroD = @compra ORDER BY NroLinea;

------------------------------------------------------------------------------
-- PV.3  P2, copia del PV, solo lectura: comprometido antes/después de grabar (y luego borrar) una OC en pantalla.
--       Poner en @p1/@p2 los dos productos que se usen (con cantidades 7 y 13).
------------------------------------------------------------------------------
USE ADM_MAY_SYNCPRB;   -- ← ajustar
-- 173300030 y 172400023: quedaron con Pedido = 0 en la copia después de P1a.
DECLARE @p1 varchar(30) = '173300030', @p2 varchar(30) = '172400023';
SELECT DB_NAME() AS base_actual;   -- debe terminar en _SYNCPRB
SELECT FieldName, ValueInt FROM SACORRELSIS WHERE FieldName = 'PrxOrdenC';
SELECT p.CodProd, p.Pedido, p.Existen, e.CodUbic, e.CantPed, e.Existen ExistenDep FROM SAPROD p LEFT JOIN SAEXIS e ON e.CodProd = p.CodProd
WHERE p.CodProd IN (@p1, @p2) ORDER BY p.CodProd, e.CodUbic;
SELECT TOP 3 TipoCom, NumeroD, CodProv, CodUsua, CodEsta, FechaE, MtoTotal FROM SACOMP WHERE TipoCom = 'L' ORDER BY FechaE DESC;
-- Después de grabar: guardar la OC completa como modelo para comparar con las OC que crea F3:
--   SELECT * FROM SACOMP WHERE TipoCom = 'L' AND NumeroD = '<oc nueva>';
--   SELECT * FROM SAITEMCOM WHERE TipoCom = 'L' AND NumeroD = '<oc nueva>' ORDER BY NroLinea;

------------------------------------------------------------------------------
-- LIMPIEZA TOTAL (solo cuando se cierren todas las pruebas). Correr en cada servidor la parte que aplique.
------------------------------------------------------------------------------
/*
USE master;
IF DB_ID('SAINT_ADM_SYNCPRB') IS NOT NULL BEGIN ALTER DATABASE SAINT_ADM_SYNCPRB SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE SAINT_ADM_SYNCPRB; END
IF DB_ID('BODEGA1_SYNCPRB')   IS NOT NULL BEGIN ALTER DATABASE BODEGA1_SYNCPRB   SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE BODEGA1_SYNCPRB;   END
IF DB_ID('ADM_MAY_SYNCPRB')   IS NOT NULL BEGIN ALTER DATABASE ADM_MAY_SYNCPRB   SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ADM_MAY_SYNCPRB;   END
-- Y en PowerShell: Remove-Item C:\SigmaSyncPruebas -Recurse ; borrar SAINT.CFG.produccion-20261104 solo si el hash de SAINT.CFG sigue en 07455FC1…3197.
*/
