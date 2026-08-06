-- SQLCMD variables: RunId, RunEventDirectory, EventFilePath.
-- Reads raw XML only inside SQL Server and returns a strict safe aggregate.
SET NOCOUNT ON;

DECLARE @runId varchar(32) = '$(ESCAPE_SQUOTE(RunId))';
DECLARE @runEventDirectory nvarchar(4000) = N'$(ESCAPE_SQUOTE(RunEventDirectory))';
DECLARE @eventFilePath nvarchar(4000) = N'$(ESCAPE_SQUOTE(EventFilePath))';
IF ISNULL(TRY_CONVERT(int, SERVERPROPERTY(N'ProductMajorVersion')), 0) <> 16
    THROW 51255, 'This toolkit supports SQL Server 2022 only.', 1;
IF LEN(@runId) <> 32 OR @runId COLLATE Latin1_General_100_BIN2 LIKE '%[^0-9a-f]%'
    THROW 51255, 'RunId must contain exactly 32 lowercase hexadecimal characters.', 1;
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
    THROW 51255, 'EventFilePath is outside the exact run-owned directory.', 1;

DECLARE @sessionName sysname = N'HvoSqlOps_' + @runId;
DECLARE @configuredFileName nvarchar(4000);
SELECT @configuredFileName = CONVERT(nvarchar(4000), fields.[value])
FROM sys.server_event_sessions AS sessions
JOIN sys.server_event_session_targets AS targets ON targets.[event_session_id] = sessions.[event_session_id]
JOIN sys.server_event_session_fields AS fields ON fields.[event_session_id] = sessions.[event_session_id] AND fields.[object_id] = targets.[target_id]
WHERE sessions.[name] = @sessionName AND targets.[name] = N'event_file' AND fields.[name] = N'filename';
IF @configuredFileName IS NULL OR @configuredFileName COLLATE Latin1_General_100_BIN2 <> @eventFilePath COLLATE Latin1_General_100_BIN2
    THROW 51255, 'The run-owned Extended Events file target does not exist.', 1;
DECLARE @fileName nvarchar(4000) = @configuredFileName + N'*.xel';

BEGIN TRY
;WITH parsed_events AS (
    SELECT
        CASE event_xml.[value]('(/event/@name)[1]', 'sysname')
            WHEN N'blocked_process_report' THEN N'blocked-process'
            WHEN N'xml_deadlock_report' THEN N'deadlock'
        END AS [event_kind],
        event_xml.[value]('(/event/@timestamp)[1]', 'datetime2(0)') AS [observed_at_utc],
        event_xml.[exist]('/event//process[@clientapp="HVO.SkyMonitor.LogicHost.Runtime"]') AS [has_runtime],
        event_xml.[exist]('/event//process[@clientapp="HVO.SkyMonitor.LogicHost.Migration"]') AS [has_migration],
        event_xml.[exist]('/event//process[@clientapp="HVO.SkyMonitor.LogicHost.ObjectLock"]') AS [has_object_lock],
        CASE WHEN event_xml.[exist]('/event//process[not(@clientapp="HVO.SkyMonitor.LogicHost.Runtime") and not(@clientapp="HVO.SkyMonitor.LogicHost.Migration") and not(@clientapp="HVO.SkyMonitor.LogicHost.ObjectLock")]') = 1 THEN 1 ELSE 0 END AS [has_other]
    FROM sys.fn_xe_file_target_read_file(@fileName, NULL, NULL, NULL) AS files
    CROSS APPLY (SELECT TRY_CONVERT(xml, files.[event_data])) AS parsed(event_xml)
    WHERE event_xml.[value]('(/event/@name)[1]', 'sysname') IN (N'blocked_process_report', N'xml_deadlock_report')),
safe_events AS (
    SELECT [event_kind], [observed_at_utc],
        CASE
            WHEN [has_other] = 1 AND CONVERT(int, [has_runtime]) + CONVERT(int, [has_migration]) + CONVERT(int, [has_object_lock]) > 0 THEN N'mixed-approved-other'
            WHEN CONVERT(int, [has_runtime]) + CONVERT(int, [has_migration]) + CONVERT(int, [has_object_lock]) > 1 THEN N'mixed-approved'
            WHEN [has_runtime] = 1 THEN N'logichost-runtime'
            WHEN [has_migration] = 1 THEN N'logichost-migration'
            WHEN [has_object_lock] = 1 THEN N'logichost-object-lock'
            ELSE N'other'
        END AS [attribution],
        STUFF(
            CASE WHEN [has_runtime] = 1 THEN N'+logichost-runtime' ELSE N'' END +
            CASE WHEN [has_migration] = 1 THEN N'+logichost-migration' ELSE N'' END +
            CASE WHEN [has_object_lock] = 1 THEN N'+logichost-object-lock' ELSE N'' END +
            CASE WHEN [has_other] = 1 THEN N'+other' ELSE N'' END,
            1, 1, N'') AS [participant_set]
    FROM parsed_events)
SELECT
    N'extended-events' AS [area],
    [event_kind],
    [attribution],
    [participant_set],
    MIN([observed_at_utc]) AS [first_observed_utc],
    MAX([observed_at_utc]) AS [last_observed_utc],
    COUNT_BIG(*) AS [event_count]
FROM safe_events
GROUP BY [event_kind], [attribution], [participant_set]
ORDER BY [event_kind], [attribution], [participant_set];
END TRY
BEGIN CATCH
    THROW 51255, 'The run-owned Extended Events files could not be read or sanitized.', 1;
END CATCH;
