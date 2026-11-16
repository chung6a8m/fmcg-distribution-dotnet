using System.Net;
using System.Net.Http.Json;
using Dapper;
using FmcgDms.Api.Persistence;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FmcgDms.Api.Tests;

[Collection("SqlScratch")]
public sealed class App01a2CommandTests(SqlScratchFixture fixture)
{
    private const string A = "tenant-a";
    private const string B = "tenant-b";
    private const string Producer = "synthetic-test-writer";
    private const string Root = "/api/synthetic/app01a/commands/";

    private sealed record Scenario(Guid Customer, Guid ShipTo, Guid Sku, Guid Warehouse,
        Guid Vehicle, Guid Trip, Guid Order, Guid OrderLine);

    private static string NewId() => Guid.NewGuid().ToString("N");

    private Scenario Seed(string tenant, decimal onHand = 100, decimal orderQuantity = 40,
        decimal creditLimit = 100)
    {
        string connectionString = tenant == A
            ? fixture.TenantAConnectionString : fixture.TenantBConnectionString;
        using var db = new SqlConnection(connectionString);
        var s = new Scenario(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        db.Execute("""
            INSERT dbo.Customer(Id,TenantId,Code,Name) VALUES(@Customer,@Tenant,@Code,N'Test Customer');
            INSERT dbo.ShipToSnapshot(Id,TenantId,CustomerId,DisplayName,Address)
                VALUES(@ShipTo,@Tenant,@Customer,N'Test ShipTo',N'Test Address');
            INSERT dbo.Sku(Id,TenantId,Code,Name,BaseUom)
                VALUES(@Sku,@Tenant,@SkuCode,N'Test SKU',N'EA');
            INSERT dbo.StockLocation(Id,TenantId,Code,Kind)
                VALUES(@Warehouse,@Tenant,@WhCode,N'WAREHOUSE');
            INSERT dbo.StockLocation(Id,TenantId,Code,Kind)
                VALUES(@Vehicle,@Tenant,@VehicleCode,N'VEHICLE');
            INSERT dbo.Trip(Id,TenantId,Code,BusinessDate,VehicleLocationId)
                VALUES(@Trip,@Tenant,@TripCode,CONVERT(date,SYSUTCDATETIME()),@Vehicle);
            INSERT dbo.AOrder(Id,TenantId,OrderNumber,CustomerId,ShipToSnapshotId,TripId,BusinessDate)
                VALUES(@Order,@Tenant,@OrderCode,@Customer,@ShipTo,@Trip,CONVERT(date,SYSUTCDATETIME()));
            INSERT dbo.AOrderLine(Id,TenantId,AOrderId,LineNumber,SkuId,
                BaseQuantity,UomCode,UomToBaseFactor,UnitPrice)
                VALUES(@OrderLine,@Tenant,@Order,1,@Sku,@OrderQuantity,N'EA',1,15);
            INSERT dbo.StockPool(TenantId,StockLocationId,SkuId,OnHandBaseQuantity)
                VALUES(@Tenant,@Warehouse,@Sku,@OnHand);
            INSERT dbo.BCreditPool(TenantId,TripId,CustomerId,PoolCode,CurrencyUnit,ApprovedLimit)
                VALUES(@Tenant,@Trip,@Customer,N'B',N'SYNTHETIC_UNITLESS',@CreditLimit);
            """, new
        {
            Tenant = tenant, s.Customer, s.ShipTo, s.Sku, s.Warehouse, s.Vehicle,
            s.Trip, s.Order, s.OrderLine, OrderQuantity = orderQuantity, OnHand = onHand,
            CreditLimit = creditLimit,
            Code = NewId(), SkuCode = NewId(), WhCode = NewId(), VehicleCode = NewId(),
            TripCode = NewId(), OrderCode = NewId()
        });
        return s;
    }

    private static ReserveAStockCommand Reserve(Scenario s, decimal quantity = 40,
        string? command = null, string? source = null) =>
        new(Producer, command ?? NewId(), source ?? NewId(),
            s.Order, s.OrderLine, s.Warehouse, quantity);

    private static GrantBQuotaCommand Quota(Scenario s, decimal quantity = 30,
        string? command = null, string? source = null) =>
        new(Producer, command ?? NewId(), source ?? NewId(),
            s.Trip, s.Warehouse, s.Sku, quantity);

    private static ConfirmAOrderCommand Confirm(Scenario s) =>
        new(Producer, NewId(), NewId(), s.Order);

    private static GrantBCreditCommand Credit(Scenario s, decimal amount,
        string? command = null, string? source = null, Guid? shipTo = null,
        string pool = "B") =>
        new(Producer, command ?? NewId(), source ?? NewId(),
            s.Trip, s.Customer, shipTo ?? s.ShipTo, pool, "SYNTHETIC_UNITLESS", amount);

    private static async Task<(HttpStatusCode Code, BusinessCommandOutcome Outcome)> Send<T>(
        HttpClient client, string route, T command)
    {
        using var response = await client.PostAsJsonAsync(
            Root + route, command, TestContext.Current.CancellationToken);
        var outcome = await response.Content.ReadFromJsonAsync<BusinessCommandOutcome>(
            TestContext.Current.CancellationToken);
        return (response.StatusCode, Assert.IsType<BusinessCommandOutcome>(outcome));
    }

    private static SqlConnection Open(SqlScratchFixture fixture, string tenant) =>
        new(tenant == A ? fixture.TenantAConnectionString : fixture.TenantBConnectionString);

    [Fact]
    public void Allocation_pool_migrations_are_replay_safe_in_two_physical_tenants()
    {
        ScratchMigrator.Migrate(fixture.TenantAConnectionString, A, fixture.Nonce);
        ScratchMigrator.Migrate(fixture.TenantBConnectionString, B, fixture.Nonce);
        foreach (string tenant in new[] { A, B })
        {
            using var db = Open(fixture, tenant);
            Assert.Equal(1, db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM dbo.VersionInfo WHERE Version=202611160002"));
            Assert.Equal(1, db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM sys.tables WHERE name=N'StockPool'"));
            Assert.Equal(1, db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM sys.tables WHERE name=N'BCreditPool'"));
        }
    }

    [Fact]
    public async Task A_confirm_requires_full_hold_and_does_not_book_AR_COGS_or_move_on_hand()
    {
        var s = Seed(A);
        using var factory = fixture.CreateFactory();
        using var a = SqlScratchFixture.WithTenant(factory.CreateClient(), A);
        var early = await Send(a, "confirm-a-order", Confirm(s));
        Assert.Equal("REJECTED", early.Outcome.Status);
        Assert.Equal("ORDER_NOT_FULLY_RESERVED", early.Outcome.ReasonCode);

        Assert.Equal("APPLIED", (await Send(a, "reserve-a-stock", Reserve(s))).Outcome.Status);
        Assert.Equal("APPLIED", (await Send(a, "grant-b-quota", Quota(s))).Outcome.Status);
        var confirm = await Send(a, "confirm-a-order", Confirm(s));
        Assert.Equal("A_ORDER_CONFIRMED", confirm.Outcome.OutcomeCode);
        Assert.NotNull(confirm.Outcome.SourceId);

        using var db = Open(fixture, A);
        Assert.Equal("CONFIRMED", db.ExecuteScalar<string>(
            "SELECT Status FROM dbo.AOrder WHERE Id=@Order", new { s.Order }));
        var pool = db.QuerySingle<(decimal OnHandBaseQuantity, decimal AllocatedBaseQuantity)>(
            "SELECT OnHandBaseQuantity,AllocatedBaseQuantity FROM dbo.StockPool WHERE StockLocationId=@Warehouse AND SkuId=@Sku",
            new { s.Warehouse, s.Sku });
        Assert.Equal(100m, pool.OnHandBaseQuantity);
        Assert.Equal(70m, pool.AllocatedBaseQuantity);
        Assert.Equal(70m, db.ExecuteScalar<decimal>("""
            SELECT SUM(GrantedBaseQuantity-ConsumedBaseQuantity-ReleasedBaseQuantity)
            FROM dbo.StockRight WHERE StockLocationId=@Warehouse AND SkuId=@Sku
            """, new { s.Warehouse, s.Sku }));
        Assert.Equal(2, db.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.LedgerEffect WHERE LedgerKind IN (N'RIGHT_A',N'RIGHT_B')"));
        Assert.Equal(0, db.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.LedgerEffect WHERE LedgerKind IN (N'AR',N'COGS',N'REVENUE',N'STOCK')"));
    }

    [Fact]
    public async Task A_and_B_share_stock_capacity_and_same_source_cannot_be_reallocated()
    {
        var s = Seed(A);
        using var factory = fixture.CreateFactory();
        using var client = SqlScratchFixture.WithTenant(factory.CreateClient(), A);
        string source = NewId();
        Assert.Equal("APPLIED", (await Send(client, "reserve-a-stock", Reserve(s, 40, source: source))).Outcome.Status);
        var duplicate = await Send(client, "grant-b-quota", Quota(s, 30, source: source));
        Assert.Equal("SOURCE_ALREADY_USED", duplicate.Outcome.ReasonCode);
        var tooMuch = await Send(client, "grant-b-quota", Quota(s, 61));
        Assert.Equal("STOCK_INSUFFICIENT", tooMuch.Outcome.ReasonCode);
        var exact = await Send(client, "grant-b-quota", Quota(s, 60));
        Assert.Equal("APPLIED", exact.Outcome.Status);
        using var db = Open(fixture, A);
        Assert.Equal(100m, db.ExecuteScalar<decimal>(
            "SELECT AllocatedBaseQuantity FROM dbo.StockPool WHERE StockLocationId=@Warehouse AND SkuId=@Sku",
            new { s.Warehouse, s.Sku }));
        Assert.Equal(2, db.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.StockRight WHERE StockLocationId=@Warehouse AND SkuId=@Sku",
            new { s.Warehouse, s.Sku }));
    }

    [Fact]
    public async Task B_credit_grant_requires_matching_customer_shipto_policy_pool_and_limit()
    {
        var s = Seed(A, creditLimit: 100);
        using var db = Open(fixture, A);
        Guid wrongCustomer = Guid.NewGuid(), wrongShipTo = Guid.NewGuid();
        db.Execute("""
            INSERT dbo.Customer(Id,TenantId,Code,Name)
                VALUES(@Customer,@Tenant,@Code,N'Other Customer');
            INSERT dbo.ShipToSnapshot(Id,TenantId,CustomerId,DisplayName,Address)
                VALUES(@ShipTo,@Tenant,@Customer,N'Other ShipTo',N'Other Address')
            """, new { Tenant = A, Customer = wrongCustomer, ShipTo = wrongShipTo, Code = NewId() });

        using var factory = fixture.CreateFactory();
        using var client = SqlScratchFixture.WithTenant(factory.CreateClient(), A);
        var first = Credit(s, 60);
        var applied = await Send(client, "grant-b-credit", first);
        Assert.Equal("B_CREDIT_GRANTED", applied.Outcome.OutcomeCode);
        var replay = await Send(client, "grant-b-credit", first);
        Assert.True(replay.Outcome.Replayed);
        Assert.Equal(applied.Outcome.SourceId, replay.Outcome.SourceId);
        Assert.Equal("SOURCE_ALREADY_USED",
            (await Send(client, "grant-b-credit", Credit(s, 10, source: first.SourceNumber))).Outcome.ReasonCode);
        Assert.Equal("CUSTOMER_SHIPTO_MISMATCH",
            (await Send(client, "grant-b-credit", Credit(s, 10, shipTo: wrongShipTo))).Outcome.ReasonCode);
        Assert.Equal("POOL_UNSUPPORTED",
            (await Send(client, "grant-b-credit", Credit(s, 10, pool: "A"))).Outcome.ReasonCode);
        Assert.Equal("CREDIT_LIMIT_INSUFFICIENT",
            (await Send(client, "grant-b-credit", Credit(s, 50))).Outcome.ReasonCode);
        Assert.Equal(60m, db.ExecuteScalar<decimal>(
            "SELECT GrantedAmount FROM dbo.BCreditPool WHERE TripId=@Trip AND CustomerId=@Customer",
            new { s.Trip, s.Customer }));
        Assert.Equal(1, db.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.BCreditGrant WHERE TripId=@Trip AND CustomerId=@Customer",
            new { s.Trip, s.Customer }));
    }

    [Fact]
    public async Task Same_identity_and_canonical_decimal_replay_but_changed_payload_conflicts_no_effect()
    {
        var s = Seed(A);
        using var factory = fixture.CreateFactory();
        using var client = SqlScratchFixture.WithTenant(factory.CreateClient(), A);
        var first = Reserve(s, 12.0m);
        var applied = await Send(client, "reserve-a-stock", first);
        var replay = await Send(client, "reserve-a-stock", first with { BaseQuantity = 12.000m });
        var changed = await Send(client, "reserve-a-stock", first with { BaseQuantity = 13m });
        Assert.Equal("APPLIED", applied.Outcome.Status);
        Assert.Equal(HttpStatusCode.OK, replay.Code);
        Assert.True(replay.Outcome.Replayed);
        Assert.Equal(applied.Outcome.SourceId, replay.Outcome.SourceId);
        Assert.Equal(HttpStatusCode.Conflict, changed.Code);
        Assert.Equal("IDEMPOTENCY_PAYLOAD_MISMATCH", changed.Outcome.ReasonCode);
        using var db = Open(fixture, A);
        Assert.Equal(1, db.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.StockRight WHERE StockLocationId=@Warehouse AND SkuId=@Sku",
            new { s.Warehouse, s.Sku }));
        Assert.Equal(12m, db.ExecuteScalar<decimal>(
            "SELECT AllocatedBaseQuantity FROM dbo.StockPool WHERE StockLocationId=@Warehouse AND SkuId=@Sku",
            new { s.Warehouse, s.Sku }));
    }

    [Fact]
    public async Task Rejected_outcome_is_durable_and_changed_rejected_payload_conflicts()
    {
        var s = Seed(A);
        using var factory = fixture.CreateFactory();
        using var client = SqlScratchFixture.WithTenant(factory.CreateClient(), A);
        var r = Quota(s, 101);
        var denied = await Send(client, "grant-b-quota", r);
        var replay = await Send(client, "grant-b-quota", r);
        var changed = await Send(client, "grant-b-quota", r with { BaseQuantity = 10 });
        Assert.Equal("STOCK_INSUFFICIENT", denied.Outcome.ReasonCode);
        Assert.Equal("STOCK_INSUFFICIENT", replay.Outcome.ReasonCode);
        Assert.True(replay.Outcome.Replayed);
        Assert.Equal(HttpStatusCode.Conflict, changed.Code);
        using var db = Open(fixture, A);
        Assert.Equal(0m, db.ExecuteScalar<decimal>(
            "SELECT AllocatedBaseQuantity FROM dbo.StockPool WHERE StockLocationId=@Warehouse AND SkuId=@Sku",
            new { s.Warehouse, s.Sku }));
        Assert.Equal(0, db.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.StockRight WHERE StockLocationId=@Warehouse AND SkuId=@Sku",
            new { s.Warehouse, s.Sku }));
    }

    [Fact]
    public async Task Business_writes_use_signed_tenant_not_header_or_foreign_resource_ids()
    {
        var aScenario = Seed(A);
        var bScenario = Seed(B);
        using var factory = fixture.CreateFactory();
        using var a = SqlScratchFixture.WithTenant(factory.CreateClient(), A);
        using var b = SqlScratchFixture.WithTenant(factory.CreateClient(), B);
        b.DefaultRequestHeaders.Add("X-Tenant-Id", A);
        Assert.Equal("ORDER_NOT_FOUND",
            (await Send(b, "reserve-a-stock", Reserve(aScenario))).Outcome.ReasonCode);
        Assert.Equal("TRIP_NOT_PREPARED",
            (await Send(b, "grant-b-quota", Quota(aScenario))).Outcome.ReasonCode);
        Assert.Equal("ORDER_NOT_FULLY_RESERVED",
            (await Send(a, "confirm-a-order", Confirm(aScenario))).Outcome.ReasonCode);
        Assert.Equal("APPLIED",
            (await Send(b, "reserve-a-stock", Reserve(bScenario))).Outcome.Status);
        using var dbA = Open(fixture, A);
        using var dbB = Open(fixture, B);
        Assert.Equal(0m, dbA.ExecuteScalar<decimal>(
            "SELECT AllocatedBaseQuantity FROM dbo.StockPool WHERE StockLocationId=@Warehouse AND SkuId=@Sku",
            new { aScenario.Warehouse, aScenario.Sku }));
        Assert.Equal(40m, dbB.ExecuteScalar<decimal>(
            "SELECT AllocatedBaseQuantity FROM dbo.StockPool WHERE StockLocationId=@Warehouse AND SkuId=@Sku",
            new { bScenario.Warehouse, bScenario.Sku }));
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync(Root + "grant-b-quota", Quota(aScenario),
                TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Concurrent_A_and_B_commands_cannot_exceed_one_physical_pool()
    {
        var s = Seed(A, onHand: 100, orderQuantity: 70);
        using var factory = fixture.CreateFactory();
        using var first = SqlScratchFixture.WithTenant(factory.CreateClient(), A);
        using var second = SqlScratchFixture.WithTenant(factory.CreateClient(), A);
        var calls = await Task.WhenAll(
            Send(first, "reserve-a-stock", Reserve(s, 70)),
            Send(second, "grant-b-quota", Quota(s, 70)));
        Assert.Single(calls, x => x.Outcome.Status == "APPLIED");
        Assert.Single(calls, x => x.Outcome.ReasonCode == "STOCK_INSUFFICIENT");
        using var db = Open(fixture, A);
        Assert.Equal(70m, db.ExecuteScalar<decimal>(
            "SELECT AllocatedBaseQuantity FROM dbo.StockPool WHERE StockLocationId=@Warehouse AND SkuId=@Sku",
            new { s.Warehouse, s.Sku }));
        Assert.Equal(1, db.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.StockRight WHERE StockLocationId=@Warehouse AND SkuId=@Sku",
            new { s.Warehouse, s.Sku }));
    }

    [Fact]
    public async Task Concurrent_retransmission_creates_a_single_durable_effect()
    {
        var s = Seed(A);
        using var factory = fixture.CreateFactory();
        using var first = SqlScratchFixture.WithTenant(factory.CreateClient(), A);
        using var second = SqlScratchFixture.WithTenant(factory.CreateClient(), A);
        var request = Quota(s, 25);
        var replies = await Task.WhenAll(
            Send(first, "grant-b-quota", request),
            Send(second, "grant-b-quota", request));
        Assert.All(replies, r => Assert.Equal("B_QUOTA_GRANTED", r.Outcome.OutcomeCode));
        Assert.Single(replies, r => r.Outcome.Replayed);
        Assert.Equal(replies[0].Outcome.SourceId, replies[1].Outcome.SourceId);
        using var db = Open(fixture, A);
        Assert.Equal(1, db.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.StockRight WHERE StockLocationId=@Warehouse AND SkuId=@Sku",
            new { s.Warehouse, s.Sku }));
    }

    [Fact]
    public async Task SQL_fault_after_stock_allocation_rolls_back_command_source_right_and_pool()
    {
        var s = Seed(A);
        using var db = Open(fixture, A);
        db.Execute("""
            CREATE TRIGGER dbo.APP01A2_TestRollback ON dbo.StockRight AFTER INSERT AS
            BEGIN
                THROW 51001, 'APP01A2 synthetic fault injection', 1;
            END;
            """);
        string commandId = NewId();
        using var factory = fixture.CreateFactory();
        using var client = SqlScratchFixture.WithTenant(factory.CreateClient(), A);
        try
        {
            // TestServer may return 500 or rethrow the unhandled SQL exception.
            try
            {
                using var response = await client.PostAsJsonAsync(
                    Root + "grant-b-quota", Quota(s, 10, command: commandId),
                    TestContext.Current.CancellationToken);
                Assert.False(response.IsSuccessStatusCode);
            }
            catch (Exception ex) when (ex is SqlException or InvalidOperationException)
            {
                // The SQL trigger intentionally aborts the transaction.
            }
        }
        finally
        {
            db.Execute("DROP TRIGGER dbo.APP01A2_TestRollback");
        }
        Assert.Equal(0m, db.ExecuteScalar<decimal>(
            "SELECT AllocatedBaseQuantity FROM dbo.StockPool WHERE StockLocationId=@Warehouse AND SkuId=@Sku",
            new { s.Warehouse, s.Sku }));
        Assert.Equal(0, db.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.StockRight WHERE StockLocationId=@Warehouse AND SkuId=@Sku",
            new { s.Warehouse, s.Sku }));
        Assert.Equal(0, db.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.BusinessCommand WHERE TenantId=@Tenant AND ProducerId=@Producer AND CommandId=@Command",
            new { Tenant = A, Producer, Command = commandId }));
    }
}
