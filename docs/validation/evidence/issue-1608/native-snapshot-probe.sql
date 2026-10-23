-- Manual study evidence only. Execute ONLY inside a newly provisioned disposable
-- SQL Server 2022 CU26 Developer container with an otherwise empty data volume.
-- No shared SQL service, network access, published ports, or production data.
-- Names and file locations below are synthetic and owned by this disposable run.
-- The owner removes the whole container and its private volume after the probe.
SET NOCOUNT ON;
CREATE DATABASE Hvo1608;
ALTER DATABASE Hvo1608 SET COMPATIBILITY_LEVEL=160;
ALTER DATABASE Hvo1608 SET RECOVERY FULL;
CREATE DATABASE Hvo1608Neighbor;
GO
USE Hvo1608;
CREATE TABLE dbo.Predecessor(Id int NOT NULL PRIMARY KEY, Payload varchar(1024) NOT NULL, Generation int NOT NULL);
INSERT dbo.Predecessor SELECT value, REPLICATE('a',1024), 1 FROM GENERATE_SERIES(1,4096);
CHECKPOINT;
USE Hvo1608Neighbor;
CREATE TABLE dbo.Sibling(Id int NOT NULL PRIMARY KEY, Payload varchar(40) NOT NULL);
INSERT dbo.Sibling VALUES (1,'unchanged-neighbor'),(2,'unrelated');
GO
-- Run this trial body three times, in order, against the same warm database.
-- The orchestration used separate sqlcmd calls for snapshot, mutation, revert,
-- verification, and the expected failing log backup; timings include that overhead.
USE master;
BACKUP DATABASE Hvo1608 TO DISK='/var/opt/mssql/data/seed.bak' WITH INIT,CHECKSUM;
BACKUP LOG Hvo1608 TO DISK='/var/opt/mssql/data/before.trn' WITH INIT,CHECKSUM;
CREATE DATABASE Hvo1608Snapshot ON
    (NAME=N'Hvo1608',FILENAME=N'/var/opt/mssql/data/Hvo1608.ss') AS SNAPSHOT OF Hvo1608;
GO
-- Measure allocated sparse-file bytes here and after mutation using stat -c %b
-- inside the container, multiplied by 512 (not the sparse file's apparent size).
USE Hvo1608;
UPDATE dbo.Predecessor SET Payload=REPLICATE('b',1024),Generation=2;
CHECKPOINT;
DROP TABLE dbo.Predecessor;
CREATE TABLE dbo.IncompatibleCandidate(Id uniqueidentifier NOT NULL PRIMARY KEY);
GO
USE master;
ALTER DATABASE Hvo1608 SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
RESTORE DATABASE Hvo1608 FROM DATABASE_SNAPSHOT=N'Hvo1608Snapshot';
ALTER DATABASE Hvo1608 SET MULTI_USER;
GO
DBCC CHECKDB(N'Hvo1608') WITH NO_INFOMSGS,ALL_ERRORMSGS;
USE Hvo1608;
IF (SELECT COUNT(*) FROM dbo.Predecessor WHERE Generation=1 AND Payload=REPLICATE('a',1024))<>4096
    THROW 51000,'Predecessor mismatch',1;
IF OBJECT_ID('dbo.IncompatibleCandidate') IS NOT NULL
    THROW 51001,'Candidate schema survived',1;
IF (SELECT COUNT(*) FROM Hvo1608Neighbor.dbo.Sibling
    WHERE (Id=1 AND Payload='unchanged-neighbor') OR (Id=2 AND Payload='unrelated'))<>2
    THROW 51002,'Neighbor changed',1;
GO
-- Expected: sqlcmd -b returns 1, SQL errors 4214 and 3013.
-- Invoke this batch separately so its expected failure does not skip cleanup.
BACKUP LOG Hvo1608 TO DISK='/var/opt/mssql/data/after.trn' WITH INIT,CHECKSUM;
GO
-- Invoke separately even after the expected failure. The next trial's full
-- backup establishes a new log chain; no untested chain continuity is inferred.
USE master;
DROP DATABASE Hvo1608Snapshot;
GO
