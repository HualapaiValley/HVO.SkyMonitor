-- Run in SQLCMD mode only after controlled database initialization succeeds.
-- Required variables: DatabaseName, RuntimeUser.
USE [$(DatabaseName)];
SET XACT_ABORT ON;

IF DATABASE_PRINCIPAL_ID(N'$(RuntimeUser)') IS NULL
    THROW 51000, 'The configured LogicHost runtime database user does not exist.', 1;
IF OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U') IS NULL
    THROW 51001, '__EFMigrationsHistory is absent; database initialization did not complete.', 1;
IF OBJECT_ID(N'dbo.DatabaseInitializationState', N'U') IS NULL
    THROW 51002, 'DatabaseInitializationState is absent; database initialization did not complete.', 1;
IF NOT EXISTS (
    SELECT 1
    FROM [dbo].[DatabaseInitializationState]
    WHERE [Id] = 1 AND [Status] = N'Completed')
    THROW 51003, 'Database initialization is not complete.', 1;

BEGIN TRANSACTION;

IF DATABASE_PRINCIPAL_ID(N'hvo_logichost_runtime') IS NULL
    CREATE ROLE [hvo_logichost_runtime] AUTHORIZATION [dbo];

IF IS_ROLEMEMBER(N'hvo_logichost_runtime', N'$(RuntimeUser)') <> 1
    ALTER ROLE [hvo_logichost_runtime] ADD MEMBER [$(RuntimeUser)];

GRANT CONNECT TO [hvo_logichost_runtime];
GRANT SELECT ON SCHEMA::[dbo] TO [hvo_logichost_runtime];

DECLARE @runtimeDml nvarchar(max);
SELECT @runtimeDml = STRING_AGG(
    CONVERT(nvarchar(max), N'GRANT INSERT, UPDATE, DELETE ON OBJECT::[dbo].' + QUOTENAME([name]) +
        N' TO [hvo_logichost_runtime];'),
    NCHAR(10))
FROM [sys].[tables]
WHERE [schema_id] = SCHEMA_ID(N'dbo')
  AND [name] NOT IN (N'__EFMigrationsHistory', N'DatabaseInitializationState');
IF @runtimeDml IS NOT NULL
    EXEC sys.sp_executesql @runtimeDml;

DECLARE @runtimeSequences nvarchar(max);
SELECT @runtimeSequences = STRING_AGG(
    CONVERT(nvarchar(max), N'GRANT UPDATE ON OBJECT::[dbo].' + QUOTENAME([name]) +
        N' TO [hvo_logichost_runtime];'),
    NCHAR(10))
FROM [sys].[sequences]
WHERE [schema_id] = SCHEMA_ID(N'dbo');
IF @runtimeSequences IS NOT NULL
    EXEC sys.sp_executesql @runtimeSequences;

DENY ALTER ON SCHEMA::[dbo] TO [hvo_logichost_runtime];
DENY CREATE SEQUENCE ON SCHEMA::[dbo] TO [hvo_logichost_runtime];
DENY ALTER, ALTER ANY SCHEMA, CREATE TABLE, CREATE VIEW, CREATE PROCEDURE,
    CREATE FUNCTION, CREATE TYPE, CREATE SYNONYM TO [hvo_logichost_runtime];

COMMIT TRANSACTION;
