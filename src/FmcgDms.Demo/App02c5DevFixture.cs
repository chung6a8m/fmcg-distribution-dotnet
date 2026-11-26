using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

internal static class App02c5DevFixture
{
    internal static Task SeedSiblingSku(SqlConnection db, string tenant) => db.ExecuteAsync("""
        DECLARE @sku uniqueidentifier=NEWID(), @now datetime2(7)=SYSUTCDATETIME();
        INSERT dbo.Sku(Id,TenantId,Code,Name,BaseUom)
        VALUES(@sku,@tenant,N'C5-SIBLING',N'Hàng đối chiếu C.5',N'EA');
        INSERT dbo.StockPool(TenantId,StockLocationId,SkuId,OnHandBaseQuantity)
        SELECT @tenant,Id,@sku,10 FROM dbo.StockLocation WHERE TenantId=@tenant AND Code=N'C2-MAIN';
        INSERT dbo.PhysicalStockOpening(TenantId,StockLocationId,SkuId,SnapshotCode,BaseQuantity,CostAmount)
        SELECT @tenant,Id,@sku,N'C5-SIBLING-v1',IIF(Code=N'C2-MAIN',10,0),IIF(Code=N'C2-MAIN',60,0)
        FROM dbo.StockLocation WHERE TenantId=@tenant AND Code IN (N'C2-MAIN',N'C2-TRUCK');
        INSERT dbo.PhysicalStockBalance(TenantId,StockLocationId,SkuId,OnHandBaseQuantity,OnHandCostAmount)
        SELECT TenantId,StockLocationId,SkuId,BaseQuantity,CostAmount FROM dbo.PhysicalStockOpening
        WHERE TenantId=@tenant AND SkuId=@sku AND SnapshotCode=N'C5-SIBLING-v1';
        INSERT dbo.C2CommercialPolicy(TenantId,Id,CustomerId,SkuId,Version,Decision,EffectiveFromUtc,EffectiveUntilUtc,
            Maker,Checker,ApprovedAtUtc,EvidenceRef,UnitPrice,DiscountPerUnit,TaxRate,TermsDays,CreditCap,SellerTimeZone)
        SELECT @tenant,NEWID(),Id,@sku,1,'APPROVED',DATEADD(day,-1,@now),DATEADD(day,1,@now),
            N'c5-fixture-maker',N'c5-fixture-checker',DATEADD(day,-1,@now),N'c5-sibling-v1',22,0,0,7,1000,'Asia/Ho_Chi_Minh'
        FROM dbo.Customer WHERE TenantId=@tenant AND Code=N'C2-FIXTURE';
        """, new { tenant });

    internal static async Task VerifyCurrentAuthority(DemoRun run, HttpClient client, string webUrl,
        string token, string actor, CancellationToken ct)
    {
        const string tenant = "tenant-a";
        await run.Verify(tenant);
        await using var db = new SqlConnection(run.Connection(tenant));
        using var original = await Report("/ar");
        original.EnsureSuccessStatusCode();
        using var originalBody = JsonDocument.Parse(await original.Content.ReadAsStringAsync(ct));
        string cutoff = originalBody.RootElement.GetProperty("asOf").GetString()!;
        string pinned = "/ar?download=true&asOf=" + Uri.EscapeDataString(cutoff);
        const string grant = """
            UPDATE dbo.App02ActorRoleGrant SET IsActive=@active,ChangedBy=N'c5-dev-oracle',ChangedAtUtc=SYSUTCDATETIME()
            WHERE TenantId=@tenant AND ActorId=@actor AND RoleCode=N'sales'
            """;
        const string assignment = """
            UPDATE dbo.C4DeliveryWriter SET ActorId=@writer
            WHERE TenantId=@tenant AND TripId=(SELECT Id FROM dbo.Trip WHERE TenantId=@tenant AND Code=N'C2-TRIP')
            """;
        try
        {
            if (await db.ExecuteAsync(grant, new { tenant, actor, active = false }) != 1)
                throw new InvalidOperationException("Expected a single current Sales grant.");
            using var denied = await Report(pinned);
            if (denied.StatusCode != HttpStatusCode.Forbidden)
                throw new InvalidOperationException("Historical AR export bypassed revoked Sales grant.");
        }
        finally { await db.ExecuteAsync(grant, new { tenant, actor, active = true }); }
        try
        {
            if (await db.ExecuteAsync(assignment, new { tenant, writer = "c5-dev-other-writer" }) != 1)
                throw new InvalidOperationException("Expected a single current trip assignment.");
            using var unassigned = await Report(pinned);
            unassigned.EnsureSuccessStatusCode();
            using var body = JsonDocument.Parse(await unassigned.Content.ReadAsStringAsync(ct));
            if (body.RootElement.GetProperty("expected").ValueKind != JsonValueKind.Null
                || body.RootElement.GetProperty("knownBalance").ValueKind != JsonValueKind.Null
                || body.RootElement.GetProperty("sources").GetArrayLength() != 0)
                throw new InvalidOperationException("Historical AR export exposed an unassigned trip.");
        }
        finally { await db.ExecuteAsync(assignment, new { tenant, writer = actor }); }
        Console.WriteLine("APP-02C.5 historical AR export respects Sales revocation and current trip assignment.");

        async Task<HttpResponseMessage> Report(string suffix)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, webUrl + "/api/synthetic/app02c5" + suffix);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await client.SendAsync(request, ct);
        }
    }
}
