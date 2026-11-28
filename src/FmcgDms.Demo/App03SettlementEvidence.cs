using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FmcgDms.Api.Persistence;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

internal static partial class App03SettlementEvidence
{
    private const string D3 = "/api/synthetic/app02d3";

    internal static async Task CheckGiftOnlyDraft(DemoRun run, App03DevHost host, Guid trip, string output,
        CancellationToken ct)
    {
        const string tenant = "tenant-a";
        await run.Verify(tenant, app03Session: true);
        await run.Verify("tenant-b", app03Session: true);
        await using var db = new SqlConnection(run.Connection(tenant));
        var offer = await db.QuerySingleAsync<(Guid Sku, Guid Customer, Guid Selection)>(new CommandDefinition("""
            SELECT p.GiftSkuId,b.CustomerId,b.SelectionId FROM dbo.F07Program p
            JOIN dbo.D2ShipToBinding b ON b.TenantId=p.TenantId
            WHERE p.TenantId=@tenant;
            """, new { tenant }, cancellationToken: ct));
        string[] tables = ["BusinessSource", "BusinessSourceLine", "C2OrderDraft", "C2OrderDraftLine",
            "F03DraftQuantity", "AOrder", "AOrderLine", "StockRight", "LedgerEffect"];
        var before = await DevF01Evidence.Capture(run, tables);
        var payload = new C2DraftInput(Guid.NewGuid(), offer.Customer, trip, offer.Selection, [new(offer.Sku, 1)]);
        using var request = new HttpRequestMessage(HttpMethod.Post, host.WebUrl + "/api/synthetic/app02c2/drafts");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", host.SourceToken);
        request.Content = JsonContent.Create(payload);
        using var response = await host.Client.SendAsync(request, ct);
        var result = await response.Content.ReadFromJsonAsync<BusinessCommandOutcome>(cancellationToken: ct);
        var after = await DevF01Evidence.Capture(run, tables);
        if (response.StatusCode != System.Net.HttpStatusCode.UnprocessableEntity ||
            result is not { Status: "REJECTED", ReasonCode: "C2_SKU_INVALID", SourceId: null } ||
            !before.SequenceEqual(after))
            throw new InvalidOperationException($"Gift-only SKU was not rejected without business effects: {response.StatusCode}, {result}.");
        await Save("gift-only-draft", new { payload, result, before, after }, output, ct);
    }

    internal static async Task Review(DemoRun run, App03DevHost host, string output, string source,
        App03FieldEvidence.UpgradeReview upgrade, CancellationToken ct)
    {
        Guid trip = upgrade.Before.Delivery.TripId;
        var before = await Read("tenant-a");
        var peer = await Read("tenant-b");
        if (before != new ReviewState(1, 1, 1, 1, 20000, -20000, 0, 4, 1, 1, 3,
                1, 3, 1, 1, 1, 200000, 40000, "RECONCILING")
            || peer != new ReviewState(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, null))
            throw new InvalidOperationException($"Unexpected settlement SQL state: primary={before}, peer={peer}.");

        await using var db = new SqlConnection(run.Connection("tenant-a"));
        var acceptance = await db.QuerySingleAsync<Acceptance>(new CommandDefinition("""
            SELECT a.Id,c.ProducerId,c.CommandId,a.AcceptedAmount,a.AcceptedBy,o.OfferedBy,
                a.OfferId,s.SourceNumber
            FROM dbo.CashHandoverAcceptance a
            JOIN dbo.CashHandoverOffer o ON o.TenantId=a.TenantId AND o.Id=a.OfferId
            JOIN dbo.BusinessSource s ON s.TenantId=a.TenantId AND s.Id=a.Id
            JOIN dbo.BusinessCommand c ON c.TenantId=s.TenantId AND c.Id=s.BusinessCommandId
            WHERE a.TripId=@trip;
            """, new { trip }, cancellationToken: ct));
        var report = await Send<TripReport>(host, ct, D3 + $"/trips/{trip}", host.CashierToken);
        var lookup = await Send<BusinessCommandOutcome>(host, ct,
            D3 + $"/trips/{trip}/commands/{acceptance.ProducerId}/{acceptance.CommandId}", host.CashierToken);
        var replay = await Send<BusinessCommandOutcome>(host, ct, D3 + "/accept", host.CashierToken,
            new App03CashCount(acceptance.ProducerId, acceptance.CommandId, acceptance.SourceNumber,
                trip, acceptance.OfferId, acceptance.AcceptedAmount));
        if (acceptance.OfferedBy == acceptance.AcceptedBy || report is not { Custody: 0, Fund: 20000, TripStatus: "RECONCILING" }
            || lookup is not { Status: "APPLIED", Replayed: true } || lookup.SourceId != acceptance.Id
            || lookup != replay || before != await Read("tenant-a") || peer != await Read("tenant-b"))
            throw new InvalidOperationException("Cash handoff lookup/replay changed source or SQL state after next-day resume.");
        await File.WriteAllTextAsync(Path.Combine(output, "sql-review.json"),
            JsonSerializer.Serialize(new { source, runId = run.Id, trip, before, peer, acceptance, report, lookup },
                new JsonSerializerOptions { WriteIndented = true }), ct);

        async Task<ReviewState> Read(string tenant)
        {
            await run.Verify(tenant, app03Session: true);
            await using var db = new SqlConnection(run.Connection(tenant));
            return await db.QuerySingleAsync<ReviewState>(new CommandDefinition("""
                WITH CashEffects AS (
                    SELECT s.SourceKind,e.LedgerKind,e.AmountDelta FROM dbo.BusinessSource s
                    JOIN dbo.BusinessSourceLine l ON l.TenantId=s.TenantId AND l.BusinessSourceId=s.Id
                    JOIN dbo.LedgerEffect e ON e.TenantId=l.TenantId AND e.BusinessSourceLineId=l.Id
                    WHERE s.TenantId=@tenant AND s.TripId=@trip
                )
                SELECT
                    (SELECT COUNT(*) FROM dbo.CashHandoverOffer WHERE TripId=@trip) Offers,
                    (SELECT COUNT(*) FROM dbo.CashHandoverAcceptance WHERE TripId=@trip) Acceptances,
                    (SELECT COUNT(*) FROM dbo.CashierCountEvidence WHERE TripId=@trip) Counts,
                    (SELECT COUNT(*) FROM dbo.D3TripUnload WHERE TripId=@trip) Unloads,
                    COALESCE((SELECT SUM(AmountDelta) FROM CashEffects WHERE LedgerKind='FUND'),0) Fund,
                    COALESCE((SELECT SUM(AmountDelta) FROM CashEffects WHERE LedgerKind='CUSTODY'
                        AND SourceKind='CASH_HANDOVER_ACCEPT'),0) AcceptanceCustody,
                    (SELECT COUNT(*) FROM CashEffects WHERE SourceKind='CASH_HANDOVER_OFFER') OfferEffects,
                    COALESCE((SELECT SUM(BaseQuantity) FROM dbo.D3TripUnloadLine WHERE TripId=@trip),0) VehicleReturn,
                    COALESCE((SELECT SUM(l.Quantity) FROM dbo.App03LotUnload l JOIN dbo.D3TripUnloadLine d
                        ON d.TenantId=l.TenantId AND d.TripManifestLineId=l.ManifestLineId WHERE d.TripId=@trip),0) LotReturn,
                    COALESCE((SELECT SUM(ReleasedBaseQuantity) FROM dbo.D3BQuotaRelease WHERE TripId=@trip),0) QuotaRelease,
                    COALESCE((SELECT SUM(GrantedBaseQuantity-ConsumedBaseQuantity-ReleasedBaseQuantity)
                        FROM dbo.StockRight WHERE TripId=@trip AND RightKind='A'),0) OrderHeld,
                    (SELECT COUNT(*) FROM dbo.F05ReturnCase) ReturnCases,
                    (SELECT COUNT(*) FROM dbo.F05ReturnStage) ReturnStages,
                    (SELECT COUNT(*) FROM dbo.F07Issue) GiftIssues,
                    (SELECT COUNT(*) FROM dbo.F07GiftReturn) GiftReturns,
                    (SELECT COUNT(*) FROM dbo.F02Advance) Advances,
                    COALESCE((SELECT SUM(Amount) FROM dbo.F02AdvanceEntry WHERE Kind='RETURN_ACCEPT'),0) AdvanceReturn,
                    COALESCE((SELECT SUM(i.OutstandingAmount) FROM dbo.ReceivableOpenItem i JOIN dbo.SaleDelivery d
                        ON d.TenantId=i.TenantId AND d.Id=i.SaleDeliveryId WHERE d.TripId=@trip),0) Ar,
                    (SELECT Status FROM dbo.Trip WHERE Id=@trip) TripStatus;
                """, new { tenant, trip }, cancellationToken: ct));
        }
    }

    private static async Task<T> Send<T>(App03DevHost host, CancellationToken ct, string path, string token, object? body = null,
        string? witnessId = null)
    {
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, host.WebUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (witnessId is not null) request.Headers.Add(App03SettlementHttpWitness.Header, witnessId);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await host.Client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Missing resumed handoff result.");
    }

    private sealed record TripReport(decimal Custody, decimal Fund, string TripStatus, decimal TripQty, decimal BQuota);

    private sealed record Acceptance(Guid Id, string ProducerId, string CommandId, decimal AcceptedAmount,
        string AcceptedBy, string OfferedBy, Guid OfferId, string SourceNumber);

    private sealed record ReviewState(int Offers, int Acceptances, int Counts, int Unloads, decimal Fund,
        decimal AcceptanceCustody, int OfferEffects, decimal VehicleReturn, decimal LotReturn, decimal QuotaRelease,
        decimal OrderHeld, int ReturnCases, int ReturnStages, int GiftIssues, int GiftReturns, int Advances,
        long AdvanceReturn, decimal Ar, string? TripStatus);
}
