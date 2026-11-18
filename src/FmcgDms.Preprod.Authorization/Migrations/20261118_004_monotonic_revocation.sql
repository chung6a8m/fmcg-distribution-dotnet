-- APP-02B issue #53. B53_20261118_001 operator-only versioned
-- migration in the INDEPENDENT B5 fence database. Never restore this
-- DB from B3/B4/tenant PITR snapshots or mount in API/Worker startup.
-- No users or permissions seeded. No business API/Worker activation.
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRY
 BEGIN TRANSACTION;
 IF DB_NAME() IN (N'master',N'model',N'msdb',N'tempdb')
     THROW 51800,N'B53_SYSTEM_DATABASE_DENIED',1;
 DECLARE @l int;
 EXEC @l=sys.sp_getapplock @Resource=N'FMCG_B53_REVOCATION_SCHEMA',
     @LockMode=N'Exclusive',@LockOwner=N'Transaction',@LockTimeout=0;
 IF @l<0 THROW 51800,N'B53_SCHEMA_LOCK_DENIED',1;
 IF OBJECT_ID(N'dbo.__B5SchemaHistory',N'U') IS NULL OR
    NOT EXISTS(SELECT 1 FROM dbo.__B5SchemaHistory
               WHERE SchemaVersion=N'B5_20261118_001') OR
    OBJECT_ID(N'dbo.B5TenantFence',N'U') IS NULL
     THROW 51800,N'B53_BASE_FENCE_MISSING',1;
 IF OBJECT_ID(N'dbo.__B53SchemaHistory',N'U') IS NULL
 BEGIN
    IF OBJECT_ID(N'dbo.B53SessionState',N'U') IS NOT NULL
       OR OBJECT_ID(N'dbo.B53ActivationPermit',N'U') IS NOT NULL
       OR OBJECT_ID(N'dbo.B53RevocationEvent',N'U') IS NOT NULL
       THROW 51800,N'B53_PARTIAL_SCHEMA_DENIED',1;
    CREATE TABLE dbo.__B53SchemaHistory
     (SchemaVersion nvarchar(64) NOT NULL PRIMARY KEY,
      AppliedUtc datetime2(7) NOT NULL);
 END;
 IF EXISTS(SELECT 1 FROM dbo.__B53SchemaHistory
           WHERE SchemaVersion<>N'B53_20261118_001')
     THROW 51800,N'B53_UNKNOWN_SCHEMA_DENIED',1;
 IF NOT EXISTS(SELECT 1 FROM dbo.__B53SchemaHistory
               WHERE SchemaVersion=N'B53_20261118_001')
 BEGIN
    IF OBJECT_ID(N'dbo.B53SessionState',N'U') IS NOT NULL
       OR OBJECT_ID(N'dbo.B53ActivationPermit',N'U') IS NOT NULL
       OR OBJECT_ID(N'dbo.B53RevocationEvent',N'U') IS NOT NULL
       THROW 51800,N'B53_EXISTING_OBJECT_DENIED',1;
    CREATE TABLE dbo.B53SessionState
    (
       SessionId uniqueidentifier NOT NULL PRIMARY KEY,
       ActorId uniqueidentifier NOT NULL,
       SessionEpoch bigint NOT NULL CHECK(SessionEpoch>=0),
       State varchar(16) NOT NULL CHECK(State IN ('ACTIVE','REVOKED')),
       SqlActor sysname NOT NULL,
       CreatedUtc datetime2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
       UpdatedUtc datetime2(7) NOT NULL DEFAULT SYSUTCDATETIME()
    );
    CREATE TABLE dbo.B53ActivationPermit
    (
       PermitId bigint IDENTITY(1,1) PRIMARY KEY,
       TenantId uniqueidentifier NOT NULL,
       BindingId uniqueidentifier NOT NULL,
       TargetRegistryEpoch bigint NOT NULL CHECK(TargetRegistryEpoch>=2),
       EvidenceRef nvarchar(160) NOT NULL,
       ApprovedBy sysname NOT NULL,
       ExpiresUtc datetime2(7) NOT NULL,
       ConsumedUtc datetime2(7) NULL
    );
    CREATE UNIQUE INDEX UX_B53ActivePermit
        ON dbo.B53ActivationPermit(TenantId)
        WHERE ConsumedUtc IS NULL;
    CREATE TABLE dbo.B53RevocationEvent
    (
       EventNo bigint IDENTITY(1,1) PRIMARY KEY,
       SubjectKind varchar(16) NOT NULL CHECK(SubjectKind IN ('TENANT','SESSION')),
       SubjectId uniqueidentifier NOT NULL,
       ActionCode varchar(16) NOT NULL,
       PriorEpoch bigint NULL,
       NewEpoch bigint NOT NULL,
       SqlActor sysname NOT NULL,
       OccurredUtc datetime2(7) NOT NULL DEFAULT SYSUTCDATETIME()
    );
    CREATE ROLE b53_identity_bridge;
    CREATE ROLE b53_registry_bridge;
    CREATE ROLE b53_reactivation_approver;
    CREATE ROLE b53_runtime;
    EXEC(N'CREATE PROCEDURE dbo.b53_RegisterSession
 @SessionId uniqueidentifier, @ActorId uniqueidentifier, @SessionEpoch bigint
AS
BEGIN
 SET NOCOUNT ON;
 IF TRIGGER_NESTLEVEL() < 1 OR ISNULL(IS_ROLEMEMBER(N''b53_identity_bridge''),0)<>1
    THROW 51801,N''B53_SESSION_BRIDGE_PRINCIPAL_DENIED'',1;
 IF @SessionId IS NULL OR @SessionId=''00000000-0000-0000-0000-000000000000''
    OR @ActorId IS NULL OR @ActorId=''00000000-0000-0000-0000-000000000000''
    OR @SessionEpoch<>0
    THROW 51802,N''B53_SESSION_REGISTRATION_DENIED'',1;
 -- Never UPSERT: a restored/duplicated SessionId cannot resurrect old rights.
 INSERT dbo.B53SessionState(SessionId,ActorId,SessionEpoch,State,SqlActor)
 VALUES(@SessionId,@ActorId,@SessionEpoch,''ACTIVE'',ORIGINAL_LOGIN());
 INSERT dbo.B53RevocationEvent(SubjectKind,SubjectId,ActionCode,PriorEpoch,NewEpoch,SqlActor)
 VALUES(''SESSION'',@SessionId,''REGISTER'',NULL,@SessionEpoch,ORIGINAL_LOGIN());
END');
    EXEC(N'CREATE PROCEDURE dbo.b53_RevokeSession
 @SessionId uniqueidentifier, @ActorId uniqueidentifier,
 @ExpectedEpoch bigint, @NewEpoch bigint
AS
BEGIN
 SET NOCOUNT ON;
 IF TRIGGER_NESTLEVEL() < 1 OR ISNULL(IS_ROLEMEMBER(N''b53_identity_bridge''),0)<>1
    THROW 51803,N''B53_SESSION_REVOKER_DENIED'',1;
 IF @ExpectedEpoch<0 OR @ExpectedEpoch=9223372036854775807
    OR @NewEpoch<>@ExpectedEpoch+1
    THROW 51804,N''B53_SESSION_EPOCH_INVALID'',1;
 UPDATE dbo.B53SessionState WITH(UPDLOCK)
    SET SessionEpoch=@NewEpoch,State=''REVOKED'',
        UpdatedUtc=SYSUTCDATETIME(),SqlActor=ORIGINAL_LOGIN()
 WHERE SessionId=@SessionId AND ActorId=@ActorId
    AND SessionEpoch=@ExpectedEpoch AND State=''ACTIVE'';
 IF @@ROWCOUNT<>1 THROW 51805,N''B53_SESSION_REVOKE_STALE'',1;
 INSERT dbo.B53RevocationEvent(SubjectKind,SubjectId,ActionCode,PriorEpoch,NewEpoch,SqlActor)
 VALUES(''SESSION'',@SessionId,''REVOKE'',@ExpectedEpoch,@NewEpoch,ORIGINAL_LOGIN());
END');
    EXEC(N'CREATE PROCEDURE dbo.b53_RegisterTenant
 @TenantId uniqueidentifier,@BindingId uniqueidentifier,@RegistryEpoch bigint
AS
BEGIN
 SET NOCOUNT ON;
 IF TRIGGER_NESTLEVEL()<1 OR ISNULL(IS_ROLEMEMBER(N''b53_registry_bridge''),0)<>1
    THROW 51810,N''B53_REGISTRY_BRIDGE_PRINCIPAL_DENIED'',1;
 IF @TenantId IS NULL OR @TenantId=''00000000-0000-0000-0000-000000000000''
    OR @BindingId IS NULL OR @BindingId=''00000000-0000-0000-0000-000000000000''
    OR @RegistryEpoch<>1
    THROW 51811,N''B53_REGISTRY_BOOTSTRAP_DENIED'',1;
 -- All new tenants start non-routable in the separately durable authority.
 INSERT dbo.B5TenantFence(TenantId,BindingId,RegistryEpoch,FenceEpoch,State)
 VALUES(@TenantId,@BindingId,@RegistryEpoch,1,''SUSPENDED'');
 INSERT dbo.B53RevocationEvent(SubjectKind,SubjectId,ActionCode,PriorEpoch,NewEpoch,SqlActor)
 VALUES(''TENANT'',@TenantId,''REGISTER'',NULL,@RegistryEpoch,ORIGINAL_LOGIN());
END');
    EXEC(N'CREATE PROCEDURE dbo.b53_ApproveActivation
 @TenantId uniqueidentifier,@BindingId uniqueidentifier,
 @TargetRegistryEpoch bigint,@EvidenceRef nvarchar(160)
AS
BEGIN
 SET NOCOUNT ON;
 IF ISNULL(IS_ROLEMEMBER(N''b53_reactivation_approver''),0)<>1
    THROW 51820,N''B53_APPROVER_PRINCIPAL_DENIED'',1;
 IF @TenantId IS NULL OR @BindingId IS NULL OR @TargetRegistryEpoch<2
    OR NULLIF(LTRIM(RTRIM(@EvidenceRef)),N'''') IS NULL
    OR LEN(@EvidenceRef)>160
    THROW 51821,N''B53_APPROVAL_INPUT_DENIED'',1;
 BEGIN TRANSACTION;
 IF NOT EXISTS(SELECT 1 FROM dbo.B5TenantFence WITH(UPDLOCK,HOLDLOCK)
    WHERE TenantId=@TenantId AND BindingId=@BindingId
      AND RegistryEpoch=@TargetRegistryEpoch-1 AND State=''SUSPENDED'')
    THROW 51822,N''B53_APPROVAL_EPOCH_OR_STATE_DENIED'',1;
 IF EXISTS(SELECT 1 FROM dbo.B53ActivationPermit WITH(UPDLOCK,HOLDLOCK)
    WHERE TenantId=@TenantId AND ConsumedUtc IS NULL)
    THROW 51823,N''B53_DUPLICATE_APPROVAL_DENIED'',1;
 INSERT dbo.B53ActivationPermit
    (TenantId,BindingId,TargetRegistryEpoch,EvidenceRef,ApprovedBy,ExpiresUtc)
 VALUES(@TenantId,@BindingId,@TargetRegistryEpoch,@EvidenceRef,
        ORIGINAL_LOGIN(),DATEADD(MINUTE,5,SYSUTCDATETIME()));
 COMMIT TRANSACTION;
END');
    EXEC(N'CREATE PROCEDURE dbo.b53_AdvanceTenant
 @TenantId uniqueidentifier,@BindingId uniqueidentifier,
 @ExpectedRegistryEpoch bigint,@NewRegistryEpoch bigint,@NewState varchar(16)
AS
BEGIN
 SET NOCOUNT ON;
 IF TRIGGER_NESTLEVEL()<1 OR ISNULL(IS_ROLEMEMBER(N''b53_registry_bridge''),0)<>1
    THROW 51830,N''B53_TENANT_BRIDGE_DENIED'',1;
 IF @TenantId IS NULL OR @BindingId IS NULL
    OR @ExpectedRegistryEpoch<1 OR @ExpectedRegistryEpoch=9223372036854775807
    OR @NewRegistryEpoch<>@ExpectedRegistryEpoch+1
    OR @NewState NOT IN (''ACTIVE'',''SUSPENDED'')
    THROW 51831,N''B53_TENANT_EPOCH_INVALID'',1;
 IF @NewState=''ACTIVE''
 BEGIN
    -- Independent permit is target-epoch/binding-bound and single-use.
    UPDATE dbo.B53ActivationPermit WITH(UPDLOCK)
       SET ConsumedUtc=SYSUTCDATETIME()
    WHERE TenantId=@TenantId AND BindingId=@BindingId
      AND TargetRegistryEpoch=@NewRegistryEpoch
      AND ConsumedUtc IS NULL AND ExpiresUtc>SYSUTCDATETIME();
    IF @@ROWCOUNT<>1 THROW 51832,N''B53_ACTIVATION_PERMIT_MISSING'',1;
 END;
 UPDATE dbo.B5TenantFence WITH(UPDLOCK)
   SET RegistryEpoch=@NewRegistryEpoch,FenceEpoch=FenceEpoch+1,
       State=@NewState,UpdatedUtc=SYSUTCDATETIME()
 WHERE TenantId=@TenantId AND BindingId=@BindingId
   AND RegistryEpoch=@ExpectedRegistryEpoch
   AND FenceEpoch<9223372036854775807
   AND (@NewState=''SUSPENDED'' OR State=''SUSPENDED'');
 IF @@ROWCOUNT<>1 THROW 51833,N''B53_TENANT_FENCE_STALE'',1;
 INSERT dbo.B53RevocationEvent(SubjectKind,SubjectId,ActionCode,PriorEpoch,NewEpoch,SqlActor)
 VALUES(''TENANT'',@TenantId,@NewState,@ExpectedRegistryEpoch,@NewRegistryEpoch,
        ORIGINAL_LOGIN());
END');
    EXEC(N'CREATE PROCEDURE dbo.b53_AssertFreshSession
 @SessionId uniqueidentifier,@ActorId uniqueidentifier,@SessionEpoch bigint
AS
BEGIN
 SET NOCOUNT ON;
 IF @@TRANCOUNT<1 OR ISNULL(IS_ROLEMEMBER(N''b53_runtime''),0)<>1
    THROW 51840,N''B53_FRESH_GUARD_DENIED'',1;
 IF @SessionId IS NULL OR @ActorId IS NULL OR @SessionEpoch<0
    OR NOT EXISTS(SELECT 1 FROM dbo.B53SessionState WITH(UPDLOCK,HOLDLOCK)
         WHERE SessionId=@SessionId AND ActorId=@ActorId
           AND SessionEpoch=@SessionEpoch AND State=''ACTIVE'')
    THROW 51841,N''B53_STALE_OR_REVOKED_SESSION_DENIED'',1;
END');
    GRANT EXECUTE ON OBJECT::dbo.b53_RegisterSession TO b53_identity_bridge;
    GRANT EXECUTE ON OBJECT::dbo.b53_RevokeSession TO b53_identity_bridge;
    GRANT EXECUTE ON OBJECT::dbo.b53_RegisterTenant TO b53_registry_bridge;
    GRANT EXECUTE ON OBJECT::dbo.b53_AdvanceTenant TO b53_registry_bridge;
    GRANT EXECUTE ON OBJECT::dbo.b53_ApproveActivation TO b53_reactivation_approver;
    GRANT EXECUTE ON OBJECT::dbo.b53_AssertFreshSession TO b53_runtime;
    INSERT dbo.__B53SchemaHistory(SchemaVersion,AppliedUtc)
    VALUES(N'B53_20261118_001',SYSUTCDATETIME());
 END;
 IF (SELECT COUNT_BIG(*) FROM dbo.__B53SchemaHistory)<>1
    OR NOT EXISTS(SELECT 1 FROM dbo.__B53SchemaHistory
       WHERE SchemaVersion=N'B53_20261118_001')
    OR OBJECT_ID(N'dbo.B53SessionState',N'U') IS NULL
    OR OBJECT_ID(N'dbo.B53ActivationPermit',N'U') IS NULL
    OR OBJECT_ID(N'dbo.B53RevocationEvent',N'U') IS NULL
    OR (SELECT COUNT(*) FROM sys.procedures WHERE schema_id=SCHEMA_ID(N'dbo')
        AND name IN (N'b53_RegisterSession',N'b53_RevokeSession',
           N'b53_RegisterTenant',N'b53_AdvanceTenant',
           N'b53_ApproveActivation',N'b53_AssertFreshSession'))<>6
    OR (SELECT COUNT(*) FROM sys.database_principals
        WHERE type=N'R' AND name IN
          (N'b53_identity_bridge',N'b53_registry_bridge',
           N'b53_reactivation_approver',N'b53_runtime'))<>4
    THROW 51899,N'B53_SCHEMA_INTEGRITY_DENIED',1;
 COMMIT TRANSACTION;
END TRY
BEGIN CATCH
 IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;
 THROW;
END CATCH;
