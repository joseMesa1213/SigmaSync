-- F1: ¿qué hace SAINT con SAEXIS al grabar (y borrar) un pedido de un producto sin fila en el depósito 005?
-- Solo lectura, copia de Fábrica. Ctrl+A y F5.
--   Paso 1: @sin = '' → la consulta 1 lista candidatos. Elegir uno y ponerlo en @sin.
--   Fotos:  con @sin lleno, antes de grabar, después de grabar y después de borrar el pedido. @con es el control (sí tiene fila).
USE SAINT_ADM_SYNCPRB;
SELECT DB_NAME() AS base_actual;   -- debe decir SAINT_ADM_SYNCPRB
DECLARE @sin varchar(15) = '', @con varchar(15) = '3458', @ped varchar(20) = '035852';

-- 1) Candidatos: activos, con Precio3, sin fila en SAEXIS del 005, vendidos alguna vez desde el 005 (más realistas primero)
IF @sin = ''
    SELECT TOP 10 p.CodProd, p.Descrip, p.Precio3, p.Existen, p.Compro,
           (SELECT COUNT(*) FROM SAEXIS e WHERE e.CodProd = p.CodProd) filas_saexis_otros_dep,
           (SELECT MAX(i.FechaE) FROM SAITEMFAC i WHERE i.CodItem = p.CodProd AND i.TipoFac = 'A') ultima_venta
    FROM SAPROD p
    WHERE p.Activo = 1 AND p.Precio3 > 0
      AND NOT EXISTS (SELECT 1 FROM SAEXIS e WHERE e.CodProd = p.CodProd AND e.CodUbic = '005')
    ORDER BY ultima_venta DESC;

-- 2) Foto: SAPROD y todas las filas de SAEXIS de los dos productos
SELECT p.CodProd, p.Compro, p.Existen, p.Precio3 FROM SAPROD p WHERE p.CodProd IN (@sin, @con) ORDER BY p.CodProd;
SELECT e.* FROM SAEXIS e WHERE e.CodProd IN (@sin, @con) ORDER BY e.CodProd, e.CodUbic;

-- 3) El pedido
SELECT FieldName, ValueInt FROM SACORRELSIS WHERE CodSucu = '00000' AND FieldName = 'PrxPedido';
SELECT TipoFac, NumeroD, CodClie, OrdenC, MtoTotal FROM SAFACT WHERE TipoFac = 'E' AND NumeroD = @ped;
SELECT NroLinea, CodItem, CodUbic, Cantidad, Precio, ExistAnt FROM SAITEMFAC WHERE TipoFac = 'E' AND NumeroD = @ped ORDER BY NroLinea;
