-- #53 Registry bridge. Apply ONLY in the operator-owned B.4 Registry DB
-- on the SAME private SQL Server instance as independent B5 fence DB.
-- Render __B53_FENCE_DB__ as an operator-approved, exact SQL identifier.
-- No dynamic tenant name or HTTP-supplied value is accepted.
-- Does not activate any tenant; reactivation requires an independently scoped
-- B53 permit tied to the future registry epoch and binding ID.
SET NOCOUNT ON; SET XACT_ABORT ON;
BEGIN TRY
 BEGIN TRANSACTION;
 IF DB_NAME() IN (N'master',N'model',N'msdb',N'tempdb')
    OR DB_NAME()=N'__B53_FENCE_DB__'
    THROW 51860,N'B53_REGISTRY_WRONG_DATABASE',1;
 IF OBJECT_ID(N'dbo.__PreprodTenantRegistrySchemaHistory',N'U') IS NULL
    OR NOT EXISTS(SELECT 1 FROM dbo.__PreprodTenantRegistrySchemaHistory
         WHERE SchemaVersion=N'B4_20261118_001')
    OR OBJECT_ID(N'dbo.PreprodTenantRegistry',N'U') IS NULL
    OR DB_ID(N'__B53_FENCE_DB__') IS NULL
    OR OBJECT_ID(N'__B53_FENCE_DB__.dbo.B5TenantFence',N'U') IS NULL
    THROW 51860,N'B53_REGISTRY_OR_FENCE_BASE_MISSING',1;
 IF OBJECT_ID(N'dbo.__B53RegistryBridgeHistory',N'U') IS NULL
 BEGIN
    IF OBJECT_ID(N'dbo.b53_TenantMonotonicTrigger',N'TR') IS NOT NULL
       THROW 51860,N'B53_REGISTRY_PARTIAL_MIGRATION',1;
    CREATE TABLE dbo.__B53RegistryBridgeHistory
      (SchemaVersion nvarchar(64) NOT NULL PRIMARY KEY,
       FenceDatabase sysname NOT NULL,AppliedUtc datetime2(7) NOT NULL);
 END;
 IF EXISTS(SELECT 1 FROM dbo.__B53RegistryBridgeHistory
     WHERE SchemaVersion<>N'B53_REGISTRY_20261118_001'
        OR FenceDatabase<>N'__B53_FENCE_DB__')
    THROW 51860,N'B53_REGISTRY_VERSION_OR_PIN_MISMATCH',1;
 IF NOT EXISTS(SELECT 1 FROM dbo.__B53RegistryBridgeHistory
     WHERE SchemaVersion=N'B53_REGISTRY_20261118_001')
 BEGIN
    IF OBJECT_ID(N'dbo.b53_TenantMonotonicTrigger',N'TR') IS NOT NULL
       THROW 51860,N'B53_REGISTRY_EXISTING_TRIGGER_DENIED',1;
    EXEC(N'CREATE TRIGGER dbo.b53_TenantMonotonicTrigger
ON dbo.PreprodTenantRegistry AFTER INSERT, UPDATE, DELETE
AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM deleted d LEFT JOIN inserted i ON i.TenantId=d.TenantId
           WHERE i.TenantId IS NULL)
    THROW 51860,N''B53_TENANT_DELETE_DENIED'',1;
 IF EXISTS(SELECT 1 FROM inserted i JOIN deleted d ON i.TenantId=d.TenantId
    WHERE i.BindingId<>d.BindingId OR i.DatabaseName<>d.DatabaseName
      OR i.TenantKey<>d.TenantKey OR i.SecurityEpoch<d.SecurityEpoch
      OR (i.SecurityEpoch=d.SecurityEpoch AND
          (i.State=''ACTIVE'' AND d.State<>''ACTIVE''
            OR i.State<>''ACTIVE'' AND d.State=''ACTIVE''))
      OR (i.SecurityEpoch<>d.SecurityEpoch AND
          (d.SecurityEpoch=9223372036854775807
           OR i.SecurityEpoch<>d.SecurityEpoch+1)))
    THROW 51861,N''B53_TENANT_STATE_OR_EPOCH_MUTATION_DENIED'',1;
 DECLARE @id uniqueidentifier,@binding uniqueidentifier,
    @oldEpoch bigint,@newEpoch bigint,@state varchar(32);
 DECLARE newCursor CURSOR LOCAL FAST_FORWARD FOR
   SELECT i.TenantId,i.BindingId,i.SecurityEpoch
   FROM inserted i WHERE NOT EXISTS(
       SELECT 1 FROM deleted d WHERE d.TenantId=i.TenantId)
   ORDER BY i.TenantId;
 OPEN newCursor;
 FETCH NEXT FROM newCursor INTO @id,@binding,@newEpoch;
 WHILE @@FETCH_STATUS=0
 BEGIN
    IF EXISTS(SELECT 1 FROM inserted i WHERE i.TenantId=@id
      AND (i.State<>''REQUESTED'' OR i.SecurityEpoch<>1))
      THROW 51862,N''B53_TENANT_UNAPPROVED_BOOTSTRAP_DENIED'',1;
    EXEC [__B53_FENCE_DB__].dbo.b53_RegisterTenant
       @TenantId=@id,@BindingId=@binding,@RegistryEpoch=@newEpoch;
    FETCH NEXT FROM newCursor INTO @id,@binding,@newEpoch;
 END;
 CLOSE newCursor; DEALLOCATE newCursor;
 DECLARE transitionCursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT i.TenantId,i.BindingId,d.SecurityEpoch,i.SecurityEpoch,i.State
    FROM inserted i JOIN deleted d ON i.TenantId=d.TenantId
    WHERE i.SecurityEpoch<>d.SecurityEpoch
    ORDER BY i.TenantId;
 OPEN transitionCursor;
 FETCH NEXT FROM transitionCursor INTO
    @id,@binding,@oldEpoch,@newEpoch,@state;
 WHILE @@FETCH_STATUS=0
 BEGIN
    DECLARE @effective varchar(16)=CASE WHEN @state=''ACTIVE''
                                            THEN ''ACTIVE'' ELSE ''SUSPENDED'' END;
    EXEC [__B53_FENCE_DB__].dbo.b53_AdvanceTenant
       @TenantId=@id,@BindingId=@binding,
       @ExpectedRegistryEpoch=@oldEpoch,@NewRegistryEpoch=@newEpoch,
       @NewState=@effective;
    FETCH NEXT FROM transitionCursor INTO
       @id,@binding,@oldEpoch,@newEpoch,@state;
 END;
 CLOSE transitionCursor; DEALLOCATE transitionCursor;
END');
    EXEC(N'CREATE PROCEDURE dbo.b53_AssertRegistryBridgeInstalled
AS
BEGIN
 SET NOCOUNT ON;
 IF @@TRANCOUNT<1 OR ISNULL(IS_ROLEMEMBER(N''b5_registry_runtime''),0)<>1
    OR NOT EXISTS(SELECT 1 FROM dbo.__B53RegistryBridgeHistory
        WHERE SchemaVersion=N''B53_REGISTRY_20261118_001'' AND FenceDatabase=N''__B53_FENCE_DB__'')
    OR OBJECT_ID(N''dbo.b53_TenantMonotonicTrigger'',N''TR'') IS NULL
    OR EXISTS(SELECT 1 FROM sys.triggers
         WHERE name=N''b53_TenantMonotonicTrigger'' AND is_disabled=1)
    THROW 51868,N''B53_CONTROL_BRIDGE_NOT_CURRENT'',1;
END');
    GRANT EXECUTE ON OBJECT::dbo.b53_AssertRegistryBridgeInstalled TO b5_registry_runtime;
    -- EXEC-only B5 assertion caller needs trigger *metadata* visibility for
    -- OBJECT_ID/sys.triggers fail-closed checks; never grant table DML/SELECT.
    GRANT VIEW DEFINITION ON OBJECT::dbo.b53_TenantMonotonicTrigger TO b5_registry_runtime;
    INSERT dbo.__B53RegistryBridgeHistory
     (SchemaVersion,FenceDatabase,AppliedUtc)
    VALUES(N'B53_REGISTRY_20261118_001',
           N'__B53_FENCE_DB__',SYSUTCDATETIME());
 END;
 IF (SELECT COUNT_BIG(*) FROM dbo.__B53RegistryBridgeHistory)<>1
    OR NOT EXISTS(SELECT 1 FROM dbo.__B53RegistryBridgeHistory
      WHERE SchemaVersion=N'B53_REGISTRY_20261118_001'
      AND FenceDatabase=N'__B53_FENCE_DB__')
    OR OBJECT_ID(N'dbo.b53_AssertRegistryBridgeInstalled',N'P') IS NULL
    OR OBJECT_ID(N'dbo.b53_TenantMonotonicTrigger',N'TR') IS NULL
    OR EXISTS(SELECT 1 FROM sys.triggers WHERE name=N'b53_TenantMonotonicTrigger'
         AND is_disabled=1)
    THROW 51869,N'B53_REGISTRY_BRIDGE_INTEGRITY_DENIED',1;
 COMMIT TRANSACTION;
END TRY
BEGIN CATCH
 IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;
 THROW;
END CATCH;
