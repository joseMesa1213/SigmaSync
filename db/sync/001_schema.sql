-- 001_schema.sql · Esquema sync de SigmaSync en cada base SAINT.
-- Idempotente: se puede ejecutar varias veces. Lo aplica el agente con "instalar-esquema".
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
IF SCHEMA_ID('sync') IS NULL EXEC('CREATE SCHEMA sync AUTHORIZATION dbo');
GO
IF OBJECT_ID('sync.SchemaVersion') IS NULL
CREATE TABLE sync.SchemaVersion (
    version     int          NOT NULL PRIMARY KEY,
    descripcion varchar(200) NOT NULL,
    aplicado_ts datetime2(0) NOT NULL DEFAULT SYSDATETIME()
);
GO
IF OBJECT_ID('sync.Config') IS NULL
CREATE TABLE sync.Config (
    clave varchar(50)  NOT NULL PRIMARY KEY,
    valor varchar(200) NOT NULL
);
GO
IF OBJECT_ID('sync.NodoTercero') IS NULL
CREATE TABLE sync.NodoTercero (
    id           int IDENTITY PRIMARY KEY,
    tipo_tercero varchar(10) NOT NULL CONSTRAINT CK_NodoTercero_tipo CHECK (tipo_tercero IN ('CLIENTE','PROVEEDOR')),
    cod_tercero  varchar(15) NOT NULL,   -- código exacto en SACLIE / SAPROV de esta base
    nodo_id      varchar(10) NOT NULL,   -- PV_MAY, FAB, CEDI…
    rol          varchar(4)  NOT NULL CONSTRAINT CK_NodoTercero_rol CHECK (rol IN ('PV','FAB','CEDI')),
    activo       bit         NOT NULL DEFAULT 1,
    CONSTRAINT UQ_NodoTercero UNIQUE (tipo_tercero, cod_tercero)
);
GO
IF OBJECT_ID('sync.Outbox') IS NULL
CREATE TABLE sync.Outbox (
    id              bigint IDENTITY PRIMARY KEY,
    msg_id          uniqueidentifier NOT NULL CONSTRAINT UQ_Outbox_msg UNIQUE,
    tipo            varchar(20)   NOT NULL,
    nodo_destino    varchar(10)   NOT NULL,
    clave_particion varchar(60)   NOT NULL,
    doc_tipo        char(1)       NULL,     -- documento SAINT que originó el mensaje (L, A…); NULL en ACK y PING
    doc_numero      varchar(20)   NULL,
    doc_nro_unico   int           NULL,
    sobre           nvarchar(max) NOT NULL, -- sobre JSON completo
    estado          varchar(10)   NOT NULL DEFAULT 'PENDIENTE', -- PENDIENTE, ENVIADO, ERROR
    intentos        int           NOT NULL DEFAULT 0,
    creado_ts       datetime2(3)  NOT NULL DEFAULT SYSDATETIME(),
    enviado_ts      datetime2(3)  NULL,
    error           nvarchar(2000) NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UQ_Outbox_doc' AND object_id = OBJECT_ID('sync.Outbox'))
    CREATE UNIQUE INDEX UQ_Outbox_doc ON sync.Outbox (tipo, doc_tipo, doc_numero, nodo_destino) WHERE doc_numero IS NOT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Outbox_estado' AND object_id = OBJECT_ID('sync.Outbox'))
    CREATE INDEX IX_Outbox_estado ON sync.Outbox (estado, id);
GO
IF OBJECT_ID('sync.Inbox') IS NULL
CREATE TABLE sync.Inbox (
    msg_id            uniqueidentifier NOT NULL PRIMARY KEY,
    tipo              varchar(20)    NOT NULL,
    nodo_origen       varchar(10)    NOT NULL,
    doc_numero_origen varchar(20)    NOT NULL,
    estado            varchar(10)    NOT NULL,  -- PROCESADO, RECHAZADO
    intentos          int            NOT NULL DEFAULT 1,
    recibido_ts       datetime2(3)   NOT NULL DEFAULT SYSDATETIME(),
    procesado_ts      datetime2(3)   NULL,
    resultado         nvarchar(max)  NULL,      -- msg_id de las respuestas encoladas
    error             nvarchar(2000) NULL
);
GO
-- Idempotencia de negocio: un documento de origen se procesa una sola vez aunque llegue con otro msg_id.
-- Un RECHAZADO no cuenta, para permitir el reproceso después de corregir maestros.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UQ_Inbox_doc' AND object_id = OBJECT_ID('sync.Inbox'))
    CREATE UNIQUE INDEX UQ_Inbox_doc ON sync.Inbox (nodo_origen, tipo, doc_numero_origen) WHERE estado = 'PROCESADO';
GO
IF OBJECT_ID('sync.DocRef') IS NULL
CREATE TABLE sync.DocRef (
    id           bigint IDENTITY PRIMARY KEY,
    relacion     varchar(12)  NOT NULL,         -- OC_PEDIDO, FACTURA_OC, OC_SALDO
    nodo_origen  varchar(10)  NOT NULL,
    tipo_origen  char(1)      NOT NULL,
    num_origen   varchar(20)  NOT NULL,
    nodo_destino varchar(10)  NOT NULL,
    tipo_destino char(1)      NOT NULL,
    num_destino  varchar(20)  NOT NULL,
    activo       bit          NOT NULL DEFAULT 1,
    msg_id       uniqueidentifier NULL,
    creado_ts    datetime2(3) NOT NULL DEFAULT SYSDATETIME()
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_DocRef_origen' AND object_id = OBJECT_ID('sync.DocRef'))
    CREATE INDEX IX_DocRef_origen ON sync.DocRef (nodo_origen, tipo_origen, num_origen);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_DocRef_destino' AND object_id = OBJECT_ID('sync.DocRef'))
    CREATE INDEX IX_DocRef_destino ON sync.DocRef (nodo_destino, tipo_destino, num_destino);
GO
IF OBJECT_ID('sync.Log') IS NULL
CREATE TABLE sync.Log (
    id         bigint IDENTITY PRIMARY KEY,
    ts         datetime2(3)  NOT NULL DEFAULT SYSDATETIME(),
    nivel      varchar(5)    NOT NULL,          -- INFO, WARN, ERROR
    componente varchar(30)   NOT NULL,
    msg_id     uniqueidentifier NULL,
    mensaje    nvarchar(500) NOT NULL,
    detalle    nvarchar(max) NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Log_ts' AND object_id = OBJECT_ID('sync.Log'))
    CREATE INDEX IX_Log_ts ON sync.Log (ts);
GO
IF NOT EXISTS (SELECT 1 FROM sync.SchemaVersion WHERE version = 1)
    INSERT sync.SchemaVersion (version, descripcion) VALUES (1, 'Esquema inicial: Config, NodoTercero, Outbox, Inbox, DocRef, Log');
GO
