-- usuario_sigmasync.sql · Login SQL dedicado del agente con permisos mínimos.
-- Lo ejecuta un administrador del cliente con sqlcmd, una vez por base SAINT del servidor:
--   sqlcmd -S SERVIDOR\SQLEXPRESS -E -v Base="SAINT_ADM" Clave="********" -i usuario_sigmasync.sql
-- Antes de esto, el administrador instala el esquema sync con "SigmaSync.Agent.exe instalar-esquema"
-- (requiere permisos para crear tablas; el agente en operación no los necesita).
SET NOCOUNT ON;
USE [master];
IF SUSER_ID('sigmasync') IS NULL
    CREATE LOGIN sigmasync WITH PASSWORD = '$(Clave)', CHECK_POLICY = ON, CHECK_EXPIRATION = OFF, DEFAULT_DATABASE = [$(Base)];
GO
USE [$(Base)];
IF USER_ID('sigmasync') IS NULL
    CREATE USER sigmasync FOR LOGIN sigmasync WITH DEFAULT_SCHEMA = dbo;

-- Lectura de SAINT (maestros, documentos, consecutivos).
GRANT SELECT ON SCHEMA::dbo TO sigmasync;

-- Escritura SAINT: solo las tablas que tocan los flujos F1, F2 y F3.
GRANT INSERT, UPDATE ON dbo.SAFACT      TO sigmasync;   -- pedido E (F1)
GRANT INSERT, UPDATE ON dbo.SAITEMFAC   TO sigmasync;
GRANT INSERT, UPDATE ON dbo.SACOMP      TO sigmasync;   -- OC L (F2, F3)
GRANT INSERT, UPDATE, DELETE ON dbo.SAITEMCOM TO sigmasync;   -- DELETE: líneas de OC que F2 elimina
GRANT UPDATE ON dbo.SAPROD      TO sigmasync;   -- comprometido
GRANT UPDATE ON dbo.SAEXIS      TO sigmasync;
GRANT UPDATE ON dbo.SACORRELSIS TO sigmasync;   -- consecutivos

-- Esquema sync completo (sin DDL).
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::sync TO sigmasync;
GO
PRINT 'Usuario sigmasync listo en $(Base).';
