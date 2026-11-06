-- P3: error "El nombre de columna 'Status' no es válido" al grabar el pedido 000001 en el CEDI. Solo lectura.
-- 1) ¿Quedó algo grabado del pedido en la copia del CEDI?
USE BODEGA1_SYNCPRB;
SELECT DB_NAME() AS base_actual;
SELECT FieldName, ValueInt FROM SACORRELSIS WHERE FieldName IN ('PrxPedido', 'PrxFact');
SELECT TipoFac, NumeroD, CodClie, CodUsua, FechaE, OrdenC, MtoTotal FROM SAFACT WHERE TipoFac = 'E';
SELECT TipoFac, NumeroD, NroLinea, CodItem, Cantidad FROM SAITEMFAC WHERE TipoFac = 'E';
SELECT p.CodProd, p.Compro, e.CantCom FROM SAPROD p LEFT JOIN SAEXIS e ON e.CodProd = p.CodProd AND e.CodUbic = '100'
WHERE p.CodProd IN ('111216', '110818', '111014') ORDER BY p.CodProd;

-- 2) Columnas Status: dónde existen en Fábrica, el PV no está en este servidor (copia y producción, solo metadatos)
SELECT 'fabrica' base, t.name tabla, c.name columna, TYPE_NAME(c.user_type_id) tipo, c.max_length, c.is_nullable,
       dc.definition AS valor_por_defecto
FROM SAINT_ADM_SYNCPRB.sys.columns c
JOIN SAINT_ADM_SYNCPRB.sys.tables t ON t.object_id = c.object_id
LEFT JOIN SAINT_ADM_SYNCPRB.sys.default_constraints dc ON dc.object_id = c.default_object_id
WHERE c.name = 'Status'
UNION ALL
SELECT 'cedi', t.name, c.name, TYPE_NAME(c.user_type_id), c.max_length, c.is_nullable, dc.definition
FROM BODEGA1_SYNCPRB.sys.columns c
JOIN BODEGA1_SYNCPRB.sys.tables t ON t.object_id = c.object_id
LEFT JOIN BODEGA1_SYNCPRB.sys.default_constraints dc ON dc.object_id = c.default_object_id
WHERE c.name = 'Status'
ORDER BY 2, 1;

-- 3) Versión del esquema de SAINT registrada en cada base (si existe la tabla de configuración)
SELECT 'fabrica' base, * FROM SAINT_ADM_SYNCPRB.sys.extended_properties WHERE class = 0;
SELECT 'cedi' base, * FROM BODEGA1_SYNCPRB.sys.extended_properties WHERE class = 0;

-- 4) Otras columnas que tiene Fábrica y le faltan al CEDI en las tablas de ventas
SELECT t.name tabla, c.name columna, TYPE_NAME(c.user_type_id) tipo
FROM SAINT_ADM_SYNCPRB.sys.columns c JOIN SAINT_ADM_SYNCPRB.sys.tables t ON t.object_id = c.object_id
WHERE t.name IN ('SAFACT', 'SAITEMFAC', 'SATAXVTA', 'SATAXITF', 'SAPROD', 'SAEXIS', 'SACLIE', 'SACORRELSIS')
  AND NOT EXISTS (SELECT 1 FROM BODEGA1_SYNCPRB.sys.columns c2 JOIN BODEGA1_SYNCPRB.sys.tables t2 ON t2.object_id = c2.object_id
                  WHERE t2.name = t.name COLLATE DATABASE_DEFAULT AND c2.name = c.name COLLATE DATABASE_DEFAULT)
ORDER BY 1, 2;
