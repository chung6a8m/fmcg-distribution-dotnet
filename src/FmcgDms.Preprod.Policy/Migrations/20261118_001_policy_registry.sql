-- APP-02B.6: operator-applied, independent Control Plane policy store.
-- No default L0/L1/L2 values, no APP/Worker registration, no startup migration.
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF DB_NAME() IN (N'master',N'model',N'msdb',N'tempdb')
    THROW 52600, N'B6_SYSTEM_DATABASE_DENIED', 1;
BEGIN TRY
    BEGIN TRANSACTION;
    DECLARE @schemaLock int;
    EXEC @schemaLock = sys.sp_getapplock @Resource=N'APP02B6_POLICY_SCHEMA',
        @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=15000;
    IF @schemaLock < 0 THROW 52601,N'B6_SCHEMA_LOCK_DENIED',1;
    IF OBJECT_ID(N'dbo.__B6SchemaHistory',N'U') IS NULL
    BEGIN
        IF OBJECT_ID(N'dbo.B6L0Policy',N'U') IS NOT NULL OR
           OBJECT_ID(N'dbo.B6PolicyProposal',N'U') IS NOT NULL OR
           OBJECT_ID(N'dbo.B6PolicySnapshot',N'U') IS NOT NULL
            THROW 52602,N'B6_PARTIAL_SCHEMA_DENIED',1;
        CREATE TABLE dbo.__B6SchemaHistory
        (SchemaVersion nvarchar(64) NOT NULL PRIMARY KEY, AppliedUtc datetime2(7) NOT NULL);
    END;
    IF EXISTS(SELECT 1 FROM dbo.__B6SchemaHistory
              WHERE SchemaVersion<>N'B6_20261118_001')
        THROW 52603,N'B6_UNKNOWN_SCHEMA_VERSION_DENIED',1;
    IF NOT EXISTS(SELECT 1 FROM dbo.__B6SchemaHistory
                  WHERE SchemaVersion=N'B6_20261118_001')
    BEGIN
        CREATE TABLE dbo.B6L0Policy
        (
            PolicyCode nvarchar(96) NOT NULL PRIMARY KEY,
            FloorValue decimal(19,4) NOT NULL,
            CeilingValue decimal(19,4) NOT NULL,
            AllowL1 bit NOT NULL,
            AllowL2 bit NOT NULL,
            AuthorityRef nvarchar(160) NOT NULL,
            DefinitionHash binary(32) NOT NULL,
            CreatedUtc datetime2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            CONSTRAINT CK_B6L0Bounds CHECK(FloorValue <= CeilingValue),
            CONSTRAINT CK_B6L0Ref CHECK(LEN(AuthorityRef)>0)
        );
        -- Provisioned ONLY by a separate security/DBA operator, never by these SQL roles.
        CREATE TABLE dbo.B6OperatorScope
        (
            TenantId uniqueidentifier NOT NULL,
            LoginName sysname NOT NULL,
            Enabled bit NOT NULL,
            CanPropose bit NOT NULL,
            CanApprove bit NOT NULL,
            CanResolve bit NOT NULL,
            CanOpenCase bit NOT NULL,
            CanApproveCase bit NOT NULL,
            CONSTRAINT PK_B6OperatorScope PRIMARY KEY(TenantId, LoginName)
        );
        CREATE TABLE dbo.B6CaseSource
        (
            TenantId uniqueidentifier NOT NULL,
            CaseId uniqueidentifier NOT NULL,
            SourceId uniqueidentifier NOT NULL,
            SourceHash binary(32) NOT NULL,
            MakerLogin sysname NOT NULL,
            OpenedUtc datetime2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            CONSTRAINT PK_B6CaseSource PRIMARY KEY(TenantId,CaseId)
        );
        CREATE TABLE dbo.B6CaseApproval
        (
            TenantId uniqueidentifier NOT NULL,
            CaseId uniqueidentifier NOT NULL,
            CheckerLogin sysname NOT NULL,
            SourceHash binary(32) NOT NULL,
            ApprovedUtc datetime2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            CONSTRAINT PK_B6CaseApproval PRIMARY KEY(TenantId,CaseId),
            CONSTRAINT FK_B6CaseApproval_Source FOREIGN KEY(TenantId,CaseId)
                REFERENCES dbo.B6CaseSource(TenantId,CaseId)
        );
        CREATE TABLE dbo.B6PolicyProposal
        (
            TenantId uniqueidentifier NOT NULL,
            ProposalId uniqueidentifier NOT NULL,
            PolicyCode nvarchar(96) NOT NULL,
            Tier char(2) NOT NULL,
            CaseId uniqueidentifier NULL,
            ValueState varchar(5) NOT NULL,
            NumericValue decimal(19,4) NULL,
            EffectiveFromUtc datetime2(7) NOT NULL,
            EffectiveUntilUtc datetime2(7) NOT NULL,
            SourceId uniqueidentifier NOT NULL,
            SourceHash binary(32) NOT NULL,
            SourceVersion bigint NOT NULL,
            MakerLogin sysname NOT NULL,
            CreatedUtc datetime2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            CONSTRAINT PK_B6PolicyProposal PRIMARY KEY(TenantId,ProposalId),
            CONSTRAINT FK_B6PolicyProposal_L0 FOREIGN KEY(PolicyCode)
                REFERENCES dbo.B6L0Policy(PolicyCode),
            CONSTRAINT CK_B6PolicyTier CHECK(Tier IN ('L1','L2')),
            CONSTRAINT CK_B6CaseTier CHECK((Tier='L1' AND CaseId IS NULL) OR
                                          (Tier='L2' AND CaseId IS NOT NULL)),
            CONSTRAINT CK_B6ValueState CHECK((ValueState='SET' AND NumericValue IS NOT NULL)
                                           OR (ValueState='UNSET' AND NumericValue IS NULL)),
            CONSTRAINT CK_B6Effective CHECK(EffectiveFromUtc<EffectiveUntilUtc),
            CONSTRAINT CK_B6SourceVersion CHECK(SourceVersion>=1)
        );
        CREATE TABLE dbo.B6PolicyApproval
        (
            TenantId uniqueidentifier NOT NULL,
            ProposalId uniqueidentifier NOT NULL,
            CheckerLogin sysname NOT NULL,
            SourceHash binary(32) NOT NULL,
            ApprovedUtc datetime2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            CONSTRAINT PK_B6PolicyApproval PRIMARY KEY(TenantId,ProposalId),
            CONSTRAINT FK_B6PolicyApproval_Proposal FOREIGN KEY(TenantId,ProposalId)
                REFERENCES dbo.B6PolicyProposal(TenantId,ProposalId)
        );
        CREATE TABLE dbo.B6PolicySnapshot
        (
            TenantId uniqueidentifier NOT NULL,
            ProposalId uniqueidentifier NOT NULL,
            PolicyCode nvarchar(96) NOT NULL,
            Tier char(2) NOT NULL,
            CaseId uniqueidentifier NULL,
            NumericValue decimal(19,4) NOT NULL,
            EffectiveFromUtc datetime2(7) NOT NULL,
            EffectiveUntilUtc datetime2(7) NOT NULL,
            SourceId uniqueidentifier NOT NULL,
            SourceHash binary(32) NOT NULL,
            SourceVersion bigint NOT NULL,
            MakerLogin sysname NOT NULL,
            CheckerLogin sysname NOT NULL,
            ApprovedUtc datetime2(7) NOT NULL,
            CONSTRAINT PK_B6PolicySnapshot PRIMARY KEY(TenantId,ProposalId),
            CONSTRAINT FK_B6PolicySnapshot_Approval FOREIGN KEY(TenantId,ProposalId)
                REFERENCES dbo.B6PolicyApproval(TenantId,ProposalId)
        );
        CREATE INDEX IX_B6PolicySnapshot_Effective
          ON dbo.B6PolicySnapshot(TenantId,PolicyCode,Tier,EffectiveFromUtc,EffectiveUntilUtc);
        CREATE ROLE b6_maker;
        CREATE ROLE b6_checker;
        CREATE ROLE b6_case_maker;
        CREATE ROLE b6_case_checker;
        CREATE ROLE b6_reader;
        EXEC(N'CREATE TRIGGER dbo.b6_ImmutableL0 ON dbo.B6L0Policy AFTER UPDATE, DELETE AS BEGIN SET NOCOUNT ON; THROW 52680, N''B6_IMMUTABLE_HISTORY_DENIED'', 1; END');

        EXEC(N'CREATE TRIGGER dbo.b6_ImmutableCaseSource ON dbo.B6CaseSource AFTER UPDATE, DELETE AS BEGIN SET NOCOUNT ON; THROW 52680, N''B6_IMMUTABLE_HISTORY_DENIED'', 1; END');

        EXEC(N'CREATE TRIGGER dbo.b6_ImmutableCaseApproval ON dbo.B6CaseApproval AFTER UPDATE, DELETE AS BEGIN SET NOCOUNT ON; THROW 52680, N''B6_IMMUTABLE_HISTORY_DENIED'', 1; END');

        EXEC(N'CREATE TRIGGER dbo.b6_ImmutableProposal ON dbo.B6PolicyProposal AFTER UPDATE, DELETE AS BEGIN SET NOCOUNT ON; THROW 52680, N''B6_IMMUTABLE_HISTORY_DENIED'', 1; END');

        EXEC(N'CREATE TRIGGER dbo.b6_ImmutablePolicyApproval ON dbo.B6PolicyApproval AFTER UPDATE, DELETE AS BEGIN SET NOCOUNT ON; THROW 52680, N''B6_IMMUTABLE_HISTORY_DENIED'', 1; END');

        EXEC(N'CREATE TRIGGER dbo.b6_ImmutableSnapshot ON dbo.B6PolicySnapshot AFTER UPDATE, DELETE AS BEGIN SET NOCOUNT ON; THROW 52680, N''B6_IMMUTABLE_HISTORY_DENIED'', 1; END');

        EXEC(N'CREATE PROCEDURE dbo.b6_OpenCase
@TenantId uniqueidentifier, @CaseId uniqueidentifier,
@SourceId uniqueidentifier, @SourceHash binary(32)
AS
BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 IF ISNULL(IS_ROLEMEMBER(N''b6_case_maker''),0)<>1
    OR NOT EXISTS(SELECT 1 FROM dbo.B6OperatorScope
        WHERE TenantId=@TenantId AND LoginName=SUSER_SNAME()
          AND Enabled=1 AND CanOpenCase=1)
    OR @TenantId IS NULL OR @TenantId=''00000000-0000-0000-0000-000000000000''
    OR @CaseId IS NULL OR @CaseId=''00000000-0000-0000-0000-000000000000''
    OR @SourceId IS NULL OR @SourceId=''00000000-0000-0000-0000-000000000000''
    OR @SourceHash IS NULL
    THROW 52610,N''B6_CASE_OPEN_DENIED'',1;
 INSERT dbo.B6CaseSource(TenantId,CaseId,SourceId,SourceHash,MakerLogin)
 VALUES(@TenantId,@CaseId,@SourceId,@SourceHash,SUSER_SNAME());
END');

        EXEC(N'CREATE PROCEDURE dbo.b6_ApproveCase
@TenantId uniqueidentifier, @CaseId uniqueidentifier
AS
BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 IF ISNULL(IS_ROLEMEMBER(N''b6_case_checker''),0)<>1
    OR NOT EXISTS(SELECT 1 FROM dbo.B6OperatorScope
        WHERE TenantId=@TenantId AND LoginName=SUSER_SNAME()
          AND Enabled=1 AND CanApproveCase=1)
    THROW 52611,N''B6_CASE_CHECKER_DENIED'',1;
 INSERT dbo.B6CaseApproval(TenantId,CaseId,CheckerLogin,SourceHash)
 SELECT s.TenantId,s.CaseId,SUSER_SNAME(),s.SourceHash
 FROM dbo.B6CaseSource s WITH(UPDLOCK,HOLDLOCK)
 WHERE s.TenantId=@TenantId AND s.CaseId=@CaseId AND s.MakerLogin<>SUSER_SNAME()
   AND NOT EXISTS(SELECT 1 FROM dbo.B6CaseApproval a WITH(UPDLOCK,HOLDLOCK)
     WHERE a.TenantId=s.TenantId AND a.CaseId=s.CaseId);
 IF @@ROWCOUNT<>1 THROW 52612,N''B6_CASE_APPROVAL_DENIED'',1;
END');

        EXEC(N'CREATE PROCEDURE dbo.b6_Propose
@TenantId uniqueidentifier, @ProposalId uniqueidentifier,
@PolicyCode nvarchar(96), @Tier char(2), @CaseId uniqueidentifier=NULL,
@ValueState varchar(5), @NumericValue decimal(19,4)=NULL,
@EffectiveFromUtc datetime2(7), @EffectiveUntilUtc datetime2(7),
@SourceId uniqueidentifier, @SourceHash binary(32), @SourceVersion bigint
AS
BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 IF ISNULL(IS_ROLEMEMBER(N''b6_maker''),0)<>1
    OR NOT EXISTS(SELECT 1 FROM dbo.B6OperatorScope
        WHERE TenantId=@TenantId AND LoginName=SUSER_SNAME()
          AND Enabled=1 AND CanPropose=1)
    OR @TenantId IS NULL OR @TenantId=''00000000-0000-0000-0000-000000000000''
    OR @ProposalId IS NULL OR @ProposalId=''00000000-0000-0000-0000-000000000000''
    OR @SourceId IS NULL OR @SourceId=''00000000-0000-0000-0000-000000000000''
    OR @SourceHash IS NULL OR @SourceVersion<1
    OR @EffectiveFromUtc<SYSUTCDATETIME()
    OR @EffectiveUntilUtc<=@EffectiveFromUtc
    OR @Tier NOT IN (''L1'',''L2'')
    OR (@Tier=''L1'' AND @CaseId IS NOT NULL)
    OR (@Tier=''L2'' AND @CaseId IS NULL)
    OR (@ValueState=''SET'' AND @NumericValue IS NULL)
    OR (@ValueState=''UNSET'' AND @NumericValue IS NOT NULL)
    OR @ValueState NOT IN (''SET'',''UNSET'')
    THROW 52620,N''B6_PROPOSAL_DENIED'',1;
 IF NOT EXISTS(SELECT 1 FROM dbo.B6L0Policy WITH(HOLDLOCK)
     WHERE PolicyCode=@PolicyCode AND
       ((@Tier=''L1'' AND AllowL1=1) OR (@Tier=''L2'' AND AllowL2=1))
       AND (@ValueState=''UNSET'' OR
            @NumericValue BETWEEN FloorValue AND CeilingValue))
    THROW 52621,N''B6_L0_BOUNDARY_DENIED'',1;
 INSERT dbo.B6PolicyProposal
  (TenantId,ProposalId,PolicyCode,Tier,CaseId,ValueState,NumericValue,
   EffectiveFromUtc,EffectiveUntilUtc,SourceId,SourceHash,SourceVersion,MakerLogin)
 VALUES(@TenantId,@ProposalId,@PolicyCode,@Tier,@CaseId,@ValueState,@NumericValue,
        @EffectiveFromUtc,@EffectiveUntilUtc,@SourceId,@SourceHash,@SourceVersion,
        SUSER_SNAME());
END');

        EXEC(N'CREATE PROCEDURE dbo.b6_Approve
@TenantId uniqueidentifier, @ProposalId uniqueidentifier
AS
BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 IF ISNULL(IS_ROLEMEMBER(N''b6_checker''),0)<>1
    OR NOT EXISTS(SELECT 1 FROM dbo.B6OperatorScope
       WHERE TenantId=@TenantId AND LoginName=SUSER_SNAME()
         AND Enabled=1 AND CanApprove=1)
    THROW 52630,N''B6_CHECKER_DENIED'',1;
 BEGIN TRY
  BEGIN TRANSACTION;
  DECLARE @code nvarchar(96), @tier char(2),
    @from datetime2(7), @until datetime2(7), @maker sysname,
    @value decimal(19,4), @state varchar(5), @case uniqueidentifier,
    @source uniqueidentifier, @hash binary(32), @version bigint;
  SELECT @code=PolicyCode,@tier=Tier,@from=EffectiveFromUtc,
         @until=EffectiveUntilUtc,@maker=MakerLogin,@value=NumericValue,
         @state=ValueState,@case=CaseId,@source=SourceId,
         @hash=SourceHash,@version=SourceVersion
  FROM dbo.B6PolicyProposal WITH(UPDLOCK,HOLDLOCK)
  WHERE TenantId=@TenantId AND ProposalId=@ProposalId;
  IF @maker IS NULL OR @maker=SUSER_SNAME() OR @state<>''SET''
     OR @from<SYSUTCDATETIME()
     OR NOT EXISTS(SELECT 1 FROM dbo.B6L0Policy WITH(HOLDLOCK)
         WHERE PolicyCode=@code AND @value BETWEEN FloorValue AND CeilingValue
           AND ((@tier=''L1'' AND AllowL1=1) OR (@tier=''L2'' AND AllowL2=1)))
    THROW 52631,N''B6_APPROVAL_SOURCE_DENIED'',1;
  IF @tier=''L2'' AND NOT EXISTS(
      SELECT 1 FROM dbo.B6CaseSource s WITH(HOLDLOCK)
      INNER JOIN dbo.B6CaseApproval d WITH(HOLDLOCK)
          ON d.TenantId=s.TenantId AND d.CaseId=s.CaseId
      WHERE s.TenantId=@TenantId AND s.CaseId=@case
        AND s.SourceId=@source AND s.SourceHash=@hash
        AND d.SourceHash=@hash AND s.MakerLogin<>d.CheckerLogin
        AND s.MakerLogin<>SUSER_SNAME() AND d.CheckerLogin<>@maker)
    THROW 52632,N''B6_CASE_EVIDENCE_DENIED'',1;
  DECLARE @gate int;
  DECLARE @lockResource nvarchar(255)=CONCAT(N''B6:'',CONVERT(nvarchar(36),@TenantId),N'':'',@code,N'':'',@tier);
  EXEC @gate=sys.sp_getapplock
      @Resource=@lockResource,
      @LockMode=N''Exclusive'',@LockOwner=N''Transaction'',@LockTimeout=12000;
  IF @gate<0 THROW 52633,N''B6_POLICY_LOCK_DENIED'',1;
  IF EXISTS(SELECT 1 FROM dbo.B6PolicySnapshot WITH(UPDLOCK,HOLDLOCK)
      WHERE TenantId=@TenantId AND PolicyCode=@code AND Tier=@tier
        AND EffectiveFromUtc<@until AND EffectiveUntilUtc>@from)
    THROW 52634,N''B6_OVERLAPPING_VERSION_DENIED'',1;
  INSERT dbo.B6PolicyApproval(TenantId,ProposalId,CheckerLogin,SourceHash)
      VALUES(@TenantId,@ProposalId,SUSER_SNAME(),@hash);
  INSERT dbo.B6PolicySnapshot
      (TenantId,ProposalId,PolicyCode,Tier,CaseId,NumericValue,
       EffectiveFromUtc,EffectiveUntilUtc,SourceId,SourceHash,SourceVersion,
       MakerLogin,CheckerLogin,ApprovedUtc)
  SELECT p.TenantId,p.ProposalId,p.PolicyCode,p.Tier,p.CaseId,p.NumericValue,
         p.EffectiveFromUtc,p.EffectiveUntilUtc,p.SourceId,p.SourceHash,
         p.SourceVersion,p.MakerLogin,SUSER_SNAME(),a.ApprovedUtc
  FROM dbo.B6PolicyProposal p
  INNER JOIN dbo.B6PolicyApproval a
      ON a.TenantId=p.TenantId AND a.ProposalId=p.ProposalId
  WHERE p.TenantId=@TenantId AND p.ProposalId=@ProposalId;
  IF @@ROWCOUNT<>1 THROW 52635,N''B6_SNAPSHOT_DENIED'',1;
  COMMIT TRANSACTION;
 END TRY
 BEGIN CATCH
  IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;
  THROW;
 END CATCH;
END');

        EXEC(N'CREATE PROCEDURE dbo.b6_ResolveCurrent
@TenantId uniqueidentifier, @PolicyCode nvarchar(96),
@ExpectedProposalId uniqueidentifier, @ExpectedSourceHash binary(32)
AS
BEGIN
 SET NOCOUNT ON;
 IF ISNULL(IS_ROLEMEMBER(N''b6_reader''),0)<>1
    OR NOT EXISTS(SELECT 1 FROM dbo.B6OperatorScope
       WHERE TenantId=@TenantId AND LoginName=SUSER_SNAME()
         AND Enabled=1 AND CanResolve=1)
    OR @ExpectedProposalId IS NULL OR @ExpectedSourceHash IS NULL
    THROW 52640,N''B6_RESOLVE_AUTHORITY_DENIED'',1;
 DECLARE @now datetime2(7)=SYSUTCDATETIME(), @chosen uniqueidentifier,
   @hash binary(32), @value decimal(19,4), @tier char(2), @version bigint,
   @source uniqueidentifier, @from datetime2(7), @until datetime2(7);
 SELECT TOP (1)
   @chosen=ProposalId,@hash=SourceHash,@value=NumericValue,
   @tier=Tier,@version=SourceVersion,@source=SourceId,
   @from=EffectiveFromUtc,@until=EffectiveUntilUtc
 FROM dbo.B6PolicySnapshot WITH(HOLDLOCK)
 WHERE TenantId=@TenantId AND PolicyCode=@PolicyCode
   AND EffectiveFromUtc<=@now AND @now<EffectiveUntilUtc
 ORDER BY CASE WHEN Tier=''L2'' THEN 0 ELSE 1 END, EffectiveFromUtc DESC;
 IF @chosen IS NULL OR @chosen<>@ExpectedProposalId
    OR @hash<>@ExpectedSourceHash
    THROW 52641,N''B6_UNSET_STALE_OR_AMBIGUOUS_DENIED'',1;
 SELECT @TenantId AS TenantId,@PolicyCode AS PolicyCode,
   @chosen AS ProposalId,@tier AS Tier,
   @value AS NumericValue,CAST(1 AS bit) AS IsExplicitlySet,
   @source AS SourceId,@hash AS SourceHash,@version AS SourceVersion,
   @from AS EffectiveFromUtc,@until AS EffectiveUntilUtc;
END');

        GRANT EXECUTE ON OBJECT::dbo.b6_OpenCase TO b6_case_maker;
        GRANT EXECUTE ON OBJECT::dbo.b6_ApproveCase TO b6_case_checker;
        GRANT EXECUTE ON OBJECT::dbo.b6_Propose TO b6_maker;
        GRANT EXECUTE ON OBJECT::dbo.b6_Approve TO b6_checker;
        GRANT EXECUTE ON OBJECT::dbo.b6_ResolveCurrent TO b6_reader;
        INSERT dbo.__B6SchemaHistory(SchemaVersion,AppliedUtc)
            VALUES(N'B6_20261118_001',SYSUTCDATETIME());
    END;
    -- A recorded migration must NOT hide dropped or corrupted critical objects.
    IF (SELECT COUNT_BIG(*) FROM dbo.__B6SchemaHistory)<>1
       OR NOT EXISTS(SELECT 1 FROM dbo.__B6SchemaHistory
          WHERE SchemaVersion=N'B6_20261118_001')
       OR OBJECT_ID(N'dbo.B6L0Policy',N'U') IS NULL
       OR OBJECT_ID(N'dbo.B6OperatorScope',N'U') IS NULL
       OR OBJECT_ID(N'dbo.B6CaseSource',N'U') IS NULL
       OR OBJECT_ID(N'dbo.B6CaseApproval',N'U') IS NULL
       OR OBJECT_ID(N'dbo.B6PolicyProposal',N'U') IS NULL
       OR OBJECT_ID(N'dbo.B6PolicyApproval',N'U') IS NULL
       OR OBJECT_ID(N'dbo.B6PolicySnapshot',N'U') IS NULL
       OR OBJECT_ID(N'dbo.b6_OpenCase',N'P') IS NULL
       OR OBJECT_ID(N'dbo.b6_ApproveCase',N'P') IS NULL
       OR OBJECT_ID(N'dbo.b6_Propose',N'P') IS NULL
       OR OBJECT_ID(N'dbo.b6_Approve',N'P') IS NULL
       OR OBJECT_ID(N'dbo.b6_ResolveCurrent',N'P') IS NULL
       OR OBJECT_ID(N'dbo.b6_ImmutableL0',N'TR') IS NULL
       OR OBJECT_ID(N'dbo.b6_ImmutableCaseSource',N'TR') IS NULL
       OR OBJECT_ID(N'dbo.b6_ImmutableCaseApproval',N'TR') IS NULL
       OR OBJECT_ID(N'dbo.b6_ImmutableProposal',N'TR') IS NULL
       OR OBJECT_ID(N'dbo.b6_ImmutablePolicyApproval',N'TR') IS NULL
       OR OBJECT_ID(N'dbo.b6_ImmutableSnapshot',N'TR') IS NULL
       OR NOT EXISTS(SELECT 1 FROM sys.database_principals
          WHERE name=N'b6_maker' AND type=N'R')
       OR NOT EXISTS(SELECT 1 FROM sys.database_principals
          WHERE name=N'b6_checker' AND type=N'R')
       OR NOT EXISTS(SELECT 1 FROM sys.database_principals
          WHERE name=N'b6_reader' AND type=N'R')
       OR NOT EXISTS(SELECT 1 FROM sys.database_principals
          WHERE name=N'b6_case_maker' AND type=N'R')
       OR NOT EXISTS(SELECT 1 FROM sys.database_principals
          WHERE name=N'b6_case_checker' AND type=N'R')
       THROW 52670,N'B6_SCHEMA_INTEGRITY_DENIED',1;
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
 IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;
 THROW;
END CATCH;
