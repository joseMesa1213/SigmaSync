-- Pruebas en pantalla sobre las copias SAINT_ADM_SYNCPRB / BODEGA1_SYNCPRB (SERVIDOR\SQLEXPRESS), 2026-11-04.
-- Todo es SELECT salvo donde se indica. SAINT de prueba = accesos "PRUEBA SYNC - ..." (carpeta C:\SigmaSyncPruebas\SaintPrueba).
-- Productos de Fábrica usados (depósito 005): 162240340070, 161240340090, 3458. Cliente: 2853060 (SGMPL ITAGUI B19).

------------------------------------------------------------------------------
-- 0. TotalApp (facturación electrónica): ¿cómo recibe las facturas el spooler?  SOLO LECTURA
--    Correr ANTES de facturar en la copia.
------------------------------------------------------------------------------
USE SAINT_ADM;   -- producción, solo lectura
SELECT * FROM TotalApp_ControlIF;
SELECT TOP 10 * FROM TotalApp_ControlIFPut;
SELECT TOP 10 * FROM TotalApp_AuditIF ORDER BY FechaE DESC;
SELECT Id, IdApp, Nombre, CodSucu FROM TotalApp_App;
SELECT UsaPFiscal FROM SCIPARAM;
-- Línea base para comparar después de facturar en la copia (no debe cambiar por nuestras pruebas):
SELECT COUNT(*) filas_put, (SELECT COUNT(*) FROM TotalApp_AuditIF) filas_audit,
       (SELECT MAX(NroUnico) FROM SAFACT) max_nrounico_prod
FROM TotalApp_ControlIFPut;

------------------------------------------------------------------------------
-- 1. FOTO de Fábrica (copia). Correr ANTES y DESPUÉS de cada paso en SAINT.
--    Poner en @ped el número de pedido cuando ya exista (antes del primer pedido, dejarlo como está).
------------------------------------------------------------------------------
USE SAINT_ADM_SYNCPRB;
SELECT DB_NAME() AS base_actual;   -- debe decir SAINT_ADM_SYNCPRB

DECLARE @ped varchar(20) = '035841';

SELECT CodSucu, FieldName, ValueInt FROM SACORRELSIS
WHERE CodSucu = '00000' AND FieldName IN ('PrxPedido', 'PrxFact');

-- Pedidos con ese número (¿SAINT sobrescribe, rechaza o salta el pedido de WORKER?)
SELECT NroUnico, TipoFac, NumeroD, CodUsua, CodEsta, FechaE, CodClie, OrdenC, OTipo, ONumero, MtoTotal
FROM SAFACT WHERE TipoFac = 'E' AND NumeroD IN (@ped, '0' + @ped, RIGHT(@ped, 5));

-- Últimos documentos grabados en la copia (el pedido y la factura nuevos salen arriba)
SELECT TOP 5 NroUnico, TipoFac, NumeroD, CodUsua, CodEsta, FechaE, CodClie, OrdenC, OTipo, ONumero, MtoTotal
FROM SAFACT ORDER BY NroUnico DESC;

-- Ítems del pedido
SELECT TipoFac, NumeroD, NroLinea, CodItem, CodUbic, Cantidad, CantidadC, OTipo, ONumero
FROM SAITEMFAC WHERE TipoFac = 'E' AND NumeroD = @ped ORDER BY NroLinea;

-- Comprometido y existencia de los 3 productos
SELECT p.CodProd, p.Compro, p.Existen, e.CodUbic, e.CantCom, e.Existen AS ExistenDep
FROM SAPROD p LEFT JOIN SAEXIS e ON e.CodProd = p.CodProd AND e.CodUbic = '005'
WHERE p.CodProd IN ('162240340070', '161240340090', '3458');

------------------------------------------------------------------------------
-- 2. Después de FACTURAR cargando el pedido: la factura nueva y sus ítems
------------------------------------------------------------------------------
/*
DECLARE @fac varchar(20) = '<numero factura>';
SELECT NroUnico, TipoFac, NumeroD, OTipo, ONumero, OrdenC, CodClie, MtoTotal FROM SAFACT WHERE TipoFac = 'A' AND NumeroD = @fac;
SELECT NroLinea, CodItem, Cantidad, OTipo, ONumero FROM SAITEMFAC WHERE TipoFac = 'A' AND NumeroD = @fac ORDER BY NroLinea;
-- Qué escribió TotalApp en la copia
SELECT TOP 5 * FROM TotalApp_ControlIFPut;
SELECT TOP 5 * FROM TotalApp_AuditIF ORDER BY FechaE DESC;
*/

------------------------------------------------------------------------------
-- 3. Histórico: qué hace SAINT con un pedido al facturarlo (SOLO LECTURA; vale en producción)
--    En la copia de octubre: borra el pedido si se factura completo; si es parcial, borra las líneas
--    facturadas completas y deja el saldo en las parciales. Las líneas de la factura quedan con OTipo='E'/ONumero.
------------------------------------------------------------------------------
/*
USE SAINT_ADM;
WITH fp AS (SELECT i.NumeroD fac, i.ONumero ped, i.CodItem, SUM(i.Cantidad) cant_fac, MAX(i.FechaE) fecha
            FROM SAITEMFAC i WHERE i.TipoFac = 'A' AND i.OTipo = 'E' AND i.FechaE >= '2025-01-01'
            GROUP BY i.NumeroD, i.ONumero, i.CodItem)
SELECT fp.fac, CONVERT(date, fp.fecha) fecha, fp.ped,
       CASE WHEN p.NumeroD IS NULL THEN 'BORRADO' ELSE 'SIGUE' END pedido,
       fp.CodItem, fp.cant_fac, pi.Cantidad cant_ped_hoy
FROM fp
LEFT JOIN SAFACT p     ON p.TipoFac = 'E'  AND p.NumeroD = fp.ped
LEFT JOIN SAITEMFAC pi ON pi.TipoFac = 'E' AND pi.NumeroD = fp.ped AND pi.CodItem = fp.CodItem
ORDER BY fp.fecha DESC;
*/
