using System.Net;
using System.Net.Http.Json;
using Dapper;

namespace FmcgDms.Demo;

internal sealed partial class App02d2Dev
{
    private async Task<Reply> NoEffect(string tenant, object request, string label, string role = "sales")
    {
        string before = await D2Sql.Fingerprint(run, tenant);
        var outcome = await Send(tenant, HttpMethod.Post, Sales, request, role);
        Verify(outcome.Denied, true, $"{label}: HTTP {outcome.Http}, status {outcome.Status}, reason {outcome.Reason}");
        Verify(outcome.Source, (Guid?)null, label + " has no source");
        Verify(await D2Sql.Fingerprint(run, tenant), before, label + " preserves protected SQL rows");
        return outcome;
    }

    private static D2Request Fresh(D2Request r) => r with { CommandId = Key(), SourceNumber = Key() };

    private async Task EvidenceCase(string tenant)
    {
        var s = await d1.SeedD2(tenant, "D2-07");
        var foreign = await d1.SeedD2(tenant, "D2-07-FOREIGN");
        string valid = await d1.SeedD2Cash(s, 60);
        var input = Request(s, valid, 60);
        var checks = new (D2Request Input, string Label)[]
        {
            (input with { VerificationEvidenceRef = "missing" }, "Missing cash witness"),
            (input with { ReceivedCashAmount = 61 }, "Cash amount mismatch"),
            (input with { VerificationEvidenceRef = await d1.SeedD2Cash(s, 60, "pending") }, "Pending cash witness"),
            (input with { VerificationEvidenceRef = await d1.SeedD2Cash(s, 60, "future") }, "Future cash witness"),
            (input with { VerificationEvidenceRef = await d1.SeedD2Cash(s, 60, "self") }, "Seller as witness"),
            (input with { VerificationEvidenceRef = await d1.SeedD2Cash(foreign, 60) }, "Cash witness outside scope"),
            (input with { ReceivedCashAmount = 0, VerificationEvidenceRef = "", PendingBankEvidenceRef = "missing" }, "Missing bank evidence"),
            (input with { PendingBankEvidenceRef = await d1.SeedD2Bank(s) }, "Bank pending cannot be shown as cash"),
            (input with { CustomerCreditReservationId = Guid.NewGuid() }, "Credit without reservation")
        };
        foreach (var (data, label) in checks) await NoEffect(tenant, Fresh(data), label);
        var pending = Request(s) with { PendingBankEvidenceRef = await d1.SeedD2Bank(s) };
        var claimedVerified = System.Text.Json.JsonSerializer.SerializeToNode(pending, Json)!.AsObject();
        claimedVerified["bankVerified"] = true;
        Verify((await NoEffect(tenant, claimedVerified, "Client cannot verify a pending bank report")).Http, 400,
            "Unrecognized bank verification flag fails request binding");

        var origin = Request(s, await d1.SeedD2Cash(s, 120), 120) with { TripManifestLineId = s.B10 };
        Guid creditSource = Applied(await Send(tenant, HttpMethod.Post, Sales, origin), "Credit fixture posted");
        Guid reserved = await d1.ReserveD2CustomerCredit(s, creditSource);
        await NoEffect(tenant, Fresh(input) with
        {
            CustomerCreditReservationId = reserved, BaseQuantity = 7
        }, "Reservation exceeds residual sale");
        var wrongCredit = Request(foreign, await d1.SeedD2Cash(foreign, 60), 60) with { CustomerCreditReservationId = reserved };
        await NoEffect(tenant, wrongCredit, "Credit reservation for another customer and trip");
        var opening = await D2Sql.Read(run, s);
        Guid sourceId = Applied(await Send(tenant, HttpMethod.Post, Sales, input), "Witness survives failed attempts");
        await VerifyPosting(s, input, sourceId, opening, expected["D2-02"]);
        await NoEffect(tenant, Fresh(input), "Cash evidence cannot be consumed twice");

        var creditSale = browserScenes[(tenant, "D2-05")];
        await NoEffect(tenant, Fresh(creditSale.Request) with
        {
            VerificationEvidenceRef = await d1.SeedD2Cash(creditSale.Scene, 60)
        }, "Previously applied CustomerCredit reservation is single-use");
    }

    private async Task IdentityCase(string tenant)
    {
        var s = await d1.SeedD2(tenant, "D2-08", withOutlet: true);
        var other = await d1.SeedD2(tenant, "D2-08-FOREIGN", withOutlet: true);
        var input = Request(s, await d1.SeedD2Cash(s, 60), 60);
        foreach (var (invalid, label) in new[]
        {
            (input with { CustomerId = other.Customer }, "Wrong Customer"),
            (input with { OutletId = other.Outlet }, "Outlet from a different Customer"),
            (input with { OutletId = null }, "Outlet does not match bound selection"),
            (input with { OutletId = await d1.SeedD2Outlet(s) }, "Different Outlet of same Customer does not match selection"),
            (input with { ShipToSnapshotId = other.ShipTo }, "Wrong frozen ShipTo"),
            (input with { SelectionId = other.Selection }, "Wrong ShipTo selection"),
            (input with { SelectionId = Guid.NewGuid() }, "Unknown ShipTo selection version")
        }) await NoEffect(tenant, Fresh(invalid), label);
        await NoEffect(tenant, Fresh(input), "Unassigned Sales actor", "other-sales");
        await NoEffect(tenant, Fresh(input), "Warehouse-only actor", "warehouse");
        await d1.SetD2SalesAuthority(tenant, false);
        try { await NoEffect(tenant, Fresh(input), "Assigned writer whose Sales role is revoked"); }
        finally { await d1.SetD2SalesAuthority(tenant, true); }
        await using var db = await Owner(tenant);
        await db.ExecuteAsync("UPDATE dbo.Customer SET IsActive=0 WHERE TenantId=@Tenant AND Id=@Customer", s);
        try { await NoEffect(tenant, Fresh(input), "Inactive Customer"); }
        finally { await db.ExecuteAsync("UPDATE dbo.Customer SET IsActive=1 WHERE TenantId=@Tenant AND Id=@Customer", s); }
    }

    private async Task StockCase(string tenant)
    {
        var s = await d1.SeedD2(tenant, "D2-09");
        var other = await d1.SeedD2(tenant, "D2-09-FOREIGN");
        var input = Request(s, await d1.SeedD2Cash(s, 60), 60);
        foreach (var (invalid, label) in new[]
        {
            (input with { TripManifestLineId = s.ALine }, "A entitlement cannot fund B sale"),
            (input with { TripManifestLineId = other.B20 }, "Manifest from another trip"),
            (input with { SkuId = other.Sku }, "SKU does not match loaded source"),
            (input with { TripManifestLineId = s.B10, BaseQuantity = 11 }, "Exceeds B10 even with total B30")
        }) await NoEffect(tenant, Fresh(invalid), label);
        var tampered = System.Text.Json.JsonSerializer.SerializeToNode(Fresh(input), Json)!.AsObject();
        tampered["unitPrice"] = 1;
        var before = await D2Sql.Read(run, s);
        var reply = await Send(tenant, HttpMethod.Post, Sales, tampered);
        if (reply.Applied)
            await VerifyPosting(s, input, reply.Source!.Value, before, expected["D2-02"]);
        else
        {
            Verify(reply.Denied, true, "Client price override refused");
            Verify(await D2Sql.Read(run, s), before, "Rejected price cannot change balances");
        }

        await using var db = await Owner(tenant);
        const string drift = """
            UPDATE dbo.PhysicalStockBalance SET OnHandBaseQuantity=OnHandBaseQuantity+@q,OnHandCostAmount=OnHandCostAmount+@money
            WHERE TenantId=@Tenant AND StockLocationId=@Truck AND SkuId=@Sku
            """;
        foreach (var (q, money) in new[] { (-1m, 0m), (0m, 1m) })
        {
            var faulted = new { s.Tenant, s.Truck, s.Sku, q, money };
            Verify(await db.ExecuteAsync(drift, faulted), 1, "Fixture modifies only exact trip/SKU balance");
            try
            {
                await NoEffect(tenant, Fresh(input) with { VerificationEvidenceRef = await d1.SeedD2Cash(s, 60) },
                    $"Vehicle source drift qty={q} amount={money}");
            }
            finally { await db.ExecuteAsync(drift, new { s.Tenant, s.Truck, s.Sku, q = -q, money = -money }); }
        }
    }

    private async Task GrantCase(string tenant)
    {
        foreach (decimal cap in new[] { 0m, 39m })
        {
            var s = await d1.SeedD2(tenant, "D2-10-CAP-" + cap, grantCap: cap);
            await NoEffect(tenant, Request(s, await d1.SeedD2Cash(s, 60), 60), $"Credit limit {cap} is insufficient");
        }
        var ambiguous = await d1.SeedD2(tenant, "D2-10-AMBIGUOUS");
        await d1.AddD2Approval(ambiguous);
        await NoEffect(tenant, Request(ambiguous, await d1.SeedD2Cash(ambiguous, 60), 60), "Overlapping approval denied");
        var scoped = await d1.SeedD2(tenant, "D2-10-WINDOW", grantCap: 40);
        var earlyWitness = await d1.SeedD2Cash(scoped, 60, verifiedAt: App02d1Dev.StartTime.AddHours(-1).AddMinutes(-4).UtcDateTime);
        var input = Request(scoped, earlyWitness, 60);
        var start = App02d1Dev.StartTime.AddHours(-1);
        try
        {
            await d1.SetClock(start.AddMinutes(-1));
            await NoEffect(tenant, Fresh(input), "Approval not effective yet");
            await d1.SetClock(start.AddHours(2));
            await NoEffect(tenant, Fresh(input), "Approval at its exclusive end");
            await d1.AddD2Approval(scoped, 40, from: start.AddHours(2), until: start.AddHours(3), version: "2");
            await NoEffect(tenant, Fresh(input), "New approval identity/version cannot authorize the old grant");
            await d1.SetClock(start);
            var before = await D2Sql.Read(run, scoped);
            Guid sourceId = Applied(await Send(tenant, HttpMethod.Post, Sales, input), "Inclusive window start and exact limit");
            await VerifyPosting(scoped, input, sourceId, before, expected["D2-10"]);
            Verify((await D2Sql.Read(run, scoped)).CreditRemaining, 0m, "Exact residual limit used");
        }
        finally { await d1.SetClock(App02d1Dev.StartTime); }

        var drift = await d1.SeedD2(tenant, "D2-10-DRIFT");
        var driftInput = Request(drift, await d1.SeedD2Cash(drift, 60), 60);
        await using var db = await Owner(tenant);
        const string counter = "UPDATE dbo.BCreditGrant SET ConsumedAmount=@amount WHERE TenantId=@Tenant AND Id=@Grant";
        await db.ExecuteAsync(counter, new { drift.Tenant, drift.Grant, amount = 1 });
        try { await NoEffect(tenant, driftInput, "Grant counter has no matching immutable consumption"); }
        finally { await db.ExecuteAsync(counter, new { drift.Tenant, drift.Grant, amount = 0 }); }
    }

    private async Task RaceCase(string tenant, bool grantRace)
    {
        string id = grantRace ? "D2-12" : "D2-11";
        var s = await d1.SeedD2(tenant, id);
        var inputs = new List<D2Request>();
        for (int i = 0; i < 2; i++)
            inputs.Add(grantRace
                ? Request(s, qty: 8) with { PendingBankEvidenceRef = await d1.SeedD2Bank(s, 80) }
                : Request(s, await d1.SeedD2Cash(s, 60), 60, 6) with { TripManifestLineId = s.B10 });
        var opening = await D2Sql.Read(run, s);
        var replies = await Task.WhenAll(inputs.Select(r => Send(tenant, HttpMethod.Post, Sales, r)));
        Verify(replies.Count(x => x.Applied), 1, "Exactly one concurrent winner");
        Verify(replies.Count(x => x.Denied), 1, "Exactly one concurrent rejection");
        int winner = Array.FindIndex(replies, x => x.Applied);
        await VerifyPosting(s, inputs[winner], replies[winner].Source!.Value, opening, expected[id]);
        var loser = inputs[1 - winner];
        Verify(await D2Sql.CommandSource(run, s, loser.ProducerId, loser.CommandId), (Guid?)null, "Losing request has no source");
        await using var db = await App02c6Dev.ReadOnly(run, tenant);
        int used = grantRace
            ? await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.BLocalSalePayment WHERE TenantId=@tenant AND PendingBankEvidenceRef=@key", new { tenant, key = loser.PendingBankEvidenceRef })
            : await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.CashReceipt WHERE TenantId=@tenant AND VerificationEvidenceRef=@key", new { tenant, key = loser.VerificationEvidenceRef });
        Verify(used, 0, "Losing witness remains unconsumed");
    }

    private async Task OldBalancesCase(string tenant)
    {
        var s = await d1.SeedD2(tenant, "D2-18", grantCap: 170);
        var oldAr = Request(s, qty: 7) with { TripManifestLineId = s.B10, PendingBankEvidenceRef = await d1.SeedD2Bank(s, 70) };
        Guid oldArSource = Applied(await Send(tenant, HttpMethod.Post, Sales, oldAr), "Old AR source");
        var oldCustody = Request(s, await d1.SeedD2Cash(s, 15), 15, 1.5m) with { TripManifestLineId = s.B10 };
        Applied(await Send(tenant, HttpMethod.Post, Sales, oldCustody), "Old custody source");
        await d1.SeedD2IndependentFundAndAdvance(s);
        var before = await D2Sql.Read(run, s);
        Verify(before.ArNet, 70m, "Old AR is 70");
        Verify(before.Custody, 15m, "Old cash custody is 15");
        Verify(before.Fund, 25m, "Old fund is 25");
        Verify(before.Advance, 10m, "Old advance is 10");
        var request = Request(s, await d1.SeedD2Cash(s, 60), 60);
        Guid sale = Applied(await Send(tenant, HttpMethod.Post, Sales, request), "New sale against separate open item");
        await VerifyPosting(s, request, sale, before, expected["D2-18"]);
        Verify((await D2Sql.Source(run, s, oldArSource)).Row.ArNet, 70m, "Earlier AR open item is unchanged");
        var after = await D2Sql.Read(run, s);
        Verify(after.ArNet, 110m, "Only new AR40 added");
        Verify(after.Custody, 75m, "Only newly confirmed cash60 added");
    }
}
