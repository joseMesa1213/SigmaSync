-- P1a, foto DESPUÉS completa (solo lectura, copia del PV). Ctrl+A y F5.
USE ADM_MAY_SYNCPRB;
SELECT DB_NAME() AS base_actual;
DECLARE @oc varchar(20) = '000003567', @compra varchar(20) = 'PRUEBA-1', @prov varchar(15) = '890919355-4';

-- 1) ¿La OC sigue en la copia? ¿Y en producción?
SELECT 'copia' donde, TipoCom, NumeroD, FechaE, OrdenC, MtoTotal FROM SACOMP WHERE TipoCom = 'L' AND NumeroD = @oc AND CodProv = @prov
UNION ALL
SELECT 'produccion', TipoCom, NumeroD, FechaE, OrdenC, MtoTotal FROM ADM_MAY.dbo.SACOMP WHERE TipoCom = 'L' AND NumeroD = @oc AND CodProv = @prov;
SELECT 'copia' donde, NroLinea, CodItem, CodUbic, Cantidad FROM SAITEMCOM WHERE TipoCom = 'L' AND NumeroD = @oc AND CodProv = @prov
UNION ALL
SELECT 'produccion', NroLinea, CodItem, CodUbic, Cantidad FROM ADM_MAY.dbo.SAITEMCOM WHERE TipoCom = 'L' AND NumeroD = @oc AND CodProv = @prov
ORDER BY 1, 2;

-- 2) La compra grabada: encabezado y líneas
SELECT TipoCom, NumeroD, CodProv, FechaE, FechaV, CodUsua, CodEsta, CodUbic, OrdenC, OTipo, ONumero, Monto, MtoTax, MtoTotal, Notas1
FROM SACOMP WHERE TipoCom = 'H' AND NumeroD = @compra AND CodProv = @prov;
SELECT NroLinea, CodItem, CodUbic, Cantidad, Costo, TotalItem, OTipo, ONumero
FROM SAITEMCOM WHERE TipoCom = 'H' AND NumeroD = @compra AND CodProv = @prov ORDER BY NroLinea;
SELECT * FROM SATAXCOM WHERE TipoCom = 'H' AND NumeroD = @compra AND CodProv = @prov;

-- 3) Compromiso de compra (pedido) de los dos productos: copia vs. producción
SELECT 'copia' donde, p.CodProd, p.Pedido, p.Existen, e.CodUbic, e.CantPed, e.Existen ExistenDep
FROM SAPROD p LEFT JOIN SAEXIS e ON e.CodProd = p.CodProd AND e.CodUbic = '115' WHERE p.CodProd IN ('173300030', '172400023')
UNION ALL
SELECT 'produccion', p.CodProd, p.Pedido, p.Existen, e.CodUbic, e.CantPed, e.Existen
FROM ADM_MAY.dbo.SAPROD p LEFT JOIN ADM_MAY.dbo.SAEXIS e ON e.CodProd = p.CodProd AND e.CodUbic = '115' WHERE p.CodProd IN ('173300030', '172400023')
ORDER BY 2, 1;
