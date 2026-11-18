-- #53 Identity bridge. Applies ONLY to the operator-owned B.3 Identity DB
-- on the SAME private SQL Server instance as the independent B5 fence DB.
-- Mandatory pinned DBA rendering: replace EVERY __B53_FENCE_DB__ token with
-- one verified SQL identifier [A-Za-z_][A-Za-z0-9_]*, never a request value.
-- Apply in an approved window after B53_20261118_001, no API startup DDL.
SET NOCOUNT ON; SET XACT_ABORT ON;
BEGIN TRY
 BEGIN TRANSACTION;
 IF DB_NAME() IN (N'master',N'model',N'msdb',N'tempdb')
    OR DB_NAME()=N'__B53_FENCE_DB__'
    THROW 51850,N'B53_IDENTITY_WRONG_DATABASE',1;
 IF OBJECT_ID(N'dbo.__PreprodIdentitySchemaHistory',N'U') IS NULL
    OR NOT EXISTS(SELECT 1 FROM dbo.__PreprodIdentitySchemaHistory
         WHERE SchemaVersion=N'B3_20261117_001')
    OR OBJECT_ID(N'dbo.PreprodLoginSessions',N'U') IS NULL
    OR DB_ID(N'__B53_FENCE_DB__') IS NULL
    OR OBJECT_ID(N'__B53_FENCE_DB__.dbo.B53SessionState',N'U') IS NULL
    THROW 51850,N'B53_IDENTITY_OR_FENCE_BASE_MISSING',1;
 IF OBJECT_ID(N'dbo.__B53IdentityBridgeHistory',N'U') IS NULL
 BEGIN
    IF OBJECT_ID(N'dbo.b53_SessionMonotonicTrigger',N'TR') IS NOT NULL
       THROW 51850,N'B53_IDENTITY_PARTIAL_MIGRATION',1;
    CREATE TABLE dbo.__B53IdentityBridgeHistory
      (SchemaVersion nvarchar(64) NOT NULL PRIMARY KEY,
       FenceDatabase sysname NOT NULL, AppliedUtc datetime2(7) NOT NULL);
 END;
 IF EXISTS(SELECT 1 FROM dbo.__B53IdentityBridgeHistory
     WHERE SchemaVersion<>N'B53_IDENTITY_20261118_001'
       OR FenceDatabase<>N'__B53_FENCE_DB__')
    THROW 51850,N'B53_IDENTITY_VERSION_OR_PIN_MISMATCH',1;
 IF NOT EXISTS(SELECT 1 FROM dbo.__B53IdentityBridgeHistory
     WHERE SchemaVersion=N'B53_IDENTITY_20261118_001')
 BEGIN
    IF OBJECT_ID(N'dbo.b53_SessionMonotonicTrigger',N'TR') IS NOT NULL
       THROW 51850,N'B53_IDENTITY_EXISTING_TRIGGER_DENIED',1;
    EXEC(N'CREATE TRIGGER dbo.b53_SessionMonotonicTrigger
ON dbo.PreprodLoginSessions AFTER INSERT, UPDATE, DELETE
AS
BEGIN
 SET NOCOUNT ON;
 -- Deletion, epoch rollback, resurrection, actor reassignment and silent
 -- epoch changes fail the ENTIRE original Identity transaction closed.
 IF EXISTS(SELECT 1 FROM deleted d LEFT JOIN inserted i ON i.Id=d.Id
           WHERE i.Id IS NULL)
    THROW 51850,N''B53_SESSION_DELETE_DENIED'',1;
 IF EXISTS(SELECT 1 FROM inserted i
      JOIN deleted d ON i.Id=d.Id
      WHERE i.UserId<>d.UserId OR i.CreatedUtc<>d.CreatedUtc
         OR i.SecurityStamp<>d.SecurityStamp
         OR i.Epoch<d.Epoch
         OR (d.RevokedUtc IS NOT NULL AND
              (i.RevokedUtc IS NULL OR i.Epoch<>d.Epoch))
         OR (d.RevokedUtc IS NULL AND i.RevokedUtc IS NOT NULL
              AND (d.Epoch=9223372036854775807 OR i.Epoch<>d.Epoch+1))
         OR (d.RevokedUtc IS NULL AND i.RevokedUtc IS NULL
              AND i.Epoch<>d.Epoch))
    THROW 51851,N''B53_SESSION_STATE_MUTATION_DENIED'',1;
 DECLARE @id uniqueidentifier,@actor uniqueidentifier,
     @oldEpoch bigint,@newEpoch bigint;
 DECLARE registerCursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT i.Id,i.UserId,i.Epoch FROM inserted i
    WHERE NOT EXISTS(SELECT 1 FROM deleted d WHERE d.Id=i.Id)
    ORDER BY i.Id;
 OPEN registerCursor;
 FETCH NEXT FROM registerCursor INTO @id,@actor,@newEpoch;
 WHILE @@FETCH_STATUS=0
 BEGIN
    IF EXISTS(SELECT 1 FROM inserted i
         WHERE i.Id=@id AND (i.Epoch<>0 OR i.RevokedUtc IS NOT NULL))
       THROW 51852,N''B53_SESSION_UNSAFE_CREATION_DENIED'',1;
    EXEC [__B53_FENCE_DB__].dbo.b53_RegisterSession
       @SessionId=@id,@ActorId=@actor,@SessionEpoch=@newEpoch;
    FETCH NEXT FROM registerCursor INTO @id,@actor,@newEpoch;
 END;
 CLOSE registerCursor; DEALLOCATE registerCursor;
 DECLARE revokeCursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT i.Id,i.UserId,d.Epoch,i.Epoch
      FROM inserted i JOIN deleted d ON i.Id=d.Id
    WHERE d.RevokedUtc IS NULL AND i.RevokedUtc IS NOT NULL
    ORDER BY i.Id;
 OPEN revokeCursor;
 FETCH NEXT FROM revokeCursor INTO @id,@actor,@oldEpoch,@newEpoch;
 WHILE @@FETCH_STATUS=0
 BEGIN
    EXEC [__B53_FENCE_DB__].dbo.b53_RevokeSession
       @SessionId=@id,@ActorId=@actor,
       @ExpectedEpoch=@oldEpoch,@NewEpoch=@newEpoch;
    FETCH NEXT FROM revokeCursor INTO @id,@actor,@oldEpoch,@newEpoch;
 END;
 CLOSE revokeCursor; DEALLOCATE revokeCursor;
END');
    EXEC(N'CREATE PROCEDURE dbo.b53_AssertIdentityBridgeInstalled
AS
BEGIN
 SET NOCOUNT ON;
 IF @@TRANCOUNT<1 OR ISNULL(IS_ROLEMEMBER(N''b5_session_runtime''),0)<>1
    OR NOT EXISTS(SELECT 1 FROM dbo.__B53IdentityBridgeHistory
        WHERE SchemaVersion=N''B53_IDENTITY_20261118_001'' AND FenceDatabase=N''__B53_FENCE_DB__'')
    OR OBJECT_ID(N''dbo.b53_SessionMonotonicTrigger'',N''TR'') IS NULL
    OR EXISTS(SELECT 1 FROM sys.triggers
         WHERE name=N''b53_SessionMonotonicTrigger'' AND is_disabled=1)
    THROW 51858,N''B53_CONTROL_BRIDGE_NOT_CURRENT'',1;
END');
    GRANT EXECUTE ON OBJECT::dbo.b53_AssertIdentityBridgeInstalled TO b5_session_runtime;
    INSERT dbo.__B53IdentityBridgeHistory
      (SchemaVersion,FenceDatabase,AppliedUtc)
    VALUES(N'B53_IDENTITY_20261118_001',
           N'__B53_FENCE_DB__',SYSUTCDATETIME());
 END;
 IF (SELECT COUNT_BIG(*) FROM dbo.__B53IdentityBridgeHistory)<>1
    OR NOT EXISTS(SELECT 1 FROM dbo.__B53IdentityBridgeHistory
      WHERE SchemaVersion=N'B53_IDENTITY_20261118_001'
        AND FenceDatabase=N'__B53_FENCE_DB__')
    OR OBJECT_ID(N'dbo.b53_AssertIdentityBridgeInstalled',N'P') IS NULL
    OR OBJECT_ID(N'dbo.b53_SessionMonotonicTrigger',N'TR') IS NULL
    OR EXISTS(SELECT 1 FROM sys.triggers WHERE name=N'b53_SessionMonotonicTrigger'
         AND is_disabled=1)
    THROW 51859,N'B53_IDENTITY_BRIDGE_INTEGRITY_DENIED',1;
 COMMIT TRANSACTION;
END TRY
BEGIN CATCH
 IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;
 THROW;
END CATCH;
