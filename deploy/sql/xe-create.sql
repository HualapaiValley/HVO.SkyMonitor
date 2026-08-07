-- SQLCMD variables: DatabaseName, RunId, IncludeInstanceWideDeadlocks, RunEventDirectory, EventFilePath.
-- Invoke through scripts/sql-xe:create. T-SQL cannot establish filesystem ownership or prefix absence.
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @runId varchar(32) = '$(ESCAPE_SQUOTE(RunId))';
DECLARE @databaseName sysname = N'$(ESCAPE_SQUOTE(DatabaseName))';
DECLARE @includeInstanceWideDeadlocks bit = TRY_CONVERT(bit, N'$(ESCAPE_SQUOTE(IncludeInstanceWideDeadlocks))');
DECLARE @runEventDirectory nvarchar(4000) = N'$(ESCAPE_SQUOTE(RunEventDirectory))';
DECLARE @eventFilePath nvarchar(4000) = N'$(ESCAPE_SQUOTE(EventFilePath))';
DECLARE @databaseId int = DB_ID(@databaseName);
IF ISNULL(TRY_CONVERT(int, SERVERPROPERTY(N'ProductMajorVersion')), 0) <> 16
    THROW 51255, 'This toolkit supports SQL Server 2022 only.', 1;
IF LEN(@runId) <> 32 OR @runId COLLATE Latin1_General_100_BIN2 LIKE '%[^0-9a-f]%'
    THROW 51255, 'RunId must contain exactly 32 lowercase hexadecimal characters.', 1;
IF DB_NAME() <> N'master' OR @databaseId IS NULL OR @databaseId <= 4
   OR @databaseName = N'' OR @databaseName LIKE N'%[^0-9A-Za-z_-]%'
    THROW 51255, 'Run from master with a validated non-system DatabaseName.', 1;
IF (SELECT [compatibility_level] FROM sys.databases WHERE [database_id] = @databaseId) <> 160
    THROW 51255, 'The target database must use SQL Server 2022 compatibility level 160.', 1;
IF @includeInstanceWideDeadlocks <> 1
    THROW 51255, 'Instance-wide deadlock capture must be explicitly selected.', 1;
IF @runEventDirectory = N'' OR @runEventDirectory LIKE N'%..%' OR @runEventDirectory LIKE N'%/./%'
   OR @runEventDirectory LIKE N'%//%' OR @runEventDirectory LIKE N'%://%' OR RIGHT(@runEventDirectory, 1) = N'/'
   OR LEFT(@runEventDirectory, 1) <> N'/'
    THROW 51255, 'The run-owned event directory is not canonical.', 1;
DECLARE @controlCode int = 0;
WHILE @controlCode <= 31
BEGIN
    IF CHARINDEX(NCHAR(@controlCode), @runEventDirectory + @eventFilePath COLLATE Latin1_General_100_BIN2) > 0
        THROW 51255, 'Event paths cannot contain control characters.', 1;
    SET @controlCode += 1;
END;
IF RIGHT(@runEventDirectory, 39) <> N'hvo-xe-' + CONVERT(nvarchar(32), @runId) COLLATE Latin1_General_100_BIN2
   OR @eventFilePath <> @runEventDirectory + N'/events' COLLATE Latin1_General_100_BIN2
   OR @eventFilePath LIKE N'%.xel' OR @eventFilePath LIKE N'%*%' OR @eventFilePath LIKE N'%?%'
    THROW 51255, 'EventFilePath is outside the exact run-owned directory.', 1;

DECLARE @sessionName sysname = N'HvoSqlOps_' + @runId;
IF EXISTS (SELECT 1 FROM sys.server_event_sessions WHERE [name] = @sessionName)
    THROW 51255, 'The run-owned Extended Events session already exists.', 1;

DECLARE @create nvarchar(max) = N'CREATE EVENT SESSION ' + QUOTENAME(@sessionName) + N' ON SERVER
ADD EVENT sqlserver.blocked_process_report(
    ACTION(sqlserver.client_app_name)
    WHERE ([database_id]=(' + CONVERT(nvarchar(11), @databaseId) + N'))),
ADD EVENT sqlserver.xml_deadlock_report(
    ACTION(sqlserver.client_app_name))
ADD TARGET package0.event_file(
    SET filename=N''' + REPLACE(@eventFilePath, N'''', N'''''') + N''',
        max_file_size=(5), max_rollover_files=(2))
WITH (MAX_MEMORY=4096 KB, EVENT_RETENTION_MODE=ALLOW_SINGLE_EVENT_LOSS,
      MAX_DISPATCH_LATENCY=5 SECONDS, TRACK_CAUSALITY=ON, STARTUP_STATE=OFF);';
BEGIN TRY
    EXEC sys.sp_executesql @create;
    DECLARE @start nvarchar(max) = N'ALTER EVENT SESSION ' + QUOTENAME(@sessionName) + N' ON SERVER STATE=START;';
    EXEC sys.sp_executesql @start;
END TRY
BEGIN CATCH
    DECLARE @cleanupFailed bit = 0;
    BEGIN TRY
        IF EXISTS (SELECT 1 FROM sys.dm_xe_sessions WHERE [name] = @sessionName)
        BEGIN
            DECLARE @failedStop nvarchar(max) = N'ALTER EVENT SESSION ' + QUOTENAME(@sessionName) + N' ON SERVER STATE=STOP;';
            EXEC sys.sp_executesql @failedStop;
        END;
        IF EXISTS (SELECT 1 FROM sys.server_event_sessions WHERE [name] = @sessionName)
        BEGIN
            DECLARE @failedDrop nvarchar(max) = N'DROP EVENT SESSION ' + QUOTENAME(@sessionName) + N' ON SERVER;';
            EXEC sys.sp_executesql @failedDrop;
        END;
    END TRY
    BEGIN CATCH
        SET @cleanupFailed = 1;
    END CATCH;
    IF EXISTS (SELECT 1 FROM sys.server_event_sessions WHERE [name] = @sessionName) SET @cleanupFailed = 1;
    IF @cleanupFailed = 1
        THROW 51256, 'The Extended Events session failed to start and exact cleanup also failed.', 1;
    THROW 51255, 'The run-owned Extended Events session could not be started and was cleaned up.', 1;
END CATCH;

SELECT N'extended-events' AS [area], N'running' AS [state],
    N'blocked-target-database;deadlock-instance-wide-explicit' AS [scope],
    5 AS [maximum_file_mb], 2 AS [maximum_rollover_files], 0 AS [startup_enabled],
    1 AS [instance_wide_deadlocks_included];
