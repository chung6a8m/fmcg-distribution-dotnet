using System.Net;
using Dapper;
using FmcgDms.Api.Persistence;

namespace FmcgDms.Demo;

internal sealed partial class App02c6Advanced
{
    private async Task<Result> Amendment()
    {
        Scene scene = await Seed("C6-AMEND");
        var originalRegistration = await Post(C1 + "/registered-ship-tos/save",
            new C1RegisteredInput(Guid.NewGuid(), scene.Customer, null, null, null, Address));
        Guid registration = originalRegistration.Body.GetProperty("id").GetGuid();
        Guid selected = await Select(scene, registered: registration, revision: 1);
        Order order = await Confirm(await Draft(scene, selected, 40));
        await Reserve(scene, order, 40);
        Guid manifest = await Load(scene, order, 40);
        await Deliver(scene, manifest, 25);

        await using var read = await App02c6Dev.ReadOnly(run, Tenant);
        string before = await AddressFingerprint();
        var detail = (await Send(HttpMethod.Get, C1 + $"/customers/{scene.Customer}")).Body.GetProperty("customer");
        await Post(C1 + "/customers/save", new C1CustomerInput(Guid.NewGuid(), scene.Customer, detail.GetProperty("version").GetString(),
            scene.Code, "C6 updated master"));
        var updatedAddress = Address with { Line = "Dia chi sua C6" };
        await Post(C1 + "/registered-ship-tos/save",
            new C1RegisteredInput(Guid.NewGuid(), scene.Customer, registration, 1, null, updatedAddress));
        Guid amended = await Select(scene, registered: registration, revision: 2);
        var amendment = new C4AmendInput(Guid.NewGuid(), scene.Trip, order.Id, amended, 1, "C6 undelivered remainder");
        int unauthorized = (int)(await Post(C4 + "/amend-shipto", amendment, HttpStatusCode.Forbidden)).Code;
        await Post(C4 + "/amend-shipto", amendment, auth: checkerToken);
        var stale = await Post(C4 + "/amend-shipto", amendment with { CommandId = Guid.NewGuid() },
            HttpStatusCode.UnprocessableEntity, checkerToken);
        string after = await AddressFingerprint();
        await Deliver(scene, manifest, 15, 2);
        var completed = await Post(C4 + "/amend-shipto", amendment with { CommandId = Guid.NewGuid(), ExpectedVersion = 2 },
            HttpStatusCode.UnprocessableEntity, checkerToken);
        var lineage = (await read.QueryAsync<string>("""
            SELECT CONCAT('AMEND|',a.Version,'|',s.ActorId,'|',l.BaseQuantity,'|',adr.Address)
            FROM dbo.C4ShipToAmendment a
            JOIN dbo.BusinessSource s ON s.TenantId=a.TenantId AND s.Id=a.Id
            JOIN dbo.BusinessSourceLine l ON l.TenantId=s.TenantId AND l.BusinessSourceId=s.Id
            JOIN dbo.ShipToSnapshot adr ON adr.TenantId=a.TenantId AND adr.Id=a.ShipToSnapshotId
            WHERE a.OrderId=@Id
            UNION ALL
            SELECT CONCAT('DELIVER|',e.ShipToVersion,'|',d.BaseQuantity,'|',adr.Address)
            FROM dbo.SaleDelivery d JOIN dbo.C4DeliveryEvidence e ON e.TenantId=d.TenantId AND e.Id=d.C4EvidenceId
            JOIN dbo.ShipToSnapshot adr ON adr.TenantId=d.TenantId AND adr.Id=d.ShipToSnapshotId
            WHERE d.AOrderId=@Id
            """, new { order.Id })).Order(StringComparer.Ordinal).ToArray();
        return Compare("DEV-02", ["FROZEN|True", "UNAUTHORIZED|403", "STALE|C4_STALE_SHIPTO",
            "FINISHED|C4_NO_UNDELIVERED_SCOPE", $"AMEND|2|{checker}|15.000|Dia chi sua C6, Khu DEV, HCM, VN",
            "DELIVER|1|25.000|Dia chi goc C6, Khu DEV, HCM, VN",
            "DELIVER|2|15.000|Dia chi sua C6, Khu DEV, HCM, VN"],
            [$"FROZEN|{before == after}", $"UNAUTHORIZED|{unauthorized}", $"STALE|{stale.Reason}",
                $"FINISHED|{completed.Reason}", .. lineage]);

        Task<string> AddressFingerprint() => read.QuerySingleAsync<string>("""
            SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONCAT(f.Id,'|',f.Address,'|',d.Id,'|',adr.Id,'|',adr.Address)),2)
            FROM dbo.C2Confirmation c JOIN dbo.ShipToSnapshot f ON f.TenantId=c.TenantId AND f.Id=c.ShipToSnapshotId
            JOIN dbo.SaleDelivery d ON d.TenantId=c.TenantId AND d.AOrderId=c.OrderId
            JOIN dbo.ShipToSnapshot adr ON adr.TenantId=d.TenantId AND adr.Id=d.ShipToSnapshotId
            WHERE c.OrderId=@Id
            """, new { order.Id });
    }

    private async Task<Result> CompetingQuota()
    {
        Scene scene = await Seed("C6-QUOTA");
        Guid quota = await GrantQuota(scene, 30);
        Order first = await Confirm(await Draft(scene, await Select(scene), 40));
        Order second = await Confirm(await Draft(scene, await Select(scene), 40));
        var left = new C2ReserveInput(Guid.NewGuid(), first.Id, first.Line, scene.Warehouse, 40, first.Confirm);
        var right = new C2ReserveInput(Guid.NewGuid(), second.Id, second.Line, scene.Warehouse, 40, second.Confirm);
        await using var read = await App02c6Dev.ReadOnly(run, Tenant);
        string before = await Balance();
        using var rival = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        var results = await Task.WhenAll(Attempt(client, left), Attempt(rival, right));
        Require(results.Count(result => result.Code == HttpStatusCode.OK) == 1, "Expected one successful competing allocation.");
        var winner = results[0].Code == HttpStatusCode.OK ? left : right;
        var loser = results.Single(result => result.Code != HttpStatusCode.OK);
        var replay = await Post(C2 + "/reserve", winner);
        string after = await Balance();
        var rights = (await read.QueryAsync<string>("""
            SELECT CONCAT(sr.RightKind,'|',sr.GrantedBaseQuantity,'|',l.QuantityDelta,'|',
              IIF(sr.RightKind='A',IIF(a.AOrderLineId=@winner,'WINNER','WRONG'),IIF(sl.BusinessSourceId=@quota,'SAME','WRONG')))
            FROM dbo.StockRight sr
            JOIN dbo.BusinessSourceLine sl ON sl.TenantId=sr.TenantId AND sl.Id=sr.BusinessSourceLineId
            JOIN dbo.LedgerEffect l ON l.TenantId=sr.TenantId AND l.BusinessSourceLineId=sr.BusinessSourceLineId
            LEFT JOIN dbo.AReservation a ON a.TenantId=sr.TenantId AND a.StockRightId=sr.Id
            WHERE sr.SkuId=@Sku
            """, new { scene.Sku, quota, winner = winner.LineId })).Order(StringComparer.Ordinal).ToArray();
        return Compare("DEV-04", ["POOL|100.000|30.000|70.000", "POOL|100.000|70.000|30.000",
            "DENIED|STOCK_INSUFFICIENT", "REPLAY|True", "A|40.000|40.000|WINNER", "B|30.000|30.000|SAME"],
            [before, after, $"DENIED|{loser.Reason}", $"REPLAY|{replay.Replayed}", .. rights]);

        Task<string> Balance() => read.QuerySingleAsync<string>("""
            SELECT CONCAT('POOL|',OnHandBaseQuantity,'|',AllocatedBaseQuantity,'|',OnHandBaseQuantity-AllocatedBaseQuantity)
            FROM dbo.StockPool WHERE SkuId=@Sku
            """, new { scene.Sku });

        async Task<Reply> Attempt(HttpClient sender, C2ReserveInput input)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, webUrl + C2 + "/reserve");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            request.Content = System.Net.Http.Json.JsonContent.Create(input);
            using var response = await sender.SendAsync(request, ct);
            var body = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
                await response.Content.ReadAsStringAsync(ct));
            lock (http) http.Add($"POST {C2}/reserve {(int)response.StatusCode} ({input.CommandId})");
            return new Reply(response.StatusCode, body);
        }
    }

    private async Task<Guid> GrantQuota(Scene s, decimal amount)
    {
        string id = Guid.NewGuid().ToString("N");
        return (await Post("/api/synthetic/app01a/commands/grant-b-quota",
            new GrantBQuotaCommand(actor, id, id, s.Trip, s.Warehouse, s.Sku, amount))).Source;
    }

    private async Task<Result> HistoricalTerms()
    {
        Scene scene = await Seed("C6-DATES");
        Guid oldManifest = await LoadOrder(scene, await Select(scene), 40);
        await setClock(First);
        await Deliver(scene, oldManifest, 25);
        await using var read = await App02c6Dev.ReadOnly(run, Tenant);
        string before = await ConfirmFingerprint();
        await Policy(scene, 2, 14, First.AddDays(1), Second.AddDays(4));
        await setClock(Second);
        await Deliver(scene, oldManifest, 15);
        Guid fresh = await LoadOrder(scene, await Select(scene), 1);
        await setClock(Second.AddMinutes(5));
        await Deliver(scene, fresh, 1);

        var blocked = new List<string>();
        foreach (string code in new[] { "MISSING", "PENDING" })
        {
            Scene probe = await Seed("C6-DATE-" + code, false);
            if (code == "PENDING") await Policy(probe, 1, 14, First, Second.AddDays(4), "PENDING");
            Guid draft = await Draft(probe, await Select(probe), 1);
            var refusal = await Post(C2 + "/confirm", new C2ConfirmInput(Guid.NewGuid(), draft), HttpStatusCode.UnprocessableEntity);
            blocked.Add(code + "|" + refusal.Reason);
        }

        string after = await ConfirmFingerprint();
        string[] rows = (await read.QueryAsync<string>("""
            SELECT CONCAT(CONVERT(varchar(19),e.OccurredAtUtc,126),'|',CONVERT(varchar(10),d.BusinessDate,23),'|',
                CONVERT(varchar(10),ar.DueBusinessDate,23),'|',f.TermsDaysSnapshot,'|',p.PolicyVersion,'|',
                IIF(p.SourceHash=HASHBYTES('SHA2_256',CONVERT(varchar(max),p.PolicyPayload COLLATE Latin1_General_100_BIN2_UTF8)),
                'HASHED','WRONG'))
            FROM dbo.SaleDelivery d JOIN dbo.C4DeliveryEvidence e ON e.TenantId=d.TenantId AND e.Id=d.C4EvidenceId
            JOIN dbo.ReceivableOpenItem ar ON ar.TenantId=d.TenantId AND ar.SaleDeliveryId=d.Id
            JOIN dbo.AConfirmedLineSnapshot f ON f.TenantId=d.TenantId AND f.AOrderLineId=d.AOrderLineId
            JOIN dbo.C2LinePolicy p ON p.TenantId=d.TenantId AND p.OrderLineId=d.AOrderLineId
            WHERE d.TripId=@Trip ORDER BY e.OccurredAtUtc
            """, new { scene.Trip })).ToArray();
        return Compare("DEV-07", [
            "2026-10-31T17:05:00|2026-11-01|2026-11-08|7|1|HASHED",
            "2026-11-07T17:05:00|2026-11-08|2026-11-15|7|1|HASHED",
            "2026-11-07T17:10:00|2026-11-08|2026-11-22|14|2|HASHED",
            "MISSING|C2_POLICY_REQUIRED", "PENDING|C2_POLICY_REQUIRED", "FROZEN|True"],
            [.. rows, .. blocked, $"FROZEN|{before == after}"]);

        Task<string> ConfirmFingerprint() => read.QuerySingleAsync<string>("""
            SELECT CONCAT(x.ConfirmSourceId,'|',p.PolicyId,'|',p.PolicyVersion,'|',CONVERT(varchar(64),p.SourceHash,2))
            FROM dbo.TripManifestLine m
            JOIN dbo.AReservation a ON a.TenantId=m.TenantId AND a.StockRightId=m.StockRightId
            JOIN dbo.AConfirmedLineSnapshot f ON f.TenantId=a.TenantId AND f.AOrderLineId=a.AOrderLineId
            JOIN dbo.C2Confirmation x ON x.TenantId=f.TenantId AND x.OrderId=f.AOrderId
            JOIN dbo.C2LinePolicy p ON p.TenantId=f.TenantId AND p.OrderLineId=f.AOrderLineId
            WHERE m.Id=@oldManifest
            """, new { oldManifest });
    }
}
