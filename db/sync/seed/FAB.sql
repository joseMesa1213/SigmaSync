-- Semilla del nodo FAB (Fábrica, base SAINT_ADM).
-- Solo inserta lo que falta: los valores cambiados a mano en sync.Config se respetan.
DECLARE @cfg TABLE (clave varchar(50) PRIMARY KEY, valor varchar(200) NOT NULL);
INSERT @cfg VALUES
    ('NodoId', 'FAB'), ('Rol', 'FAB'), ('CodSucu', '00000'),
    -- Depósito 005 "Despachos", vendedor 1305 y operación 1069 "ENTREGAS ALMACENES": los de las facturas de Fábrica al PV en 2026.
    ('CodUsua', 'SYNC'), ('CodEsta', 'SYNC'), ('CodUbic', '005'), ('CodVend', '1305'), ('CodOper', '1069'),
    ('VentanaDias', '3'), ('EsperaSegundos', '60'),
    ('CapturaDesde', CONVERT(varchar(10), GETDATE(), 23));

MERGE sync.Config AS t USING @cfg AS s ON t.clave = s.clave
WHEN NOT MATCHED THEN INSERT (clave, valor) VALUES (s.clave, s.valor);

DECLARE @ter TABLE (tipo_tercero varchar(10), cod_tercero varchar(15), nodo_id varchar(10), rol varchar(4));
INSERT @ter VALUES
    ('CLIENTE', '2853060', 'PV_MAY', 'PV');   -- SGMPL ITAGUI B19

MERGE sync.NodoTercero AS t USING @ter AS s ON t.tipo_tercero = s.tipo_tercero AND t.cod_tercero = s.cod_tercero
WHEN NOT MATCHED THEN INSERT (tipo_tercero, cod_tercero, nodo_id, rol) VALUES (s.tipo_tercero, s.cod_tercero, s.nodo_id, s.rol);
