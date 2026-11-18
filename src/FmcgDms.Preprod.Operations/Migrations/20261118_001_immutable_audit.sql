-- APP-02B.8 operator-applied independent audit Control Plane SQL.
-- No startup migration, no seeded users or tenant registration, no delete/purge endpoint.
-- Writer may EXECUTE ONLY after a separately approved DBA binds a scoped SQL login.
-- A DBA/system-admin can drop triggers: signed off-host anchors remain mandatory.
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF DB_NAME() IN (N'master',N'model',N'msdb',N'tempdb')
    THROW 52800, N'B8_SYSTEM_DATABASE_DENIED', 1;
BEGIN TRY
 BEGIN TRANSACTION;
 DECLARE @lock int;
 EXEC @lock=sys.sp_getapplock @Resource=N'APP02B8_AUDIT_SCHEMA',
  @LockMode=N'Exclusive',@LockOwner=N'Transaction',@LockTimeout=15000;
 IF @lock < 0 THROW 52801,N'B8_MIGRATION_LOCK_DENIED',1;

 IF OBJECT_ID(N'dbo.__B8SchemaHistory',N'U') IS NULL
 BEGIN
   IF OBJECT_ID(N'dbo.B8AuditEntry',N'U') IS NOT NULL OR
      OBJECT_ID(N'dbo.B8AuditHead',N'U') IS NOT NULL OR
      OBJECT_ID(N'dbo.B8LegalHold',N'U') IS NOT NULL
      THROW 52802,N'B8_PARTIAL_SCHEMA_DENIED',1;
   CREATE TABLE dbo.__B8SchemaHistory(
     SchemaVersion nvarchar(64) NOT NULL PRIMARY KEY,
     AppliedUtc datetime2(7) NOT NULL DEFAULT SYSUTCDATETIME());
 END;
 IF EXISTS(SELECT 1 FROM dbo.__B8SchemaHistory
           WHERE SchemaVersion<>N'B8_20261118_001')
    THROW 52803,N'B8_UNKNOWN_SCHEMA_VERSION_DENIED',1;

 IF NOT EXISTS(SELECT 1 FROM dbo.__B8SchemaHistory
               WHERE SchemaVersion=N'B8_20261118_001')
 BEGIN
   IF OBJECT_ID(N'dbo.B8AuditEntry',N'U') IS NOT NULL OR
      OBJECT_ID(N'dbo.B8AuditHead',N'U') IS NOT NULL OR
      OBJECT_ID(N'dbo.B8LegalHold',N'U') IS NOT NULL
      THROW 52804,N'B8_EXISTING_SCHEMA_DENIED',1;
   CREATE TABLE dbo.B8WriterScope(
     TenantId uniqueidentifier NOT NULL,
     LoginName sysname NOT NULL,
     CanAppend bit NOT NULL,
     CanHold bit NOT NULL,
     Enabled bit NOT NULL,
     CONSTRAINT PK_B8WriterScope PRIMARY KEY(TenantId,LoginName));
   CREATE TABLE dbo.B8AuditHead(
     TenantId uniqueidentifier NOT NULL PRIMARY KEY,
     LastSequence bigint NOT NULL,
     LastHash binary(32) NOT NULL,
     CONSTRAINT CK_B8HeadSequence CHECK(LastSequence>=1));
   CREATE TABLE dbo.B8AuditEntry(
     TenantId uniqueidentifier NOT NULL,
     Sequence bigint NOT NULL,
     EntryId uniqueidentifier NOT NULL,
     Kind smallint NOT NULL,
     PrincipalSha256 binary(32) NOT NULL,
     CorrelationId uniqueidentifier NOT NULL,
     SourceId uniqueidentifier NULL,
     PolicyVersionId uniqueidentifier NULL,
     MessageId uniqueidentifier NULL,
     PayloadSha256 binary(32) NOT NULL,
     PreviousSha256 binary(32) NOT NULL,
     EntrySha256 binary(32) NOT NULL,
     LoggedUtc datetime2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
     CONSTRAINT PK_B8AuditEntry PRIMARY KEY(TenantId,Sequence),
     CONSTRAINT UQ_B8AuditEntry_Event UNIQUE(TenantId,EntryId),
     CONSTRAINT CK_B8AuditEntryKind CHECK(Kind BETWEEN 1 AND 12),
     CONSTRAINT CK_B8AuditEntrySeq CHECK(Sequence>=1),
     CONSTRAINT CK_B8AuditEntryIdentity CHECK(
         EntryId<>'00000000-0000-0000-0000-000000000000' AND
         CorrelationId<>'00000000-0000-0000-0000-000000000000'));
   CREATE TABLE dbo.B8LegalHold(
     TenantId uniqueidentifier NOT NULL,
     HoldId uniqueidentifier NOT NULL,
     EvidenceClass tinyint NOT NULL,
     ManifestSha256 binary(32) NOT NULL,
     AuthoritySha256 binary(32) NOT NULL,
     IssuerLoginSha256 binary(32) NOT NULL,
     PlacedUtc datetime2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
     CONSTRAINT PK_B8LegalHold PRIMARY KEY(TenantId,HoldId),
     CONSTRAINT CK_B8HoldClass CHECK(EvidenceClass BETWEEN 1 AND 5));
   CREATE ROLE b8_writer;
   CREATE ROLE b8_hold;
   -- SQL DML guards defend against accidental/privilege-spill mutations. No
   -- INSERT/UPDATE/DELETE/SELECT grants on protected tables to either role.
   EXEC(N'CREATE TRIGGER dbo.b8_ImmutableAudit ON dbo.B8AuditEntry AFTER UPDATE,DELETE
     AS BEGIN SET NOCOUNT ON; THROW 52880,N''B8_IMMUTABLE_AUDIT_DENIED'',1; END');
   EXEC(N'CREATE TRIGGER dbo.b8_ImmutableHold ON dbo.B8LegalHold AFTER UPDATE,DELETE
     AS BEGIN SET NOCOUNT ON; THROW 52881,N''B8_IMMUTABLE_HOLD_DENIED'',1; END');

   EXEC(N'CREATE PROCEDURE dbo.b8_Append
      @TenantId uniqueidentifier,
      @EntryId uniqueidentifier,
      @Kind smallint,
      @CorrelationId uniqueidentifier,
      @SourceId uniqueidentifier=NULL,
      @PolicyVersionId uniqueidentifier=NULL,
      @MessageId uniqueidentifier=NULL,
      @PayloadSha256 binary(32)
     AS
     BEGIN
       SET NOCOUNT ON; SET XACT_ABORT ON;
       IF @TenantId IS NULL OR @TenantId=''00000000-0000-0000-0000-000000000000''
         OR @EntryId IS NULL OR @EntryId=''00000000-0000-0000-0000-000000000000''
         OR @CorrelationId IS NULL OR @CorrelationId=''00000000-0000-0000-0000-000000000000''
         OR @PayloadSha256 IS NULL OR @Kind IS NULL OR @Kind NOT BETWEEN 1 AND 12
         OR ISNULL(IS_ROLEMEMBER(N''b8_writer''),0)<>1
         OR NOT EXISTS(SELECT 1 FROM dbo.B8WriterScope
            WHERE TenantId=@TenantId AND LoginName=SUSER_SNAME()
              AND CanAppend=1 AND Enabled=1)
         THROW 52810,N''B8_TENANT_WRITER_SCOPE_DENIED'',1;
       BEGIN TRANSACTION;
       DECLARE @lockResult int;
       DECLARE @resource nvarchar(255)=N''B8_AUDIT_''+CONVERT(nvarchar(36),@TenantId);
       EXEC @lockResult=sys.sp_getapplock
          @Resource=@resource,
          @LockMode=N''Exclusive'',@LockOwner=N''Transaction'',@LockTimeout=15000;
       IF @lockResult<0 THROW 52811,N''B8_SEQUENCE_LOCK_DENIED'',1;
       DECLARE @prev binary(32)=CONVERT(binary(32),0x),
               @seq bigint=1;
       SELECT @prev=LastHash, @seq=LastSequence+1
         FROM dbo.B8AuditHead WITH(UPDLOCK,HOLDLOCK)
         WHERE TenantId=@TenantId;
       IF @seq<1 OR @seq=9223372036854775807
          THROW 52812,N''B8_SEQUENCE_EXHAUSTED'',1;
       DECLARE @principal binary(32)=HASHBYTES(''SHA2_256'',
                     CONVERT(varbinary(256),SUSER_SNAME()));
       DECLARE @zeroGuid binary(16)=CONVERT(binary(16),0x);
       DECLARE @next binary(32)=HASHBYTES(''SHA2_256'',
           @prev +
           CONVERT(binary(16),@TenantId) +
           CONVERT(binary(8),@seq) +
           CONVERT(binary(16),@EntryId) +
           CONVERT(binary(2),@Kind) +
           @principal +
           CONVERT(binary(16),@CorrelationId) +
           ISNULL(CONVERT(binary(16),@SourceId),@zeroGuid) +
           ISNULL(CONVERT(binary(16),@PolicyVersionId),@zeroGuid) +
           ISNULL(CONVERT(binary(16),@MessageId),@zeroGuid) +
           @PayloadSha256);
       INSERT dbo.B8AuditEntry(TenantId,Sequence,EntryId,Kind,PrincipalSha256,
          CorrelationId,SourceId,PolicyVersionId,MessageId,PayloadSha256,
          PreviousSha256,EntrySha256)
       VALUES(@TenantId,@seq,@EntryId,@Kind,@principal,@CorrelationId,
          @SourceId,@PolicyVersionId,@MessageId,@PayloadSha256,@prev,@next);
       IF @seq=1
         INSERT dbo.B8AuditHead(TenantId,LastSequence,LastHash)
         VALUES(@TenantId,@seq,@next);
       ELSE
       BEGIN
         UPDATE dbo.B8AuditHead SET LastSequence=@seq,LastHash=@next
           WHERE TenantId=@TenantId AND LastSequence=@seq-1;
         IF @@ROWCOUNT<>1 THROW 52813,N''B8_HEAD_STALE_DENIED'',1;
       END;
       COMMIT TRANSACTION;
       SELECT @seq AS Sequence, @next AS EntrySha256;
     END');

   EXEC(N'CREATE PROCEDURE dbo.b8_PlaceHold
      @TenantId uniqueidentifier,@HoldId uniqueidentifier,
      @EvidenceClass tinyint,@ManifestSha256 binary(32),@AuthoritySha256 binary(32)
     AS
     BEGIN
       SET NOCOUNT ON; SET XACT_ABORT ON;
       IF @TenantId IS NULL OR @TenantId=''00000000-0000-0000-0000-000000000000''
         OR @HoldId IS NULL OR @HoldId=''00000000-0000-0000-0000-000000000000''
         OR @EvidenceClass IS NULL OR @EvidenceClass NOT BETWEEN 1 AND 5
         OR @ManifestSha256 IS NULL OR @AuthoritySha256 IS NULL
         OR ISNULL(IS_ROLEMEMBER(N''b8_hold''),0)<>1
         OR NOT EXISTS(SELECT 1 FROM dbo.B8WriterScope
            WHERE TenantId=@TenantId AND LoginName=SUSER_SNAME()
             AND CanHold=1 AND Enabled=1)
         THROW 52820,N''B8_HOLD_AUTHORITY_DENIED'',1;
       INSERT dbo.B8LegalHold(TenantId,HoldId,EvidenceClass,ManifestSha256,
           AuthoritySha256,IssuerLoginSha256)
       VALUES(@TenantId,@HoldId,@EvidenceClass,@ManifestSha256,
           @AuthoritySha256,HASHBYTES(''SHA2_256'',
                 CONVERT(varbinary(256),SUSER_SNAME())));
     END');
   GRANT EXECUTE ON dbo.b8_Append TO b8_writer;
   GRANT EXECUTE ON dbo.b8_PlaceHold TO b8_hold;
   INSERT dbo.__B8SchemaHistory(SchemaVersion) VALUES(N'B8_20261118_001');
 END
 ELSE
 BEGIN
   -- A recorded history row cannot conceal a dropped guard/procedure/role.
   IF OBJECT_ID(N'dbo.B8WriterScope',N'U') IS NULL OR
      OBJECT_ID(N'dbo.B8AuditHead',N'U') IS NULL OR
      OBJECT_ID(N'dbo.B8AuditEntry',N'U') IS NULL OR
      OBJECT_ID(N'dbo.B8LegalHold',N'U') IS NULL OR
      OBJECT_ID(N'dbo.b8_Append',N'P') IS NULL OR
      OBJECT_ID(N'dbo.b8_PlaceHold',N'P') IS NULL OR
      OBJECT_ID(N'dbo.b8_ImmutableAudit',N'TR') IS NULL OR
      OBJECT_ID(N'dbo.b8_ImmutableHold',N'TR') IS NULL OR
      DATABASE_PRINCIPAL_ID(N'b8_writer') IS NULL OR
      DATABASE_PRINCIPAL_ID(N'b8_hold') IS NULL
      THROW 52805,N'B8_RECORDED_SCHEMA_CORRUPTED',1;
 END;
 COMMIT TRANSACTION;
END TRY
BEGIN CATCH
 IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;
 THROW;
END CATCH;
