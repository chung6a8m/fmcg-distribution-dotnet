-- B5_20261118_001 identity bridge: B.3 Control Plane database ONLY.
-- Operator-applied forward migration, no runtime auto-apply, no default SQL users.
SET NOCOUNT ON; SET XACT_ABORT ON;
BEGIN TRY
 BEGIN TRANSACTION;
 IF DB_NAME() IN (N'master',N'model',N'msdb',N'tempdb')
    THROW 51630,N'B5_IDENTITY_SYSTEM_DB_DENIED',1;
 IF OBJECT_ID(N'dbo.__PreprodIdentitySchemaHistory',N'U') IS NULL
    OR NOT EXISTS(SELECT 1 FROM dbo.__PreprodIdentitySchemaHistory
                  WHERE SchemaVersion=N'B3_20261117_001')
    OR OBJECT_ID(N'dbo.PreprodLoginSessions',N'U') IS NULL
    OR OBJECT_ID(N'dbo.AspNetUsers',N'U') IS NULL
    THROW 51630,N'B5_IDENTITY_BASE_MISSING',1;
 IF OBJECT_ID(N'dbo.__B5IdentityGuardHistory',N'U') IS NULL
 BEGIN
    IF OBJECT_ID(N'dbo.b5_AssertCurrentSession',N'P') IS NOT NULL
       THROW 51630,N'B5_IDENTITY_PARTIAL_DENIED',1;
    CREATE TABLE dbo.__B5IdentityGuardHistory
      (SchemaVersion nvarchar(64) NOT NULL PRIMARY KEY,
       AppliedUtc datetime2(7) NOT NULL);
 END;
 IF EXISTS(SELECT 1 FROM dbo.__B5IdentityGuardHistory
           WHERE SchemaVersion<>N'B5_20261118_001')
    THROW 51630,N'B5_IDENTITY_UNKNOWN_VERSION',1;
 IF NOT EXISTS(SELECT 1 FROM dbo.__B5IdentityGuardHistory
               WHERE SchemaVersion=N'B5_20261118_001')
 BEGIN
    IF OBJECT_ID(N'dbo.b5_AssertCurrentSession',N'P') IS NOT NULL
       THROW 51630,N'B5_IDENTITY_PARTIAL_DENIED',1;
    CREATE ROLE b5_session_runtime;
    EXEC(N'CREATE PROCEDURE dbo.b5_AssertCurrentSession
 @ActorId uniqueidentifier, @SessionId uniqueidentifier, @SessionEpoch bigint
AS
BEGIN
 SET NOCOUNT ON;
 IF @@TRANCOUNT < 1 OR IS_ROLEMEMBER(N''b5_session_runtime'')<>1
    THROW 51630,N''B5_SESSION_PRINCIPAL_DENIED'',1;
 IF @ActorId IS NULL OR @SessionId IS NULL OR @SessionEpoch < 0 OR NOT EXISTS(
     SELECT 1 FROM dbo.PreprodLoginSessions s WITH (UPDLOCK,HOLDLOCK)
     INNER JOIN dbo.AspNetUsers u WITH (UPDLOCK,HOLDLOCK) ON u.Id=s.UserId
     WHERE s.Id=@SessionId AND s.UserId=@ActorId AND s.Epoch=@SessionEpoch
       AND s.RevokedUtc IS NULL AND u.Disabled=0 AND u.TwoFactorEnabled=0
       AND s.SecurityStamp=u.SecurityStamp AND u.SecurityStamp IS NOT NULL
       AND (u.LockoutEnabled=0 OR u.LockoutEnd IS NULL
            OR u.LockoutEnd<=TODATETIMEOFFSET(SYSUTCDATETIME(),0)))
    THROW 51631,N''B5_CURRENT_SESSION_DENIED'',1;
END');
    GRANT EXECUTE ON OBJECT::dbo.b5_AssertCurrentSession TO b5_session_runtime;
    INSERT dbo.__B5IdentityGuardHistory(SchemaVersion,AppliedUtc)
      VALUES(N'B5_20261118_001',SYSUTCDATETIME());
 END;
 COMMIT TRANSACTION;
END TRY
BEGIN CATCH
 IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;
 THROW;
END CATCH;
