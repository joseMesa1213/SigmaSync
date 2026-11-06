-- PV: ¿qué hace SAINT con SAEXIS al grabar (y borrar) una OC de un producto sin fila en el depósito 115?
-- Solo lectura, copia del PV. Ctrl+A y F5.
--   Paso 1: @prod = '' → la consulta 1 lista candidatos. Elegir uno y ponerlo en @prod.
--   Fotos:  con @prod lleno, antes de grabar la OC, después de grabarla y después de borrarla.
USE ADM_MAY_SYNCPRB;
SELECT DB_NAME() AS base_actual;   -- debe decir ADM_MAY_SYNCPRB
DECLARE @prod varchar(15) = '';

-- 1) Candidatos: activos, sin fila en SAEXIS del 115, comprados alguna vez (más recientes primero)
IF @prod = ''
    SELECT TOP 10 p.CodProd, p.Descrip, p.Pedido, p.Existen,
           (SELECT COUNT(*) FROM SAEXIS e WHERE e.CodProd = p.CodProd) filas_saexis_otros_dep,
           (SELECT MAX(i.FechaE) FROM SAITEMCOM i WHERE i.CodItem = p.CodProd) ultima_compra
    FROM SAPROD p
    WHERE p.Activo = 1 AND NOT EXISTS (SELECT 1 FROM SAEXIS e WHERE e.CodProd = p.CodProd AND e.CodUbic = '115')
    ORDER BY ultima_compra DESC;

-- 2) Foto: SAPROD, todas las filas de SAEXIS del producto y la última OC
SELECT CodProd, Pedido, Existen FROM SAPROD WHERE CodProd = @prod;
SELECT * FROM SAEXIS WHERE CodProd = @prod ORDER BY CodUbic;
SELECT FieldName, ValueInt FROM SACORRELSIS WHERE FieldName = 'PrxOrdenC';
SELECT TOP 2 c.NumeroD, c.CodProv, c.FechaE, c.MtoTotal, i.CodItem, i.Cantidad
FROM SACOMP c JOIN SAITEMCOM i ON i.TipoCom = c.TipoCom AND i.NumeroD = c.NumeroD AND i.CodProv = c.CodProv
WHERE c.TipoCom = 'L' ORDER BY c.NumeroD DESC;
