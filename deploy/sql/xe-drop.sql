-- SQLCMD variable: RunId. Drops only the exactly named run-owned session; remove its exact files separately.
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @runId varchar(32) = '$(ESCAPE_SQUOTE(RunId))';
IF ISNULL(TRY_CONVERT(int, SERVERPROPERTY(N'ProductMajorVersion')), 0) <> 16
    THROW 51255, 'This toolkit supports SQL Server 2022 only.', 1;
IF LEN(@runId) <> 32 OR @runId COLLATE Latin1_General_100_BIN2 LIKE '%[^0-9a-f]%'
    THROW 51255, 'RunId must contain exactly 32 lowercase hexadecimal characters.', 1;

DECLARE @sessionName sysname = N'HvoSqlOps_' + @runId;
BEGIN TRY
IF EXISTS (SELECT 1 FROM sys.dm_xe_sessions WHERE [name] = @sessionName)
BEGIN
    DECLARE @stop nvarchar(max) = N'ALTER EVENT SESSION ' + QUOTENAME(@sessionName) + N' ON SERVER STATE=STOP;';
    EXEC sys.sp_executesql @stop;
END;
IF EXISTS (SELECT 1 FROM sys.server_event_sessions WHERE [name] = @sessionName)
BEGIN
    DECLARE @drop nvarchar(max) = N'DROP EVENT SESSION ' + QUOTENAME(@sessionName) + N' ON SERVER;';
    EXEC sys.sp_executesql @drop;
END;
END TRY
BEGIN CATCH
    THROW 51255, 'The exact run-owned Extended Events session could not be dropped.', 1;
END CATCH;

SELECT N'extended-events' AS [area], N'dropped' AS [state];
