-- APP-02B.5 B5_20261118_001. SOURCE ONLY, never API/Worker boot DDL.
-- Run ONLY on a DBA-approved, independently recoverable revocation-fence
-- database. Do not restore it from a tenant or Control Plane PITR snapshot.
-- This schema has NO implicit users, tenant roles, permissions or activation.
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRY
  BEGIN TRANSACTION;
  IF DB_NAME() IN (N'master',N'model',N'msdb',N'tempdb')
      THROW 51600,N'B5_SYSTEM_DB_DENIED',1;
  DECLARE @schemaLock int;
  EXEC @schemaLock=sys.sp_getapplock @Resource=N'FMCG_PREPROD_B5_SCHEMA',
       @LockMode=N'Exclusive',@LockOwner=N'Transaction',@LockTimeout=0;
  IF @schemaLock < 0 THROW 51600,N'B5_SCHEMA_LOCKED',1;
  IF OBJECT_ID(N'dbo.__B5SchemaHistory',N'U') IS NULL
  BEGIN
      IF OBJECT_ID(N'dbo.B5TenantFence',N'U') IS NOT NULL
          OR OBJECT_ID(N'dbo.B5Membership',N'U') IS NOT NULL
          OR OBJECT_ID(N'dbo.B5SecurityEvent',N'U') IS NOT NULL
          THROW 51600,N'B5_PARTIAL_SCHEMA_DENIED',1;
      CREATE TABLE dbo.__B5SchemaHistory
      (SchemaVersion nvarchar(64) NOT NULL PRIMARY KEY, AppliedUtc datetime2(7) NOT NULL);
  END;
  IF EXISTS (SELECT 1 FROM dbo.__B5SchemaHistory
             WHERE SchemaVersion<>N'B5_20261118_001')
      THROW 51600,N'B5_UNKNOWN_SCHEMA_DENIED',1;
  IF NOT EXISTS (SELECT 1 FROM dbo.__B5SchemaHistory
                 WHERE SchemaVersion=N'B5_20261118_001')
  BEGIN
      IF OBJECT_ID(N'dbo.B5TenantFence',N'U') IS NOT NULL
          OR OBJECT_ID(N'dbo.B5Membership',N'U') IS NOT NULL
          OR OBJECT_ID(N'dbo.B5SecurityEvent',N'U') IS NOT NULL
          THROW 51600,N'B5_EXISTING_SCHEMA_DENIED',1;
      CREATE TABLE dbo.B5TenantFence
      (
          TenantId uniqueidentifier NOT NULL PRIMARY KEY,
          BindingId uniqueidentifier NOT NULL,
          RegistryEpoch bigint NOT NULL CHECK(RegistryEpoch>0),
          FenceEpoch bigint NOT NULL CHECK(FenceEpoch>0),
          State nvarchar(16) NOT NULL CHECK(State IN(N'ACTIVE',N'SUSPENDED',N'REVOKED')),
          UpdatedUtc datetime2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
          RowVer rowversion NOT NULL
      );
      CREATE TABLE dbo.B5Membership
      (
          TenantId uniqueidentifier NOT NULL,
          ActorId uniqueidentifier NOT NULL,
          MembershipEpoch bigint NOT NULL CHECK(MembershipEpoch>0),
          RevokedUtc datetime2(7) NULL,
          CONSTRAINT PK_B5Membership PRIMARY KEY(TenantId,ActorId),
          CONSTRAINT FK_B5Membership_Tenant FOREIGN KEY(TenantId)
              REFERENCES dbo.B5TenantFence(TenantId)
      );
      CREATE TABLE dbo.B5Role
      (
          TenantId uniqueidentifier NOT NULL,
          RoleId uniqueidentifier NOT NULL,
          Code nvarchar(96) NOT NULL,
          Enabled bit NOT NULL DEFAULT(0),
          CONSTRAINT PK_B5Role PRIMARY KEY(TenantId,RoleId),
          CONSTRAINT UQ_B5Role_Code UNIQUE(TenantId,Code),
          CONSTRAINT FK_B5Role_Tenant FOREIGN KEY(TenantId)
              REFERENCES dbo.B5TenantFence(TenantId)
      );
      CREATE TABLE dbo.B5ActorRole
      (
          TenantId uniqueidentifier NOT NULL,
          ActorId uniqueidentifier NOT NULL,
          RoleId uniqueidentifier NOT NULL,
          CONSTRAINT PK_B5ActorRole PRIMARY KEY(TenantId,ActorId,RoleId),
          CONSTRAINT FK_B5ActorRole_Membership FOREIGN KEY(TenantId,ActorId)
              REFERENCES dbo.B5Membership(TenantId,ActorId),
          CONSTRAINT FK_B5ActorRole_Role FOREIGN KEY(TenantId,RoleId)
              REFERENCES dbo.B5Role(TenantId,RoleId)
      );
      CREATE TABLE dbo.B5ActionCatalog
      (
          PermissionCode nvarchar(96) NOT NULL PRIMARY KEY,
          ActionKind varchar(16) NOT NULL CHECK(ActionKind IN ('READ','WRITE','APPROVAL'))
      );
      CREATE TABLE dbo.B5RolePermission
      (
          TenantId uniqueidentifier NOT NULL,
          RoleId uniqueidentifier NOT NULL,
          PermissionCode nvarchar(96) NOT NULL,
          ResourceKey nvarchar(160) NOT NULL,
          CONSTRAINT PK_B5RolePermission PRIMARY KEY
              (TenantId,RoleId,PermissionCode,ResourceKey),
          CONSTRAINT FK_B5RolePermission_Role FOREIGN KEY(TenantId,RoleId)
              REFERENCES dbo.B5Role(TenantId,RoleId),
          CONSTRAINT FK_B5RolePermission_Catalog FOREIGN KEY(PermissionCode)
              REFERENCES dbo.B5ActionCatalog(PermissionCode)
      );
      CREATE TABLE dbo.B5ApprovalSource
      (
          TenantId uniqueidentifier NOT NULL,
          ApprovalSourceId uniqueidentifier NOT NULL,
          PermissionCode nvarchar(96) NOT NULL,
          ResourceKey nvarchar(160) NOT NULL,
          MakerId uniqueidentifier NOT NULL,
          InitiatorId uniqueidentifier NOT NULL,
          SourceHash binary(32) NOT NULL,
          CreatedUtc datetime2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
          CONSTRAINT PK_B5ApprovalSource PRIMARY KEY(TenantId,ApprovalSourceId),
          CONSTRAINT FK_B5ApprovalSource_Tenant FOREIGN KEY(TenantId)
              REFERENCES dbo.B5TenantFence(TenantId),
          CONSTRAINT FK_B5ApprovalSource_Catalog FOREIGN KEY(PermissionCode)
              REFERENCES dbo.B5ActionCatalog(PermissionCode)
      );
      CREATE TABLE dbo.B5ApprovalDecision
      (
          TenantId uniqueidentifier NOT NULL,
          ApprovalSourceId uniqueidentifier NOT NULL,
          CheckerId uniqueidentifier NOT NULL,
          SourceHash binary(32) NOT NULL,
          FenceEpoch bigint NOT NULL,
          CheckedUtc datetime2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
          CONSTRAINT PK_B5ApprovalDecision PRIMARY KEY(TenantId,ApprovalSourceId),
          CONSTRAINT FK_B5ApprovalDecision_Source FOREIGN KEY(TenantId,ApprovalSourceId)
              REFERENCES dbo.B5ApprovalSource(TenantId,ApprovalSourceId)
      );
      EXEC(N'CREATE TRIGGER dbo.b5_ImmutableApprovalSource
          ON dbo.B5ApprovalSource AFTER UPDATE, DELETE AS
          BEGIN THROW 51650,N''B5_IMMUTABLE_APPROVAL_SOURCE'',1; END');
      EXEC(N'CREATE TRIGGER dbo.b5_ImmutableApprovalDecision
          ON dbo.B5ApprovalDecision AFTER UPDATE, DELETE AS
          BEGIN THROW 51651,N''B5_IMMUTABLE_APPROVAL_DECISION'',1; END');
      CREATE TABLE dbo.B5SecurityEvent
      (
          EventId bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
          TenantId uniqueidentifier NOT NULL,
          ActorId uniqueidentifier NULL,
          ActionCode nvarchar(32) NOT NULL,
          FenceEpoch bigint NOT NULL,
          SqlActor sysname NOT NULL,
          EvidenceRef nvarchar(160) NOT NULL,
          EventUtc datetime2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
          CONSTRAINT FK_B5SecurityEvent_Tenant FOREIGN KEY(TenantId)
              REFERENCES dbo.B5TenantFence(TenantId)
      );
      CREATE ROLE b5_runtime;
      CREATE ROLE b5_revoker;
    EXEC(N'CREATE PROCEDURE dbo.b5_AssertAuthority
    @TenantId uniqueidentifier, @ResourceTenantId uniqueidentifier,
    @ActorId uniqueidentifier, @SessionId uniqueidentifier,
    @BindingId uniqueidentifier, @RegistryEpoch bigint,
    @FenceEpoch bigint, @MembershipEpoch bigint,
    @PermissionCode nvarchar(96), @ResourceKey nvarchar(160),
    @ApprovalSourceId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF @@TRANCOUNT < 1 OR ISNULL(IS_ROLEMEMBER(N''b5_runtime''),0) <> 1
        THROW 51601, N''B5_TRANSACTION_OR_PRINCIPAL_DENIED'', 1;
    IF @TenantId IS NULL OR @TenantId = ''00000000-0000-0000-0000-000000000000''
       OR @TenantId <> @ResourceTenantId OR @ActorId IS NULL OR @SessionId IS NULL
       OR @BindingId IS NULL OR @RegistryEpoch < 1 OR @FenceEpoch < 1
       OR @MembershipEpoch < 1 OR NULLIF(LTRIM(RTRIM(@PermissionCode)),N'''') IS NULL
       OR NULLIF(LTRIM(RTRIM(@ResourceKey)),N'''') IS NULL
        THROW 51602, N''B5_BOUNDARY_DENIED'', 1;
    -- UPDLOCK/HOLDLOCK retained until the caller''s protected effect COMMIT.
    -- Revoke/suspend must update the same row, so they cannot overtake it.
    IF NOT EXISTS (SELECT 1 FROM dbo.B5TenantFence WITH (UPDLOCK,HOLDLOCK)
       WHERE TenantId=@TenantId AND BindingId=@BindingId
         AND RegistryEpoch=@RegistryEpoch AND FenceEpoch=@FenceEpoch
         AND State=N''ACTIVE'')
        THROW 51603, N''B5_FENCE_STALE_OR_DISABLED'', 1;
    IF NOT EXISTS (SELECT 1 FROM dbo.B5Membership WITH (UPDLOCK,HOLDLOCK)
       WHERE TenantId=@TenantId AND ActorId=@ActorId AND RevokedUtc IS NULL
         AND MembershipEpoch=@MembershipEpoch)
        THROW 51604, N''B5_MEMBERSHIP_DENIED'', 1;
    IF NOT EXISTS (
        SELECT 1 FROM dbo.B5ActorRole ar WITH (HOLDLOCK)
          INNER JOIN dbo.B5Role r WITH (HOLDLOCK)
             ON r.TenantId=ar.TenantId AND r.RoleId=ar.RoleId
          INNER JOIN dbo.B5RolePermission p WITH (HOLDLOCK)
             ON p.TenantId=r.TenantId AND p.RoleId=r.RoleId
        WHERE ar.TenantId=@TenantId AND ar.ActorId=@ActorId
          AND r.Enabled=1 AND p.PermissionCode=@PermissionCode
          AND p.ResourceKey=@ResourceKey)
        THROW 51605, N''B5_PERMISSION_DENIED'', 1;

    DECLARE @kind varchar(16);
    SELECT @kind=ActionKind FROM dbo.B5ActionCatalog WITH (HOLDLOCK)
        WHERE PermissionCode=@PermissionCode;
    IF @kind IS NULL THROW 51606,N''B5_UNCLASSIFIED_ACTION_DENIED'',1;
    IF @kind=''APPROVAL''
    BEGIN
        IF @ApprovalSourceId IS NULL THROW 51607,N''B5_APPROVAL_SOURCE_REQUIRED'',1;
        INSERT dbo.B5ApprovalDecision
          (TenantId,ApprovalSourceId,CheckerId,SourceHash,FenceEpoch)
        SELECT s.TenantId,s.ApprovalSourceId,@ActorId,s.SourceHash,@FenceEpoch
        FROM dbo.B5ApprovalSource s WITH (UPDLOCK,HOLDLOCK)
        WHERE s.TenantId=@TenantId AND s.ApprovalSourceId=@ApprovalSourceId
          AND s.PermissionCode=@PermissionCode AND s.ResourceKey=@ResourceKey
          AND s.MakerId<>@ActorId AND s.InitiatorId<>@ActorId
          AND NOT EXISTS (
              SELECT 1 FROM dbo.B5ApprovalDecision d WITH (UPDLOCK,HOLDLOCK)
              WHERE d.TenantId=s.TenantId AND d.ApprovalSourceId=s.ApprovalSourceId);
        IF @@ROWCOUNT <> 1 THROW 51608,N''B5_APPROVAL_DENIED'',1;
    END
    ELSE IF @ApprovalSourceId IS NOT NULL
        THROW 51609,N''B5_UNEXPECTED_APPROVAL_SOURCE_DENIED'',1;
END');
    EXEC(N'CREATE PROCEDURE dbo.b5_RevokeMember
    @TenantId uniqueidentifier, @ActorId uniqueidentifier,
    @ExpectedFenceEpoch bigint, @EvidenceRef nvarchar(160)
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    IF ISNULL(IS_ROLEMEMBER(N''b5_revoker''),0) <> 1 OR NULLIF(LTRIM(RTRIM(@EvidenceRef)),N'''') IS NULL
       OR @TenantId IS NULL OR @ActorId IS NULL
        THROW 51610, N''B5_REVOKER_DENIED'', 1;
    BEGIN TRANSACTION;
    UPDATE dbo.B5TenantFence WITH (UPDLOCK)
      SET FenceEpoch=FenceEpoch+1, UpdatedUtc=SYSUTCDATETIME()
    WHERE TenantId=@TenantId AND FenceEpoch=@ExpectedFenceEpoch AND State=N''ACTIVE'';
    IF @@ROWCOUNT <> 1 THROW 51611, N''B5_REVOKE_STALE'', 1;
    UPDATE dbo.B5Membership
      SET MembershipEpoch=MembershipEpoch+1, RevokedUtc=SYSUTCDATETIME()
    WHERE TenantId=@TenantId AND ActorId=@ActorId AND RevokedUtc IS NULL;
    IF @@ROWCOUNT <> 1 THROW 51612, N''B5_REVOKE_MEMBER_MISSING'', 1;
    INSERT dbo.B5SecurityEvent(TenantId,ActorId,ActionCode,FenceEpoch,SqlActor,EvidenceRef)
      VALUES(@TenantId,@ActorId,N''REVOKE_MEMBER'',@ExpectedFenceEpoch+1,SUSER_SNAME(),@EvidenceRef);
    COMMIT TRANSACTION;
END');
    EXEC(N'CREATE PROCEDURE dbo.b5_SuspendTenant
    @TenantId uniqueidentifier, @ExpectedFenceEpoch bigint,
    @EvidenceRef nvarchar(160)
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    IF ISNULL(IS_ROLEMEMBER(N''b5_revoker''),0) <> 1 OR NULLIF(LTRIM(RTRIM(@EvidenceRef)),N'''') IS NULL
        THROW 51620, N''B5_REVOKER_DENIED'', 1;
    BEGIN TRANSACTION;
    UPDATE dbo.B5TenantFence WITH (UPDLOCK)
       SET FenceEpoch=FenceEpoch+1, State=N''SUSPENDED'',UpdatedUtc=SYSUTCDATETIME()
    WHERE TenantId=@TenantId AND FenceEpoch=@ExpectedFenceEpoch AND State=N''ACTIVE'';
    IF @@ROWCOUNT <> 1 THROW 51621, N''B5_SUSPEND_STALE'', 1;
    INSERT dbo.B5SecurityEvent(TenantId,ActorId,ActionCode,FenceEpoch,SqlActor,EvidenceRef)
      VALUES(@TenantId,NULL,N''SUSPEND'',@ExpectedFenceEpoch+1,SUSER_SNAME(),@EvidenceRef);
    COMMIT TRANSACTION;
END');
      GRANT EXECUTE ON OBJECT::dbo.b5_AssertAuthority TO b5_runtime;
      GRANT EXECUTE ON OBJECT::dbo.b5_RevokeMember TO b5_revoker;
      GRANT EXECUTE ON OBJECT::dbo.b5_SuspendTenant TO b5_revoker;
      INSERT dbo.__B5SchemaHistory(SchemaVersion,AppliedUtc)
        VALUES(N'B5_20261118_001',SYSUTCDATETIME());
  END;

  -- A recorded migration version must never mask dropped/partial authority objects.
  IF (SELECT COUNT_BIG(*) FROM dbo.__B5SchemaHistory) <> 1
     OR NOT EXISTS (SELECT 1 FROM dbo.__B5SchemaHistory
                    WHERE SchemaVersion=N'B5_20261118_001')
     OR OBJECT_ID(N'dbo.B5TenantFence',N'U') IS NULL
     OR OBJECT_ID(N'dbo.B5Membership',N'U') IS NULL
     OR OBJECT_ID(N'dbo.B5Role',N'U') IS NULL
     OR OBJECT_ID(N'dbo.B5ActorRole',N'U') IS NULL
     OR OBJECT_ID(N'dbo.B5ActionCatalog',N'U') IS NULL
     OR OBJECT_ID(N'dbo.B5RolePermission',N'U') IS NULL
     OR OBJECT_ID(N'dbo.B5ApprovalSource',N'U') IS NULL
     OR OBJECT_ID(N'dbo.B5ApprovalDecision',N'U') IS NULL
     OR OBJECT_ID(N'dbo.B5SecurityEvent',N'U') IS NULL
     OR OBJECT_ID(N'dbo.b5_ImmutableApprovalSource',N'TR') IS NULL
     OR OBJECT_ID(N'dbo.b5_ImmutableApprovalDecision',N'TR') IS NULL
     OR OBJECT_ID(N'dbo.b5_AssertAuthority',N'P') IS NULL
     OR OBJECT_ID(N'dbo.b5_RevokeMember',N'P') IS NULL
     OR OBJECT_ID(N'dbo.b5_SuspendTenant',N'P') IS NULL
     OR NOT EXISTS (SELECT 1 FROM sys.database_principals
                    WHERE name=N'b5_runtime' AND type=N'R')
     OR NOT EXISTS (SELECT 1 FROM sys.database_principals
                    WHERE name=N'b5_revoker' AND type=N'R')
     THROW 51670,N'B5_SCHEMA_INTEGRITY_DENIED',1;
  COMMIT TRANSACTION;
END TRY
BEGIN CATCH
  IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
  THROW;
END CATCH;
