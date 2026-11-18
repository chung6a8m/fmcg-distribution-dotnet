-- APP-02B.4 / B4_TENANT_20261118_001.
-- Executed only by approved operator provisioning service on a newly created,
-- empty, approved tenant database. A mismatched marker is never overwritten.
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;
    IF DB_NAME() IN (N'master',N'model',N'msdb',N'tempdb')
        THROW 51501,'TENANT_MIGRATION_SYSTEM_DB_DENIED',1;
    IF OBJECT_ID(N'dbo.__PreprodTenantBinding',N'U') IS NULL
    BEGIN
        IF OBJECT_ID(N'dbo.__PreprodTenantSchemaHistory',N'U') IS NOT NULL
            OR EXISTS (SELECT 1 FROM sys.tables WHERE schema_id=SCHEMA_ID(N'dbo')
                       AND name NOT IN (N'sysdiagrams'))
            THROW 51502,'TENANT_MIGRATION_UNOWNED_OR_PARTIAL_DB_DENIED',1;
        CREATE TABLE dbo.__PreprodTenantBinding (
            TenantId uniqueidentifier NOT NULL CONSTRAINT PK___PreprodTenantBinding PRIMARY KEY,
            BindingId uniqueidentifier NOT NULL,
            BindingVersion bigint NOT NULL,
            CreatedUtc datetime2(7) NOT NULL CONSTRAINT DF___TenantBinding_Created DEFAULT SYSUTCDATETIME()
        );
        CREATE TABLE dbo.__PreprodTenantSchemaHistory (
            SchemaVersion nvarchar(64) NOT NULL CONSTRAINT PK___PreprodTenantSchemaHistory PRIMARY KEY,
            AppliedUtc datetime2(7) NOT NULL
        );
        INSERT dbo.__PreprodTenantBinding (TenantId,BindingId,BindingVersion)
            VALUES (@TenantId,@BindingId,@BindingVersion);
        INSERT dbo.__PreprodTenantSchemaHistory (SchemaVersion,AppliedUtc)
            VALUES (N'B4_TENANT_20261118_001',SYSUTCDATETIME());
    END;
    IF (SELECT COUNT_BIG(*) FROM dbo.__PreprodTenantBinding
        WHERE TenantId=@TenantId AND BindingId=@BindingId AND BindingVersion=@BindingVersion)<>1
        OR (SELECT COUNT_BIG(*) FROM dbo.__PreprodTenantBinding)<>1
        OR (SELECT COUNT_BIG(*) FROM dbo.__PreprodTenantSchemaHistory
            WHERE SchemaVersion=N'B4_TENANT_20261118_001')<>1
        OR (SELECT COUNT_BIG(*) FROM dbo.__PreprodTenantSchemaHistory)<>1
        OR (SELECT COUNT(*) FROM sys.tables WHERE schema_id=SCHEMA_ID(N'dbo')
            AND name IN (N'__PreprodTenantBinding',N'__PreprodTenantSchemaHistory'))<>2
        THROW 51503,'TENANT_MIGRATION_BINDING_OR_SCHEMA_DENIED',1;
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
