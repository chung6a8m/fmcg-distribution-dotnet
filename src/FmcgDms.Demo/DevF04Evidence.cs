using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

internal static class DevF04Evidence
{
    internal static readonly DateTime FixtureUtc = new(2026, 11, 23, 2, 0, 0, DateTimeKind.Utc);

    internal sealed record ApiPrice(string Sku, Guid? LineId, string Mode, string Currency, decimal Base,
        decimal Discount, decimal Net, decimal Tax, decimal Gross, int Version);
    internal sealed record MasterReceipt(string Tenant, ApiPrice[] Before, ApiPrice[] After, bool Unchanged);
    private sealed record PriceRow(string Sku, decimal Base, decimal Discount, decimal Net, decimal Tax, decimal Gross);
    private sealed record PriceLine(string Sku, Guid OrderLineId, Guid PlanId, Guid QuotePolicyId, Guid FrozenPolicyId,
        Guid ConfirmSourceId, string Mode, string ResidualProfile,
        string Currency, int Version, int PolicyVersion, decimal Ordered, decimal Base, decimal Discount,
        decimal Net, decimal Tax, decimal Gross, decimal GrossSnapshot, decimal QuoteUnitPrice, decimal TaxRate);
    private sealed record Delivery(Guid DeliveryId, Guid OrderLineId, string Sku, decimal Quantity, string Currency,
        decimal Net, decimal Tax, decimal Gross, decimal Payable, decimal Outstanding, decimal Cost, bool InvoiceEligible);

    internal static string CheckpointDirectory(string root, DemoRun run) =>
        Path.Combine(root, "TestResults", "dev-f04", run.Id, "checkpoints");

    internal static async Task Seed(SqlConnection db, string tenant, CancellationToken ct)
    {
        await db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await db.BeginTransactionAsync(ct);
        bool exclusive = tenant == "tenant-a";
        await db.ExecuteAsync(new CommandDefinition("""
            DECLARE @p uniqueidentifier=NEWID(), @q uniqueidentifier=NEWID(),
                @plan uniqueidentifier=NEWID(), @customer uniqueidentifier, @main uniqueidentifier, @truck uniqueidentifier;
            SELECT @customer=Id FROM dbo.Customer WHERE TenantId=@Tenant AND Code=N'C2-FIXTURE';
            SELECT @main=Id FROM dbo.StockLocation WHERE TenantId=@Tenant AND Code=N'C2-MAIN';
            SELECT @truck=Id FROM dbo.StockLocation WHERE TenantId=@Tenant AND Code=N'C2-TRUCK';

            INSERT dbo.Sku(Id,TenantId,Code,Name,BaseUom)
            VALUES(@p,@Tenant,N'P',N'F04 Product P',N'EA'),(@q,@Tenant,N'Q',N'F04 Product Q',N'EA');
            INSERT dbo.StockPool(TenantId,StockLocationId,SkuId,OnHandBaseQuantity)
            VALUES(@Tenant,@main,@p,10),(@Tenant,@main,@q,10);
            INSERT dbo.PhysicalStockOpening(TenantId,StockLocationId,SkuId,SnapshotCode,BaseQuantity,CostAmount)
            VALUES(@Tenant,@main,@p,N'F04-v1',10,100),(@Tenant,@main,@q,N'F04-v1',10,100),
                (@Tenant,@truck,@p,N'F04-v1',0,0),(@Tenant,@truck,@q,N'F04-v1',0,0);
            INSERT dbo.PhysicalStockBalance(TenantId,StockLocationId,SkuId,OnHandBaseQuantity,OnHandCostAmount)
            VALUES(@Tenant,@main,@p,10,100),(@Tenant,@main,@q,10,100),
                (@Tenant,@truck,@p,0,0),(@Tenant,@truck,@q,0,0);
            INSERT dbo.C2CommercialPolicy(TenantId,Id,CustomerId,SkuId,Version,Decision,EffectiveFromUtc,EffectiveUntilUtc,
                Maker,Checker,ApprovedAtUtc,EvidenceRef,UnitPrice,DiscountPerUnit,TaxRate,TermsDays,CreditCap,SellerTimeZone)
            VALUES(@Tenant,NEWID(),@customer,@p,1,'APPROVED',@From,@Until,N'f04-maker',N'f04-checker',
                @From,N'F04-quote-P-v1',@PriceP,0,0.08,7,400000,'Asia/Ho_Chi_Minh'),
                (@Tenant,NEWID(),@customer,@q,1,'APPROVED',@From,@Until,N'f04-maker',N'f04-checker',
                @From,N'F04-quote-Q-v1',@PriceQ,0,0.10,7,400000,'Asia/Ho_Chi_Minh');
            INSERT dbo.F04PricePlan(TenantId,Id,CustomerId,Version,TaxMode,OrderDiscount,
                EffectiveFromUtc,EffectiveUntilUtc,Maker,Checker,ApprovedAtUtc,EvidenceRef)
            VALUES(@Tenant,@plan,@customer,1,@Mode,10001,@From,@Until,N'f04-maker',N'f04-checker',@From,N'F04-plan-v1');
            INSERT dbo.F04PlanQuote(TenantId,PlanId,PolicyId)
            SELECT TenantId,@plan,Id FROM dbo.C2CommercialPolicy
            WHERE TenantId=@Tenant AND CustomerId=@customer AND Version=1 AND SkuId IN(@p,@q);
            """, new
        {
            Tenant = tenant, From = FixtureUtc.AddHours(-1), Until = FixtureUtc.AddDays(1),
            PriceP = exclusive ? 66666.6667m : 72000m, PriceQ = exclusive ? 100000m : 110000m,
            Mode = exclusive ? "EXCLUSIVE" : "INCLUSIVE"
        }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    internal static async Task<MasterReceipt[]?> ServeMasterChange(DemoRun run, string root, Process browser,
        HttpClient client, string webUrl, string tokenA, string tokenB, CancellationToken ct)
    {
        string dir = CheckpointDirectory(root, run);
        Directory.CreateDirectory(dir);
        string request = Path.Combine(dir, "master-change.request");
        while (!File.Exists(request))
        {
            if (browser.HasExited) return null;
            await Task.Delay(80, ct);
        }
        if ((await File.ReadAllTextAsync(request, ct)) != run.Id)
            throw new InvalidDataException("DEV-F04 master-change request has the wrong run ID.");

        var receipts = new List<MasterReceipt>();
        foreach (var (tenant, token) in new[] { ("tenant-a", tokenA), ("tenant-b", tokenB) })
        {
            await run.Verify(tenant);
            ApiPrice[] before = await ReadPrices(client, webUrl, token, ct);
            if (before.Length != 2 || before.Any(row => row.Version != 1))
                throw new InvalidDataException($"DEV-F04 expected a v1 P/Q confirmation in {tenant}.");
            await AppendMasterVersion(run, tenant, ct);
            ApiPrice[] after = await ReadPrices(client, webUrl, token, ct);
            receipts.Add(new MasterReceipt(tenant, before, after, before.SequenceEqual(after)));
        }

        await File.WriteAllTextAsync(Path.Combine(dir, "master-change.json"),
            JsonSerializer.Serialize(receipts, new JsonSerializerOptions { WriteIndented = true }), ct);
        if (receipts.Any(receipt => !receipt.Unchanged))
            throw new InvalidOperationException("DEV-F04 a confirmed API price changed after inserting v2.");
        string ready = Path.Combine(dir, "master-change.ready");
        await File.WriteAllTextAsync(ready + ".partial", run.Id, ct);
        File.Move(ready + ".partial", ready);
        return receipts.ToArray();
    }

    private static async Task<ApiPrice[]> ReadPrices(HttpClient client, string webUrl, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, webUrl + "/api/synthetic/app02c2/orders");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return json.RootElement.EnumerateArray()
            .SelectMany(order => order.GetProperty("lines").EnumerateArray())
            .Where(line => line.TryGetProperty("pricing", out var price) && price.ValueKind == JsonValueKind.Object)
            .Select(line =>
            {
                var price = line.GetProperty("pricing");
                return new ApiPrice(line.GetProperty("sku").GetString()!, line.GetProperty("lineId").GetGuid(),
                    price.GetProperty("mode").GetString()!, price.GetProperty("currency").GetString()!,
                    ReadDecimal(price.GetProperty("base")), ReadDecimal(price.GetProperty("discount")),
                    ReadDecimal(price.GetProperty("net")), ReadDecimal(price.GetProperty("tax")),
                    ReadDecimal(price.GetProperty("gross")), (int)ReadDecimal(price.GetProperty("version")));
            }).OrderBy(line => line.Sku, StringComparer.Ordinal).ToArray();
    }

    private static decimal ReadDecimal(JsonElement value) => value.ValueKind == JsonValueKind.Number
        ? value.GetDecimal() : decimal.Parse(value.GetString()!, System.Globalization.CultureInfo.InvariantCulture);

    private static async Task AppendMasterVersion(DemoRun run, string tenant, CancellationToken ct)
    {
        await using var db = new SqlConnection(run.Connection(tenant));
        await db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await db.BeginTransactionAsync(ct);
        var parameters = new { Tenant = tenant, From = FixtureUtc.AddMinutes(-30) };
        int prices = await db.ExecuteAsync(new CommandDefinition("""
            INSERT dbo.C2CommercialPolicy(TenantId,Id,CustomerId,SkuId,Version,Decision,EffectiveFromUtc,EffectiveUntilUtc,
                Maker,Checker,ApprovedAtUtc,EvidenceRef,UnitPrice,DiscountPerUnit,TaxRate,TermsDays,CreditCap,SellerTimeZone)
            SELECT p.TenantId,NEWID(),p.CustomerId,p.SkuId,2,p.Decision,@From,p.EffectiveUntilUtc,
                p.Maker,p.Checker,@From,p.EvidenceRef+N'-revised',p.UnitPrice+113,p.DiscountPerUnit,p.TaxRate,
                p.TermsDays,p.CreditCap,p.SellerTimeZone
            FROM dbo.C2CommercialPolicy p JOIN dbo.Sku k ON k.TenantId=p.TenantId AND k.Id=p.SkuId
            WHERE p.TenantId=@Tenant AND p.Version=1 AND k.Code IN(N'P',N'Q')
            """, parameters, tx, cancellationToken: ct));
        int plans = await db.ExecuteAsync(new CommandDefinition("""
            INSERT dbo.F04PricePlan(TenantId,Id,CustomerId,Version,TaxMode,OrderDiscount,
                EffectiveFromUtc,EffectiveUntilUtc,Maker,Checker,ApprovedAtUtc,EvidenceRef)
            SELECT TenantId,NEWID(),CustomerId,2,TaxMode,20003,@From,EffectiveUntilUtc,Maker,Checker,@From,N'F04-plan-v2'
            FROM dbo.F04PricePlan WHERE TenantId=@Tenant AND Version=1
            """, parameters, tx, cancellationToken: ct));
        int links = await db.ExecuteAsync(new CommandDefinition("""
            INSERT dbo.F04PlanQuote(TenantId,PlanId,PolicyId)
            SELECT f.TenantId,f.Id,p.Id
            FROM dbo.F04PricePlan f
            JOIN dbo.C2CommercialPolicy p ON p.TenantId=f.TenantId AND p.CustomerId=f.CustomerId AND p.Version=f.Version
            JOIN dbo.Sku k ON k.TenantId=p.TenantId AND k.Id=p.SkuId
            WHERE f.TenantId=@Tenant AND f.Version=2 AND k.Code IN(N'P',N'Q')
            """, parameters, tx, cancellationToken: ct));
        if (prices != 2 || plans != 1 || links != 2)
            throw new InvalidOperationException("DEV-F04 did not append one v2 plan and two linked v2 prices.");
        await tx.CommitAsync(ct);
    }

    internal static async Task Verify(DemoRun run, string root, string source, string apiSha256, MasterReceipt[]? master,
        CancellationToken ct)
    {
        string goldenPath = Path.Combine(root, "business", "reference-data", "expected-results", "golden-oracles.json");
        using JsonDocument golden = JsonDocument.Parse(await File.ReadAllTextAsync(goldenPath, ct));
        JsonElement oracle = golden.RootElement.GetProperty("AC-19");
        string currency = oracle.GetProperty("currency").GetString()!;
        decimal expectedDiscount = oracle.GetProperty("discount").GetDecimal();
        var evidence = new List<object>();
        bool matches = master is [{ Unchanged: true }, { Unchanged: true }];
        foreach (string tenant in DemoRun.Tenants)
        {
            await run.Verify(tenant);
            await using var db = new SqlConnection(run.Connection(tenant));
            bool exclusive = tenant == "tenant-a";
            string mode = exclusive ? "EXCLUSIVE" : "INCLUSIVE";
            PriceRow[] expectedRows = oracle.GetProperty(mode).EnumerateArray().Select(row => new PriceRow(
                row[0].GetString()!, row[1].GetDecimal(), row[2].GetDecimal(), row[3].GetDecimal(),
                row[4].GetDecimal(), row[5].GetDecimal())).ToArray();
            decimal[] expectedTotals = oracle.GetProperty("totals").GetProperty(mode).EnumerateArray()
                .Select(number => number.GetDecimal()).ToArray();
            PriceLine[] rows = (await db.QueryAsync<PriceLine>(new CommandDefinition("""
                SELECT k.Code AS Sku,p.OrderLineId,p.PlanId,p.QuotePolicyId,s.PolicyId AS FrozenPolicyId,
                    c.ConfirmSourceId,f.TaxMode AS Mode,p.ResidualProfile,p.Currency,f.Version,
                    s.PolicyVersion,l.BaseQuantity AS Ordered,p.SourceBase AS Base,p.AllocatedDiscount AS Discount,
                    p.NetAmount AS Net,p.TaxAmount AS Tax,p.GrossAmount AS Gross,s.GrossAmount AS GrossSnapshot,
                    p.QuoteUnitPrice,p.TaxRate
                FROM dbo.F04PriceLine p
                JOIN dbo.F04PricePlan f ON f.TenantId=p.TenantId AND f.Id=p.PlanId
                JOIN dbo.AOrderLine l ON l.TenantId=p.TenantId AND l.Id=p.OrderLineId
                JOIN dbo.Sku k ON k.TenantId=l.TenantId AND k.Id=l.SkuId
                JOIN dbo.C2Confirmation c ON c.TenantId=l.TenantId AND c.OrderId=l.AOrderId
                JOIN dbo.C2LinePolicy s ON s.TenantId=l.TenantId AND s.OrderLineId=l.Id
                ORDER BY k.Code
                """, cancellationToken: ct))).ToArray();
            PriceRow[] actualRows = rows.Select(row => new PriceRow(row.Sku, row.Base, row.Discount, row.Net, row.Tax, row.Gross))
                .ToArray();
            decimal[] actualTotals = [rows.Sum(r => r.Net), rows.Sum(r => r.Tax), rows.Sum(r => r.Gross)];
            decimal actualDiscount = rows.Sum(row => row.Discount);
            decimal[] totalDiff = actualTotals.Zip(expectedTotals, (actual, expected) => actual - expected).ToArray();
            var rowDiff = expectedRows.Zip(actualRows, (e, a) => new
            {
                e.Sku, Base = a.Base - e.Base, Discount = a.Discount - e.Discount,
                Net = a.Net - e.Net, Tax = a.Tax - e.Tax, Gross = a.Gross - e.Gross
            }).ToArray();
            var masterV2 = await db.QuerySingleAsync<(int Prices, int Plans, int Links)>(new CommandDefinition("""
                SELECT (SELECT COUNT(*) FROM dbo.C2CommercialPolicy WHERE Version=2) AS Prices,
                    (SELECT COUNT(*) FROM dbo.F04PricePlan WHERE Version=2 AND OrderDiscount=20003) AS Plans,
                    (SELECT COUNT(*) FROM dbo.F04PlanQuote q JOIN dbo.F04PricePlan f
                        ON f.TenantId=q.TenantId AND f.Id=q.PlanId AND f.Version=2) AS Links
                """, cancellationToken: ct));
            Delivery[] deliveries = (await db.QueryAsync<Delivery>(new CommandDefinition("""
                SELECT d.Id AS DeliveryId,p.OrderLineId,k.Code AS Sku,d.BaseQuantity AS Quantity,p.Currency,p.NetAmount AS Net,
                    p.TaxAmount AS Tax,p.GrossAmount AS Gross,d.PayableAmount AS Payable,a.OutstandingAmount AS Outstanding,
                    d.CostAmount AS Cost,d.InvoiceEligible
                FROM dbo.F04DeliveryPricing p
                JOIN dbo.SaleDelivery d ON d.TenantId=p.TenantId AND d.Id=p.DeliveryId
                JOIN dbo.Sku k ON k.TenantId=d.TenantId AND k.Id=d.SkuId
                JOIN dbo.ReceivableOpenItem a ON a.TenantId=d.TenantId AND a.SaleDeliveryId=d.Id
                ORDER BY d.Id
                """, cancellationToken: ct))).ToArray();
            int deliveryCount = await db.QuerySingleAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM dbo.SaleDelivery", cancellationToken: ct));
            decimal[] expectedPartial = exclusive ? oracle.GetProperty("returnParts")[0].EnumerateArray()
                .Select(amount => amount.GetDecimal()).ToArray() : [];
            decimal[] actualPartial = deliveries is [var delivery] ? [delivery.Net, delivery.Tax, delivery.Gross] : [];
            bool partialMatches = exclusive
                ? deliveryCount == 1 && deliveries is [{ Sku: "P", Quantity: 1, Cost: 10, InvoiceEligible: false } entry]
                    && entry.Currency == currency && entry.OrderLineId == rows.FirstOrDefault(row => row.Sku == "P")?.OrderLineId
                    && entry.Payable == expectedPartial[2] && entry.Outstanding == expectedPartial[2]
                    && actualPartial.SequenceEqual(expectedPartial)
                : deliveryCount == 0 && deliveries.Length == 0;
            bool lineMatches = actualRows.SequenceEqual(expectedRows) && actualTotals.SequenceEqual(expectedTotals)
                && actualDiscount == expectedDiscount && rows.All(row => row.Mode == mode && row.Currency == currency
                    && row.Version == 1 && row.PolicyVersion == 1 && row.ConfirmSourceId != Guid.Empty
                    && row.PlanId != Guid.Empty && row.QuotePolicyId == row.FrozenPolicyId
                    && row.ResidualProfile == "VND_NET_TAX_CUMULATIVE_V1" && row.GrossSnapshot == row.Gross
                    && (row.Sku == "P"
                        ? row.Ordered == 3 && row.TaxRate == .08m && row.QuoteUnitPrice == (exclusive ? 66666.6667m : 72000m)
                        : row.Ordered == 1 && row.TaxRate == .10m && row.QuoteUnitPrice == (exclusive ? 100000m : 110000m)));
            var apiAtChange = master?.SingleOrDefault(receipt => receipt.Tenant == tenant);
            bool apiMatches = apiAtChange is { Unchanged: true } && apiAtChange.After.SequenceEqual(rows.Select(row =>
                new ApiPrice(row.Sku, row.OrderLineId, row.Mode, row.Currency, row.Base, row.Discount, row.Net, row.Tax,
                    row.Gross, row.Version)));
            var isolation = await db.QuerySingleAsync<(int DraftRows, int Orders, int ConfirmSources, int RejectedMixedQuote, int BaseQuoteLinks)>(
                new CommandDefinition("""
                    SELECT (SELECT COUNT(*) FROM dbo.C2OrderDraft) AS DraftRows,
                        (SELECT COUNT(*) FROM dbo.AOrder) AS Orders,
                        (SELECT COUNT(*) FROM dbo.BusinessSource WHERE SourceKind=N'A_CONFIRM') AS ConfirmSources,
                        (SELECT COUNT(*) FROM dbo.BusinessCommand WHERE ReasonCode=N'F04_PRICE_INVALID') AS RejectedMixedQuote,
                        (SELECT COUNT(*) FROM dbo.F04PlanQuote q JOIN dbo.C2CommercialPolicy p
                            ON p.TenantId=q.TenantId AND p.Id=q.PolicyId
                            JOIN dbo.Sku k ON k.TenantId=p.TenantId AND k.Id=p.SkuId AND k.Code=N'C2-SKU') AS BaseQuoteLinks
                    """, cancellationToken: ct));
            bool isolationMatches = exclusive ? isolation == (2, 1, 1, 1, 0) : isolation == (1, 1, 1, 0, 0);
            bool tenantMatches = lineMatches && partialMatches && apiMatches && isolationMatches && masterV2 == (2, 1, 2);
            matches &= tenantMatches;
            evidence.Add(new
            {
                tenant, mode, currency, cases = exclusive ? new[] { "F04-01", "F04-03", "F04-04" } : new[] { "F04-02", "F04-03" },
                expectedRows, actualRows = rows, rowDiff, expectedTotals, actualTotals, totalDiff,
                expectedDiscount, actualDiscount, discountDiff = actualDiscount - expectedDiscount,
                expectedPartial, actualPartial, partialDiff = actualPartial.Zip(expectedPartial, (a, e) => a - e).ToArray(),
                deliveries, deliveryCount, v2Prices = masterV2.Prices, v2Plans = masterV2.Plans, v2Links = masterV2.Links,
                draftRows = isolation.DraftRows, confirmedOrders = isolation.Orders, confirmSources = isolation.ConfirmSources,
                mixedQuoteRejected = isolation.RejectedMixedQuote,
                baseQuoteLinks = isolation.BaseQuoteLinks, isolationMatches,
                apiAtChange, lineMatches, partialMatches, apiMatches, status = tenantMatches ? "PASS" : "FAIL"
            });
        }

        string output = Path.Combine(root, "TestResults", "dev-f04", run.Id, "sql-result.json");
        static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
        {
            runId = run.Id, source, apiSha256, environment = "Synthetic Windows LocalDB", instance = DemoRun.Instance,
            observedAtUtc = DateTime.UtcNow, fixtureUtc = FixtureUtc, acceptance = "AC-19", fixtureVersion = "v1",
            immutableOracleSha256 = Hash(goldenPath),
            browserFixtureSha256 = Hash(Path.Combine(root, "web", "e2e-dev-f04", "pricing.spec.ts")),
            browserReportSha256 = Hash(Path.Combine(root, "web", "test-results-app02c5", run.Id, "junit.xml")),
            evidence, matches
        }, new JsonSerializerOptions { WriteIndented = true }), ct);
        Console.WriteLine($"DEV-F04 SQL comparison: {output}");
        if (!matches) throw new InvalidOperationException("DEV-F04 observations differ from frozen AC-19 expectations.");
    }
}
