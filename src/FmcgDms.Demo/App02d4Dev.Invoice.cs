using System.Security.Cryptography;
using System.Text;
using Dapper;
using FmcgDms.Api.Persistence;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

internal sealed partial class App02d4Dev
{
    private sealed record InvoiceSql(Guid IntentId, Guid DeliveryId, Guid SourceLineId, string Seller, string Channel,
        string Hash, string SourceHash, string Payload, string Status, Guid? FactId, bool InvoiceEligible, Guid BusinessSourceLineId,
        string CommittedHash);
    private sealed record InvoiceAttemptSql(string Operation, Guid? TransportId, string? Fault, string? Result,
        string? ObservedStatus, Guid? ObservedIntentId, string? ObservedHash, string? ObservedChannel);
    private sealed record InvoiceFactSql(Guid IntentId, Guid FactId, string Channel, string Hash, string Payload, DateTimeOffset CommittedAt);
    private sealed record InvoiceCaseSql(Guid CaseId, string Kind, Guid? IntentId, Guid? FactId, string Seller,
        DateTimeOffset OccurredAt, DateTimeOffset ReceivedAt, string OwnerRole, string NextAction);

    private async Task InvoiceCases(string tenant)
    {
        var scene = scenes[tenant];
        await Record(tenant, 2, async () =>
        {
            await ResetInvoice(tenant);
            Guid a10 = await PrepareInvoice(tenant, scene.A10);
            Guid a15 = await PrepareInvoice(tenant, scene.A15);
            await using var oracleReader = await Reader(tenant);
            Equal("distinct intents for partial deliveries", a10 != a15, true);
            Equal("one A order for partial deliveries", await oracleReader.ExecuteScalarAsync<int>("""
                SELECT COUNT(*) FROM dbo.SaleDelivery WHERE AOrderId=@Order AND Id IN (@A10,@A15);
                """, scene), 2);
            await CheckSourceInvoice(oracleReader, scene.A10, a10, "D4-A", "PREPARED");
            await CheckSourceInvoice(oracleReader, scene.A15, a15, "D4-A", "PREPARED");
            Equal("two intents only", await InvoiceCount(oracleReader, "d4app.InvoiceIntent"), 2);
        });
        await Record(tenant, 3, async () =>
        {
            await ResetInvoice(tenant);
            await using (var admin = await Owner(tenant))
            {
                await admin.ExecuteAsync("UPDATE d4app.InvoiceProfile SET Channel=NULL WHERE Seller='D4-B'");
                try
                {
                    await DeniedInvoice(tenant, "invoice/prepare", new { deliveryId = scene.B20 }, "B_NO_CHANNEL");
                    await admin.ExecuteAsync("UPDATE d4app.InvoiceProfile SET Channel='MOCK-V1',Active=0 WHERE Seller='D4-B'");
                    await DeniedInvoice(tenant, "invoice/prepare", new { deliveryId = scene.B20 }, "PROFILE_INACTIVE");
                }
                finally
                {
                    await admin.ExecuteAsync("UPDATE d4app.InvoiceProfile SET Active=1,Channel='MOCK-V1' WHERE Seller='D4-B'");
                }
            }
            await DeniedInvoice(tenant, "invoice/prepare", new { deliveryId = scene.B20, seller = "D4-A" }, "SOURCE_CONFLICT");
            var crossTenant = await Request(tenant, "invoice", "invoice/prepare", new { deliveryId = scene.B20 }, peer: Peer(tenant));
            Equal("attempt with a foreign tenant selector", crossTenant.Http, 403);
            await using var oracleReader = await Reader(tenant);
            Equal("denied candidates created no intent", await InvoiceCount(oracleReader, "d4app.InvoiceIntent"), 0);
            Equal("denied candidates created no partner fact", await InvoiceCount(oracleReader, "d4invoice.InvoiceFact"), 0);
            Guid intentId = await PrepareInvoice(tenant, scene.B20);
            await CheckSourceInvoice(oracleReader, scene.B20, intentId, "D4-B", "PREPARED");
            Equal("valid B intent count", await InvoiceCount(oracleReader, "d4app.InvoiceIntent"), 1);
        });
        await Record(tenant, 4, async () =>
        {
            await ResetInvoice(tenant);
            var candidates = invoiceNegativeSources[tenant];
            string sourceBefore = await InvoiceSourceFingerprint(tenant);
            await using var oracleReader = await Reader(tenant);
            int receiptsBefore = await InvoiceCount(oracleReader, "d4accounting.Receipt");
            int postingsBefore = await InvoiceCount(oracleReader, "d4accounting.Posting");
            foreach (var (kind, id) in candidates)
            {
                Equal(kind + " candidate id", id == Guid.Empty, false);
                await DeniedInvoice(tenant, "invoice/prepare", new { deliveryId = id }, "SOURCE_NOT_COMMITTED");
                var receive = await Request(tenant, "accountant", $"accounting/{id}/receive", new { transportId = Key() });
                Equal(kind + " accounting receipt denied", receive.Http >= 400, true);
                var post = await Request(tenant, "accountant", $"accounting/{id}/post", new { transportId = Key() });
                Equal(kind + " accounting posting denied", post.Http >= 400, true);
            }
            await DeniedInvoice(tenant, "invoice/prepare", new { deliveryId = scene.A10, hash = new string('0', 64) },
                "PAYLOAD_CONFLICT");
            var tamperedAccounting = await Request(tenant, "accountant", $"accounting/{scene.A10}/receive",
                new { transportId = Key(), expectedHash = new string('0', 64) });
            Equal("payload conflict denied by accounting", tamperedAccounting.Http >= 400, true);
            Equal("invalid invoice effects", await InvoiceCount(oracleReader, "d4app.InvoiceIntent"), 0);
            Equal("invalid invoice partner effects", await InvoiceCount(oracleReader, "d4invoice.InvoiceFact"), 0);
            Equal("accounting receipts unchanged", await InvoiceCount(oracleReader, "d4accounting.Receipt"), receiptsBefore);
            Equal("accounting postings unchanged", await InvoiceCount(oracleReader, "d4accounting.Posting"), postingsBefore);
            Equal("application source/ledger fingerprint after negative actions", await InvoiceSourceFingerprint(tenant), sourceBefore);
        });
        await Record(tenant, 5, async () =>
        {
            await ResetInvoice(tenant);
            Guid intent = await PrepareInvoice(tenant, scene.A10);
            var posted = await SendInvoice(tenant, intent);
            Equal("normal transport acknowledgement", posted.Status, "MOCK_RECORDED");
            var read = await Request(tenant, "invoice", $"invoice/{intent}");
            Equal("readback durable state", read.Status, "MOCK_RECORDED");
            await using var oracleReader = await Reader(tenant);
            var app = await CheckSourceInvoice(oracleReader, scene.A10, intent, "D4-A", "MOCK_RECORDED");
            var fact = await CheckInvoiceFact(oracleReader, app);
            Equal("partner commit time", fact.CommittedAt, At("operationUtc"));
            Equal("single SEND with valid ACK", (await InvoiceAttempts(oracleReader, intent)).Single().Result, "MOCK_RECORDED");
        });
        await Record(tenant, 6, async () =>
        {
            await ResetInvoice(tenant);
            Guid intent = await PrepareInvoice(tenant, scene.A10);
            var lost = await SendInvoice(tenant, intent, "BEFORE_COMMIT");
            Equal("state after outage before partner commit", lost.Status, "UNKNOWN");
            await using var oracleReader = await Reader(tenant);
            Equal("partner transaction rolled back", await InvoiceCount(oracleReader, "d4invoice.InvoiceFact"), 0);
            var verifiedAbsent = await LookupInvoice(tenant, intent);
            Equal("retry reopened only by definitive ABSENT", verifiedAbsent.Status, "PREPARED");
            Equal("identity after ABSENT", verifiedAbsent.Id("intentId"), intent);
            var retried = await SendInvoice(tenant, intent);
            Equal("retry preserves intent", retried.Id("intentId"), intent);
            Equal("retry obtains partner ACK", retried.Status, "MOCK_RECORDED");
            var app = await CheckSourceInvoice(oracleReader, scene.A10, intent, "D4-A", "MOCK_RECORDED");
            await CheckInvoiceFact(oracleReader, app);
            var attempts = await InvoiceAttempts(oracleReader, intent);
            Equal("dispatch/lookup/dispatch audit", string.Join("/", attempts.Select(a => $"{a.Operation}:{a.Result}")),
                "SEND:ACK_MISSING/LOOKUP:ABSENT/SEND:MOCK_RECORDED");
            Equal("lookup actual result", attempts[1].ObservedStatus, "ABSENT");
            Equal("lookup was for the original identity", attempts[1].ObservedIntentId, (Guid?)intent);
            Equal("lookup was for the original payload", attempts[1].ObservedHash, app.Hash);
        });
        await Record(tenant, 7, async () =>
        {
            await ResetInvoice(tenant);
            Guid intent = await PrepareInvoice(tenant, scene.A10);
            var lost = await SendInvoice(tenant, intent, "CRASH_AFTER_COMMIT");
            Equal("unresolved state after partner commit", lost.Status, "UNKNOWN");
            await using (var oracleReader = await Reader(tenant))
            {
                Equal("fact committed before sender resolved ACK", await InvoiceCount(oracleReader, "d4invoice.InvoiceFact"), 1);
                Equal("crashed send attempt unresolved", (await InvoiceAttempts(oracleReader, intent)).Single().Result, null);
            }
            await restart(true);
            var recovered = await LookupInvoice(tenant, intent);
            Equal("persisted original intent after restart", recovered.Id("intentId"), intent);
            Equal("valid FOUND recovers recorded state", recovered.Status, "MOCK_RECORDED");
            await using var oracleAfter = await Reader(tenant);
            var app = await CheckSourceInvoice(oracleAfter, scene.A10, intent, "D4-A", "MOCK_RECORDED");
            await CheckInvoiceFact(oracleAfter, app);
            var attempts = await InvoiceAttempts(oracleAfter, intent);
            Equal("no new dispatch after restart", attempts.Count(a => a.Operation == "SEND"), 1);
            Equal("lookup confirms original fact", attempts.Single(a => a.Operation == "LOOKUP").Result, "FOUND");
        });
        await Record(tenant, 8, async () =>
        {
            await ResetInvoice(tenant);
            var prepared = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => PrepareInvoice(tenant, scene.A10)));
            Guid intent = prepared[0];
            Equal("concurrent prepare returns one ID", prepared.Distinct().Count(), 1);
            var dispatched = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => SendInvoice(tenant, intent)));
            Equal("all concurrent transports return original intent", dispatched.All(x => x.Id("intentId") == intent), true);
            Equal("all concurrent transports return original fact",
                dispatched.Select(x => x.Id("factId")).Distinct().Count(), 1);
            await DeniedInvoice(tenant, $"invoice/{intent}/send", new { transportId = Guid.NewGuid(), seller = "D4-B" },
                "PAYLOAD_CONFLICT");
            await DeniedInvoice(tenant, $"invoice/{intent}/send", new { transportId = Guid.NewGuid(), channel = "P2" },
                "PAYLOAD_CONFLICT");
            await DeniedInvoice(tenant, $"invoice/{intent}/send", new { transportId = Guid.NewGuid(), hash = new string('0', 64) },
                "PAYLOAD_CONFLICT");
            await using var oracleReader = await Reader(tenant);
            var app = await CheckSourceInvoice(oracleReader, scene.A10, intent, "D4-A", "MOCK_RECORDED");
            await CheckInvoiceFact(oracleReader, app);
            Equal("one intent after concurrent and conflicting commands", await InvoiceCount(oracleReader, "d4app.InvoiceIntent"), 1);
            Equal("one dispatch across competing transport identifiers", (await InvoiceAttempts(oracleReader, intent)).Count(), 1);
        });
        await Record(tenant, 9, async () =>
        {
            await ResetInvoice(tenant);
            Guid intent = await PrepareInvoice(tenant, scene.A10);
            Equal("lost ACK marks UNKNOWN", (await SendInvoice(tenant, intent, "AFTER_COMMIT")).Status, "UNKNOWN");
            foreach (string fault in new[] { "UNAVAILABLE", "PROCESSING", "WRONG_ID", "WRONG_HASH", "WRONG_PROFILE", "WRONG_HASH" })
                Equal("UNKNOWN restored after " + fault, (await LookupInvoice(tenant, intent, fault)).Status, "UNKNOWN");
            await using (var admin = await Owner(tenant))
            {
                await admin.ExecuteAsync("UPDATE d4app.InvoiceProfile SET Channel='INACTIVE-REV' WHERE Seller='D4-A'");
                try
                {
                    Equal("profile conflict holds UNKNOWN", (await LookupInvoice(tenant, intent)).Status, "UNKNOWN");
                }
                finally
                {
                    await admin.ExecuteAsync("UPDATE d4app.InvoiceProfile SET Channel='MOCK-V1' WHERE Seller='D4-A'");
                }
            }
            await using var oracleReader = await Reader(tenant);
            var app = await CheckSourceInvoice(oracleReader, scene.A10, intent, "D4-A", "UNKNOWN");
            Equal("wrong facts never attached", app.FactId, null);
            var fact = await oracleReader.QuerySingleAsync<InvoiceFactSql>(
                "SELECT IntentId,FactId,Channel,Hash,Payload,CommittedAt FROM d4invoice.InvoiceFact");
            var mismatch = await oracleReader.QuerySingleAsync<InvoiceCaseSql>(
                "SELECT CaseId,Kind,IntentId,FactId,Seller,OccurredAt,ReceivedAt,OwnerRole,NextAction FROM d4app.InvoiceCase");
            Equal("case keyed from original intent", mismatch.IntentId, (Guid?)intent);
            Equal("mismatch case linked to partner fact version", mismatch.FactId, (Guid?)fact.FactId);
            Equal("case kind", mismatch.Kind, "LOOKUP_MISMATCH");
            Equal("case owner", mismatch.OwnerRole, "invoice");
            Equal("case next action", !string.IsNullOrWhiteSpace(mismatch.NextAction), true);
            var attempts = await InvoiceAttempts(oracleReader, intent);
            Equal("UNAVAILABLE keeps pending intent", attempts.Single(a => a.Fault == "UNAVAILABLE").Result, "LOOKUP_UNAVAILABLE");
            Equal("PROCESSING is not ABSENT", attempts.Single(a => a.Fault == "PROCESSING").ObservedStatus, "PROCESSING");
            Equal("partner wrong ID captured", attempts.Single(a => a.Fault == "WRONG_ID").ObservedIntentId, (Guid?)Guid.Empty);
            Equal("partner hash mismatch captured", attempts.Where(a => a.Fault == "WRONG_HASH").All(a => a.ObservedHash != app.Hash), true);
            Equal("partner profile mismatch captured", attempts.Single(a => a.Fault == "WRONG_PROFILE").ObservedChannel != app.Channel, true);
            Equal("profile conflict denied against frozen channel", attempts[^1].Result, "PROFILE_CONFLICT");
            Equal("no resend while lookup untrusted", attempts.Count(a => a.Operation == "SEND"), 1);
        });
        await Record(tenant, 10, async () =>
        {
            await d1.SetClock(At("operationUtc"));
            string before = await InvoiceSourceFingerprint(tenant);
            await using var oracleReader = await Reader(tenant);
            int intents = await InvoiceCount(oracleReader, "d4app.InvoiceIntent");
            int facts = await InvoiceCount(oracleReader, "d4invoice.InvoiceFact");
            var missing = fixture.GetProperty("missingChannel");
            string seller = missing.GetProperty("seller").GetString()!;
            await DeniedInvoice(tenant, "invoice/b-intent", new { seller }, "B_NO_CHANNEL");
            Equal("attempted B sale opens no observed-case by itself",
                await oracleReader.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM d4app.InvoiceCase WHERE Kind='B_NO_CHANNEL_OCCURRED'"), 0);
            var observation = new { factId = Guid.NewGuid(), seller, occurredAt = missing.GetProperty("occurredUtc").GetDateTimeOffset() };
            var first = await Request(tenant, "invoice", "invoice/b-observed", observation);
            Success(first, "independent B happened report");
            var duplicate = await Request(tenant, "invoice", "invoice/b-observed", observation);
            Success(duplicate, "B report replay");
            Equal("replay case identity", duplicate.Id("caseId"), first.Id("caseId"));
            var observed = await oracleReader.QuerySingleAsync<InvoiceCaseSql>("""
                SELECT CaseId,Kind,IntentId,FactId,Seller,OccurredAt,ReceivedAt,OwnerRole,NextAction
                FROM d4app.InvoiceCase WHERE Kind='B_NO_CHANNEL_OCCURRED';
                """);
            Equal("observed fact identity", observed.FactId, (Guid?)observation.factId);
            Equal("observed fact isolated from intent", observed.IntentId, null);
            Equal("occurred time retained", observed.OccurredAt, observation.occurredAt);
            Equal("independent receive time", observed.ReceivedAt, At("operationUtc"));
            Equal("observed B tracks owner and next action",
                observed.OwnerRole == "invoice" && !string.IsNullOrWhiteSpace(observed.NextAction), true);
            Equal("observed B creates no invoice intent", await InvoiceCount(oracleReader, "d4app.InvoiceIntent"), intents);
            Equal("observed B creates no partner invoice fact", await InvoiceCount(oracleReader, "d4invoice.InvoiceFact"), facts);
            Equal("B no-channel probes change no posted application source or ledger", await InvoiceSourceFingerprint(tenant), before);
        });
    }

    private async Task<Guid> PrepareInvoice(string tenant, Guid deliveryId)
    {
        var response = await Request(tenant, "invoice", "invoice/prepare", new { deliveryId });
        Success(response, "prepare invoice");
        return response.Id("intentId");
    }

    private async Task<Wire> SendInvoice(string tenant, Guid intent, string? fault = null)
    {
        var response = await Request(tenant, "invoice", $"invoice/{intent}/send", new { transportId = Guid.NewGuid(), fault });
        Success(response, "dispatch invoice");
        return response;
    }

    private async Task<Wire> LookupInvoice(string tenant, Guid intent, string? fault = null)
    {
        var response = await Request(tenant, "invoice", $"invoice/{intent}/lookup", new { fault });
        Success(response, "lookup invoice");
        return response;
    }

    private async Task DeniedInvoice(string tenant, string route, object input, string status)
    {
        var response = await Request(tenant, "invoice", route, input);
        Equal(route + "/" + status + " denied", response.Http, 409);
        Equal(route + " denial code", response.Status, status);
    }

    private async Task<InvoiceSql> CheckSourceInvoice(SqlConnection db, Guid deliveryId, Guid intent, string seller,
        string status)
    {
        var app = await db.QuerySingleAsync<InvoiceSql>("""
            SELECT i.IntentId,i.DeliveryId,i.SourceLineId,i.Seller,i.Channel,i.Hash,i.SourceHash,i.Payload,i.Status,i.FactId,
                d.InvoiceEligible,d.BusinessSourceLineId,s.Hash AS CommittedHash
            FROM d4app.InvoiceIntent i JOIN dbo.SaleDelivery d ON d.Id=i.DeliveryId
            JOIN d4app.CommittedSource s ON s.DeliveryId=d.Id
            WHERE i.IntentId=@intent;
            """, new { intent });
        Equal("SQL intent linked delivery", app.DeliveryId, deliveryId);
        Equal("SQL matching source line", app.SourceLineId, app.BusinessSourceLineId);
        Equal("SQL synthetic seller", app.Seller, seller);
        Equal("SQL frozen channel", app.Channel, "MOCK-V1");
        Equal("SQL frozen payload digest", app.Hash, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(app.Payload))));
        Equal("SQL retained independent source digest", app.SourceHash, app.CommittedHash);
        Equal("SQL invoice eligibility untouched", app.InvoiceEligible, false);
        Equal("SQL authoritative intent state", app.Status, status);
        return app;
    }

    private async Task<InvoiceFactSql> CheckInvoiceFact(SqlConnection db, InvoiceSql app)
    {
        var fact = await db.QuerySingleAsync<InvoiceFactSql>("""
            SELECT IntentId,FactId,Channel,Hash,Payload,CommittedAt FROM d4invoice.InvoiceFact WHERE IntentId=@IntentId;
            """, app);
        Equal("SQL one fact for original intent", await InvoiceCount(db, "d4invoice.InvoiceFact"), 1);
        Equal("SQL invoice fact identity attached", (Guid?)fact.FactId, app.FactId);
        Equal("SQL cross-boundary channel", fact.Channel, app.Channel);
        Equal("SQL cross-boundary payload hash", fact.Hash, app.Hash);
        Equal("SQL cross-boundary immutable payload", fact.Payload, app.Payload);
        return fact;
    }

    private static async Task<InvoiceAttemptSql[]> InvoiceAttempts(SqlConnection db, Guid intent) =>
        (await db.QueryAsync<InvoiceAttemptSql>("""
            SELECT Operation,TransportId,Fault,Result,ObservedStatus,ObservedIntentId,ObservedHash,ObservedChannel
            FROM d4app.InvoiceAttempt WHERE IntentId=@intent ORDER BY AttemptId;
            """, new { intent })).ToArray();

    private static Task<int> InvoiceCount(SqlConnection db, string table) => db.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {table}");

    private readonly Dictionary<string, (string Kind, Guid Id)[]> invoiceNegativeSources = [];

    private async Task SeedInvoiceNegativeCases(string tenant)
    {
        DateTimeOffset setup = At("setupUtc");
        await d1.SetClock(setup);
        var sale = await d1.SeedD2(tenant, "D4-NEG-SALE", stock: new(FirstB: 0, SecondB: 20),
            fixtureTime: setup);
        var empty = await d1.SeedD2(tenant, "D4-NEG-RETURN", stock: new(FirstB: 0, SecondB: 20),
            fixtureTime: setup);
        await using (var admin = await Owner(tenant))
        {
            await admin.ExecuteAsync("""
                INSERT dbo.D3CashierTrip(TenantId,TripId,ActorId)
                VALUES(@Tenant,@Trip,'d4-fixture-cashier'),(@Tenant,@ReturnTrip,'d4-fixture-cashier');
                INSERT dbo.D3PriceProfile(TenantId,TripId,SkuId,PolicyVersion,UnitPrice,Decision,
                    EffectiveFromUtc,EffectiveUntilUtc,ApprovedAtUtc,Maker,Checker,EvidenceRef)
                VALUES(@Tenant,@Trip,@Sku,1,15,'APPROVED','2026-11-23T03:00:00','2026-11-23T05:00:00',
                    '2026-11-23T02:55:00','d4-price-maker','d4-price-checker','D4-v0.1');
                """, new { sale.Tenant, sale.Trip, sale.Sku, ReturnTrip = empty.Trip });
        }

        var draft = await Request(tenant, "sales", "/api/synthetic/app02c2/drafts",
            new C2DraftInput(Guid.NewGuid(), sale.Customer, sale.Trip, sale.Selection, [new(sale.Sku, 1)]),
            sourceApi: true);
        CheckFixtureSource(draft, "draft");
        string actor = $"d1-{tenant[^1]}-sales";
        await d1.SetClock(At("operationUtc"));
        var historic = await Request(tenant, "sales", "/api/synthetic/app02c4/deliver",
            new C4DeliveryInput(Guid.NewGuid(), sale.Trip, sale.ALine, 1, 10), sourceApi: true);
        CheckFixtureSource(historic, "historical A delivery");
        string witness = await d1.SeedD2Cash(sale, 200, verifiedAt: setup.UtcDateTime);
        var received = await Request(tenant, "sales", "/api/synthetic/app02d2/sales",
            new D2Request(actor, Key(), Key(), sale.Trip, sale.Customer, sale.ShipTo, sale.Selection,
                sale.Outlet, sale.B10, sale.Sku, 20, 200, witness, null), sourceApi: true);
        CheckFixtureSource(received, "B cash receipt delivery");
        var handover = await Request(tenant, "sales", "/api/synthetic/app02d3/offer",
            new D3Offer(actor, Key(), Key(), sale.Trip, 200), sourceApi: true);
        CheckFixtureSource(handover, "cash handover offer");
        var unloaded = await Request(tenant, "warehouse", "/api/synthetic/app02d3/unload",
            new D3Unload($"d1-{tenant[^1]}-warehouse", Key(), Key(), empty.Trip), sourceApi: true);
        CheckFixtureSource(unloaded, "vehicle return");

        await using var reader = await App02c6Dev.ReadOnly(run, tenant);
        Guid receipt = await reader.QuerySingleAsync<Guid>("SELECT Id FROM dbo.CashReceipt WHERE TripId=@Trip", sale);
        Guid delivery = await reader.QuerySingleAsync<Guid>(
            "SELECT Id FROM dbo.SaleDelivery WHERE TripId=@Trip AND RightKind='A'", sale);
        invoiceNegativeSources.Add(tenant, [
            ("unsubmitted draft", draft.Id("sourceId")), ("loaded manifest line", sale.ALine),
            ("vehicle return", unloaded.Id("sourceId")), ("cash receipt", receipt),
            ("cash handover offer", handover.Id("sourceId")), ("historical delivery without D.4 profile", delivery)
        ]);
    }

    private static void CheckFixtureSource(Wire response, string kind)
    {
        if (response.Http != 200 || response.Status != "APPLIED")
            throw new InvalidDataException($"D.4 invoice negative fixture {kind}: HTTP {response.Http} {response.Body}");
    }

    private async Task<string> InvoiceSourceFingerprint(string tenant)
    {
        await using var reader = await Reader(tenant);
        string snapshot = await reader.ExecuteScalarAsync<string>("""
            SELECT CONCAT(
                (SELECT * FROM dbo.BusinessSource ORDER BY Id FOR JSON PATH),
                (SELECT * FROM dbo.BusinessSourceLine ORDER BY Id FOR JSON PATH),
                (SELECT * FROM dbo.LedgerEffect ORDER BY Id FOR JSON PATH),
                (SELECT * FROM dbo.SaleDelivery ORDER BY Id FOR JSON PATH),
                (SELECT * FROM dbo.CashReceipt ORDER BY Id FOR JSON PATH),
                (SELECT * FROM dbo.ReceivableOpenItem ORDER BY Id FOR JSON PATH),
                (SELECT * FROM dbo.PhysicalStockBalance ORDER BY TenantId,StockLocationId,SkuId FOR JSON PATH),
                (SELECT * FROM dbo.App02OutboxMessage ORDER BY MessageId FOR JSON PATH));
            """) ?? throw new InvalidDataException("Missing invoice source fingerprint.");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot)));
    }

}
