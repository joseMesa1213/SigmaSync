-- P1, foto ANTES/DESPUÉS (solo lectura, copia del PV). Ajustar @oc y @compra, luego Ctrl+A y F5.
--   P1b: @oc = '000003538', @compra = 'PRUEBA-2'      P1c: @oc = '000003567', @compra = 'PRUEBA-3'
USE ADM_MAY_SYNCPRB;
SELECT DB_NAME() AS base_actual;
DECLARE @oc varchar(20) = '000003538', @compra varchar(20) = 'PRUEBA-2', @prov varchar(15) = '890919355-4';

-- 1) La OC: copia vs. producción
SELECT 'copia' donde, TipoCom, NumeroD, FechaE, OrdenC, Monto, MtoTax, MtoTotal FROM SACOMP WHERE TipoCom = 'L' AND NumeroD = @oc AND CodProv = @prov
UNION ALL
SELECT 'produccion', TipoCom, NumeroD, FechaE, OrdenC, Monto, MtoTax, MtoTotal FROM ADM_MAY.dbo.SACOMP WHERE TipoCom = 'L' AND NumeroD = @oc AND CodProv = @prov;
SELECT 'copia' donde, NroLinea, CodItem, CodUbic, Cantidad, Costo, TotalItem FROM SAITEMCOM WHERE TipoCom = 'L' AND NumeroD = @oc AND CodProv = @prov
UNION ALL
SELECT 'produccion', NroLinea, CodItem, CodUbic, Cantidad, Costo, TotalItem FROM ADM_MAY.dbo.SAITEMCOM WHERE TipoCom = 'L' AND NumeroD = @oc AND CodProv = @prov
ORDER BY 1, 2;

-- 2) La compra: encabezado, líneas e impuestos
SELECT TipoCom, NumeroD, CodProv, FechaE, FechaV, CodUsua, CodEsta, CodUbic, OrdenC, OTipo, ONumero, Monto, MtoTax, MtoTotal, Notas1
FROM SACOMP WHERE TipoCom = 'H' AND NumeroD = @compra AND CodProv = @prov;
SELECT NroLinea, CodItem, CodUbic, Cantidad, Costo, TotalItem, OTipo, ONumero
FROM SAITEMCOM WHERE TipoCom = 'H' AND NumeroD = @compra AND CodProv = @prov ORDER BY NroLinea;
SELECT * FROM SATAXCOM WHERE TipoCom = 'H' AND NumeroD = @compra AND CodProv = @prov;

-- 3) Comprometido de compra de los productos de la OC (según producción): copia vs. producción
SELECT 'copia' donde, p.CodProd, p.Pedido, p.Existen, e.CodUbic, e.CantPed, e.Existen ExistenDep
FROM SAPROD p LEFT JOIN SAEXIS e ON e.CodProd = p.CodProd AND e.CodUbic = '115'
WHERE p.CodProd IN (SELECT CodItem FROM ADM_MAY.dbo.SAITEMCOM WHERE TipoCom = 'L' AND NumeroD = @oc AND CodProv = @prov)
UNION ALL
SELECT 'produccion', p.CodProd, p.Pedido, p.Existen, e.CodUbic, e.CantPed, e.Existen
FROM ADM_MAY.dbo.SAPROD p LEFT JOIN ADM_MAY.dbo.SAEXIS e ON e.CodProd = p.CodProd AND e.CodUbic = '115'
WHERE p.CodProd IN (SELECT CodItem FROM ADM_MAY.dbo.SAITEMCOM WHERE TipoCom = 'L' AND NumeroD = @oc AND CodProv = @prov)
ORDER BY 2, 1;

-- 4) IVA en compras reales a Fábrica y CEDI (producción, últimos 90 días): ¿siempre llevan IVA?
SELECT TipoCom, CodProv, COUNT(*) docs, SUM(CASE WHEN MtoTax > 0 THEN 1 ELSE 0 END) con_iva, MAX(FechaE) ultima
FROM ADM_MAY.dbo.SACOMP WHERE TipoCom IN ('H', 'L') AND CodProv IN ('890919355-4', '8909193545')
  AND FechaE >= DATEADD(DAY, -90, GETDATE())
GROUP BY TipoCom, CodProv ORDER BY TipoCom, CodProv;
