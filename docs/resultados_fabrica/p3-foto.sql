-- P3, CEDI: pedido grabado en pantalla en la copia BODEGA1_SYNCPRB, como modelo para comparar con F1.
-- Solo lectura. Ctrl+A y F5.
--   ANTES:   @ped = '' (sale la lista de productos sugeridos y el comprometido actual)
--   DESPUÉS: @ped = número del pedido que asignó SAINT (como sale en pantalla)
USE BODEGA1_SYNCPRB;
SELECT DB_NAME() AS base_actual;   -- debe decir BODEGA1_SYNCPRB
DECLARE @ped varchar(20) = '', @clie varchar(15) = '9012380209';

SELECT CodSucu, FieldName, ValueInt FROM SACORRELSIS WHERE FieldName IN ('PrxPedido', 'PrxFact', 'LenCorrel') ORDER BY CodSucu, FieldName;
SELECT COUNT(*) pedidos_en_cedi, MAX(FechaE) ultimo FROM SAFACT WHERE TipoFac = 'E';

-- 1) Productos sugeridos: 111216 y los de la última factura del CEDI al PV (depósito 100)
SELECT TOP 4 i.CodItem, i.Descrip1, i.Cantidad, i.Precio, f.NumeroD fac, f.FechaE
FROM SAITEMFAC i JOIN SAFACT f ON f.TipoFac = i.TipoFac AND f.NumeroD = i.NumeroD
WHERE f.TipoFac = 'A' AND f.CodClie = @clie
  AND f.NumeroD = (SELECT TOP 1 NumeroD FROM SAFACT WHERE TipoFac = 'A' AND CodClie = @clie ORDER BY FechaE DESC)
ORDER BY i.NroLinea;

-- 2) Comprometido de 111216 y de los productos del pedido
SELECT p.CodProd, p.Compro, p.Existen, e.CodUbic, e.CantCom, e.Existen ExistenDep
FROM SAPROD p LEFT JOIN SAEXIS e ON e.CodProd = p.CodProd AND e.CodUbic = '100'
WHERE p.CodProd = '111216' OR p.CodProd IN (SELECT CodItem FROM SAITEMFAC WHERE TipoFac = 'E' AND NumeroD = @ped)
ORDER BY p.CodProd;

-- 3) El pedido completo (modelo para comparar con lo que graba F1 en el CEDI)
SELECT * FROM SAFACT WHERE TipoFac = 'E' AND NumeroD = @ped;
SELECT * FROM SAITEMFAC WHERE TipoFac = 'E' AND NumeroD = @ped ORDER BY NroLinea;
SELECT * FROM SATAXVTA WHERE TipoFac = 'E' AND NumeroD = @ped;
SELECT * FROM SATAXITF WHERE TipoFac = 'E' AND NumeroD = @ped;
