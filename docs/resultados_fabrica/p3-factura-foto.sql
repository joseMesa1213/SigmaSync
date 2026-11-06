-- P3, CEDI: ¿qué quedó después del error "'Estado' no es válido" al facturar el pedido 000001? Solo lectura. Ctrl+A y F5.
USE BODEGA1_SYNCPRB;
SELECT DB_NAME() AS base_actual;   -- debe decir BODEGA1_SYNCPRB
DECLARE @ped varchar(20) = '000001';

SELECT CodSucu, FieldName, ValueInt FROM SACORRELSIS WHERE FieldName IN ('PrxPedido', 'PrxFact') ORDER BY FieldName;

-- 1) Últimos documentos de la copia: ¿se grabó la factura (*00001 u otro número)?
SELECT TOP 5 NroUnico, TipoFac, NumeroD, CodUsua, CodEsta, FechaE, CodClie, OrdenC, CodOper, OTipo, ONumero, MtoTotal
FROM SAFACT ORDER BY NroUnico DESC;

-- 2) ¿Sigue el pedido?
SELECT TipoFac, NumeroD, OrdenC, Monto, MtoTax, MtoTotal FROM SAFACT WHERE TipoFac = 'E' AND NumeroD = @ped;
SELECT NroLinea, CodItem, Cantidad, TotalItem FROM SAITEMFAC WHERE TipoFac = 'E' AND NumeroD = @ped ORDER BY NroLinea;

-- 3) Líneas e impuestos de la factura más reciente grabada por GROUPIT
DECLARE @fac varchar(20) = (SELECT TOP 1 NumeroD FROM SAFACT WHERE TipoFac = 'A' AND CodUsua = 'GROUPIT' ORDER BY NroUnico DESC);
SELECT @fac AS factura_groupit;
SELECT NroLinea, CodItem, CodUbic, Cantidad, Precio, TotalItem, OTipo, ONumero FROM SAITEMFAC WHERE TipoFac = 'A' AND NumeroD = @fac ORDER BY NroLinea;
SELECT * FROM SATAXVTA WHERE TipoFac = 'A' AND NumeroD = @fac;
SELECT COUNT(*) cxc FROM SAACXC WHERE NumeroD = @fac;

-- 4) Comprometido y existencia (antes de facturar: Compro 171 / 48 / 296, CantCom 20 / 5 / 3, Existen 266 / 248 / 237)
SELECT p.CodProd, p.Compro, p.Existen, e.CantCom, e.Existen ExistenDep, p.CostPro, p.CostAct, p.Refere
FROM SAPROD p LEFT JOIN SAEXIS e ON e.CodProd = p.CodProd AND e.CodUbic = '100'
WHERE p.CodProd IN ('111216', '110818', '111014') ORDER BY p.CodProd;

-- 5) Impuestos de pedidos en Fábrica: ¿SAINT escribe SATAXVTA/SATAXITF para los pedidos? (copia y producción)
SELECT 'copia SATAXVTA' tabla, NumeroD, COUNT(*) filas FROM SAINT_ADM_SYNCPRB.dbo.SATAXVTA WHERE TipoFac = 'E' AND NumeroD IN ('035850', '035851') GROUP BY NumeroD
UNION ALL
SELECT 'copia SATAXITF', NumeroD, COUNT(*) FROM SAINT_ADM_SYNCPRB.dbo.SATAXITF WHERE TipoFac = 'E' AND NumeroD IN ('035850', '035851') GROUP BY NumeroD;
SELECT (SELECT COUNT(*) FROM SAINT_ADM.dbo.SAFACT WHERE TipoFac = 'E') pedidos_produccion,
       (SELECT COUNT(DISTINCT NumeroD) FROM SAINT_ADM.dbo.SATAXVTA WHERE TipoFac = 'E') pedidos_con_taxvta;
