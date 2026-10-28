-- 001_hub.sql · Base SigmaSyncHub: nodos y cola de mensajes.
-- Idempotente. Lo aplica el hub con "instalar-bd" (crea la base si no existe).
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
IF SCHEMA_ID('hub') IS NULL EXEC('CREATE SCHEMA hub AUTHORIZATION dbo');
GO
IF OBJECT_ID('hub.SchemaVersion') IS NULL
CREATE TABLE hub.SchemaVersion (
    version     int          NOT NULL PRIMARY KEY,
    descripcion varchar(200) NOT NULL,
    aplicado_ts datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
IF OBJECT_ID('hub.Nodo') IS NULL
CREATE TABLE hub.Nodo (
    nodo_id        varchar(10)    NOT NULL PRIMARY KEY,
    descripcion    varchar(100)   NOT NULL,
    api_key_hash   varbinary(32)  NOT NULL,   -- SHA-256 de la API key
    hmac_secret    varbinary(512) NOT NULL,   -- secreto HMAC cifrado con DPAPI (máquina)
    activo         bit            NOT NULL DEFAULT 1,
    ultimo_latido  datetime2(0)   NULL,
    version_agente varchar(20)    NULL,
    estado_latido  nvarchar(max)  NULL
);
GO
IF OBJECT_ID('hub.Mensaje') IS NULL
CREATE TABLE hub.Mensaje (
    seq             bigint IDENTITY NOT NULL,   -- orden de llegada (orden dentro de la partición)
    msg_id          uniqueidentifier NOT NULL CONSTRAINT PK_Mensaje PRIMARY KEY NONCLUSTERED,
    tipo            varchar(20)    NOT NULL,
    nodo_origen     varchar(10)    NOT NULL,
    nodo_destino    varchar(10)    NOT NULL,
    clave_particion varchar(60)    NOT NULL,
    sobre           nvarchar(max)  NOT NULL,    -- sobre JSON completo
    estado          varchar(12)    NOT NULL DEFAULT 'PENDIENTE', -- PENDIENTE, EN_PROCESO, CONFIRMADO, RECHAZADO, DLQ
    intentos        int            NOT NULL DEFAULT 0,
    visible_desde   datetime2(3)   NOT NULL DEFAULT SYSUTCDATETIME(),
    lease_hasta     datetime2(3)   NULL,
    creado_ts       datetime2(3)   NOT NULL DEFAULT SYSUTCDATETIME(),
    confirmado_ts   datetime2(3)   NULL,
    error           nvarchar(2000) NULL,
    CONSTRAINT UQ_Mensaje_seq UNIQUE CLUSTERED (seq)
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Mensaje_cola' AND object_id = OBJECT_ID('hub.Mensaje'))
    CREATE INDEX IX_Mensaje_cola ON hub.Mensaje (nodo_destino, estado, visible_desde) INCLUDE (clave_particion);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Mensaje_particion' AND object_id = OBJECT_ID('hub.Mensaje'))
    CREATE INDEX IX_Mensaje_particion ON hub.Mensaje (nodo_destino, clave_particion, seq) INCLUDE (estado);
GO
IF NOT EXISTS (SELECT 1 FROM hub.SchemaVersion WHERE version = 1)
    INSERT hub.SchemaVersion (version, descripcion) VALUES (1, 'Esquema inicial: Nodo, Mensaje');
GO
