-- 002_alertas.sql · Alertas centralizadas del hub (fase 5). Idempotente.
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
IF OBJECT_ID('hub.Alerta') IS NULL
CREATE TABLE hub.Alerta (
    id         bigint IDENTITY PRIMARY KEY,
    ts         datetime2(0)   NOT NULL DEFAULT SYSUTCDATETIME(),
    nodo_id    varchar(10)    NOT NULL,
    fuente     varchar(4)     NOT NULL,          -- HUB (DLQ, rechazo, latido) o NODO (sync.Log WARN/ERROR del agente)
    nivel      varchar(5)     NOT NULL,          -- INFO, WARN, ERROR
    mensaje    nvarchar(1000) NOT NULL,
    msg_id     uniqueidentifier NULL,
    log_id     bigint         NULL,              -- id en sync.Log del nodo (deduplicación de lo que llega con el latido)
    notificada bit            NOT NULL DEFAULT 0 -- incluida en un correo de resumen
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UQ_Alerta_log' AND object_id = OBJECT_ID('hub.Alerta'))
    CREATE UNIQUE INDEX UQ_Alerta_log ON hub.Alerta (nodo_id, log_id) WHERE log_id IS NOT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Alerta_pendientes' AND object_id = OBJECT_ID('hub.Alerta'))
    CREATE INDEX IX_Alerta_pendientes ON hub.Alerta (notificada, id);
GO
-- Marca de nodo caído: se fija al detectar la falta de latido y se limpia al volver (una alerta por caída, no una por ciclo).
IF COL_LENGTH('hub.Nodo', 'sin_latido_desde') IS NULL
    ALTER TABLE hub.Nodo ADD sin_latido_desde datetime2(0) NULL;
GO
IF NOT EXISTS (SELECT 1 FROM hub.SchemaVersion WHERE version = 2)
    INSERT hub.SchemaVersion (version, descripcion) VALUES (2, 'Alertas centralizadas y control de latido');
GO
