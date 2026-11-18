-- APP-02B.4 B4_20261118_001 / operator-APPLIED control-plane migration.
-- SQL Server; no seed operators/tenant grants, no API/Worker migration at startup.
-- Run only on a DBA-approved private control database using reviewed script SHA.
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;
    IF DB_NAME() IN (N'master',N'model',N'msdb',N'tempdb')
        THROW 51400, 'TENANT_REGISTRY_SYSTEM_DB_DENIED', 1;
    DECLARE @lock int;
    EXEC @lock = sys.sp_getapplock @Resource=N'FMCG_PREPROD_B4_SCHEMA',
        @LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=0;
    IF @lock<0 THROW 51400,'TENANT_REGISTRY_MIGRATION_LOCKED',1;
    IF OBJECT_ID(N'dbo.__PreprodTenantRegistrySchemaHistory',N'U') IS NULL
    BEGIN
        IF OBJECT_ID(N'dbo.PreprodTenantOperators',N'U') IS NOT NULL
            OR OBJECT_ID(N'dbo.PreprodTenantRegistry',N'U') IS NOT NULL
            OR OBJECT_ID(N'dbo.PreprodTenantLifecycleEvents',N'U') IS NOT NULL
            THROW 51400,'TENANT_REGISTRY_PARTIAL_SCHEMA_DENIED',1;
        CREATE TABLE dbo.__PreprodTenantRegistrySchemaHistory (
            SchemaVersion nvarchar(64) NOT NULL CONSTRAINT PK___PreprodTenantRegistrySchemaHistory PRIMARY KEY,
            AppliedUtc datetime2(7) NOT NULL);
    END;
    IF EXISTS (SELECT 1 FROM dbo.__PreprodTenantRegistrySchemaHistory
               WHERE SchemaVersion<>N'B4_20261118_001')
        THROW 51400,'TENANT_REGISTRY_UNKNOWN_VERSION_DENIED',1;
    IF NOT EXISTS (SELECT 1 FROM dbo.__PreprodTenantRegistrySchemaHistory
                   WHERE SchemaVersion=N'B4_20261118_001')
    BEGIN
        IF OBJECT_ID(N'dbo.PreprodTenantOperators',N'U') IS NOT NULL
            OR OBJECT_ID(N'dbo.PreprodTenantRegistry',N'U') IS NOT NULL
            OR OBJECT_ID(N'dbo.PreprodTenantLifecycleEvents',N'U') IS NOT NULL
            THROW 51400,'TENANT_REGISTRY_EXISTING_SCHEMA_DENIED',1;

    CREATE TABLE dbo.PreprodTenantOperators
    (
        LoginName sysname NOT NULL CONSTRAINT PK_PreprodTenantOperators PRIMARY KEY,
        Enabled bit NOT NULL CONSTRAINT DF_PreprodTenantOperators_Enabled DEFAULT (0),
        CanRequest bit NOT NULL CONSTRAINT DF_PreprodTenantOperators_Request DEFAULT (0),
        CanApprove bit NOT NULL CONSTRAINT DF_PreprodTenantOperators_Approve DEFAULT (0),
        CanProvision bit NOT NULL CONSTRAINT DF_PreprodTenantOperators_Provision DEFAULT (0),
        CanFinalize bit NOT NULL CONSTRAINT DF_PreprodTenantOperators_Finalize DEFAULT (0),
        CanRetireFinalizer bit NOT NULL CONSTRAINT DF_PreprodTenantOperators_Retire DEFAULT (0),
        CanVerify bit NOT NULL CONSTRAINT DF_PreprodTenantOperators_Verify DEFAULT (0),
        CanSuspend bit NOT NULL CONSTRAINT DF_PreprodTenantOperators_Suspend DEFAULT (0)
    );
    -- No seeded administrator. Only an authorized DBA controls grants and operator rows.
    CREATE TABLE dbo.PreprodTenantRegistry
    (
        TenantId uniqueidentifier NOT NULL CONSTRAINT PK_PreprodTenantRegistry PRIMARY KEY,
        TenantKey nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
        DatabaseName sysname NOT NULL,
        BindingId uniqueidentifier NOT NULL,
        BindingVersion bigint NOT NULL CONSTRAINT DF_PreprodTenantRegistry_Binding DEFAULT (1),
        SecurityEpoch bigint NOT NULL CONSTRAINT DF_PreprodTenantRegistry_Epoch DEFAULT (1),
        State varchar(32) NOT NULL,
        RequestRef nvarchar(160) NOT NULL,
        RequestedBy sysname NOT NULL,
        ApprovedBy sysname NULL,
        ApprovalRef nvarchar(160) NULL,
        ApprovedOwnerLogin sysname NULL,
        ApprovedProvisionerLogin sysname NULL,
        ApprovedFinalizerLogin sysname NULL,
        ApprovedRetirerLogin sysname NULL,
        ProvisionedBy sysname NULL,
        VerifiedBy sysname NULL,
        VerificationHash binary(32) NULL,
        VerifiedUtc datetime2(7) NULL,
        VerifiedEpoch bigint NULL,
        VerifiedBindingId uniqueidentifier NULL,
        OperationId uniqueidentifier NULL,
        LeaseToken uniqueidentifier NULL,
        LeaseUntilUtc datetime2(7) NULL,
        FinalizationOperationId uniqueidentifier NULL,
        FinalizationLeaseToken uniqueidentifier NULL,
        FinalizationLeaseUntilUtc datetime2(7) NULL,
        FinalizedBy sysname NULL,
        FinalizedUtc datetime2(7) NULL,
        FinalizedEpoch bigint NULL,
        FinalizerRetiredBy sysname NULL,
        FinalizerRetiredUtc datetime2(7) NULL,
        FinalizedBindingId uniqueidentifier NULL,
        ReactivationApprovedBy sysname NULL,
        DecommissionApprovedBy sysname NULL,
        EvidenceRef nvarchar(200) NULL,
        CreatedUtc datetime2(7) NOT NULL CONSTRAINT DF_PreprodTenantRegistry_Created DEFAULT SYSUTCDATETIME(),
        UpdatedUtc datetime2(7) NOT NULL CONSTRAINT DF_PreprodTenantRegistry_Updated DEFAULT SYSUTCDATETIME(),
        RowVer rowversion NOT NULL,
        CONSTRAINT UQ_PreprodTenantRegistry_Key UNIQUE (TenantKey),
        CONSTRAINT UQ_PreprodTenantRegistry_Database UNIQUE (DatabaseName),
        CONSTRAINT UQ_PreprodTenantRegistry_Binding UNIQUE (BindingId),
        CONSTRAINT CK_PreprodTenantRegistry_State CHECK (State IN
            ('REQUESTED','APPROVED','REJECTED','PROVISIONING','FAILED_RETRYABLE','VERIFYING',
             'FINALIZING','ACTIVE','SUSPENDED','REACTIVATION_VERIFYING','DECOMMISSIONING','DECOMMISSIONED'))
    );
    -- A finalizer and its retiring DBA credential are bound to EXACTLY ONE
    -- tenant; sharing either SQL login across tenants would let privilege
    -- retirement of tenant A strand or expose tenant B.
    CREATE UNIQUE INDEX UX_PreprodTenantRegistry_OwnerLogin
        ON dbo.PreprodTenantRegistry(ApprovedOwnerLogin)
        WHERE ApprovedOwnerLogin IS NOT NULL;
    CREATE UNIQUE INDEX UX_PreprodTenantRegistry_FinalizerLogin
        ON dbo.PreprodTenantRegistry(ApprovedFinalizerLogin)
        WHERE ApprovedFinalizerLogin IS NOT NULL;
    CREATE UNIQUE INDEX UX_PreprodTenantRegistry_RetirerLogin
        ON dbo.PreprodTenantRegistry(ApprovedRetirerLogin)
        WHERE ApprovedRetirerLogin IS NOT NULL;

    CREATE TABLE dbo.PreprodTenantLifecycleEvents
    (
        EventNo bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_PreprodTenantLifecycleEvents PRIMARY KEY,
        TenantId uniqueidentifier NOT NULL,
        ActionCode varchar(32) NOT NULL,
        FromState varchar(32) NULL,
        ToState varchar(32) NOT NULL,
        ActorLogin sysname NOT NULL,
        OperationId uniqueidentifier NULL,
        SecurityEpoch bigint NOT NULL,
        BindingId uniqueidentifier NOT NULL,
        EvidenceRef nvarchar(200) NULL,
        OccurredUtc datetime2(7) NOT NULL CONSTRAINT DF_PreprodTenantEvents_Utc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_PreprodTenantEvents_Tenant FOREIGN KEY (TenantId)
            REFERENCES dbo.PreprodTenantRegistry (TenantId)
    );
    CREATE INDEX IX_PreprodTenantEvents_TenantUtc ON dbo.PreprodTenantLifecycleEvents(TenantId, EventNo);

    EXEC(N'
CREATE PROCEDURE dbo.cp_RequestTenant
    @TenantId uniqueidentifier, @TenantKey nvarchar(64), @RequestRef nvarchar(160)
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    DECLARE @actor sysname = SUSER_SNAME();
    IF NOT EXISTS (SELECT 1 FROM dbo.PreprodTenantOperators
        WHERE LoginName=@actor AND Enabled=1 AND CanRequest=1)
        THROW 51401, ''TENANT_OPERATOR_DENIED'', 1;
    IF @TenantId IS NULL OR @TenantId=''00000000-0000-0000-0000-000000000000''
        OR @TenantKey IS NULL OR LEN(@TenantKey) NOT BETWEEN 3 AND 64
        OR @TenantKey COLLATE Latin1_General_100_BIN2 LIKE N''%[^a-z0-9-]%''
        OR @RequestRef IS NULL OR LEN(TRIM(@RequestRef)) NOT BETWEEN 3 AND 160
        THROW 51402, ''TENANT_REQUEST_INVALID'', 1;
    DECLARE @db sysname = N''FmcgPpT_'' + LOWER(REPLACE(CONVERT(nvarchar(36), @TenantId), N''-'', N''''));
    BEGIN TRANSACTION;
    IF EXISTS (SELECT 1 FROM dbo.PreprodTenantRegistry WITH (UPDLOCK, HOLDLOCK) WHERE TenantId=@TenantId)
    BEGIN
        IF NOT EXISTS (SELECT 1 FROM dbo.PreprodTenantRegistry
            WHERE TenantId=@TenantId AND TenantKey=@TenantKey AND RequestRef=@RequestRef AND RequestedBy=@actor)
            THROW 51403, ''TENANT_REQUEST_IDEMPOTENCY_CONFLICT'', 1;
    END
    ELSE
    BEGIN
        INSERT dbo.PreprodTenantRegistry(TenantId,TenantKey,DatabaseName,BindingId,State,RequestRef,RequestedBy)
        VALUES(@TenantId,@TenantKey,@db,NEWID(),''REQUESTED'',@RequestRef,@actor);
        INSERT dbo.PreprodTenantLifecycleEvents(TenantId,ActionCode,FromState,ToState,ActorLogin,
            SecurityEpoch,BindingId,EvidenceRef)
        SELECT TenantId,''REQUEST'',NULL,State,@actor,SecurityEpoch,BindingId,@RequestRef
        FROM dbo.PreprodTenantRegistry WHERE TenantId=@TenantId;
    END;
    COMMIT TRANSACTION;
    SELECT TenantId,DatabaseName,BindingId,BindingVersion,SecurityEpoch,State,RowVer,
        OperationId,LeaseToken,LeaseUntilUtc,VerifiedBy,VerifiedUtc,
        ApprovedOwnerLogin,ApprovedProvisionerLogin,ApprovedFinalizerLogin,ApprovedRetirerLogin,
        FinalizationOperationId,FinalizationLeaseToken,FinalizationLeaseUntilUtc,
        FinalizedBy,FinalizedUtc,FinalizedEpoch,FinalizedBindingId,
        FinalizerRetiredBy,FinalizerRetiredUtc
    FROM dbo.PreprodTenantRegistry WHERE TenantId=@TenantId;
END;');
    EXEC(N'
CREATE PROCEDURE dbo.cp_ApproveTenant @TenantId uniqueidentifier, @ApprovalRef nvarchar(160),
    @ApprovedOwnerLogin sysname, @ProvisionerLogin sysname, @FinalizerLogin sysname,
    @RetirerLogin sysname
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    DECLARE @actor sysname=SUSER_SNAME(), @old varchar(32);
    IF NOT EXISTS (SELECT 1 FROM dbo.PreprodTenantOperators
        WHERE LoginName=@actor AND Enabled=1 AND CanApprove=1)
        THROW 51401, ''TENANT_OPERATOR_DENIED'', 1;
    IF @ApprovalRef IS NULL OR LEN(TRIM(@ApprovalRef)) NOT BETWEEN 3 AND 160
        THROW 51402, ''TENANT_APPROVAL_REF_INVALID'', 1;
    IF @ApprovedOwnerLogin IS NULL OR @ProvisionerLogin IS NULL OR @FinalizerLogin IS NULL
        OR @RetirerLogin IS NULL OR SUSER_SID(@RetirerLogin) IS NULL
        OR @ApprovedOwnerLogin=@ProvisionerLogin OR @ApprovedOwnerLogin=@actor
        OR @FinalizerLogin IN (@ApprovedOwnerLogin,@ProvisionerLogin,@actor)
        OR @RetirerLogin IN (@ApprovedOwnerLogin,@ProvisionerLogin,@FinalizerLogin,@actor)
        OR SUSER_SID(@FinalizerLogin) IS NULL
        OR COALESCE(IS_SRVROLEMEMBER(''sysadmin'',@FinalizerLogin),1)<>0
        OR COALESCE(IS_SRVROLEMEMBER(''dbcreator'',@FinalizerLogin),1)<>0
        OR SUSER_SID(@ApprovedOwnerLogin) IS NULL OR SUSER_SID(@ProvisionerLogin) IS NULL
        OR COALESCE(IS_SRVROLEMEMBER(''sysadmin'',@ApprovedOwnerLogin),1)<>0
        OR COALESCE(IS_SRVROLEMEMBER(''dbcreator'',@ApprovedOwnerLogin),1)<>0
        OR COALESCE(IS_SRVROLEMEMBER(''sysadmin'',@ProvisionerLogin),1)<>0
        OR COALESCE(IS_SRVROLEMEMBER(''dbcreator'',@ProvisionerLogin),1)<>0
        THROW 51402, ''TENANT_APPROVED_OWNER_OR_PROVISIONER_INVALID'', 1;
    BEGIN TRANSACTION;
    SELECT @old=State FROM dbo.PreprodTenantRegistry WITH (UPDLOCK,HOLDLOCK)
        WHERE TenantId=@TenantId AND RequestedBy<>@actor AND RequestedBy<>@FinalizerLogin;
    IF @old IS NULL THROW 51404, ''TENANT_SELF_APPROVAL_OR_NOT_FOUND'', 1;
    IF @old=''APPROVED'' AND EXISTS (SELECT 1 FROM dbo.PreprodTenantRegistry
        WHERE TenantId=@TenantId AND ApprovedBy=@actor AND ApprovalRef=@ApprovalRef
            AND ApprovedOwnerLogin=@ApprovedOwnerLogin AND ApprovedProvisionerLogin=@ProvisionerLogin
            AND ApprovedFinalizerLogin=@FinalizerLogin AND ApprovedRetirerLogin=@RetirerLogin)
    BEGIN COMMIT TRANSACTION; RETURN; END;
    IF @old<>''REQUESTED'' THROW 51405, ''TENANT_APPROVAL_STATE_DENIED'', 1;
    UPDATE dbo.PreprodTenantRegistry SET State=''APPROVED'', ApprovedBy=@actor,
        ApprovalRef=@ApprovalRef,ApprovedOwnerLogin=@ApprovedOwnerLogin,
        ApprovedProvisionerLogin=@ProvisionerLogin,ApprovedFinalizerLogin=@FinalizerLogin,
        ApprovedRetirerLogin=@RetirerLogin,
        UpdatedUtc=SYSUTCDATETIME()
    WHERE TenantId=@TenantId;
    INSERT dbo.PreprodTenantLifecycleEvents(TenantId,ActionCode,FromState,ToState,ActorLogin,
        SecurityEpoch,BindingId,EvidenceRef)
    SELECT TenantId,''APPROVE'',@old,State,@actor,SecurityEpoch,BindingId,@ApprovalRef
    FROM dbo.PreprodTenantRegistry WHERE TenantId=@TenantId;
    COMMIT TRANSACTION;
END;');
    EXEC(N'
CREATE PROCEDURE dbo.cp_ClaimProvision @TenantId uniqueidentifier, @LeaseToken uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    DECLARE @actor sysname=SUSER_SNAME(), @old varchar(32);
    IF NOT EXISTS (SELECT 1 FROM dbo.PreprodTenantOperators
        WHERE LoginName=@actor AND Enabled=1 AND CanProvision=1)
        THROW 51401, ''TENANT_OPERATOR_DENIED'', 1;
    IF @LeaseToken IS NULL OR @LeaseToken=''00000000-0000-0000-0000-000000000000''
        THROW 51402, ''TENANT_LEASE_INVALID'', 1;
    BEGIN TRANSACTION;
    SELECT @old=State FROM dbo.PreprodTenantRegistry WITH (UPDLOCK,HOLDLOCK)
        WHERE TenantId=@TenantId AND ApprovedBy IS NOT NULL AND ApprovedFinalizerLogin IS NOT NULL;
    IF @old IS NULL OR @old NOT IN (''APPROVED'',''FAILED_RETRYABLE'',''PROVISIONING'')
        THROW 51406, ''TENANT_NOT_CLAIMABLE'', 1;
    IF @old=''PROVISIONING'' AND EXISTS (SELECT 1 FROM dbo.PreprodTenantRegistry
        WHERE TenantId=@TenantId AND LeaseUntilUtc >= SYSUTCDATETIME())
        THROW 51407, ''TENANT_LEASE_BUSY'', 1;
    UPDATE dbo.PreprodTenantRegistry SET State=''PROVISIONING'',
        OperationId=COALESCE(OperationId,NEWID()), LeaseToken=@LeaseToken,
        LeaseUntilUtc=DATEADD(minute,5,SYSUTCDATETIME()),
        ProvisionedBy=@actor, VerifiedBy=NULL, VerificationHash=NULL,
        VerifiedUtc=NULL, VerifiedEpoch=NULL, VerifiedBindingId=NULL,
        UpdatedUtc=SYSUTCDATETIME() WHERE TenantId=@TenantId;
    INSERT dbo.PreprodTenantLifecycleEvents(TenantId,ActionCode,FromState,ToState,ActorLogin,
        OperationId,SecurityEpoch,BindingId)
    SELECT TenantId,''CLAIM'',@old,State,@actor,OperationId,SecurityEpoch,BindingId
    FROM dbo.PreprodTenantRegistry WHERE TenantId=@TenantId;
    SELECT TenantId,DatabaseName,BindingId,BindingVersion,SecurityEpoch,State,RowVer,
        OperationId,LeaseToken,LeaseUntilUtc,VerifiedBy,VerifiedUtc,
        ApprovedOwnerLogin,ApprovedProvisionerLogin,ApprovedFinalizerLogin,ApprovedRetirerLogin,
        FinalizationOperationId,FinalizationLeaseToken,FinalizationLeaseUntilUtc,
        FinalizedBy,FinalizedUtc,FinalizedEpoch,FinalizedBindingId,
        FinalizerRetiredBy,FinalizerRetiredUtc
    FROM dbo.PreprodTenantRegistry WHERE TenantId=@TenantId;
    COMMIT TRANSACTION;
END;');
    EXEC(N'
CREATE PROCEDURE dbo.cp_CompleteProvision @TenantId uniqueidentifier, @LeaseToken uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    DECLARE @actor sysname=SUSER_SNAME();
    IF NOT EXISTS (SELECT 1 FROM dbo.PreprodTenantOperators
        WHERE LoginName=@actor AND Enabled=1 AND CanProvision=1)
        THROW 51401, ''TENANT_OPERATOR_DENIED'', 1;
    BEGIN TRANSACTION;
    UPDATE dbo.PreprodTenantRegistry SET State=''VERIFYING'',LeaseToken=NULL,
        LeaseUntilUtc=NULL,UpdatedUtc=SYSUTCDATETIME()
    WHERE TenantId=@TenantId AND State=''PROVISIONING''
        AND LeaseToken=@LeaseToken AND LeaseUntilUtc>SYSUTCDATETIME()
        AND ProvisionedBy=@actor;
    IF @@ROWCOUNT<>1 THROW 51408, ''TENANT_COMPLETION_FENCED'', 1;
    INSERT dbo.PreprodTenantLifecycleEvents(TenantId,ActionCode,FromState,ToState,ActorLogin,
        OperationId,SecurityEpoch,BindingId)
    SELECT TenantId,''PROVISIONED'',''PROVISIONING'',State,@actor,OperationId,SecurityEpoch,BindingId
    FROM dbo.PreprodTenantRegistry WHERE TenantId=@TenantId;
    COMMIT TRANSACTION;
END;');
    EXEC(N'
CREATE PROCEDURE dbo.cp_FailProvision
    @TenantId uniqueidentifier, @LeaseToken uniqueidentifier, @ErrorCode varchar(32)
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    DECLARE @actor sysname=SUSER_SNAME();
    IF NOT EXISTS (SELECT 1 FROM dbo.PreprodTenantOperators
        WHERE LoginName=@actor AND Enabled=1 AND CanProvision=1)
        THROW 51401, ''TENANT_OPERATOR_DENIED'', 1;
    IF @ErrorCode IS NULL OR LEN(@ErrorCode) NOT BETWEEN 3 AND 32
        OR @ErrorCode COLLATE Latin1_General_100_BIN2 LIKE ''%[^A-Z0-9_]''
        THROW 51402, ''TENANT_ERROR_CODE_INVALID'', 1;
    BEGIN TRANSACTION;
    UPDATE dbo.PreprodTenantRegistry SET State=''FAILED_RETRYABLE'', LeaseToken=NULL,
        LeaseUntilUtc=NULL,UpdatedUtc=SYSUTCDATETIME()
    WHERE TenantId=@TenantId AND State=''PROVISIONING'' AND LeaseToken=@LeaseToken
        AND ProvisionedBy=@actor AND LeaseUntilUtc>SYSUTCDATETIME();
    IF @@ROWCOUNT<>1 THROW 51409, ''TENANT_FAILURE_FENCED'', 1;
    INSERT dbo.PreprodTenantLifecycleEvents(TenantId,ActionCode,FromState,ToState,ActorLogin,
        OperationId,SecurityEpoch,BindingId,EvidenceRef)
    SELECT TenantId,''PROVISION_FAILED'',''PROVISIONING'',State,@actor,OperationId,SecurityEpoch,
        BindingId,@ErrorCode FROM dbo.PreprodTenantRegistry WHERE TenantId=@TenantId;
    COMMIT TRANSACTION;
END;');
    EXEC(N'
CREATE PROCEDURE dbo.cp_ClaimFinalization
    @TenantId uniqueidentifier, @LeaseToken uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    DECLARE @actor sysname=SUSER_SNAME(),@old varchar(32);
    IF @LeaseToken IS NULL OR @LeaseToken=''00000000-0000-0000-0000-000000000000''
        THROW 51420,''TENANT_FINALIZER_TOKEN_INVALID'',1;
    IF NOT EXISTS (SELECT 1 FROM dbo.PreprodTenantOperators
        WHERE LoginName=@actor AND Enabled=1 AND CanFinalize=1
          AND CanRequest=0 AND CanApprove=0 AND CanProvision=0 AND CanVerify=0 AND CanSuspend=0)
        THROW 51421,''TENANT_FINALIZER_OPERATOR_DENIED'',1;
    BEGIN TRANSACTION;
    SELECT @old=State FROM dbo.PreprodTenantRegistry WITH (UPDLOCK,HOLDLOCK)
    WHERE TenantId=@TenantId AND ApprovedFinalizerLogin=@actor AND ApprovedBy<>@actor
      AND RequestedBy<>@actor AND ProvisionedBy<>@actor
      AND ApprovedFinalizerLogin<>ApprovedOwnerLogin
      AND ApprovedFinalizerLogin<>ApprovedProvisionerLogin
      AND FinalizedUtc IS NULL;
    IF @old IS NULL OR @old NOT IN (''VERIFYING'',''FINALIZING'')
        THROW 51422,''TENANT_FINALIZER_STATE_DENIED'',1;
    IF @old=''FINALIZING'' AND EXISTS (SELECT 1 FROM dbo.PreprodTenantRegistry
        WHERE TenantId=@TenantId AND FinalizationLeaseUntilUtc>=SYSUTCDATETIME())
        THROW 51423,''TENANT_FINALIZER_LEASE_BUSY'',1;
    UPDATE dbo.PreprodTenantRegistry
      SET State=''FINALIZING'',FinalizationOperationId=COALESCE(FinalizationOperationId,NEWID()),
          FinalizationLeaseToken=@LeaseToken,
          FinalizationLeaseUntilUtc=DATEADD(minute,5,SYSUTCDATETIME()),
          UpdatedUtc=SYSUTCDATETIME()
    WHERE TenantId=@TenantId;
    INSERT dbo.PreprodTenantLifecycleEvents
        (TenantId,ActionCode,FromState,ToState,ActorLogin,OperationId,SecurityEpoch,BindingId)
    SELECT TenantId,''FINALIZE_CLAIM'',@old,State,@actor,FinalizationOperationId,
        SecurityEpoch,BindingId FROM dbo.PreprodTenantRegistry WHERE TenantId=@TenantId;
    COMMIT TRANSACTION;
END;
');
    EXEC(N'
CREATE PROCEDURE dbo.cp_CompleteFinalization
    @TenantId uniqueidentifier, @LeaseToken uniqueidentifier,
    @ExpectedOperationId uniqueidentifier, @ExpectedBindingId uniqueidentifier,
    @ExpectedEpoch bigint
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    DECLARE @actor sysname=SUSER_SNAME(), @db sysname,@fin sysname,@owner sysname,
        @userCount bigint=1;
    IF NOT EXISTS (SELECT 1 FROM dbo.PreprodTenantOperators
        WHERE LoginName=@actor AND Enabled=1 AND CanRetireFinalizer=1
          AND CanFinalize=0 AND CanRequest=0 AND CanApprove=0
          AND CanProvision=0 AND CanVerify=0 AND CanSuspend=0)
        THROW 51426,''TENANT_RETIRER_OPERATOR_DENIED'',1;
    BEGIN TRANSACTION;
    SELECT @db=DatabaseName,@fin=ApprovedFinalizerLogin,@owner=ApprovedOwnerLogin
      FROM dbo.PreprodTenantRegistry WITH(UPDLOCK,HOLDLOCK)
      WHERE TenantId=@TenantId AND State=''FINALIZING''
        AND ApprovedRetirerLogin=@actor
        AND FinalizationLeaseToken=@LeaseToken
        AND FinalizationOperationId=@ExpectedOperationId
        AND BindingId=@ExpectedBindingId AND SecurityEpoch=@ExpectedEpoch;
    IF @db IS NULL OR @fin IS NULL OR @owner IS NULL
        THROW 51427,''TENANT_RETIRER_BINDING_DENIED'',1;
    -- This is an independent DBA-owned SQL authority check, not CLI self-attestation.
    IF NOT EXISTS (SELECT 1 FROM sys.server_principals
        WHERE name=@fin AND type=''S'' AND is_disabled=1)
        OR EXISTS (SELECT 1 FROM sys.server_permissions AS p
            JOIN sys.server_principals AS sp ON sp.principal_id=p.grantee_principal_id
            WHERE sp.name=@fin AND p.permission_name=''IMPERSONATE''
              AND p.major_id=SUSER_ID(@owner) AND p.state IN (''G'',''W''))
        OR EXISTS (SELECT 1 FROM sys.dm_exec_sessions
            WHERE login_name=@fin OR original_login_name=@fin)
        THROW 51428,''TENANT_FINALIZER_PRIVILEGE_STILL_ACTIVE'',1;
    DECLARE @check nvarchar(max)=N''SELECT @count=COUNT_BIG(*) FROM ''
        +QUOTENAME(@db)+N''.sys.database_principals WHERE name=@login'';
    EXEC sys.sp_executesql @check,
        N''@login sysname,@count bigint OUTPUT'',@login=@fin,@count=@userCount OUTPUT;
    IF @userCount<>0
        THROW 51428,''TENANT_FINALIZER_DB_PRINCIPAL_PRESENT'',1;
    UPDATE r SET State=''VERIFYING'',FinalizationLeaseToken=NULL,
        FinalizationLeaseUntilUtc=NULL,FinalizedBy=r.ApprovedFinalizerLogin,
        FinalizedUtc=SYSUTCDATETIME(),
        FinalizedEpoch=SecurityEpoch,FinalizedBindingId=BindingId,
        FinalizerRetiredBy=@actor,FinalizerRetiredUtc=SYSUTCDATETIME(),
        UpdatedUtc=SYSUTCDATETIME()
    FROM dbo.PreprodTenantRegistry AS r
    WHERE r.TenantId=@TenantId AND r.State=''FINALIZING''
      AND r.ApprovedRetirerLogin=@actor AND r.FinalizationLeaseToken=@LeaseToken
      AND r.FinalizationLeaseUntilUtc>SYSUTCDATETIME()
      AND r.FinalizationOperationId=@ExpectedOperationId
      AND r.BindingId=@ExpectedBindingId AND r.SecurityEpoch=@ExpectedEpoch
      AND r.ApprovedOwnerLogin IS NOT NULL AND r.ApprovedProvisionerLogin IS NOT NULL
      AND EXISTS (SELECT 1 FROM sys.databases d WHERE d.name=r.DatabaseName
          AND d.owner_sid=SUSER_SID(r.ApprovedOwnerLogin)
          AND d.owner_sid<>SUSER_SID(r.ApprovedProvisionerLogin));
    IF @@ROWCOUNT<>1 THROW 51424,''TENANT_FINALIZER_COMPLETION_FENCED'',1;
    INSERT dbo.PreprodTenantLifecycleEvents
        (TenantId,ActionCode,FromState,ToState,ActorLogin,OperationId,SecurityEpoch,BindingId)
    SELECT TenantId,''FINALIZED'',''FINALIZING'',State,@actor,FinalizationOperationId,
       SecurityEpoch,BindingId FROM dbo.PreprodTenantRegistry WHERE TenantId=@TenantId;
    COMMIT TRANSACTION;
END;
');
    EXEC(N'
CREATE PROCEDURE dbo.cp_FailFinalization
    @TenantId uniqueidentifier, @LeaseToken uniqueidentifier, @ErrorCode varchar(32)
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    DECLARE @actor sysname=SUSER_SNAME();
    IF NOT EXISTS (SELECT 1 FROM dbo.PreprodTenantOperators
        WHERE LoginName=@actor AND Enabled=1 AND CanFinalize=1
          AND CanRequest=0 AND CanApprove=0 AND CanProvision=0 AND CanVerify=0 AND CanSuspend=0)
        THROW 51421,''TENANT_FINALIZER_OPERATOR_DENIED'',1;
    IF @ErrorCode IS NULL OR LEN(@ErrorCode) NOT BETWEEN 3 AND 32
       OR @ErrorCode COLLATE Latin1_General_100_BIN2 LIKE ''%[^A-Z0-9_]''
        THROW 51402,''TENANT_FINALIZER_ERROR_CODE_INVALID'',1;
    BEGIN TRANSACTION;
    UPDATE dbo.PreprodTenantRegistry SET State=''VERIFYING'',
        FinalizationLeaseToken=NULL,FinalizationLeaseUntilUtc=NULL,
        UpdatedUtc=SYSUTCDATETIME()
    WHERE TenantId=@TenantId AND State=''FINALIZING''
        AND ApprovedFinalizerLogin=@actor AND FinalizationLeaseToken=@LeaseToken
        AND FinalizationLeaseUntilUtc>SYSUTCDATETIME();
    IF @@ROWCOUNT<>1 THROW 51425,''TENANT_FINALIZER_FAILURE_FENCED'',1;
    INSERT dbo.PreprodTenantLifecycleEvents
        (TenantId,ActionCode,FromState,ToState,ActorLogin,OperationId,
            SecurityEpoch,BindingId,EvidenceRef)
    SELECT TenantId,''FINALIZE_FAILED'',''FINALIZING'',State,@actor,FinalizationOperationId,
        SecurityEpoch,BindingId,@ErrorCode FROM dbo.PreprodTenantRegistry WHERE TenantId=@TenantId;
    COMMIT TRANSACTION;
END;
');
    EXEC(N'
CREATE PROCEDURE dbo.cp_RecordVerification
    @TenantId uniqueidentifier, @ExpectedRowVer binary(8),
    @ObservedBindingId uniqueidentifier, @WitnessHash binary(32)
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    DECLARE @actor sysname=SUSER_SNAME(), @old varchar(32);
    IF NOT EXISTS (SELECT 1 FROM dbo.PreprodTenantOperators
        WHERE LoginName=@actor AND Enabled=1 AND CanVerify=1)
        THROW 51401, ''TENANT_OPERATOR_DENIED'', 1;
    BEGIN TRANSACTION;
    SELECT @old=State FROM dbo.PreprodTenantRegistry AS r WITH (UPDLOCK,HOLDLOCK)
    WHERE r.TenantId=@TenantId AND r.RowVer=@ExpectedRowVer AND r.BindingId=@ObservedBindingId
        AND r.ProvisionedBy<>@actor
        AND @WitnessHash IS NOT NULL AND @WitnessHash<>CONVERT(binary(32),0x)
        AND r.ApprovedOwnerLogin IS NOT NULL AND r.ApprovedProvisionerLogin IS NOT NULL
        AND r.ApprovedFinalizerLogin IS NOT NULL AND r.FinalizedBy=r.ApprovedFinalizerLogin
        AND r.FinalizedUtc IS NOT NULL AND r.FinalizedBindingId=r.BindingId
        AND r.FinalizerRetiredBy=r.ApprovedRetirerLogin
        AND r.FinalizerRetiredUtc IS NOT NULL
        AND (r.State=''REACTIVATION_VERIFYING'' OR r.FinalizedEpoch=r.SecurityEpoch)
        AND r.ApprovedOwnerLogin<>r.ApprovedProvisionerLogin
        AND SUSER_SID(r.ApprovedOwnerLogin) IS NOT NULL
        AND SUSER_SID(r.ApprovedProvisionerLogin) IS NOT NULL
        AND COALESCE(IS_SRVROLEMEMBER(''sysadmin'',r.ApprovedOwnerLogin),1)=0
        AND COALESCE(IS_SRVROLEMEMBER(''dbcreator'',r.ApprovedOwnerLogin),1)=0
        AND EXISTS (SELECT 1 FROM sys.databases AS d
            WHERE d.name=r.DatabaseName AND d.owner_sid=SUSER_SID(r.ApprovedOwnerLogin)
                AND d.owner_sid<>SUSER_SID(r.ApprovedProvisionerLogin));
    -- A missing row or failed owner/rowversion/binding predicate yields NULL.
    -- SQL IF (NULL NOT IN (...)) is UNKNOWN, not TRUE: explicitly fence NULL.
    IF @old IS NULL OR @old NOT IN (''VERIFYING'',''REACTIVATION_VERIFYING'')
        THROW 51410, ''TENANT_VERIFICATION_FENCED'', 1;
    UPDATE dbo.PreprodTenantRegistry SET VerifiedBy=@actor, VerificationHash=@WitnessHash,
        VerifiedUtc=SYSUTCDATETIME(), VerifiedEpoch=SecurityEpoch,
        VerifiedBindingId=BindingId,UpdatedUtc=SYSUTCDATETIME()
    WHERE TenantId=@TenantId;
    INSERT dbo.PreprodTenantLifecycleEvents(TenantId,ActionCode,FromState,ToState,ActorLogin,
        OperationId,SecurityEpoch,BindingId)
    SELECT TenantId,''VERIFY'',@old,State,@actor,OperationId,SecurityEpoch,BindingId
    FROM dbo.PreprodTenantRegistry WHERE TenantId=@TenantId;
    COMMIT TRANSACTION;
END;');
    EXEC(N'
CREATE PROCEDURE dbo.cp_TenantTransition
    @TenantId uniqueidentifier, @Action varchar(32),
    @ExpectedRowVer binary(8)=NULL, @EvidenceRef nvarchar(200)=NULL
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    DECLARE @actor sysname=SUSER_SNAME(), @old varchar(32),
        @new varchar(32), @epoch bigint, @binding uniqueidentifier,
        @verifiedBy sysname, @approvedBy sysname;
    IF NOT EXISTS (SELECT 1 FROM dbo.PreprodTenantOperators
        WHERE LoginName=@actor AND Enabled=1 AND (
          @Action IN (''ACTIVATE'',''START_REACTIVATION'',''DECOMMISSION'') AND CanApprove=1
          OR @Action IN (''SUSPEND'',''REVOKE'') AND CanSuspend=1
          OR @Action IN (''ABORT_REACTIVATION'',''COMPLETE_DECOMMISSION'') AND CanVerify=1))
        THROW 51401, ''TENANT_OPERATOR_DENIED'', 1;
    BEGIN TRANSACTION;
    SELECT @old=State,@epoch=SecurityEpoch,@binding=BindingId,@verifiedBy=VerifiedBy,
        @approvedBy=CASE WHEN State=''REACTIVATION_VERIFYING''
            THEN ReactivationApprovedBy ELSE ApprovedBy END
    FROM dbo.PreprodTenantRegistry WITH (UPDLOCK,HOLDLOCK) WHERE TenantId=@TenantId;
    IF @old IS NULL THROW 51411, ''TENANT_UNKNOWN'', 1;
    IF @Action=''ACTIVATE''
    BEGIN
        IF @old NOT IN (''VERIFYING'',''REACTIVATION_VERIFYING'')
            OR @ExpectedRowVer IS NULL
            OR NOT EXISTS (SELECT 1 FROM dbo.PreprodTenantRegistry
                WHERE TenantId=@TenantId AND RowVer=@ExpectedRowVer
                AND VerifiedEpoch=SecurityEpoch AND VerifiedBindingId=BindingId
                AND VerifiedBy IS NOT NULL AND VerifiedBy<>@actor
                AND VerifiedBy<>@approvedBy
                AND VerificationHash IS NOT NULL AND VerifiedUtc>DATEADD(minute,-5,SYSUTCDATETIME())
                AND ApprovedOwnerLogin IS NOT NULL AND ApprovedProvisionerLogin IS NOT NULL
                AND ApprovedFinalizerLogin IS NOT NULL AND FinalizedBy=ApprovedFinalizerLogin
                AND FinalizedUtc IS NOT NULL AND FinalizedBindingId=BindingId
                AND FinalizerRetiredBy=ApprovedRetirerLogin AND FinalizerRetiredUtc IS NOT NULL
                AND (State=''REACTIVATION_VERIFYING'' OR FinalizedEpoch=SecurityEpoch)
                AND ApprovedOwnerLogin<>ApprovedProvisionerLogin
                AND SUSER_SID(ApprovedOwnerLogin) IS NOT NULL
                AND SUSER_SID(ApprovedProvisionerLogin) IS NOT NULL
                AND EXISTS (SELECT 1 FROM sys.databases d
                    WHERE d.name=PreprodTenantRegistry.DatabaseName
                        AND d.owner_sid=SUSER_SID(ApprovedOwnerLogin)
                        AND d.owner_sid<>SUSER_SID(ApprovedProvisionerLogin)))
            THROW 51412, ''TENANT_ACTIVATION_GATE_DENIED'', 1;
        SET @new=''ACTIVE'';
    END
    ELSE IF @Action IN (''SUSPEND'',''REVOKE'')
    BEGIN
        IF @old NOT IN (''REQUESTED'',''APPROVED'',''PROVISIONING'',''FAILED_RETRYABLE'',
            ''VERIFYING'',''FINALIZING'',''ACTIVE'',''REACTIVATION_VERIFYING'',''SUSPENDED'')
            THROW 51413, ''TENANT_SUSPEND_STATE_DENIED'', 1;
        SET @new=''SUSPENDED'';
    END
    ELSE IF @Action=''START_REACTIVATION''
    BEGIN
        IF @old<>''SUSPENDED'' OR @EvidenceRef IS NULL OR LEN(TRIM(@EvidenceRef))<3
            THROW 51414, ''TENANT_REACTIVATION_APPROVAL_DENIED'', 1;
        SET @new=''REACTIVATION_VERIFYING'';
    END
    ELSE IF @Action=''ABORT_REACTIVATION''
    BEGIN
        IF @old<>''REACTIVATION_VERIFYING'' THROW 51415, ''TENANT_REACTIVATION_FENCED'', 1;
        SET @new=''SUSPENDED'';
    END
    ELSE IF @Action=''DECOMMISSION''
    BEGIN
        IF @old NOT IN (''ACTIVE'',''SUSPENDED'')
            OR @EvidenceRef IS NULL OR LEN(TRIM(@EvidenceRef))<3
            THROW 51416, ''TENANT_DECOMMISSION_APPROVAL_DENIED'', 1;
        SET @new=''DECOMMISSIONING'';
    END
    ELSE IF @Action=''COMPLETE_DECOMMISSION''
    BEGIN
        IF @old<>''DECOMMISSIONING''
            OR @EvidenceRef IS NULL OR LEN(TRIM(@EvidenceRef))<3
            OR NOT EXISTS (SELECT 1 FROM dbo.PreprodTenantRegistry
                WHERE TenantId=@TenantId AND DecommissionApprovedBy<>@actor)
            THROW 51417, ''TENANT_ARCHIVE_VERIFICATION_DENIED'', 1;
        SET @new=''DECOMMISSIONED'';
    END
    ELSE THROW 51418, ''TENANT_ACTION_INVALID'', 1;

    UPDATE dbo.PreprodTenantRegistry SET State=@new,SecurityEpoch=SecurityEpoch+1,
        LeaseToken=NULL,LeaseUntilUtc=NULL,
        FinalizationLeaseToken=NULL,FinalizationLeaseUntilUtc=NULL,
        VerifiedBy=CASE WHEN @Action=''ACTIVATE'' THEN VerifiedBy ELSE NULL END,
        VerifiedUtc=CASE WHEN @Action=''ACTIVATE'' THEN VerifiedUtc ELSE NULL END,
        VerificationHash=CASE WHEN @Action=''ACTIVATE'' THEN VerificationHash ELSE NULL END,
        VerifiedEpoch=CASE WHEN @Action=''ACTIVATE'' THEN VerifiedEpoch ELSE NULL END,
        VerifiedBindingId=CASE WHEN @Action=''ACTIVATE'' THEN VerifiedBindingId ELSE NULL END,
        ReactivationApprovedBy=CASE WHEN @Action=''START_REACTIVATION'' THEN @actor
            ELSE ReactivationApprovedBy END,
        DecommissionApprovedBy=CASE WHEN @Action=''DECOMMISSION'' THEN @actor
            ELSE DecommissionApprovedBy END,
        EvidenceRef=COALESCE(@EvidenceRef,EvidenceRef), UpdatedUtc=SYSUTCDATETIME()
    WHERE TenantId=@TenantId;
    INSERT dbo.PreprodTenantLifecycleEvents(TenantId,ActionCode,FromState,ToState,ActorLogin,
        OperationId,SecurityEpoch,BindingId,EvidenceRef)
    SELECT TenantId,@Action,@old,@new,@actor,OperationId,SecurityEpoch,BindingId,@EvidenceRef
    FROM dbo.PreprodTenantRegistry WHERE TenantId=@TenantId;
    COMMIT TRANSACTION;
END;');


    EXEC(N'
CREATE PROCEDURE dbo.cp_GetTenant @TenantId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM dbo.PreprodTenantOperators
        WHERE LoginName=SUSER_SNAME() AND Enabled=1 AND
        (CanRequest=1 OR CanApprove=1 OR CanProvision=1 OR CanFinalize=1 OR CanRetireFinalizer=1 OR CanVerify=1 OR CanSuspend=1))
        THROW 51401, ''TENANT_OPERATOR_DENIED'', 1;
    IF EXISTS (SELECT 1 FROM dbo.PreprodTenantOperators
        WHERE LoginName=SUSER_SNAME() AND Enabled=1 AND CanFinalize=1)
      AND NOT EXISTS (SELECT 1 FROM dbo.PreprodTenantRegistry
        WHERE TenantId=@TenantId AND ApprovedFinalizerLogin=SUSER_SNAME())
        THROW 51421, ''TENANT_FINALIZER_PEER_METADATA_DENIED'', 1;
    IF EXISTS (SELECT 1 FROM dbo.PreprodTenantOperators
        WHERE LoginName=SUSER_SNAME() AND Enabled=1 AND CanRetireFinalizer=1)
      AND NOT EXISTS (SELECT 1 FROM dbo.PreprodTenantRegistry
        WHERE TenantId=@TenantId AND ApprovedRetirerLogin=SUSER_SNAME())
        THROW 51429, ''TENANT_RETIRER_PEER_METADATA_DENIED'', 1;
    SELECT TenantId,DatabaseName,BindingId,BindingVersion,SecurityEpoch,State,
        RowVer,OperationId,LeaseToken,LeaseUntilUtc,VerifiedBy,VerifiedUtc,
        ApprovedOwnerLogin,ApprovedProvisionerLogin,ApprovedFinalizerLogin,ApprovedRetirerLogin,
        FinalizationOperationId,FinalizationLeaseToken,FinalizationLeaseUntilUtc,
        FinalizedBy,FinalizedUtc,FinalizedEpoch,FinalizedBindingId,
        FinalizerRetiredBy,FinalizerRetiredUtc
    FROM dbo.PreprodTenantRegistry WHERE TenantId=@TenantId;
END;');
    EXEC(N'
CREATE PROCEDURE dbo.cp_GetPeerForVerification @TenantId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM dbo.PreprodTenantOperators
        WHERE LoginName=SUSER_SNAME() AND Enabled=1 AND CanVerify=1)
        THROW 51401, ''TENANT_VERIFIER_OPERATOR_DENIED'', 1;
    SELECT TOP (1) DatabaseName
    FROM dbo.PreprodTenantRegistry
    WHERE TenantId<>@TenantId AND State IN (''VERIFYING'',''FINALIZING'',''ACTIVE'',''SUSPENDED'')
    ORDER BY TenantId;
END;');

        INSERT dbo.__PreprodTenantRegistrySchemaHistory (SchemaVersion,AppliedUtc)
            VALUES(N'B4_20261118_001',SYSUTCDATETIME());
    END;
    IF (SELECT COUNT(*) FROM sys.tables WHERE schema_id=SCHEMA_ID(N'dbo')
        AND name IN (N'__PreprodTenantRegistrySchemaHistory',N'PreprodTenantOperators',
                     N'PreprodTenantRegistry',N'PreprodTenantLifecycleEvents'))<>4
       OR (SELECT COUNT(*) FROM sys.procedures WHERE schema_id=SCHEMA_ID(N'dbo')
        AND name IN (N'cp_RequestTenant',N'cp_ApproveTenant',N'cp_ClaimProvision',
                     N'cp_CompleteProvision',N'cp_FailProvision',N'cp_RecordVerification',
                     N'cp_TenantTransition',N'cp_GetTenant',N'cp_GetPeerForVerification',
                     N'cp_ClaimFinalization',N'cp_CompleteFinalization',N'cp_FailFinalization'))<>12
       OR NOT EXISTS (SELECT 1 FROM sys.columns
            WHERE object_id=OBJECT_ID(N'dbo.PreprodTenantRegistry') AND name=N'RowVer')
        THROW 51400,'TENANT_REGISTRY_SCHEMA_DRIFT_DENIED',1;
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
