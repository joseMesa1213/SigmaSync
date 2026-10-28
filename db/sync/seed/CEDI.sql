-- Semilla del nodo CEDI (base BODEGA1).
-- Solo inserta lo que falta: los valores cambiados a mano en sync.Config se respetan.
-- Depósito 100 "CENTRO DE DISTRIBUCION C.D." y vendedor 9810: los más usados en las facturas del CEDI al PV en 2026
-- (hay también 4258, 4287 y 4288). El CEDI no usa CodOper. Vendedor por confirmar con el cliente.
DECLARE @cfg TABLE (clave varchar(50) PRIMARY KEY, valor varchar(200) NOT NULL);
INSERT @cfg VALUES
    ('NodoId', 'CEDI'), ('Rol', 'CEDI'), ('CodSucu', '00000'),
    ('CodUsua', 'SYNC'), ('CodEsta', 'SYNC'), ('CodUbic', '100'), ('CodVend', '9810'),
    ('VentanaDias', '3'), ('EsperaSegundos', '60'),
    ('CapturaDesde', CONVERT(varchar(10), GETDATE(), 23));

MERGE sync.Config AS t USING @cfg AS s ON t.clave = s.clave
WHEN NOT MATCHED THEN INSERT (clave, valor) VALUES (s.clave, s.valor);

DECLARE @ter TABLE (tipo_tercero varchar(10), cod_tercero varchar(15), nodo_id varchar(10), rol varchar(4));
INSERT @ter VALUES
    ('CLIENTE', '9012380209', 'PV_MAY', 'PV');   -- SIGMAPLASTICOS MAYORISTA BLOQUE 19

MERGE sync.NodoTercero AS t USING @ter AS s ON t.tipo_tercero = s.tipo_tercero AND t.cod_tercero = s.cod_tercero
WHEN NOT MATCHED THEN INSERT (tipo_tercero, cod_tercero, nodo_id, rol) VALUES (s.tipo_tercero, s.cod_tercero, s.nodo_id, s.rol);
