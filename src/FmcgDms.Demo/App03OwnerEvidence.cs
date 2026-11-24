using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

internal static class App03OwnerEvidence
{
    internal static async Task ReviewSettlement(DemoRun run, App03DevHost host, string folder, string source, CancellationToken ct)
    {
        await run.Verify("tenant-a", app03Session: true);
        await using var db = new SqlConnection(run.Connection("tenant-a"));
        var expected = await db.QuerySingleAsync<(decimal Custody, decimal Fund, decimal ArB)>(new CommandDefinition("""
            SELECT (SELECT SUM(ReceivedAmount) FROM dbo.CashReceipt WHERE TenantId='tenant-a')-
                (SELECT SUM(AcceptedAmount) FROM dbo.CashHandoverAcceptance WHERE TenantId='tenant-a'),
                (SELECT SUM(AcceptedAmount) FROM dbo.CashHandoverAcceptance WHERE TenantId='tenant-a'),
                (SELECT SUM(OutstandingAmount) FROM dbo.ReceivableOpenItem WHERE TenantId='tenant-a' AND RightKind='B');
            """, cancellationToken: ct));
        using var request = new HttpRequestMessage(HttpMethod.Get, host.WebUrl + "/api/synthetic/app03/owner-overview");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", host.FinanceCredentials!["OWNER"]);
        using var response = await host.Client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var report = (await response.Content.ReadFromJsonAsync<JsonDocument>(ct))!;
        var actual = (Value(report.RootElement, "custody", "VND"), Value(report.RootElement, "fund", "VND"),
            Value(report.RootElement, "ar-b", "VND"));
        if (expected.Fund <= 0 || actual != ((decimal?)expected.Custody, expected.Fund, expected.ArB))
            throw new InvalidOperationException($"Owner settlement values differ: expected {expected}; actual {actual}.");
        await File.WriteAllTextAsync(Path.Combine(folder, "owner-settlement-review.json"),
            JsonSerializer.Serialize(new { source, runId = run.Id, expected = new { expected.Custody, expected.Fund, expected.ArB },
                report = report.RootElement }, new JsonSerializerOptions { WriteIndented = true }), ct);
    }

    internal static async Task Review(DemoRun run, App03DevHost host, string folder, string source, CancellationToken ct)
    {
        const string endpoint = "/api/synthetic/app03/owner-overview";
        const string primaryTenant = "tenant-a", peerTenant = "tenant-b";
        string[] tables = ["BusinessSource", "LedgerEffect", "PhysicalStockBalance", "ReceivableOpenItem",
            "F02Advance", "F02AdvanceEntry", "F08Treasury", "F08Disbursement"];
        var before = await DevF01Evidence.Capture(run, tables);
        using var primary = await Read(endpoint, "OWNER");
        using var peer = await Read(endpoint, "PEER_OWNER");
        var reports = new[] { primary.RootElement, peer.RootElement };
        var expected = new[] { await Expected(primaryTenant), await Expected(peerTenant) };
        var checks = reports.Zip(expected, (report, oracle) => oracle.Select(row =>
            new { tenant = report.GetProperty("tenantId").GetString(), row.Metric, row.Unit, expected = row.Value,
                actual = Value(report, row.Metric, row.Unit) }).ToArray()).SelectMany(rows => rows).ToArray();
        if (checks.Any(row => row.actual != row.expected))
            throw new InvalidOperationException("Owner totals differ from SQL: " + JsonSerializer.Serialize(checks.Where(row => row.actual != row.expected)));

        var main = Metric(primary.RootElement, "main-qty", "EA").GetProperty("sources").EnumerateArray().ToArray();
        var trip = Metric(primary.RootElement, "trip-qty", "EA").GetProperty("sources").EnumerateArray().ToArray();
        if (main.Count(row => row.GetProperty("label").GetString() == "C2-MAIN · R-05") != 1
            || trip.Count(row => row.GetProperty("label").GetString()!.EndsWith(" · R-05", StringComparison.Ordinal)
                && Number(row.GetProperty("value")) == 1) != 2)
            throw new InvalidOperationException("Shared MAIN SKU or trip attribution is duplicated or missing.");
        if (Metric(peer.RootElement, "advance", "VND").ValueKind != JsonValueKind.Undefined
            || Metric(peer.RootElement, "ar-a", "VND").ValueKind != JsonValueKind.Undefined)
            throw new InvalidOperationException("Missing peer source was presented as a measured value.");
        var after = await DevF01Evidence.Capture(run, tables);
        if (!before.SequenceEqual(after))
            throw new InvalidOperationException("Owner reads changed a source, ledger or balance.");

        await run.Verify(primaryTenant, app03Session: true);
        await using var db = new SqlConnection(run.Connection(primaryTenant));
        var cleared = await db.ExecuteAsync(new CommandDefinition("""
            UPDATE dbo.App02ActorRoleGrant SET IsActive=0,ChangedBy='owner-evidence',ChangedAtUtc=SYSUTCDATETIME()
            WHERE TenantId=@primaryTenant AND ActorId=@Owner AND RoleCode='owner';
            """, new { primaryTenant, App03FinanceBootstrap.Owner }, cancellationToken: ct));
        if (cleared != 1 || await Status(endpoint, "OWNER") != HttpStatusCode.Forbidden
            || await Status("/api/synthetic/app03/session", "OWNER") != HttpStatusCode.Forbidden
            || await Status(endpoint, "PEER_OWNER") != HttpStatusCode.OK)
            throw new InvalidOperationException("Current Owner grant or peer isolation was not enforced.");
        await File.WriteAllTextAsync(Path.Combine(folder, "owner-review.json"),
            JsonSerializer.Serialize(new { source, runId = run.Id, checks, sharedMainSkuRows = 1, attributedTrips = 2,
                ownerReadsChangedSources = false, revoked = true, reports }, new JsonSerializerOptions { WriteIndented = true }), ct);

        async Task<JsonDocument> Read(string path, string role)
        {
            using var response = await Send(path, role);
            response.EnsureSuccessStatusCode();
            if (response.Headers.CacheControl?.NoStore != true)
                throw new InvalidOperationException("Owner report must be served with no-store.");
            return (await response.Content.ReadFromJsonAsync<JsonDocument>(ct))!;
        }

        async Task<HttpStatusCode> Status(string path, string role)
        {
            using var response = await Send(path, role);
            return response.StatusCode;
        }

        async Task<HttpResponseMessage> Send(string path, string role)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, host.WebUrl + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", host.FinanceCredentials![role]);
            return await host.Client.SendAsync(request, ct);
        }

        async Task<ExpectedValue[]> Expected(string tenant)
        {
            await run.Verify(tenant, app03Session: true);
            await using var connection = new SqlConnection(run.Connection(tenant));
            return (await connection.QueryAsync<ExpectedValue>(new CommandDefinition("""
                SELECT 'main-qty' AS Metric,'EA' AS Unit,SUM(b.OnHandBaseQuantity) AS Value
                FROM dbo.PhysicalStockBalance b JOIN dbo.StockLocation l ON l.Id=b.StockLocationId
                WHERE b.TenantId=@tenant AND l.Kind='WAREHOUSE'
                UNION ALL
                SELECT 'main-value','VND',SUM(b.OnHandCostAmount)
                FROM dbo.PhysicalStockBalance b JOIN dbo.StockLocation l ON l.Id=b.StockLocationId
                WHERE b.TenantId=@tenant AND l.Kind='WAREHOUSE'
                UNION ALL
                SELECT 'ar-a','VND',SUM(OriginalAmount-AppliedAmount) FROM dbo.ReceivableOpenItem WHERE TenantId=@tenant AND RightKind='A'
                UNION ALL
                SELECT 'cogs','VND',SUM(d.CostAmount)-
                    COALESCE((SELECT SUM(AcceptedCost) FROM dbo.F05ReturnStage WHERE TenantId=@tenant AND Stage='RELEASE'),0)+
                    COALESCE((SELECT SUM(AdjustedCost-OriginalCost-PriorAdjustment) FROM dbo.F06CostAdjustment WHERE TenantId=@tenant),0)
                FROM dbo.SaleDelivery d WHERE d.TenantId=@tenant
                UNION ALL
                SELECT 'advance','VND',SUM(a.Issued)-
                    COALESCE((SELECT SUM(Amount) FROM dbo.F02AdvanceEntry WHERE TenantId=@tenant AND Kind IN ('EXPENSE_APPROVE','RETURN_ACCEPT')),0)
                FROM dbo.F02Advance a WHERE a.TenantId=@tenant
                UNION ALL
                SELECT 'return-credit','VND',SUM(c.GrossAmount)-
                    COALESCE((SELECT SUM(Amount) FROM dbo.F08Disbursement WHERE TenantId=@tenant),0)
                FROM dbo.F08ReturnCredit c WHERE c.TenantId=@tenant
                UNION ALL
                SELECT 'refund-paid','VND',SUM(Amount) FROM dbo.F08Disbursement WHERE TenantId=@tenant
                UNION ALL
                SELECT 'refund-treasury','VND',t.OpeningAmount-
                    COALESCE((SELECT SUM(Amount) FROM dbo.F08Disbursement WHERE TenantId=@tenant),0)
                FROM dbo.F08Treasury t WHERE t.TenantId=@tenant;
                """, new { tenant }, cancellationToken: ct))).ToArray();
        }
    }

    private static JsonElement Metric(JsonElement report, string metric, string unit) =>
        report.GetProperty("metrics").EnumerateArray().SingleOrDefault(row =>
            row.GetProperty("metric").GetString() == metric && row.GetProperty("unit").GetString() == unit);

    private static decimal? Value(JsonElement report, string metric, string unit)
    {
        var row = Metric(report, metric, unit);
        return row.ValueKind == JsonValueKind.Undefined ? null : Number(row.GetProperty("value"));
    }

    private static decimal? Number(JsonElement value) => value.ValueKind == JsonValueKind.Null
        ? null : decimal.Parse(value.GetString()!, CultureInfo.InvariantCulture);

    private sealed record ExpectedValue(string Metric, string Unit, decimal? Value);
}
