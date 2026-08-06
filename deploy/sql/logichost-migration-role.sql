-- Run in SQLCMD mode after the database users have been provisioned.
-- Required variables: DatabaseName, MigrationUser.
USE [$(DatabaseName)];
SET XACT_ABORT ON;

IF DATABASE_PRINCIPAL_ID(N'$(MigrationUser)') IS NULL
    THROW 51000, 'The configured LogicHost migration database user does not exist.', 1;

BEGIN TRANSACTION;

IF DATABASE_PRINCIPAL_ID(N'hvo_logichost_migrator') IS NULL
    CREATE ROLE [hvo_logichost_migrator] AUTHORIZATION [dbo];

IF IS_ROLEMEMBER(N'hvo_logichost_migrator', N'$(MigrationUser)') <> 1
    ALTER ROLE [hvo_logichost_migrator] ADD MEMBER [$(MigrationUser)];

GRANT CONNECT TO [hvo_logichost_migrator];
GRANT CREATE TABLE TO [hvo_logichost_migrator];
GRANT ALTER, REFERENCES, SELECT, INSERT, UPDATE, DELETE
    ON SCHEMA::[dbo]
    TO [hvo_logichost_migrator];

COMMIT TRANSACTION;
