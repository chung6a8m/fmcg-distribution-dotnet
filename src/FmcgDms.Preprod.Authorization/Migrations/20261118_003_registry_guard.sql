-- B5_20261118_001 registry bridge: B.4 Control Plane database ONLY.
-- Operator-applied forward migration, no runtime auto-apply, no default SQL users.
SET NOCOUNT ON; SET XACT_ABORT ON;
BEGIN TRY
 BEGIN TRANSACTION;
 IF DB_NAME() IN (N'master',N'model',N'msdb',N'tempdb')
    THROW 51640,N'B5_REGISTRY_SYSTEM_DB_DENIED',1;
 IF OBJECT_ID(N'dbo.__PreprodTenantRegistrySchemaHistory',N'U') IS NULL
    OR NOT EXISTS(SELECT 1 FROM dbo.__PreprodTenantRegistrySchemaHistory
                  WHERE SchemaVersion=N'B4_20261118_001')
    OR OBJECT_ID(N'dbo.PreprodTenantRegistry',N'U') IS NULL
    THROW 51640,N'B5_REGISTRY_BASE_MISSING',1;
 IF OBJECT_ID(N'dbo.__B5RegistryGuardHistory',N'U') IS NULL
 BEGIN
    IF OBJECT_ID(N'dbo.b5_AssertCurrentTenant',N'P') IS NOT NULL
       THROW 51640,N'B5_REGISTRY_PARTIAL_DENIED',1;
    CREATE TABLE dbo.__B5RegistryGuardHistory
      (SchemaVersion nvarchar(64) NOT NULL PRIMARY KEY,
       AppliedUtc datetime2(7) NOT NULL);
 END;
 IF EXISTS(SELECT 1 FROM dbo.__B5RegistryGuardHistory
           WHERE SchemaVersion<>N'B5_20261118_001')
    THROW 51640,N'B5_REGISTRY_UNKNOWN_VERSION',1;
 IF NOT EXISTS(SELECT 1 FROM dbo.__B5RegistryGuardHistory
               WHERE SchemaVersion=N'B5_20261118_001')
 BEGIN
    IF OBJECT_ID(N'dbo.b5_AssertCurrentTenant',N'P') IS NOT NULL
       THROW 51640,N'B5_REGISTRY_PARTIAL_DENIED',1;
    CREATE ROLE b5_registry_runtime;
    EXEC(N'CREATE PROCEDURE dbo.b5_AssertCurrentTenant
 @TenantId uniqueidentifier,@BindingId uniqueidentifier,@RegistryEpoch bigint,
 @TenantDatabaseName sysname
AS
BEGIN
 SET NOCOUNT ON;
 IF @@TRANCOUNT < 1 OR ISNULL(IS_ROLEMEMBER(N''b5_registry_runtime''),0)<>1
     THROW 51640,N''B5_REGISTRY_PRINCIPAL_DENIED'',1;
 IF @TenantId IS NULL OR @BindingId IS NULL OR @RegistryEpoch < 1
     OR NULLIF(LTRIM(RTRIM(@TenantDatabaseName)),N'''') IS NULL
     OR NOT EXISTS(SELECT 1 FROM dbo.PreprodTenantRegistry WITH (UPDLOCK,HOLDLOCK)
         WHERE TenantId=@TenantId AND BindingId=@BindingId
           AND DatabaseName=@TenantDatabaseName
           AND SecurityEpoch=@RegistryEpoch AND State=''ACTIVE'')
     THROW 51641,N''B5_REGISTRY_STALE_OR_DISABLED'',1;
END');
    GRANT EXECUTE ON OBJECT::dbo.b5_AssertCurrentTenant TO b5_registry_runtime;
    INSERT dbo.__B5RegistryGuardHistory(SchemaVersion,AppliedUtc)
      VALUES(N'B5_20261118_001',SYSUTCDATETIME());
 END;
 COMMIT TRANSACTION;
END TRY
BEGIN CATCH
 IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;
 THROW;
END CATCH;
