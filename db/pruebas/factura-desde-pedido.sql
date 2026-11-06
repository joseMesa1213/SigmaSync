-- SOLO PARA PRUEBAS EN LocalDB (dentro de los snapshots de docs/pruebas-manuales.md, paso 10b). Nunca en el cliente.
-- Simula en SAINT_ADM (LocalDB, dentro del snapshot) lo que hace SAINT al facturar cargando un pedido, según *33647/*33648:
-- factura A con líneas OTipo='E'/ONumero=pedido, SATAXVTA, OrdenC del pedido; el pedido queda con el saldo
-- (líneas cumplidas borradas, parciales con Cantidad = saldo) o se borra; Compro/CantCom bajan lo facturado.
-- Variables: $(FAC) número de factura, $(PED) pedido, $(LINEAS) 'CodItem:cant,CodItem:cant'
USE SAINT_ADM;
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
DECLARE @fac varchar(20) = '$(FAC)', @ped varchar(20) = '$(PED)', @lineas varchar(4000) = '$(LINEAS)';

DECLARE @l TABLE (CodItem varchar(15), Cantidad decimal(28, 5));
DECLARE @x xml = CAST('<i><c>' + REPLACE(REPLACE(@lineas, '~', '</c><q>'), '+', '</q></i><i><c>') + '</q></i>' AS xml);
INSERT @l SELECT n.value('c[1]', 'varchar(15)'), n.value('q[1]', 'decimal(28,5)') FROM @x.nodes('/i') t(n);

BEGIN TRAN;
-- Ítems: precio y datos del pedido, cantidad facturada
SELECT i.* INTO #i FROM SAITEMFAC i WHERE i.TipoFac = 'E' AND i.NumeroD = @ped;
UPDATE #i SET TipoFac = 'A', NumeroD = @fac, OTipo = 'E', ONumero = @ped, Cantidad = l.Cantidad,
       TotalItem = ROUND(#i.Precio * l.Cantidad, 2), MtoTax = ROUND(#i.Precio * l.Cantidad * 0.19, 3),
       MtoTaxO = ROUND(#i.Precio * l.Cantidad * 0.19, 4), FechaE = GETDATE()
FROM @l l WHERE l.CodItem = #i.CodItem;
DELETE FROM #i WHERE TipoFac = 'E';   -- líneas del pedido que no se facturan

DECLARE @monto decimal(28, 4) = (SELECT SUM(TotalItem) FROM #i), @iva decimal(28, 4);
SET @iva = ROUND(@monto * 0.19, 2);

-- Encabezado: copia del pedido como A
SELECT f.* INTO #f FROM SAFACT f WHERE f.TipoFac = 'E' AND f.NumeroD = @ped;
UPDATE #f SET TipoFac = 'A', NumeroD = @fac, FechaE = GETDATE(), FechaI = GETDATE(), FechaV = GETDATE(), FechaT = GETDATE(),
       Monto = @monto, TGravable = @monto, TExento = 0, TotalPrd = @monto, MtoTax = @iva, MtoTotal = @monto + @iva,
       Credito = @monto + @iva, Notas1 = NULL, CodUsua = 'JFM', CodEsta = 'DESKTOP-HJ';

DECLARE @cols nvarchar(max), @sql nvarchar(max);
SELECT @cols = STUFF((SELECT ',' + QUOTENAME(name) FROM sys.columns WHERE object_id = OBJECT_ID('SAFACT') AND is_identity = 0
                      ORDER BY column_id FOR XML PATH('')), 1, 1, '');
SET @sql = N'INSERT SAFACT (' + @cols + N') SELECT ' + @cols + N' FROM #f'; EXEC (@sql);
SELECT @cols = STUFF((SELECT ',' + QUOTENAME(name) FROM sys.columns WHERE object_id = OBJECT_ID('SAITEMFAC') AND is_identity = 0
                      ORDER BY column_id FOR XML PATH('')), 1, 1, '');
SET @sql = N'INSERT SAITEMFAC (' + @cols + N') SELECT ' + @cols + N' FROM #i'; EXEC (@sql);
INSERT SATAXVTA (CodSucu, TipoFac, NumeroD, CodTaxs, Monto, MtoTax, TGravable, EsReten)
VALUES ('00000', 'A', @fac, 'IVA', @iva, 19, @monto, 0);

-- Pedido: como SAINT (borra lo cumplido, deja el saldo) y comprometido
UPDATE p SET Compro = Compro - l.Cantidad FROM SAPROD p JOIN @l l ON l.CodItem = p.CodProd;
UPDATE e SET CantCom = CantCom - l.Cantidad FROM SAEXIS e JOIN @l l ON l.CodItem = e.CodProd WHERE e.CodUbic = '005';
UPDATE i SET Cantidad = i.Cantidad - l.Cantidad FROM SAITEMFAC i JOIN @l l ON l.CodItem = i.CodItem WHERE i.TipoFac = 'E' AND i.NumeroD = @ped;
DELETE FROM SAITEMFAC WHERE TipoFac = 'E' AND NumeroD = @ped AND Cantidad <= 0;
IF NOT EXISTS (SELECT 1 FROM SAITEMFAC WHERE TipoFac = 'E' AND NumeroD = @ped)
    DELETE FROM SAFACT WHERE TipoFac = 'E' AND NumeroD = @ped;
COMMIT;

SELECT NumeroD, OrdenC, Monto, MtoTax, MtoTotal FROM SAFACT WHERE TipoFac = 'A' AND NumeroD = @fac;
SELECT NroLinea, CodItem, Cantidad, Precio, TotalItem, OTipo, ONumero FROM SAITEMFAC WHERE TipoFac = 'A' AND NumeroD = @fac;
SELECT 'pedido' doc, NroLinea, CodItem, Cantidad FROM SAITEMFAC WHERE TipoFac = 'E' AND NumeroD = @ped;
