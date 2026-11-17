using System.Net;
using System.Net.Http.Json;
using Dapper;
using FmcgDms.Api.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FmcgDms.Api.Tests;

/// <summary>
/// APP-02A.3 review regressions: suppress the ACTUAL intake HTTP reply,
/// and compete two distinct C identities for one P/APP-01D offer.
/// APP-02A.4 remains the independent 12-group SQL oracle increment.
/// </summary>
public sealed partial class App01e2CrossCommandRaceTests
{
    [Theory]
    [InlineData("tenant-a")]
    [InlineData("tenant-b")]
    public async Task App02a3_lost_intake_ACK_is_recovered_by_owner_lookup_and_same_id(
        string tenant)
    {
        using var factory = fixture.CreateFactory();
        using var setup = SqlScratchFixture.WithTenant(factory.CreateClient(), tenant);
        using var sales = Client(factory, tenant, SalesActor, "sales");
        using var cashier = Client(factory, tenant, CashierActor, "cashier");
        var scene = await SetupA3(tenant, setup, sales);
        App02EventInput prerequisite = NewPEvent(scene.Scene.Trip, scene.Offer);
        App02EventInput c = NewCEvent(scene.Scene.Trip, prerequisite, scene.Count);

        // Real TestServer commits receipt + progress, but the client does
        // not receive the 202. The next request must NOT manufacture a new ID.
        var transport = new AfterServerAckLossHandler(A3 + "intake");
        using var disconnected = Client(
            factory, tenant, CashierActor, "cashier", transport);
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            using var _ = await disconnected.PostAsJsonAsync(
                A3 + "intake", c, TestContext.Current.CancellationToken);
        });
        Assert.Equal(1, transport.Suppressed);

        using var db = Open(tenant);
        Guid received = db.QuerySingle<Guid>("""
            SELECT ReceiptId FROM dbo.App02EventReceipt
            WHERE TenantId=@Tenant AND ProducerId=@Producer AND EventId=@Event
            """, new { Tenant = tenant, Producer = c.ProducerId, Event = c.EventId });
        Assert.Equal("PENDING_DEPENDENCY", db.QuerySingle<string>("""
            SELECT Status FROM dbo.App02EventProgress
            WHERE TenantId=@Tenant AND ReceiptId=@Receipt
            """, new { Tenant = tenant, Receipt = received }));
        Assert.Equal(0, db.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM dbo.App02AcceptanceApplication
            WHERE TenantId=@Tenant AND CReceiptId=@Receipt
            """, new { Tenant = tenant, Receipt = received }));

        var outcome = await cashier.GetFromJsonAsync<App02OutcomeResult>(
            A3 + "outcome?producerId=" + Uri.EscapeDataString(c.ProducerId) +
            "&eventId=" + Uri.EscapeDataString(c.EventId),
            TestContext.Current.CancellationToken);
        Assert.Equal(received, outcome?.ReceiptId);
        Assert.Equal("PENDING_DEPENDENCY", outcome?.Status);

        var replay = await IntakeA3(cashier, c);
        Assert.True(replay.Replayed);
        Assert.Equal(received, replay.ReceiptId);
        Assert.Equal(1, db.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM dbo.App02EventReceipt
            WHERE TenantId=@Tenant AND ProducerId=@Producer AND EventId=@Event
            """, new { Tenant = tenant, Producer = c.ProducerId, Event = c.EventId }));
    }

    [Theory]
    [InlineData("tenant-a")]
    [InlineData("tenant-b")]
    public async Task App02a3_two_distinct_C_event_ids_cannot_accept_same_named_P_twice(
        string tenant)
    {
        using var factory = fixture.CreateFactory();
        using var setup = SqlScratchFixture.WithTenant(factory.CreateClient(), tenant);
        using var sales = Client(factory, tenant, SalesActor, "sales");
        using var cashier = Client(factory, tenant, CashierActor, "cashier");
        var scene = await SetupA3(tenant, setup, sales);
        App02EventInput p = NewPEvent(scene.Scene.Trip, scene.Offer);
        App02EventInput c1 = NewCEvent(scene.Scene.Trip, p, scene.Count);
        App02EventInput c2 = NewCEvent(scene.Scene.Trip, p, scene.Count);
        await IntakeA3(sales, p);
        Guid r1 = Assert.IsType<Guid>((await IntakeA3(cashier, c1)).ReceiptId);
        Guid r2 = Assert.IsType<Guid>((await IntakeA3(cashier, c2)).ReceiptId);
        Assert.NotEqual(r1, r2);

        using var scope = factory.Services.CreateScope();
        var worker = scope.ServiceProvider.GetRequiredService<App02a3ProcessingService>();
        var now = DateTimeOffset.UtcNow;
        string[] results = await Task.WhenAll(
            worker.ProcessAsync(tenant, r1, now, TestContext.Current.CancellationToken),
            worker.ProcessAsync(tenant, r2, now, TestContext.Current.CancellationToken));
        Assert.Equal(1, results.Count(x => x == "APPLIED"));
        Assert.Equal(1, results.Count(x => x == "REJECTED"));

        using var db = Open(tenant);
        Assert.Equal(1, db.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM dbo.CashHandoverAcceptance
            WHERE TenantId=@Tenant AND OfferId=@Offer
            """, new { Tenant = tenant, Offer = scene.Offer }));
        Assert.Equal(1, db.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM dbo.App02AcceptanceApplication
            WHERE TenantId=@Tenant AND CReceiptId IN (@R1,@R2)
            """, new { Tenant = tenant, R1 = r1, R2 = r2 }));
        Assert.Equal(1, db.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM dbo.App02OutboxMessage
            WHERE TenantId=@Tenant AND CReceiptId IN (@R1,@R2)
            """, new { Tenant = tenant, R1 = r1, R2 = r2 }));
        Assert.Equal(2, db.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM dbo.LedgerEffect e
            JOIN dbo.CashHandoverAcceptance a
              ON a.TenantId=e.TenantId AND a.SourceLineId=e.BusinessSourceLineId
            WHERE a.TenantId=@Tenant AND a.OfferId=@Offer
            """, new { Tenant = tenant, Offer = scene.Offer }));
        Assert.Equal(0, db.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM dbo.LedgerEffect e
            JOIN dbo.CashHandoverOffer o
              ON o.TenantId=e.TenantId AND o.SourceLineId=e.BusinessSourceLineId
            WHERE o.TenantId=@Tenant AND o.Id=@Offer
            """, new { Tenant = tenant, Offer = scene.Offer }));
    }
}
