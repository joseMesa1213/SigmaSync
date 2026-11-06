-- F2, foto ANTES/DESPUÉS de facturar cargando el pedido (solo lectura, copia de Fábrica). Ctrl+A y F5.
--   ANTES:   @ped = '035850', @fac = ''
--   DESPUÉS: @ped = '035850', @fac = número de la factura grabada (como sale en pantalla)
USE SAINT_ADM_SYNCPRB;
SELECT DB_NAME() AS base_actual;   -- debe decir SAINT_ADM_SYNCPRB
DECLARE @ped varchar(20) = '035850', @fac varchar(20) = '';

SELECT CodSucu, FieldName, ValueInt FROM SACORRELSIS WHERE CodSucu = '00000' AND FieldName IN ('PrxPedido', 'PrxFact');

-- 1) El pedido: encabezado y líneas (después de facturar completo debería desaparecer)
SELECT NroUnico, TipoFac, NumeroD, CodUsua, CodEsta, FechaE, CodClie, OrdenC, Monto, MtoTax, MtoTotal
FROM SAFACT WHERE TipoFac = 'E' AND NumeroD = @ped;
SELECT NroLinea, CodItem, CodUbic, Cantidad, CantidadC, Precio, TotalItem
FROM SAITEMFAC WHERE TipoFac = 'E' AND NumeroD = @ped ORDER BY NroLinea;

-- 2) La factura: encabezado, líneas (¿OTipo = 'E' y ONumero = pedido?) e impuestos
SELECT NroUnico, TipoFac, NumeroD, CodUsua, CodEsta, FechaE, CodClie, CodVend, CodOper, OrdenC, OTipo, ONumero, Monto, MtoTax, MtoTotal
FROM SAFACT WHERE TipoFac = 'A' AND NumeroD = @fac;
SELECT NroLinea, CodItem, CodUbic, Cantidad, Precio, TotalItem, OTipo, ONumero
FROM SAITEMFAC WHERE TipoFac = 'A' AND NumeroD = @fac ORDER BY NroLinea;

-- 3) Comprometido y existencia de los productos del pedido o de la factura
SELECT p.CodProd, p.Compro, p.Existen, e.CodUbic, e.CantCom, e.Existen AS ExistenDep
FROM SAPROD p LEFT JOIN SAEXIS e ON e.CodProd = p.CodProd AND e.CodUbic = '005'
WHERE p.CodProd IN (SELECT CodItem FROM SAITEMFAC WHERE (TipoFac = 'E' AND NumeroD = @ped) OR (TipoFac = 'A' AND NumeroD = @fac))
ORDER BY p.CodProd;

-- 4) Últimos documentos de la copia (por si la factura sale con otro número)
SELECT TOP 5 NroUnico, TipoFac, NumeroD, CodUsua, CodEsta, FechaE, CodClie, OrdenC, OTipo, ONumero, MtoTotal
FROM SAFACT ORDER BY NroUnico DESC;
