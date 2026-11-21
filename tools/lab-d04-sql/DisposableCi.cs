using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

internal sealed partial class LabSql
{
    private const string ProbePermission = "lab-d04.observe";
    private const string ProbeResource = "LAB_D04_SYNTHETIC";
    private static readonly Guid Actor = new("13cd8b2c-ee07-42ca-b152-3015cc4200b8");
    private static readonly Guid Session = new("13cd8b2c-ee07-42ca-b152-3015cc4200b9");
    private static readonly Guid RoleId = new("13cd8b2c-ee07-42ca-b152-3015cc4200ba");

    internal async Task<object> RunDisposableCiAsync(string mode)
    {
        Need(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true"
            && Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") == "github-hosted"
            && Environment.GetEnvironmentVariable("RUNNER_OS") == "Linux"
            && scope.Project.StartsWith("fmcg-lab-d04-", StringComparison.Ordinal)
            && request.Credentials.Keys.ToHashSet().SetEquals(Roles.Concat(["bootstrap","revoker"])),
            "D04_CI_BOOTSTRAP_ONLY");
        return mode switch
        {
            "bootstrap" => await BootstrapAsync(),
            "fixture-fence" => await FenceAsync(),
            _ => throw new RehearsalError("D04_INVALID_MODE")
        };
    }

    private async Task<object> BootstrapAsync()
    {
        await using var master = await OpenAsync("bootstrap", "master");
        Need(await Count(master, "SELECT COUNT_BIG(*) FROM sys.databases WHERE database_id > 4") == 0
            && await Count(master, """
                SELECT COUNT_BIG(*) FROM sys.tables WHERE name=N'LabD04Instance' AND schema_id=SCHEMA_ID(N'dbo')
                """) == 0, "D04_CI_INSTANCE_NOT_EMPTY");
        await Execute(master, "CREATE TABLE dbo.LabD04Instance (Nonce char(32) NOT NULL PRIMARY KEY)");
        await using (var marker = Command(master, "INSERT dbo.LabD04Instance (Nonce) VALUES (@nonce)"))
        {
            marker.Parameters.AddWithValue("@nonce", scope.Nonce);
            await marker.ExecuteNonQueryAsync(ct);
        }
        foreach (var slot in DbSlots)
        {
            await MatchMarker(master);
            await Execute(master, $"CREATE DATABASE [{scope.Databases[slot]}]");
        }
        foreach (var role in Roles.Concat(["revoker"]))
        {
            await MatchMarker(master);
            var password = request.Credentials[role];
            // The disposable fixture uses high-entropy passwords with a fixed alphabet.
            Need(Regex.IsMatch(password, "^[a-zA-Z0-9!]{24,100}$"), "D04_CI_PASSWORD_FORMAT");
            await Execute(master, $"CREATE LOGIN [{Login(role)}] WITH PASSWORD=N'{password}', CHECK_POLICY=OFF");
        }
        foreach (var slot in DbSlots)
        {
            await MatchMarker(master);
            await using var db = await OpenAsync("bootstrap", scope.Databases[slot]);
            await Execute(db, $"CREATE USER [{Login("observer")}] FOR LOGIN [{Login("observer")}]");
            if (slot.StartsWith("tenant", StringComparison.Ordinal))
                await SetupTenantAsync(db, slot[^1..]);
            else
                await SetupControlAsync(db, slot);
        }
        // A separately authenticated observer witnesses idempotent reapply on all five databases.
        var before = await SnapshotAsync();
        foreach (var slot in DbSlots)
        {
            await MatchMarker(master);
            await using var db = await OpenAsync("bootstrap", scope.Databases[slot]);
            await MigrateAsync(db, slot);
        }
        Need(before.SequenceEqual(await SnapshotAsync()), "D04_MIGRATION_DRIFT");
        return new { status = "SYNTHETIC_SCHEMA_REAPPLY_OBSERVED", databaseCount = 5 };
    }

    private async Task MatchMarker(SqlConnection master)
    {
        await using var marker = Command(master, """
            SELECT COUNT_BIG(*) FROM dbo.LabD04Instance WHERE Nonce=@nonce
            AND (SELECT COUNT_BIG(*) FROM dbo.LabD04Instance)=1
            """);
        marker.Parameters.AddWithValue("@nonce", scope.Nonce);
        Need((long?)await marker.ExecuteScalarAsync(ct) == 1, "D04_CI_INSTANCE_MARKER");
    }

    private async Task MigrateAsync(SqlConnection db, string slot)
    {
        var tenant = slot.StartsWith("tenant", StringComparison.Ordinal);
        var resource = tenant ? "tenant.sql" : slot + ".sql";
        await ApplyResourceAsync(db, resource, tenant ? slot[^1..] : null);
        if (slot is "identity" or "registry")
            await ApplyResourceAsync(db, slot + "-guard.sql", null);
    }

    private async Task ApplyResourceAsync(SqlConnection db, string resource, string? tenantSlot)
    {
        using var stream = typeof(LabSql).Assembly.GetManifestResourceStream(resource)
            ?? throw new RehearsalError("D04_MIGRATION_MISSING");
        using var reader = new StreamReader(stream);
        await using var command = Command(db, await reader.ReadToEndAsync(ct));
        if (tenantSlot is not null)
        {
            command.Parameters.AddWithValue("@TenantId", scope.Tenants[tenantSlot]);
            command.Parameters.AddWithValue("@BindingId", scope.Bindings[tenantSlot]);
            command.Parameters.AddWithValue("@BindingVersion", 1L);
        }
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task SetupTenantAsync(SqlConnection db, string tenant)
    {
        await MigrateAsync(db, "tenant" + tenant);
        await Execute(db, $"""
            CREATE USER [{Login("api" + tenant)}] FOR LOGIN [{Login("api" + tenant)}];
            CREATE TABLE dbo.LabD04Probe (ProbeId int PRIMARY KEY, Marker nvarchar(64) NOT NULL);
            INSERT dbo.LabD04Probe VALUES (1,N'LAB_D04_SYNTHETIC');
            GRANT SELECT, INSERT ON dbo.LabD04Probe TO [{Login("api" + tenant)}];
            GRANT SELECT ON dbo.LabD04Probe TO [{Login("observer")}];
            GRANT SELECT ON dbo.__PreprodTenantBinding TO [{Login("observer")}];
            GRANT SELECT ON dbo.__PreprodTenantSchemaHistory TO [{Login("observer")}];
            """);
    }

    private async Task SetupControlAsync(SqlConnection db, string slot)
    {
        await MigrateAsync(db, slot);
        var names = slot switch
        {
            "identity" => new[] { "__PreprodIdentitySchemaHistory", "__B5IdentityGuardHistory" },
            "registry" => new[] { "__PreprodTenantRegistrySchemaHistory", "__B5RegistryGuardHistory" },
            _ => new[] { "__B5SchemaHistory", "B5TenantFence", "B5Membership" }
        };
        foreach (var table in names)
            await Execute(db, $"GRANT SELECT ON dbo.[{table}] TO [{Login("observer")}]");
        if (slot != "fence")
            return;
        await Execute(db, $"""
            CREATE USER [{Login("apiA")}] FOR LOGIN [{Login("apiA")}];
            CREATE USER [{Login("revoker")}] FOR LOGIN [{Login("revoker")}];
            ALTER ROLE b5_runtime ADD MEMBER [{Login("apiA")}];
            ALTER ROLE b5_revoker ADD MEMBER [{Login("revoker")}];
            """);
        await using var seed = Command(db, """
            INSERT dbo.B5TenantFence(TenantId,BindingId,RegistryEpoch,FenceEpoch,State)
                VALUES(@tenant,@binding,1,1,N'ACTIVE');
            INSERT dbo.B5Membership(TenantId,ActorId,MembershipEpoch) VALUES(@tenant,@actor,1);
            INSERT dbo.B5Role(TenantId,RoleId,Code,Enabled) VALUES(@tenant,@role,N'LAB_D04_PROBE',1);
            INSERT dbo.B5ActorRole(TenantId,ActorId,RoleId) VALUES(@tenant,@actor,@role);
            INSERT dbo.B5ActionCatalog(PermissionCode,ActionKind) VALUES(@permission,'READ');
            INSERT dbo.B5RolePermission(TenantId,RoleId,PermissionCode,ResourceKey)
                VALUES(@tenant,@role,@permission,@resource);
            """);
        seed.Parameters.AddWithValue("@tenant", scope.Tenants["A"]);
        seed.Parameters.AddWithValue("@binding", scope.Bindings["A"]);
        seed.Parameters.AddWithValue("@actor", Actor);
        seed.Parameters.AddWithValue("@role", RoleId);
        seed.Parameters.AddWithValue("@permission", ProbePermission);
        seed.Parameters.AddWithValue("@resource", ProbeResource);
        await seed.ExecuteNonQueryAsync(ct);
    }

    private async Task<object> FenceAsync()
    {
        await using var master = await OpenAsync("bootstrap", "master");
        await MatchMarker(master);
        var before = await SnapshotAsync();
        var fenceDb = scope.Databases["fence"];
        await using var api = await OpenAsync("apiA", fenceDb);
        await AssertFenceAsync(api, 1, null);
        await AssertFenceAsync(api, 2, 51603);
        await InRollback(api, "SELECT COUNT_BIG(*) FROM dbo.B5Membership", [229]);
        await using var revoker = await OpenAsync("revoker", fenceDb);
        await using (var cmd = Command(revoker, """
            EXEC dbo.b5_RevokeMember @TenantId=@tenant,@ActorId=@actor,
                @ExpectedFenceEpoch=1,@EvidenceRef=N'LAB_D04_SYNTHETIC';
            """))
        {
            cmd.Parameters.AddWithValue("@tenant", scope.Tenants["A"]);
            cmd.Parameters.AddWithValue("@actor", Actor);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await AssertFenceAsync(api, 1, 51603);
        await AssertFenceAsync(api, 2, 51604);
        await using var observer = await OpenAsync("observer", fenceDb);
        Need(await Count(observer, "SELECT COUNT_BIG(*) FROM dbo.B5TenantFence WHERE FenceEpoch=2") == 1
            && await Count(observer, "SELECT COUNT_BIG(*) FROM dbo.B5Membership WHERE RevokedUtc IS NOT NULL") == 1
            && before.SequenceEqual(await SnapshotAsync()), "D04_FENCE_WITNESS_MISMATCH");
        return new { status = "SYNTHETIC_SQL_FENCE_OBSERVED", staleDenied = true,
            revokedDenied = true, peerFingerprintsEqual = true };
    }

    private async Task AssertFenceAsync(SqlConnection db, long epoch, int? denied)
    {
        await using var tx = (SqlTransaction)await db.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            await using var cmd = Command(db, """
                EXEC dbo.b5_AssertAuthority @TenantId=@tenant,@ResourceTenantId=@tenant,@ActorId=@actor,
                    @SessionId=@session,@BindingId=@binding,@RegistryEpoch=1,@FenceEpoch=@epoch,
                    @MembershipEpoch=1,@PermissionCode=@permission,@ResourceKey=@resource;
                """, tx);
            cmd.Parameters.AddWithValue("@tenant", scope.Tenants["A"]);
            cmd.Parameters.AddWithValue("@actor", Actor);
            cmd.Parameters.AddWithValue("@session", Session);
            cmd.Parameters.AddWithValue("@binding", scope.Bindings["A"]);
            cmd.Parameters.AddWithValue("@epoch", epoch);
            cmd.Parameters.AddWithValue("@permission", ProbePermission);
            cmd.Parameters.AddWithValue("@resource", ProbeResource);
            await cmd.ExecuteNonQueryAsync(ct);
            Need(denied is null, "D04_STALE_OR_REVOKED_ALLOWED");
        }
        catch (SqlException error) when (error.Number == denied)
        {
            return;
        }
        finally
        {
            if (tx.Connection is not null)
                await tx.RollbackAsync(CancellationToken.None);
        }
    }
}
