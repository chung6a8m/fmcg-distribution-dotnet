using System.Data;
using System.Security;
using System.Text.Json;
using System.Text.RegularExpressions;
using FmcgDms.Preprod.Tenancy;
using Microsoft.Data.SqlClient;

try
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
    var input = await Console.In.ReadToEndAsync(deadline.Token);
    if (input.Length > 32768 || args.Length != 1)
        throw new RehearsalError("D04_INVALID_INPUT");
    var request = JsonSerializer.Deserialize<Request>(input, new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    }) ?? throw new RehearsalError("D04_INVALID_INPUT");
    var sql = new LabSql(request, deadline.Token);
    var response = args[0] == "verify" ? await sql.VerifyAsync() : await sql.RunDisposableCiAsync(args[0]);
    Console.WriteLine(JsonSerializer.Serialize(response));
    return 0;
}
catch (RehearsalError error)
{
    Console.Error.WriteLine(error.Code);
    return 2;
}
catch (SqlException error)
{
    Console.Error.WriteLine($"D04_SQL_ERROR_{error.Number}");
    return 2;
}
catch (Exception error)
{
    Console.Error.WriteLine($"D04_COLLECTION_FAILED_{error.GetType().Name}");
    return 1;
}

internal sealed record Request(string Endpoint, Scope Spec, Dictionary<string, string> Credentials);
internal sealed record Scope(string Project, string Nonce, string Instance, Dictionary<string, string> Databases,
    Dictionary<string, Guid> Tenants, Dictionary<string, Guid> Bindings, Dictionary<string, string> Logins);
internal sealed class RehearsalError(string code) : Exception
{
    public string Code { get; } = code;
}

internal sealed partial class LabSql
{
    private const string Build = "17.0.5065.9";
    private static readonly string[] DbSlots = ["identity", "registry", "fence", "tenantA", "tenantB"];
    private static readonly string[] Roles = ["apiA", "apiB", "observer"];
    private readonly Request request;
    private readonly Scope scope;
    private readonly CancellationToken ct;

    public LabSql(Request request, CancellationToken ct)
    {
        this.request = request;
        scope = request.Spec;
        this.ct = ct;
        Need(scope is not null && Regex.IsMatch(scope.Nonce, "^[a-f0-9]{32}$") && scope.Nonce.Any(c => c != '0')
            && scope.Instance == "d04-" + scope.Nonce && request.Endpoint.Length <= 45
            && (request.Endpoint == "sql" || System.Net.IPAddress.TryParse(request.Endpoint, out _)),
            "D04_SQL_SCOPE");
        Need(scope!.Databases.Keys.ToHashSet().SetEquals(DbSlots) && scope.Logins.Keys.ToHashSet().SetEquals(Roles)
            && scope.Tenants.Keys.ToHashSet().SetEquals(["A", "B"])
            && scope.Bindings.Keys.ToHashSet().SetEquals(["A", "B"])
            && scope.Tenants.Values.Distinct().Count() == 2 && scope.Tenants.Values.All(id => id != Guid.Empty)
            && scope.Bindings.Values.Distinct().Count() == 2 && scope.Bindings.Values.All(id => id != Guid.Empty),
            "D04_SQL_SCOPE");
        foreach (var slot in DbSlots)
        {
            var expected = slot.StartsWith("tenant", StringComparison.Ordinal)
                ? TenantRegistryEntry.ExpectedDatabase(scope.Tenants[slot[^1..]])
                : $"LabD04_{slot}_{scope.Nonce}";
            Need(scope.Databases[slot] == expected, "D04_SQL_DATABASE_ALLOWLIST");
        }
        foreach (var role in Roles)
            Need(scope.Logins[role] == $"labd04_{role.ToLowerInvariant()}_{scope.Nonce}", "D04_SQL_LOGIN_SCOPE");
    }

    private static void Need(bool valid, string code)
    {
        if (!valid)
            throw new RehearsalError(code);
    }

    private string Login(string role) => role switch
    {
        "bootstrap" => "sa",
        "revoker" => "labd04_revoke_" + scope.Nonce,
        _ => scope.Logins[role]
    };

    private async Task<SqlConnection> OpenAsync(string role, string database)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = $"tcp:{request.Endpoint},1433",
            InitialCatalog = database,
            Encrypt = SqlConnectionEncryptOption.Strict,
            HostNameInCertificate = "sql",
            TrustServerCertificate = false,
            Pooling = false,
            ConnectTimeout = 5
        };
        Need(request.Credentials.TryGetValue(role, out var password) && password.Length is >= 12 and <= 128,
            "D04_ROLE_CREDENTIAL_MISSING");
        using var securePassword = new SecureString();
        foreach (var character in password!)
            securePassword.AppendChar(character);
        securePassword.MakeReadOnly();
        var connection = new SqlConnection(builder.ConnectionString, new SqlCredential(Login(role), securePassword));
        try
        {
            await connection.OpenAsync(ct);
            using var cmd = Command(connection, """
                SELECT CASE WHEN CONVERT(sysname, SERVERPROPERTY('MachineName'))=@machine
                    AND CONVERT(nvarchar(64),SERVERPROPERTY('ProductVersion'))=@build
                    AND CONVERT(nvarchar(128),SERVERPROPERTY('Edition')) LIKE N'Developer Edition%'
                    AND DB_NAME()=@db AND ORIGINAL_LOGIN()=@login
                    AND (@bootstrap=1 OR (IS_SRVROLEMEMBER('sysadmin')=0 AND IS_MEMBER('db_owner')=0
                        AND ISNULL(HAS_PERMS_BY_NAME(NULL,NULL,'CONTROL SERVER'),0)=0
                        AND ISNULL(HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','CONTROL'),0)=0))
                    THEN 1 ELSE 0 END
                """);
            cmd.Parameters.AddWithValue("@machine", scope.Instance);
            cmd.Parameters.AddWithValue("@build", Build);
            cmd.Parameters.AddWithValue("@db", database);
            cmd.Parameters.AddWithValue("@login", Login(role));
            cmd.Parameters.AddWithValue("@bootstrap", role == "bootstrap");
            Need((int?)await cmd.ExecuteScalarAsync(ct) == 1, "D04_WRONG_SERVER_DATABASE_OR_PRIVILEGE");
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static SqlCommand Command(SqlConnection db, string sql, SqlTransaction? transaction = null) =>
        new(sql, db, transaction) { CommandTimeout = 25 };

    private async Task Execute(SqlConnection db, string sql)
    {
        await using var command = Command(db, sql);
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<long> Count(SqlConnection db, string sql)
    {
        await using var command = Command(db, sql);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }

    private async Task InRollback(SqlConnection db, string sql, int[] deniedNumbers)
    {
        await using var tx = (SqlTransaction)await db.BeginTransactionAsync(ct);
        try
        {
            await using var command = Command(db, sql, tx);
            var affected = await command.ExecuteNonQueryAsync(ct);
            Need(deniedNumbers.Length == 0 && affected == 1, "D04_UNEXPECTED_SQL_ALLOW");
        }
        catch (SqlException error) when (deniedNumbers.Contains(error.Number))
        {
            return;
        }
        finally
        {
            if (tx.Connection is not null)
                await tx.RollbackAsync(CancellationToken.None);
        }
    }

    private async Task<string> ReadTenant(SqlConnection observer, string slot)
    {
        await using var command = Command(observer, """
            SELECT CONVERT(varchar(64), HASHBYTES('SHA2_256', CONCAT(
              CONVERT(varchar(36), b.TenantId), ':', CONVERT(varchar(36), b.BindingId), ':',
              b.BindingVersion, ':', CONVERT(varchar(33),b.CreatedUtc,126), ':',
              s.SchemaVersion, ':', CONVERT(varchar(33),s.AppliedUtc,126), ':',
              p.ProbeId, ':', p.Marker)), 2)
            FROM dbo.__PreprodTenantBinding b CROSS JOIN dbo.__PreprodTenantSchemaHistory s
            CROSS JOIN dbo.LabD04Probe p
            WHERE b.TenantId=@tenant AND b.BindingId=@binding AND b.BindingVersion=1
            AND s.SchemaVersion=N'B4_TENANT_20261118_001'
            AND p.ProbeId=1 AND p.Marker=N'LAB_D04_SYNTHETIC'
            AND (SELECT COUNT_BIG(*) FROM dbo.__PreprodTenantBinding)=1
            AND (SELECT COUNT_BIG(*) FROM dbo.__PreprodTenantSchemaHistory)=1
            AND (SELECT COUNT_BIG(*) FROM dbo.LabD04Probe)=1
            """);
        command.Parameters.AddWithValue("@tenant", scope.Tenants[slot]);
        command.Parameters.AddWithValue("@binding", scope.Bindings[slot]);
        return await command.ExecuteScalarAsync(ct) as string ?? throw new RehearsalError("D04_TENANT_WITNESS_MISSING");
    }

    private async Task<Dictionary<string, string>> SnapshotAsync()
    {
        var values = new Dictionary<string, string>();
        var databaseIds = new HashSet<long>();
        string[][] history =
        [
            ["__PreprodIdentitySchemaHistory", "B3_20261117_001", "__B5IdentityGuardHistory", "B5_20261118_001"],
            ["__PreprodTenantRegistrySchemaHistory", "B4_20261118_001", "__B5RegistryGuardHistory", "B5_20261118_001"],
            ["__B5SchemaHistory", "B5_20261118_001"]
        ];
        foreach (var slot in DbSlots)
        {
            await using var observer = await OpenAsync("observer", scope.Databases[slot]);
            Need(databaseIds.Add(await Count(observer, "SELECT DB_ID()")), "D04_DATABASES_NOT_DISTINCT");
            if (slot.StartsWith("tenant", StringComparison.Ordinal))
            {
                values.Add(slot, await ReadTenant(observer, slot[^1..]));
                continue;
            }

            var shape = history[Array.IndexOf(DbSlots, slot)];
            for (var offset = 0; offset < shape.Length; offset += 2)
            {
                var table = shape[offset];
                await using var cmd = Command(observer, $"""
                    SELECT CONVERT(varchar(64), HASHBYTES('SHA2_256',
                        CONCAT(SchemaVersion,':',CONVERT(varchar(33),AppliedUtc,126))),2)
                    FROM dbo.[{table}]
                    WHERE SchemaVersion=@version AND (SELECT COUNT_BIG(*) FROM dbo.[{table}])=1
                    """);
                cmd.Parameters.AddWithValue("@version", shape[offset + 1]);
                values.Add(table, await cmd.ExecuteScalarAsync(ct) as string ??
                    throw new RehearsalError("D04_SCHEMA_WITNESS_MISSING"));
            }
        }
        return values;
    }

    internal async Task<object> VerifyAsync()
    {
        Need(request.Credentials.Keys.ToHashSet().SetEquals(Roles), "D04_ROLE_CREDENTIAL_SCOPE");
        var before = await SnapshotAsync();
        foreach (var slot in new[] { "A", "B" })
        {
            var database = scope.Databases["tenant" + slot];
            var other = scope.Databases[slot == "A" ? "tenantB" : "tenantA"];
            await using var api = await OpenAsync("api" + slot, database);
            await using var observer = await OpenAsync("observer", database);
            Need(await Count(api, "SELECT COUNT_BIG(*) FROM dbo.LabD04Probe") == 1, "D04_API_OWN_READ");
            const string insert = "INSERT dbo.LabD04Probe(ProbeId,Marker) VALUES (2,N'LAB_D04_ROLLBACK')";
            await InRollback(api, insert, []);
            await InRollback(api, "CREATE TABLE dbo.LabD04DdlDenied (Value int)", [262, 229]);
            await InRollback(observer, insert, [229]);
            await InRollback(api, $"SELECT COUNT_BIG(*) FROM [{other}].dbo.LabD04Probe", [916, 229]);
        }
        var after = await SnapshotAsync();
        Need(before.SequenceEqual(after), "D04_SQL_FINGERPRINT_DRIFT");
        return new
        {
            status = "SQL_ROLE_PROBES_OBSERVED", databaseCount = 5, sameInstanceChecks = "OBSERVED",
            apiOwnReadAndRolledBackInsert = 2, crossTenantDenied = 2, observerInsertDenied = 2, apiDdlDenied = 2,
            independentObserverFingerprints = after, beforeAfterEqual = true
        };
    }
}
