-- APP-02B.3 / B3_20261117_001
-- REVIEWED SOURCE-ONLY DDL. Apply with a separately approved PREPROD DBA change.
-- Do NOT invoke from API startup, tenant worker, or an unaudited deployment hook.
-- SQL Server; no external provider/tenant grants; no seeded principals or passwords.
-- The versioned script is intentionally a single idempotent transaction / batch.
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF DB_NAME() IN (N'master', N'model', N'msdb', N'tempdb')
        THROW 51001, 'IDENTITY_MIGRATION_SYSTEM_DATABASE_DENIED', 1;

    IF OBJECT_ID(N'dbo.__PreprodIdentitySchemaHistory', N'U') IS NULL
    BEGIN
        IF EXISTS (SELECT 1 FROM sys.tables
                   WHERE schema_id = SCHEMA_ID(N'dbo')
                     AND name IN (N'AspNetUsers', N'AspNetRoles', N'AspNetUserRoles',
                                  N'AspNetUserClaims', N'AspNetUserLogins', N'AspNetUserTokens',
                                  N'AspNetRoleClaims', N'PreprodLoginSessions', N'PreprodRefreshGrants'))
            THROW 51002, 'IDENTITY_MIGRATION_PARTIAL_SCHEMA_DENIED', 1;

        CREATE TABLE dbo.__PreprodIdentitySchemaHistory
        (
            SchemaVersion nvarchar(64) NOT NULL CONSTRAINT PK___PreprodIdentitySchemaHistory PRIMARY KEY,
            AppliedUtc datetime2(7) NOT NULL
        );
    END;

    IF EXISTS (SELECT 1 FROM dbo.__PreprodIdentitySchemaHistory
               WHERE SchemaVersion <> N'B3_20261117_001')
        THROW 51003, 'IDENTITY_MIGRATION_UNKNOWN_VERSION_DENIED', 1;

    IF NOT EXISTS (SELECT 1 FROM dbo.__PreprodIdentitySchemaHistory
                   WHERE SchemaVersion = N'B3_20261117_001')
    BEGIN
        IF EXISTS (SELECT 1 FROM sys.tables
                   WHERE schema_id = SCHEMA_ID(N'dbo')
                     AND name IN (N'AspNetUsers', N'AspNetRoles', N'AspNetUserRoles',
                                  N'AspNetUserClaims', N'AspNetUserLogins', N'AspNetUserTokens',
                                  N'AspNetRoleClaims', N'PreprodLoginSessions', N'PreprodRefreshGrants'))
            THROW 51004, 'IDENTITY_MIGRATION_EXISTING_SCHEMA_DENIED', 1;

        CREATE TABLE dbo.AspNetRoles
        (
            Id uniqueidentifier NOT NULL CONSTRAINT PK_AspNetRoles PRIMARY KEY,
            Name nvarchar(256) NULL,
            NormalizedName nvarchar(256) NULL,
            ConcurrencyStamp nvarchar(max) NULL
        );
        CREATE TABLE dbo.AspNetUsers
        (
            Id uniqueidentifier NOT NULL CONSTRAINT PK_AspNetUsers PRIMARY KEY,
            UserName nvarchar(256) NULL,
            NormalizedUserName nvarchar(256) NULL,
            Email nvarchar(256) NULL,
            NormalizedEmail nvarchar(256) NULL,
            EmailConfirmed bit NOT NULL,
            PasswordHash nvarchar(max) NULL,
            SecurityStamp nvarchar(max) NULL,
            ConcurrencyStamp nvarchar(max) NULL,
            PhoneNumber nvarchar(max) NULL,
            PhoneNumberConfirmed bit NOT NULL,
            TwoFactorEnabled bit NOT NULL,
            LockoutEnd datetimeoffset(7) NULL,
            LockoutEnabled bit NOT NULL,
            AccessFailedCount int NOT NULL,
            Disabled bit NOT NULL,
            ExternalIssuer nvarchar(200) NULL,
            ExternalSubject nvarchar(300) NULL
        );
        CREATE TABLE dbo.AspNetRoleClaims
        (
            Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_AspNetRoleClaims PRIMARY KEY,
            RoleId uniqueidentifier NOT NULL,
            ClaimType nvarchar(max) NULL,
            ClaimValue nvarchar(max) NULL,
            CONSTRAINT FK_AspNetRoleClaims_AspNetRoles_RoleId FOREIGN KEY (RoleId)
                REFERENCES dbo.AspNetRoles(Id) ON DELETE CASCADE
        );
        CREATE TABLE dbo.AspNetUserClaims
        (
            Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_AspNetUserClaims PRIMARY KEY,
            UserId uniqueidentifier NOT NULL,
            ClaimType nvarchar(max) NULL,
            ClaimValue nvarchar(max) NULL,
            CONSTRAINT FK_AspNetUserClaims_AspNetUsers_UserId FOREIGN KEY (UserId)
                REFERENCES dbo.AspNetUsers(Id) ON DELETE CASCADE
        );
        CREATE TABLE dbo.AspNetUserLogins
        (
            LoginProvider nvarchar(128) NOT NULL,
            ProviderKey nvarchar(128) NOT NULL,
            ProviderDisplayName nvarchar(max) NULL,
            UserId uniqueidentifier NOT NULL,
            CONSTRAINT PK_AspNetUserLogins PRIMARY KEY (LoginProvider, ProviderKey),
            CONSTRAINT FK_AspNetUserLogins_AspNetUsers_UserId FOREIGN KEY (UserId)
                REFERENCES dbo.AspNetUsers(Id) ON DELETE CASCADE
        );
        CREATE TABLE dbo.AspNetUserRoles
        (
            UserId uniqueidentifier NOT NULL,
            RoleId uniqueidentifier NOT NULL,
            CONSTRAINT PK_AspNetUserRoles PRIMARY KEY (UserId, RoleId),
            CONSTRAINT FK_AspNetUserRoles_AspNetUsers_UserId FOREIGN KEY (UserId)
                REFERENCES dbo.AspNetUsers(Id) ON DELETE CASCADE,
            CONSTRAINT FK_AspNetUserRoles_AspNetRoles_RoleId FOREIGN KEY (RoleId)
                REFERENCES dbo.AspNetRoles(Id) ON DELETE CASCADE
        );
        CREATE TABLE dbo.AspNetUserTokens
        (
            UserId uniqueidentifier NOT NULL,
            LoginProvider nvarchar(128) NOT NULL,
            Name nvarchar(128) NOT NULL,
            Value nvarchar(max) NULL,
            CONSTRAINT PK_AspNetUserTokens PRIMARY KEY (UserId, LoginProvider, Name),
            CONSTRAINT FK_AspNetUserTokens_AspNetUsers_UserId FOREIGN KEY (UserId)
                REFERENCES dbo.AspNetUsers(Id) ON DELETE CASCADE
        );
        CREATE TABLE dbo.PreprodLoginSessions
        (
            Id uniqueidentifier NOT NULL CONSTRAINT PK_PreprodLoginSessions PRIMARY KEY,
            UserId uniqueidentifier NOT NULL,
            Epoch bigint NOT NULL,
            SecurityStamp nvarchar(256) NOT NULL,
            CreatedUtc datetime2(7) NOT NULL,
            RevokedUtc datetime2(7) NULL,
            CONSTRAINT FK_PreprodLoginSessions_AspNetUsers_UserId FOREIGN KEY (UserId)
                REFERENCES dbo.AspNetUsers(Id) ON DELETE NO ACTION
        );
        CREATE TABLE dbo.PreprodRefreshGrants
        (
            Id uniqueidentifier NOT NULL CONSTRAINT PK_PreprodRefreshGrants PRIMARY KEY,
            SessionId uniqueidentifier NOT NULL,
            TokenHash varchar(64) NOT NULL,
            ExpiresUtc datetime2(7) NOT NULL,
            CreatedUtc datetime2(7) NOT NULL,
            ConsumedUtc datetime2(7) NULL,
            CONSTRAINT FK_PreprodRefreshGrants_PreprodLoginSessions_SessionId FOREIGN KEY (SessionId)
                REFERENCES dbo.PreprodLoginSessions(Id) ON DELETE NO ACTION
        );

        CREATE UNIQUE INDEX RoleNameIndex ON dbo.AspNetRoles(NormalizedName)
            WHERE NormalizedName IS NOT NULL;
        CREATE INDEX IX_AspNetRoleClaims_RoleId ON dbo.AspNetRoleClaims(RoleId);
        CREATE INDEX EmailIndex ON dbo.AspNetUsers(NormalizedEmail);
        CREATE UNIQUE INDEX UserNameIndex ON dbo.AspNetUsers(NormalizedUserName)
            WHERE NormalizedUserName IS NOT NULL;
        CREATE UNIQUE INDEX IX_AspNetUsers_ExternalIssuer_ExternalSubject
            ON dbo.AspNetUsers(ExternalIssuer, ExternalSubject)
            WHERE ExternalIssuer IS NOT NULL AND ExternalSubject IS NOT NULL;
        CREATE INDEX IX_AspNetUserClaims_UserId ON dbo.AspNetUserClaims(UserId);
        CREATE INDEX IX_AspNetUserLogins_UserId ON dbo.AspNetUserLogins(UserId);
        CREATE INDEX IX_AspNetUserRoles_RoleId ON dbo.AspNetUserRoles(RoleId);
        CREATE INDEX IX_PreprodLoginSessions_UserId_RevokedUtc
            ON dbo.PreprodLoginSessions(UserId, RevokedUtc);
        CREATE INDEX IX_PreprodRefreshGrants_SessionId ON dbo.PreprodRefreshGrants(SessionId);
        CREATE UNIQUE INDEX IX_PreprodRefreshGrants_TokenHash ON dbo.PreprodRefreshGrants(TokenHash);

        INSERT dbo.__PreprodIdentitySchemaHistory (SchemaVersion, AppliedUtc)
            VALUES (N'B3_20261117_001', SYSUTCDATETIME());
    END;

    IF (SELECT COUNT_BIG(*) FROM sys.tables WHERE schema_id = SCHEMA_ID(N'dbo')
          AND name IN (N'AspNetUsers', N'AspNetRoles', N'AspNetUserRoles',
                       N'AspNetUserClaims', N'AspNetUserLogins', N'AspNetUserTokens',
                       N'AspNetRoleClaims', N'PreprodLoginSessions', N'PreprodRefreshGrants')) <> 9
        THROW 51005, 'IDENTITY_MIGRATION_SCHEMA_COUNT_DENIED', 1;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE object_id = OBJECT_ID(N'dbo.PreprodRefreshGrants')
                     AND name = N'IX_PreprodRefreshGrants_TokenHash'
                     AND is_unique = 1)
        THROW 51006, 'IDENTITY_MIGRATION_REFRESH_INDEX_DENIED', 1;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
