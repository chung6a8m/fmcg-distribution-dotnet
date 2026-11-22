using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FmcgDms.Api.Tests;

[Collection("SqlScratch")]
public sealed class App02c1Tests(SqlScratchFixture fixture)
{
    private const string Route = "/api/synthetic/app02c1";
    private readonly CancellationToken ct = TestContext.Current.CancellationToken;
    private static readonly object Address = new
    {
        country = "VN", region = "HCM", locality = "  Khu   DEV ",
        line = " 01   Duong DEV ", recipient = "  Nguoi Nhan  "
    };

    [Fact]
    public async Task Direct_selection_has_no_outlet_and_replays_the_original_persisted_result()
    {
        using var factory = fixture.CreateFactory();
        using var a = await SalesAsync(factory, "tenant-a");
        Guid customerId = await CreateCustomer(a);
        var commandId = Guid.NewGuid();
        var request = new { commandId, customerId, address = Address };
        var applied = await Post(a, "/ship-to-selections", request);
        var replay = await Post(a, "/ship-to-selections", request);
        Guid id = applied.GetProperty("id").GetGuid();
        Assert.Equal(id, replay.GetProperty("id").GetGuid());
        Assert.True(replay.GetProperty("replayed").GetBoolean());
        var recovered = await a.GetFromJsonAsync<JsonElement>(Route + $"/commands/{commandId}", ct);
        Assert.Equal(id, recovered.GetProperty("id").GetGuid());
        using var peer = await SalesAsync(factory, "tenant-a");
        Assert.Equal(HttpStatusCode.NotFound, (await peer.GetAsync(Route + $"/commands/{commandId}", ct)).StatusCode);
        await Post(a, "/ship-to-selections", new { commandId, customerId }, HttpStatusCode.Conflict);

        await using var db = new SqlConnection(fixture.TenantAConnectionString);
        var row = await db.QuerySingleAsync<(string Origin, Guid? Outlet, string Line, string Locality, byte[] Hash)>(
            """
            SELECT Origin,OutletId,JSON_VALUE(Payload,'$.line'),JSON_VALUE(Payload,'$.locality'),PayloadHash
            FROM dbo.C1ShipToSelection WHERE CustomerId=@customerId
            """, new { customerId });
        Assert.Equal(("DIRECT", (Guid?)null, "01 Duong DEV", "Khu DEV"), (row.Origin, row.Outlet, row.Line, row.Locality));
        var expectedJson = "{\"country\":\"VN\",\"region\":\"HCM\",\"locality\":\"Khu DEV\",\"line\":\"01 Duong DEV\",\"recipient\":\"Nguoi Nhan\"}";
        Assert.Equal(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(expectedJson)), row.Hash);
        Assert.Equal(0, await db.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.C1RegisteredShipTo WHERE CustomerId=@customerId", new { customerId }));
        await Assert.ThrowsAsync<SqlException>(() => db.ExecuteAsync(
            "UPDATE dbo.C1ShipToSelection SET Payload=N'{}' WHERE Id=@id", new { id }));
    }

    [Fact]
    public async Task Registered_revisions_are_scoped_and_preserve_original_selection()
    {
        using var factory = fixture.CreateFactory();
        using var a = await SalesAsync(factory, "tenant-a");
        Guid customerId = await CreateCustomer(a);
        Guid otherCustomer = await CreateCustomer(a);
        var outlet = await Post(a, "/outlets/save", new { commandId = Guid.NewGuid(), customerId, name = "Outlet DEV" });
        Guid outletId = outlet.GetProperty("id").GetGuid();
        await Post(a, "/registered-ship-tos/save", new
        {
            commandId = Guid.NewGuid(), customerId = otherCustomer, outletId, address = Address
        }, HttpStatusCode.NotFound);
        var first = await Post(a, "/registered-ship-tos/save", new
        {
            commandId = Guid.NewGuid(), customerId, outletId, address = Address
        });
        Guid id = first.GetProperty("id").GetGuid();
        var selected = await Post(a, "/ship-to-selections", new
        {
            commandId = Guid.NewGuid(), customerId, registeredId = id, revision = 1
        });
        var revised = await Post(a, "/registered-ship-tos/save", new
        {
            commandId = Guid.NewGuid(), customerId, outletId, id, revision = 1,
            address = new { country = "VN", region = "HN", locality = "Zone", line = "02 Duong DEV", recipient = "DEV" }
        });
        Assert.Equal(2, revised.GetProperty("revision").GetInt32());
        await Post(a, "/ship-to-selections", new
        {
            commandId = Guid.NewGuid(), customerId, registeredId = id, revision = 1
        }, HttpStatusCode.Conflict);
        await using var db = new SqlConnection(fixture.TenantAConnectionString);
        Assert.Equal(new[] { "01 Duong DEV", "02 Duong DEV" }, await db.QueryAsync<string>("""
            SELECT JSON_VALUE(Payload,'$.line') FROM dbo.C1RegisteredShipToRevision
            WHERE RegisteredId=@id ORDER BY Revision
            """, new { id }));
        Assert.Equal("01 Duong DEV", await db.QuerySingleAsync<string>("""
            SELECT JSON_VALUE(Payload,'$.line') FROM dbo.C1ShipToSelection WHERE Id=@selection
            """, new { selection = selected.GetProperty("id").GetGuid() }));
        await Assert.ThrowsAsync<SqlException>(() => db.ExecuteAsync(
            "DELETE dbo.C1RegisteredShipToRevision WHERE RegisteredId=@id AND Revision=2", new { id }));
    }

    [Fact]
    public async Task Signed_tenant_and_current_grant_control_reads_writes_and_recovery()
    {
        using var factory = fixture.CreateFactory();
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Route + "/customers", ct)).StatusCode);
        using var wrongRole = SqlScratchFixture.WithTenant(factory.CreateClient(), "tenant-a", roles: ["warehouse"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await wrongRole.GetAsync(Route + "/customers", ct)).StatusCode);
        using var a = await SalesAsync(factory, "tenant-a");
        using var b = await SalesAsync(factory, "tenant-b");
        a.DefaultRequestHeaders.Add("X-Tenant-Id", "tenant-b");
        Guid customerId = await CreateCustomer(a);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync(Route + $"/customers/{customerId}", ct)).StatusCode);
        await Post(b, "/ship-to-selections", new { commandId = Guid.NewGuid(), customerId, address = Address }, HttpStatusCode.NotFound);
        var invalidCommand = Guid.NewGuid();
        var invalidAddress = new
        {
            commandId = invalidCommand, customerId,
            address = new { country = "VN", region = "UNKNOWN", locality = "Zone", line = "DEV", recipient = "DEV" }
        };
        await Post(a, "/ship-to-selections", invalidAddress, HttpStatusCode.BadRequest);
        var rejected = await Post(a, "/ship-to-selections", invalidAddress, HttpStatusCode.BadRequest);
        Assert.True(rejected.GetProperty("replayed").GetBoolean());
        Assert.Equal("ADDRESS_INVALID", rejected.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync(Route + $"/commands/{invalidCommand}", ct)).StatusCode);
        await using var db = new SqlConnection(fixture.TenantAConnectionString);
        await db.ExecuteAsync("""
            UPDATE dbo.App02ActorRoleGrant SET IsActive=0
            WHERE TenantId='tenant-a' AND ActorId=@actor AND RoleCode='sales'
            """, new { actor = a.DefaultRequestHeaders.GetValues("X-Test-Actor").Single() });
        Assert.Equal(HttpStatusCode.Forbidden, (await a.GetAsync(Route + $"/commands/{invalidCommand}", ct)).StatusCode);
        await Post(a, "/ship-to-selections", new { commandId = Guid.NewGuid(), customerId, address = Address }, HttpStatusCode.Forbidden);
        Assert.Equal(0, await db.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.C1ShipToSelection WHERE CustomerId=@customerId", new { customerId }));
    }

    [Fact]
    public async Task Concurrent_customer_edits_with_same_version_have_one_winner()
    {
        using var factory = fixture.CreateFactory();
        using var a = await SalesAsync(factory, "tenant-a");
        Guid id = await CreateCustomer(a);
        var detail = await a.GetFromJsonAsync<JsonElement>(Route + $"/customers/{id}", ct);
        var customer = detail.GetProperty("customer");
        string? code = customer.GetProperty("code").GetString();
        string? version = customer.GetProperty("version").GetString();
        Assert.Matches("^[0-9a-fA-F]{16}$", version ?? "");
        var results = await Task.WhenAll(new[] { "First edit", "Second edit" }.Select(name =>
            a.PostAsJsonAsync(Route + "/customers/save", new { commandId = Guid.NewGuid(), id, code, name, version }, ct)));
        try
        {
            var actual = await Task.WhenAll(results.Select(async r =>
                $"{(int)r.StatusCode}: {await r.Content.ReadAsStringAsync(ct)}"));
            Assert.True(results.Any(r => r.StatusCode == HttpStatusCode.OK), string.Join("\n", actual));
            Assert.True(results.Any(r => r.StatusCode == HttpStatusCode.Conflict), string.Join("\n", actual));
        }
        finally { foreach (var response in results) response.Dispose(); }
    }

    [Fact]
    public async Task Unsafe_targets_and_misconfigured_sessions_are_refused_before_writes()
    {
        using var validFactory = fixture.CreateFactory();
        using var valid = await SalesAsync(validFactory, "tenant-a");
        Assert.Equal(HttpStatusCode.OK, (await valid.GetAsync(Route + "/session", ct)).StatusCode);
        string anotherInstance = new SqlConnectionStringBuilder(fixture.TenantAConnectionString)
        {
            DataSource = @"(localdb)\OtherInstance"
        }.ConnectionString;
        foreach (string unsafeTarget in new[] { fixture.TenantBConnectionString, fixture.ConnectionString("master"), anotherInstance })
        {
            using var factory = fixture.CreateFactory(tenantAOverride: unsafeTarget);
            using var a = factory.CreateClient();
            a.DefaultRequestHeaders.Authorization = valid.DefaultRequestHeaders.Authorization;
            await Post(a, "/customers/save", new { commandId = Guid.NewGuid(), code = "UNSAFE-C1", name = "Unsafe" },
                HttpStatusCode.Forbidden);
        }
        foreach (string nonce in new[] { "", Guid.NewGuid().ToString("N") })
        {
            using var wrongNonce = fixture.CreateFactory(c1Nonce: nonce);
            using var wrongSession = wrongNonce.CreateClient();
            wrongSession.DefaultRequestHeaders.Authorization = valid.DefaultRequestHeaders.Authorization;
            Assert.Equal(HttpStatusCode.Forbidden, (await wrongSession.GetAsync(Route + "/session", ct)).StatusCode);
        }
        using var disabled = fixture.CreateFactory(c1Enabled: false);
        using var disabledClient = SqlScratchFixture.WithTenant(disabled.CreateClient(), "tenant-a", roles: ["sales"]);
        Assert.Equal(HttpStatusCode.NotFound, (await disabledClient.GetAsync(Route + "/session", ct)).StatusCode);
        await using var db = new SqlConnection(fixture.TenantBConnectionString);
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.Customer WHERE Code=N'UNSAFE-C1'"));
    }

    [Fact]
    public async Task Expired_and_invalidly_signed_tokens_cannot_open_C1_session()
    {
        using var factory = fixture.CreateFactory();
        using var a = await SalesAsync(factory, "tenant-a");
        string actor = a.DefaultRequestHeaders.GetValues("X-Test-Actor").Single();
        string expired = SqlScratchFixture.IssueSyntheticToken("tenant-a", actor, ["sales"], expiresInSeconds: -60);
        string original = a.DefaultRequestHeaders.Authorization!.Parameter!;
        var parts = original.Split('.');
        string forged = $"{parts[0]}.{parts[1]}.{(parts[2][0] == 'A' ? 'B' : 'A')}{parts[2][1..]}";
        foreach (string token in new[] { expired, forged })
        {
            a.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            Assert.Equal(HttpStatusCode.Unauthorized, (await a.GetAsync(Route + "/session", ct)).StatusCode);
        }
    }

    [Fact]
    public async Task Existing_order_snapshot_is_immutable_in_SQL()
    {
        using var factory = fixture.CreateFactory();
        using var a = await SalesAsync(factory, "tenant-a");
        Guid customerId = await CreateCustomer(a);
        Guid id = Guid.NewGuid();
        await using var db = new SqlConnection(fixture.TenantAConnectionString);
        await db.ExecuteAsync("""
            INSERT dbo.ShipToSnapshot(Id,TenantId,CustomerId,DisplayName,Address)
            VALUES(@id,N'tenant-a',@customerId,N'Legacy synthetic',N'Original DEV address')
            """, new { id, customerId });
        await Assert.ThrowsAsync<SqlException>(() => db.ExecuteAsync(
            "UPDATE dbo.ShipToSnapshot SET Address=N'Changed' WHERE Id=@id", new { id }));
        await Assert.ThrowsAsync<SqlException>(() => db.ExecuteAsync(
            "DELETE dbo.ShipToSnapshot WHERE Id=@id", new { id }));
        Assert.Equal("Original DEV address", await db.QuerySingleAsync<string>(
            "SELECT Address FROM dbo.ShipToSnapshot WHERE Id=@id", new { id }));
    }

    [Fact]
    public async Task Fault_after_registered_identity_insert_rolls_back_command_and_master_before_retry()
    {
        using var factory = fixture.CreateFactory();
        using var a = await SalesAsync(factory, "tenant-a");
        Guid customerId = await CreateCustomer(a);
        Guid commandId = Guid.NewGuid();
        var request = new { commandId, customerId, address = Address };
        await using var db = new SqlConnection(fixture.TenantAConnectionString);
        await db.ExecuteAsync($"""
            CREATE TRIGGER dbo.C1_TestRevisionFault ON dbo.C1RegisteredShipToRevision AFTER INSERT AS
            BEGIN
                IF EXISTS (SELECT 1 FROM inserted WHERE CommandId='{commandId:D}')
                    THROW 51022, 'C.1 synthetic rollback fixture', 1;
            END
            """);
        try
        {
            try
            {
                using var failed = await a.PostAsJsonAsync(Route + "/registered-ship-tos/save", request, ct);
                Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            }
            catch (SqlException ex) { Assert.Equal(51022, ex.Number); }
        }
        finally { await db.ExecuteAsync("DROP TRIGGER dbo.C1_TestRevisionFault"); }
        Assert.Equal(HttpStatusCode.NotFound, (await a.GetAsync(Route + $"/commands/{commandId}", ct)).StatusCode);
        Assert.Equal(0, await db.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.C1RegisteredShipTo WHERE CustomerId=@customerId", new { customerId }));
        var retry = await Post(a, "/registered-ship-tos/save", request);
        Assert.False(retry.GetProperty("replayed").GetBoolean());
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM dbo.C1RegisteredShipToRevision
            WHERE CustomerId=@customerId AND CommandId=@commandId AND Revision=1
            """, new { customerId, commandId }));
    }

    private async Task<HttpClient> SalesAsync(TestApiFactory factory, string tenantId)
    {
        string actor = "c1-" + Guid.NewGuid().ToString("N");
        await using var db = new SqlConnection(fixture.ConnectionString(
            tenantId == "tenant-a" ? fixture.TenantADb : fixture.TenantBDb));
        await db.ExecuteAsync("""
            INSERT dbo.App02ActorRoleGrant(TenantId, ActorId, RoleCode, IsActive, ChangedBy)
            VALUES(@tenantId, @actor, N'sales', 1, N'c1-test')
            """, new { tenantId, actor });
        var client = SqlScratchFixture.WithTenant(factory.CreateClient(), tenantId, actor, ["sales"]);
        client.DefaultRequestHeaders.Add("X-Test-Actor", actor);
        return client;
    }

    private async Task<Guid> CreateCustomer(HttpClient client) => (await Post(client, "/customers/save", new
    {
        commandId = Guid.NewGuid(), code = Guid.NewGuid().ToString("N"), name = "Customer DEV"
    })).GetProperty("id").GetGuid();

    private async Task<JsonElement> Post(HttpClient client, string path, object request, HttpStatusCode status = HttpStatusCode.OK)
    {
        using var response = await client.PostAsJsonAsync(Route + path, request, ct);
        Assert.Equal(status, response.StatusCode);
        return response.Content.Headers.ContentType?.MediaType == "application/json"
            ? await response.Content.ReadFromJsonAsync<JsonElement>(ct) : default;
    }
}
