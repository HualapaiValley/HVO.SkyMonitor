-- SQLCMD variables: DatabaseName, RunId, ApprovedBackupDirectory, BackupFilePrefix, BackupFilePath,
-- InvariantSchema, InvariantTable, InvariantKeyColumn, InvariantTextColumn, InvariantNumberColumn,
-- MetadataRetentionDeclaredDays, MetadataRetentionControlReference.
-- Run from master. The unique restored database is always dropped; the approved backup is retained for its owner.
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @databaseName sysname = N'$(ESCAPE_SQUOTE(DatabaseName))';
DECLARE @runId varchar(32) = '$(ESCAPE_SQUOTE(RunId))';
DECLARE @approvedDirectory nvarchar(4000) = N'$(ESCAPE_SQUOTE(ApprovedBackupDirectory))';
DECLARE @filePrefix nvarchar(64) = N'$(ESCAPE_SQUOTE(BackupFilePrefix))';
DECLARE @backupFilePath nvarchar(4000) = N'$(ESCAPE_SQUOTE(BackupFilePath))';
DECLARE @invariantSchema sysname = N'$(ESCAPE_SQUOTE(InvariantSchema))';
DECLARE @invariantTable sysname = N'$(ESCAPE_SQUOTE(InvariantTable))';
DECLARE @invariantKeyColumn sysname = N'$(ESCAPE_SQUOTE(InvariantKeyColumn))';
DECLARE @invariantTextColumn sysname = N'$(ESCAPE_SQUOTE(InvariantTextColumn))';
DECLARE @invariantNumberColumn sysname = N'$(ESCAPE_SQUOTE(InvariantNumberColumn))';
DECLARE @metadataRetentionDeclaredDays int = TRY_CONVERT(int, N'$(ESCAPE_SQUOTE(MetadataRetentionDeclaredDays))');
DECLARE @metadataRetentionControlReference nvarchar(128) = N'$(ESCAPE_SQUOTE(MetadataRetentionControlReference))';
DECLARE @restoredDatabase sysname = N'HvoVerify_' + @runId;
DECLARE @databaseId int = DB_ID(@databaseName);

IF ISNULL(TRY_CONVERT(int, SERVERPROPERTY(N'ProductMajorVersion')), 0) <> 16
    THROW 51255, 'This toolkit supports SQL Server 2022 only.', 1;
IF DB_NAME() <> N'master' OR @databaseId IS NULL OR @databaseId <= 4
   OR @databaseName = N'' OR @databaseName LIKE N'%[^0-9A-Za-z_-]%'
    THROW 51255, 'Run from master with a validated non-system DatabaseName.', 1;
IF (SELECT [compatibility_level] FROM sys.databases WHERE [database_id] = @databaseId) <> 160
    THROW 51255, 'The source database must use SQL Server 2022 compatibility level 160.', 1;
IF LEN(@runId) <> 32 OR @runId COLLATE Latin1_General_100_BIN2 LIKE '%[^0-9a-f]%'
    THROW 51255, 'RunId must contain exactly 32 lowercase hexadecimal characters.', 1;
IF @approvedDirectory = N'' OR @approvedDirectory LIKE N'%..%' OR @approvedDirectory LIKE N'%/./%' OR @approvedDirectory LIKE N'%\.\%'
   OR @approvedDirectory LIKE N'%//%' OR @approvedDirectory LIKE N'%\\%' OR @approvedDirectory LIKE N'%://%'
   OR @approvedDirectory LIKE N'\\%' OR RIGHT(@approvedDirectory, 1) IN (N'/', N'\')
   OR NOT (LEFT(@approvedDirectory, 1) = N'/' OR @approvedDirectory LIKE N'[A-Za-z]:\%')
   OR @filePrefix = N'' OR @filePrefix LIKE N'%[^0-9A-Za-z_-]%'
    THROW 51255, 'The approved backup directory or file prefix is not canonical.', 1;
IF @invariantSchema = N'' OR @invariantSchema LIKE N'%[^0-9A-Za-z_]%' OR @invariantTable = N'' OR @invariantTable LIKE N'%[^0-9A-Za-z_]%'
   OR @invariantKeyColumn = N'' OR @invariantKeyColumn LIKE N'%[^0-9A-Za-z_]%'
   OR @invariantTextColumn = N'' OR @invariantTextColumn LIKE N'%[^0-9A-Za-z_]%'
   OR @invariantNumberColumn = N'' OR @invariantNumberColumn LIKE N'%[^0-9A-Za-z_]%'
    THROW 51255, 'Invariant schema, table, or column names are invalid.', 1;
IF @metadataRetentionDeclaredDays NOT BETWEEN 1 AND 3650 OR LEN(@metadataRetentionControlReference) NOT BETWEEN 8 AND 128
   OR @metadataRetentionControlReference LIKE N'%[^0-9A-Za-z._:-]%'
    THROW 51255, 'The external backup-history retention declaration is invalid.', 1;
DECLARE @metadataRetentionControlDigest char(64) = CONVERT(char(64), HASHBYTES('SHA2_256',
    CONVERT(varbinary(max), @metadataRetentionControlReference)), 2);
DECLARE @controlCode int = 0;
WHILE @controlCode <= 31
BEGIN
    IF CHARINDEX(NCHAR(@controlCode), @approvedDirectory + @filePrefix + @backupFilePath COLLATE Latin1_General_100_BIN2) > 0
        THROW 51255, 'Backup paths cannot contain control characters.', 1;
    SET @controlCode += 1;
END;
DECLARE @separator nchar(1) = CASE WHEN LEFT(@approvedDirectory, 1) = N'/' THEN N'/' ELSE N'\' END;
IF @backupFilePath <> @approvedDirectory + @separator + @filePrefix + @runId + N'.bak' COLLATE Latin1_General_100_BIN2
   OR @backupFilePath LIKE N'%*%' OR @backupFilePath LIKE N'%?%'
    THROW 51255, 'BackupFilePath is outside the exact approved run-owned prefix.', 1;
IF DB_ID(@restoredDatabase) IS NOT NULL
    THROW 51255, 'The unique verification database already exists.', 1;
DECLARE @backupExists int;
SELECT @backupExists = [file_exists] FROM sys.dm_os_file_exists(@backupFilePath);
IF ISNULL(@backupExists, 0) <> 0
    THROW 51255, 'The run-owned backup path already exists.', 1;

DECLARE @startedAt datetime2(3) = SYSUTCDATETIME();
DECLARE @backupCompletedAt datetime2(3);
DECLARE @restoreCompletedAt datetime2(3);
DECLARE @backupBytes bigint;
DECLARE @sourceRows bigint;
DECLARE @sourceHash varbinary(32);
DECLARE @restoredRows bigint;
DECLARE @restoredHash varbinary(32);
DECLARE @backupName nvarchar(128) = N'HvoSqlOps_' + @runId;
DECLARE @mediaName nvarchar(128) = N'HvoSqlOpsMedia_' + @runId;
DECLARE @metadataMatches int;
DECLARE @mediaFamilyCount int;
DECLARE @safeStage int = 1;

BEGIN TRY
    DECLARE @columnsValid int;
    DECLARE @columnSql nvarchar(max) = N'USE ' + QUOTENAME(@databaseName) + N';
        SELECT @valid = CASE WHEN
            EXISTS (SELECT 1 FROM sys.columns WHERE [object_id]=OBJECT_ID(N''' + @invariantSchema + N'.' + @invariantTable + N''') AND [name]=N''' + @invariantKeyColumn + N''' AND TYPE_NAME([user_type_id])=N''int'') AND
            EXISTS (SELECT 1 FROM sys.columns WHERE [object_id]=OBJECT_ID(N''' + @invariantSchema + N'.' + @invariantTable + N''') AND [name]=N''' + @invariantTextColumn + N''' AND TYPE_NAME([user_type_id])=N''nvarchar'') AND
            EXISTS (SELECT 1 FROM sys.columns WHERE [object_id]=OBJECT_ID(N''' + @invariantSchema + N'.' + @invariantTable + N''') AND [name]=N''' + @invariantNumberColumn + N''' AND TYPE_NAME([user_type_id])=N''int'') AND
            EXISTS (
                SELECT 1 FROM sys.indexes AS indexes
                JOIN sys.index_columns AS columns ON columns.[object_id]=indexes.[object_id] AND columns.[index_id]=indexes.[index_id]
                JOIN sys.columns AS key_column ON key_column.[object_id]=columns.[object_id] AND key_column.[column_id]=columns.[column_id]
                WHERE indexes.[object_id]=OBJECT_ID(N''' + @invariantSchema + N'.' + @invariantTable + N''')
                  AND indexes.[is_unique]=1 AND indexes.[is_disabled]=0 AND indexes.[is_hypothetical]=0
                  AND indexes.[has_filter]=0 AND indexes.[filter_definition] IS NULL
                  AND columns.[key_ordinal]=1 AND key_column.[name]=N''' + @invariantKeyColumn + N'''
                  AND NOT EXISTS (SELECT 1 FROM sys.index_columns AS extra WHERE extra.[object_id]=indexes.[object_id] AND extra.[index_id]=indexes.[index_id] AND extra.[key_ordinal]>1))
            THEN 1 ELSE 0 END;';
    EXEC sys.sp_executesql @columnSql, N'@valid int OUTPUT', @valid=@columnsValid OUTPUT;
    DECLARE @nullKeyRows bigint;
    DECLARE @duplicateKeys bigint;
    DECLARE @keyIntegritySql nvarchar(max) = N'USE ' + QUOTENAME(@databaseName) + N';
        SELECT @nulls=COUNT_BIG(*) FROM ' + QUOTENAME(@invariantSchema) + N'.' + QUOTENAME(@invariantTable) + N'
            WHERE ' + QUOTENAME(@invariantKeyColumn) + N' IS NULL;
        SELECT @duplicates=COUNT_BIG(*) FROM (SELECT ' + QUOTENAME(@invariantKeyColumn) + N'
            FROM ' + QUOTENAME(@invariantSchema) + N'.' + QUOTENAME(@invariantTable) + N'
            GROUP BY ' + QUOTENAME(@invariantKeyColumn) + N' HAVING COUNT_BIG(*) > 1) AS duplicate_keys;';
    EXEC sys.sp_executesql @keyIntegritySql, N'@nulls bigint OUTPUT, @duplicates bigint OUTPUT',
        @nulls=@nullKeyRows OUTPUT, @duplicates=@duplicateKeys OUTPUT;
    IF @nullKeyRows <> 0 OR @duplicateKeys <> 0
        THROW 51255, 'The canonical invariant ordering key contains null or duplicate values.', 1;
    IF @columnsValid <> 1
        THROW 51255, 'The canonical invariant requires an enabled non-hypothetical unfiltered unique int key plus nvarchar and int value columns.', 1;

    DECLARE @invariantSql nvarchar(max) = N'USE ' + QUOTENAME(@databaseName) + N';
        SELECT @rows=COUNT_BIG(*),
            @hash=HASHBYTES(''SHA2_256'', COALESCE(STRING_AGG(CONVERT(nvarchar(max),
                CONCAT(N''K'', DATALENGTH(CONVERT(varbinary(max), CONVERT(nvarchar(30), ' + QUOTENAME(@invariantKeyColumn) + N'))), N'':'', CONVERT(nvarchar(30), ' + QUOTENAME(@invariantKeyColumn) + N'),
                       N'';T'', DATALENGTH(CONVERT(varbinary(max), ' + QUOTENAME(@invariantTextColumn) + N')), N'':'', ' + QUOTENAME(@invariantTextColumn) + N',
                       N'';N'', DATALENGTH(CONVERT(varbinary(max), CONVERT(nvarchar(30), ' + QUOTENAME(@invariantNumberColumn) + N'))), N'':'', CONVERT(nvarchar(30), ' + QUOTENAME(@invariantNumberColumn) + N'), N'';'')), NCHAR(10))
                WITHIN GROUP (ORDER BY ' + QUOTENAME(@invariantKeyColumn) + N'), N''''))
        FROM ' + QUOTENAME(@invariantSchema) + N'.' + QUOTENAME(@invariantTable) + N';';
    EXEC sys.sp_executesql @invariantSql, N'@rows bigint OUTPUT, @hash varbinary(32) OUTPUT', @rows=@sourceRows OUTPUT, @hash=@sourceHash OUTPUT;

    SET @safeStage = 2;
    IF EXISTS (
        SELECT 1
        FROM msdb.dbo.backupset AS backup_set
        JOIN msdb.dbo.backupmediafamily AS media ON media.[media_set_id] = backup_set.[media_set_id]
        WHERE backup_set.[name] = @backupName OR media.[physical_device_name] = @backupFilePath)
        THROW 51255, 'Backup metadata already exists for the run-owned identity.', 1;

    SET @safeStage = 3;
    DECLARE @backupSql nvarchar(max) = N'BACKUP DATABASE ' + QUOTENAME(@databaseName) + N'
        TO DISK=@path WITH COPY_ONLY, FORMAT, CHECKSUM, STATS=100, NAME=@backupName, MEDIANAME=@mediaName;';
    EXEC sys.sp_executesql @backupSql,
        N'@path nvarchar(4000), @backupName nvarchar(128), @mediaName nvarchar(128)',
        @path=@backupFilePath, @backupName=@backupName, @mediaName=@mediaName;
    SET @backupCompletedAt = SYSUTCDATETIME();

    SET @safeStage = 4;
    SELECT
        @metadataMatches = COUNT(*),
        @backupBytes = MAX(CONVERT(bigint, backup_set.[backup_size])),
        @mediaFamilyCount = COUNT(DISTINCT media.[family_sequence_number])
    FROM msdb.dbo.backupset AS backup_set
    JOIN msdb.dbo.backupmediafamily AS media ON media.[media_set_id] = backup_set.[media_set_id]
    JOIN msdb.dbo.backupmediaset AS media_set ON media_set.[media_set_id] = backup_set.[media_set_id]
    WHERE backup_set.[database_name] = @databaseName
      AND backup_set.[name] = @backupName
      AND backup_set.[type] = N'D'
      AND backup_set.[is_copy_only] = 1
      AND media_set.[name] = @mediaName
      AND media.[physical_device_name] = @backupFilePath;
    IF @metadataMatches <> 1 OR @mediaFamilyCount <> 1 OR @backupBytes IS NULL
        THROW 51255, 'The exact COPY_ONLY database backup metadata could not be verified.', 1;

    SET @safeStage = 5;
    RESTORE VERIFYONLY FROM DISK=@backupFilePath WITH CHECKSUM;
    DECLARE @dataRoot nvarchar(4000) = CONVERT(nvarchar(4000), SERVERPROPERTY(N'InstanceDefaultDataPath'));
    DECLARE @logRoot nvarchar(4000) = CONVERT(nvarchar(4000), SERVERPROPERTY(N'InstanceDefaultLogPath'));
    IF @dataRoot IS NULL OR @logRoot IS NULL
        THROW 51255, 'SQL Server default data and log locations are unavailable.', 1;
    DECLARE @dataSeparator nchar(1) = CASE WHEN CHARINDEX(N'\', @dataRoot) > 0 THEN N'\' ELSE N'/' END;
    DECLARE @logSeparator nchar(1) = CASE WHEN CHARINDEX(N'\', @logRoot) > 0 THEN N'\' ELSE N'/' END;
    WHILE RIGHT(@dataRoot, 1) IN (N'/', N'\') SET @dataRoot = LEFT(@dataRoot, LEN(@dataRoot) - 1);
    WHILE RIGHT(@logRoot, 1) IN (N'/', N'\') SET @logRoot = LEFT(@logRoot, LEN(@logRoot) - 1);
    IF @dataRoot = N'' OR @logRoot = N''
        THROW 51255, 'SQL Server default data and log locations are invalid.', 1;
    SET @dataRoot += @dataSeparator;
    SET @logRoot += @logSeparator;

    CREATE TABLE #restoreFiles (
        [LogicalName] nvarchar(128), [PhysicalName] nvarchar(260), [Type] char(1), [FileGroupName] nvarchar(128) NULL,
        [Size] numeric(20,0), [MaxSize] numeric(20,0), [FileId] bigint, [CreateLSN] numeric(25,0), [DropLSN] numeric(25,0) NULL,
        [UniqueId] uniqueidentifier, [ReadOnlyLSN] numeric(25,0) NULL, [ReadWriteLSN] numeric(25,0) NULL,
        [BackupSizeInBytes] bigint, [SourceBlockSize] int, [FileGroupId] int, [LogGroupGUID] uniqueidentifier NULL,
        [DifferentialBaseLSN] numeric(25,0) NULL, [DifferentialBaseGUID] uniqueidentifier NULL,
        [IsReadOnly] bit, [IsPresent] bit, [TDEThumbprint] varbinary(32) NULL, [SnapshotUrl] nvarchar(360) NULL);
    DECLARE @fileListSql nvarchar(max) = N'RESTORE FILELISTONLY FROM DISK=N''' + REPLACE(@backupFilePath, N'''', N'''''') + N''';';
    INSERT #restoreFiles EXEC sys.sp_executesql @fileListSql;
    IF NOT EXISTS (SELECT 1 FROM #restoreFiles WHERE [IsPresent]=1)
        THROW 51255, 'The exact backup file list is empty.', 1;
    DECLARE @moves nvarchar(max);
    SELECT @moves = STRING_AGG(CONVERT(nvarchar(max), N'MOVE N''' + REPLACE([name], N'''', N'''''') + N''' TO N''' +
        REPLACE(CASE [Type] WHEN N'L' THEN @logRoot ELSE @dataRoot END + @restoredDatabase + N'_' + CONVERT(nvarchar(20), [FileId]) +
        CASE [Type] WHEN N'L' THEN N'.ldf' ELSE N'.mdf' END, N'''', N'''''') + N''''), N',')
    FROM (SELECT [LogicalName] AS [name], [Type], [FileId] FROM #restoreFiles WHERE [IsPresent]=1) AS backup_files;
    SET @safeStage = 6;
    DECLARE @restoreSql nvarchar(max) = N'RESTORE DATABASE ' + QUOTENAME(@restoredDatabase) + N' FROM DISK=@path WITH CHECKSUM, RECOVERY, ' + @moves + N';';
    EXEC sys.sp_executesql @restoreSql, N'@path nvarchar(4000)', @path=@backupFilePath;
    SET @restoreCompletedAt = SYSUTCDATETIME();

    SET @safeStage = 7;
    CREATE TABLE #checkdb (
        [Error] int NULL, [Level] int NULL, [State] int NULL, [MessageText] nvarchar(4000) NULL,
        [RepairLevel] nvarchar(4000) NULL, [Status] int NULL, [DbId] int NULL, [DbFragId] int NULL,
        [Id] bigint NULL, [IndId] bigint NULL, [PartitionId] bigint NULL, [AllocUnitId] bigint NULL,
        [RidDbId] int NULL, [RidPruId] int NULL, [File] int NULL, [Page] int NULL, [Slot] int NULL,
        [RefDbId] int NULL, [RefPruId] int NULL, [RefFile] int NULL, [RefPage] int NULL, [RefSlot] int NULL,
        [Allocation] int NULL);
    DECLARE @checkSql nvarchar(max) = N'DBCC CHECKDB (' + QUOTENAME(@restoredDatabase, N'''') + N') WITH NO_INFOMSGS, ALL_ERRORMSGS, TABLERESULTS;';
    INSERT #checkdb EXEC sys.sp_executesql @checkSql;
    IF EXISTS (SELECT 1 FROM #checkdb WHERE [Level] >= 16)
        THROW 51255, 'DBCC CHECKDB found a consistency error; details remain inside the operator boundary.', 1;

    SET @safeStage = 8;
    SET @invariantSql = REPLACE(@invariantSql, QUOTENAME(@databaseName), QUOTENAME(@restoredDatabase));
    EXEC sys.sp_executesql @invariantSql, N'@rows bigint OUTPUT, @hash varbinary(32) OUTPUT', @rows=@restoredRows OUTPUT, @hash=@restoredHash OUTPUT;
    IF @sourceRows <> @restoredRows OR @sourceHash <> @restoredHash
        THROW 51255, 'The isolated restore canonical invariant does not match the source.', 1;

    SET @safeStage = 9;
    DECLARE @dropSql nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(@restoredDatabase) + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ' + QUOTENAME(@restoredDatabase) + N';';
    EXEC sys.sp_executesql @dropSql;

    SELECT N'backup-restore-checkdb' AS [area], N'passed' AS [state],
        N'database' AS [backup_type], 1 AS [copy_only], 1 AS [metadata_verified], @mediaFamilyCount AS [media_family_count],
        N'external-control-declared' AS [metadata_retention],
        @metadataRetentionDeclaredDays AS [metadata_retention_declared_days],
        @metadataRetentionControlDigest AS [metadata_retention_control_reference_sha256],
        @sourceRows AS [source_row_count], @restoredRows AS [restored_row_count],
        CONVERT(char(64), @sourceHash, 2) AS [source_canonical_sha256],
        CONVERT(char(64), @restoredHash, 2) AS [restored_canonical_sha256],
        @backupBytes AS [backup_bytes],
        DATEDIFF_BIG(millisecond, @startedAt, @backupCompletedAt) AS [backup_elapsed_ms],
        DATEDIFF_BIG(millisecond, @backupCompletedAt, @restoreCompletedAt) AS [restore_elapsed_ms],
        DATEDIFF_BIG(millisecond, @startedAt, SYSUTCDATETIME()) AS [elapsed_ms];
END TRY
BEGIN CATCH
    DECLARE @cleanupFailed bit = 0;
    BEGIN TRY
        IF DB_ID(@restoredDatabase) IS NOT NULL
        BEGIN
            DECLARE @cleanupSql nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(@restoredDatabase) + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ' + QUOTENAME(@restoredDatabase) + N';';
            EXEC sys.sp_executesql @cleanupSql;
        END;
    END TRY
    BEGIN CATCH
        SET @cleanupFailed = 1;
    END CATCH;
    IF DB_ID(@restoredDatabase) IS NOT NULL SET @cleanupFailed = 1;
    IF @cleanupFailed = 1
        THROW 51259, 'Backup verification failed and exact restored-database cleanup also failed.', 1;
    DECLARE @safeErrorNumber int = 51260 + @safeStage;
    THROW @safeErrorNumber, 'Backup, isolated restore, or consistency verification failed at the reported safe stage.', 1;
END CATCH;
