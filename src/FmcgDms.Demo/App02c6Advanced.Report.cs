using System.Globalization;
using System.Net;
using System.Text.Json;
using Dapper;
using FmcgDms.Api.Persistence;

namespace FmcgDms.Demo;

internal sealed partial class App02c6Advanced
{
    private async Task<Result> ReportCutoffs()
    {
        Scene allocation = await Seed("C6-ASOF");
        Guid manifest = await LoadOrder(allocation, await Select(allocation), 25);
        await setClock(First);
        Guid deliverySource = (await Deliver(allocation, manifest, 25)).Source;
        await using var read = await App02c6Dev.ReadOnly(run, Tenant);
        Posted posted = await read.QuerySingleAsync<Posted>("""
            SELECT d.Id,ar.Id AS OpenItem,d.ShipToSnapshotId AS ShipTo
            FROM dbo.SaleDelivery d JOIN dbo.ReceivableOpenItem ar ON ar.TenantId=d.TenantId AND ar.SaleDeliveryId=d.Id
            WHERE d.BusinessSourceId=@deliverySource
            """, new { deliverySource });
        var allocationAt = At("2026-11-03T10:00:00Z");
        await setClock(allocationAt);
        Guid receipt = await SeedVerifiedReceipt(allocation, posted.ShipTo, First);
        string id = Guid.NewGuid().ToString("N");
        var applied = await Post("/api/synthetic/app01d/commands/allocate-cash-receipt",
            new AllocateCashReceiptCommand(actor, id, id, allocation.Trip, receipt, posted.OpenItem, 100));
        var dueDay = At("2026-11-08T16:59:59Z");
        var nextDay = dueDay.AddSeconds(1);
        await setClock(nextDay);

        var rows = new List<string>();
        foreach (var (label, cutoff, download) in new[]
        {
            ("BEFORE", allocationAt.AddSeconds(-1), false), ("AT", allocationAt, true),
            ("DUE", dueDay, false), ("OVERDUE", nextDay, true)
        })
        {
            var report = await GetReport(allocation.Customer, cutoff, download, posted.Id);
            rows.Add($"{label}|API|{ReportValues(report)}");
            rows.Add($"{label}|SQL|" + await ReadBalanceAt(cutoff));
        }

        await setClock(Start);
        Scene pending = await Seed("C6-PENDING");
        Guid pendingManifest = await LoadOrder(pending, await Select(pending), 40);
        await setClock(First);
        await Deliver(pending, pendingManifest, 25);
        var pendingAt = First.AddHours(1);
        await setClock(pendingAt);
        await InjectArFault(pendingManifest);
        await Deliver(pending, pendingManifest, 13, expected: HttpStatusCode.InternalServerError);
        foreach (var (label, cutoff) in new[] { ("PENDING-BEFORE", pendingAt.AddSeconds(-1)), ("PENDING-AFTER", pendingAt) })
        {
            var report = await GetReport(pending.Customer, cutoff, false);
            rows.Add($"{label}|API|{ReportValues(report)}");
            string evidence = await read.QuerySingleAsync<string>("""
                SELECT CONCAT(COUNT(d.Id),'|',COUNT(*),'|',
                    CONVERT(decimal(19,4),SUM(ar.OriginalAmount)),'|',CONVERT(decimal(19,4),0))
                FROM dbo.C4DeliveryEvidence e
                LEFT JOIN dbo.SaleDelivery d ON d.TenantId=e.TenantId AND d.C4EvidenceId=e.Id
                LEFT JOIN dbo.ReceivableOpenItem ar ON ar.TenantId=d.TenantId AND ar.SaleDeliveryId=d.Id
                WHERE e.TripId=@Trip AND e.OccurredAtUtc<=@Cutoff
                """, new { pending.Trip, Cutoff = cutoff.UtcDateTime });
            rows.Add($"{label}|SQL|{evidence}");
        }

        return Compare("DEV-08", [
            "BEFORE|API|1|1|375.0000|0.0000", "BEFORE|SQL|1|1|375.0000|0.0000",
            "AT|API|1|1|275.0000|0.0000", "AT|SQL|1|1|275.0000|0.0000",
            "DUE|API|1|1|275.0000|0.0000", "DUE|SQL|1|1|275.0000|0.0000",
            "OVERDUE|API|1|1|275.0000|275.0000", "OVERDUE|SQL|1|1|275.0000|275.0000",
            "PENDING-BEFORE|API|1|1|375.0000|0.0000", "PENDING-BEFORE|SQL|1|1|375.0000|0.0000",
            "PENDING-AFTER|API|1|2|375.0000|0.0000", "PENDING-AFTER|SQL|1|2|375.0000|0.0000"], rows);

        Task<string> ReadBalanceAt(DateTimeOffset at) => read.QuerySingleAsync<string>("""
            SELECT CONCAT('1|1|',CONVERT(decimal(19,4),ar.OriginalAmount-IIF(
                a.RecordedAtUtc<=@Cutoff AND s.RecordedAtUtc<=@Cutoff AND cmd.CompletedAtUtc<=@Cutoff
                AND ledger.RecordedAtUtc<=@Cutoff,a.AppliedAmount,0)),'|',CONVERT(decimal(19,4),
                IIF(ar.DueBusinessDate<@SellerDate,ar.OutstandingAmount,0)))
            FROM dbo.ReceivableOpenItem ar
            JOIN dbo.ReceiptAllocation a ON ar.TenantId=a.TenantId AND ar.Id=a.ReceivableOpenItemId
            JOIN dbo.BusinessSourceLine l ON a.TenantId=l.TenantId AND a.AllocationSourceLineId=l.Id
            JOIN dbo.BusinessSource s ON l.TenantId=s.TenantId AND l.BusinessSourceId=s.Id AND s.Id=@Source
            JOIN dbo.BusinessCommand cmd ON s.TenantId=cmd.TenantId AND s.BusinessCommandId=cmd.Id AND cmd.Status='APPLIED'
            JOIN dbo.LedgerEffect ledger ON l.TenantId=ledger.TenantId AND l.Id=ledger.BusinessSourceLineId
                AND ledger.EffectKey='AR-CASH-ALLOC' AND ledger.AmountDelta=-a.AppliedAmount
            WHERE ar.Id=@OpenItem
            """, new { Cutoff = at.UtcDateTime, SellerDate = at.ToOffset(TimeSpan.FromHours(7)).Date, posted.OpenItem,
                Source = applied.Source });
    }

    private async Task<JsonElement> GetReport(Guid customer, DateTimeOffset cutoff, bool download, Guid? delivery = null)
    {
        string utc = cutoff.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        string path = C5 + $"/ar?customerId={customer}&asOf={Uri.EscapeDataString(utc)}"
            + (download ? "&download=true" : "") + (delivery.HasValue ? $"&deliveryId={delivery:D}" : "");
        using var request = new HttpRequestMessage(HttpMethod.Get, webUrl + path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, ct);
        string content = await response.Content.ReadAsStringAsync(ct);
        http.Add($"GET {path} {(int)response.StatusCode}");
        Require(response.IsSuccessStatusCode, $"Report {utc}: HTTP {(int)response.StatusCode} {content}");
        if (download)
            Require(response.Content.Headers.ContentDisposition?.FileName?.Contains("sales-a-ar.json", StringComparison.Ordinal) == true,
                $"Report cutoff {utc}: expected JSON attachment.");
        return JsonSerializer.Deserialize<JsonElement>(content);
    }

    private static string ReportValues(JsonElement body)
    {
        var reconciled = body.GetProperty("reconciled").GetInt32();
        var expected = body.GetProperty("expected").GetInt32();
        var sources = body.GetProperty("sources").EnumerateArray().ToArray();
        Require(sources.Length == expected && sources.Count(source => source.GetProperty("status").GetString() == "RECONCILED") == reconciled,
            "Report counts do not match source rows.");
        Require(sources.Where(source => source.GetProperty("status").GetString() == "PENDING")
            .All(source => source.GetProperty("balance").ValueKind == JsonValueKind.Null),
            "Pending receipt contributes a posted balance.");
        decimal balance = Amount(body.GetProperty("knownBalance"));
        decimal overdue = Amount(body.GetProperty("knownOverdue"));
        Require(sources.Where(source => source.GetProperty("status").GetString() == "RECONCILED")
            .Sum(source => Amount(source.GetProperty("balance"))) == balance,
            "Report subtotal does not reconcile to visible posted sources.");
        return FormattableString.Invariant($"{reconciled}|{expected}|{balance:F4}|{overdue:F4}");

        static decimal Amount(JsonElement number) => decimal.Parse(number.GetString()!, CultureInfo.InvariantCulture);
    }

    private async Task InjectArFault(Guid manifest)
    {
        await using var write = await Writable();
        await write.ExecuteAsync($"""
            CREATE TRIGGER dbo.TR_C6ReportAbortAr ON dbo.LedgerEffect AFTER INSERT AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (SELECT 1 FROM inserted i JOIN dbo.SaleDelivery d
                    ON d.TenantId=i.TenantId AND d.BusinessSourceLineId=i.BusinessSourceLineId
                    WHERE d.TripManifestLineId='{manifest:D}' AND d.BaseQuantity=13 AND i.EffectKey=N'AR-GROSS')
                    THROW 51609, 'C6 scoped report fixture rollback', 1;
            END;
            """);
    }

    // As in the AC-21 fixture, stage a verified opening. The tested allocation is
    // the actual APP-01D command. This seed is outside the A_ONLINE deliveries.
    private async Task<Guid> SeedVerifiedReceipt(Scene scene, Guid shipTo, DateTimeOffset at)
    {
        Guid receipt = Guid.NewGuid();
        await using var db = await Writable();
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.ExecuteAsync(new CommandDefinition("""
            DECLARE @CashSku uniqueidentifier=NEWID(), @Right uniqueidentifier=NEWID(), @Manifest uniqueidentifier=NEWID(),
                @ManifestLine uniqueidentifier=NEWID(), @Delivery uniqueidentifier=NEWID(), @QuotaSource uniqueidentifier=NEWID(),
                @LoadSource uniqueidentifier=NEWID(), @CashSource uniqueidentifier=NEWID(), @QuotaLine uniqueidentifier=NEWID(),
                @LoadLine uniqueidentifier=NEWID(), @CashLine uniqueidentifier=NEWID(), @QuotaCommand uniqueidentifier=NEWID(),
                @LoadCommand uniqueidentifier=NEWID(), @CashCommand uniqueidentifier=NEWID();
            INSERT dbo.Sku(Id,TenantId,Code,Name,BaseUom) VALUES(@CashSku,N'tenant-a',N'C6-CASH-OPENING',N'C6 receipt opening SKU',N'EA');
            INSERT dbo.BusinessCommand(Id,TenantId,ProducerId,CommandId,PayloadHashVersion,PayloadHash,ActorId,Status,OutcomeCode,CompletedAtUtc)
            SELECT id,N'tenant-a',N'c6-approved-receipt-opening',CONVERT(nvarchar(36),id),1,
                HASHBYTES('SHA2_256',CONVERT(nvarchar(36),id)),N'c6-fixture',N'APPLIED',N'CONFIRMED_OPENING',@At
            FROM (VALUES(@QuotaCommand),(@LoadCommand),(@CashCommand)) c(id);
            INSERT dbo.BusinessSource(Id,TenantId,BusinessCommandId,SourceKind,SourceNumber,ActorId,CustomerId,TripId,OccurredAtUtc)
            VALUES(@QuotaSource,N'tenant-a',@QuotaCommand,N'STOCK_ALLOC',N'C6-CASH-QUOTA',N'c6-fixture',NULL,@Trip,@At),
                (@LoadSource,N'tenant-a',@LoadCommand,N'MANIFEST_LOAD',N'C6-CASH-LOAD',N'c6-fixture',NULL,@Trip,@At),
                (@CashSource,N'tenant-a',@CashCommand,N'B_SALE_DELIVERY',N'C6-CASH-OPENING',N'c6-fixture',@Customer,@Trip,@At);
            INSERT dbo.BusinessSourceLine(Id,TenantId,BusinessSourceId,LineNumber,SkuId,BaseQuantity,Amount)
            VALUES(@QuotaLine,N'tenant-a',@QuotaSource,1,@CashSku,1,NULL),
                (@LoadLine,N'tenant-a',@LoadSource,1,@CashSku,1,10),(@CashLine,N'tenant-a',@CashSource,1,@CashSku,1,100);
            INSERT dbo.StockRight(Id,TenantId,TripId,StockLocationId,SkuId,BusinessSourceLineId,RightKind,GrantedBaseQuantity)
            VALUES(@Right,N'tenant-a',@Trip,@Warehouse,@CashSku,@QuotaLine,'B',1);
            INSERT dbo.BQuota(Id,TenantId,StockRightId) VALUES(NEWID(),N'tenant-a',@Right);
            INSERT dbo.TripManifest(Id,TenantId,ManifestNumber,TripId,WarehouseLocationId,VehicleLocationId)
            VALUES(@Manifest,N'tenant-a',N'C6-CASH-OPENING',@Trip,@Warehouse,@Vehicle);
            INSERT dbo.TripManifestLine(Id,TenantId,TripManifestId,TripId,WarehouseLocationId,VehicleLocationId,
                LineNumber,SkuId,StockRightId,RightKind,PlannedBaseQuantity)
            VALUES(@ManifestLine,N'tenant-a',@Manifest,@Trip,@Warehouse,@Vehicle,1,@CashSku,@Right,'B',1);
            UPDATE dbo.TripManifest SET Status=N'LOADED',LoadBusinessSourceId=@LoadSource,LoadedAtUtc=@At WHERE Id=@Manifest;
            UPDATE dbo.TripManifestLine SET LoadBusinessSourceId=@LoadSource,LoadSourceLineId=@LoadLine,
                LoadedBaseQuantity=1,LoadedCostAmount=10 WHERE Id=@ManifestLine;
            INSERT dbo.SaleDelivery(Id,TenantId,TripId,CustomerId,ShipToSnapshotId,TripManifestLineId,LoadSourceLineId,
                StockRightId,RightKind,SkuId,BusinessSourceId,BusinessSourceKind,BusinessSourceLineId,BaseQuantity,
                PayableAmount,CostAmount,CostBasisVersion,BusinessDate)
            SELECT @Delivery,N'tenant-a',@Trip,@Customer,@ShipTo,@ManifestLine,@LoadLine,@Right,'B',@CashSku,@CashSource,
                N'B_SALE_DELIVERY',@CashLine,1,100,10,N'SYNTHETIC_MA_V1',BusinessDate FROM dbo.Trip WHERE Id=@Trip;
            INSERT dbo.VerifiedCashEvidence(TenantId,EvidenceRef,TripId,CustomerId,ShipToSnapshotId,PaymentMethod,
                VerificationStatus,VerifiedAmount,VerifiedBy,VerifiedAtUtc)
            VALUES(N'tenant-a',N'C6-CASH-WITNESS',@Trip,@Customer,@ShipTo,N'CASH',N'VERIFIED',100,N'c6-fixture-witness',@At);
            INSERT dbo.CashReceipt(Id,TenantId,SaleDeliveryId,TripId,CustomerId,BusinessSourceLineId,
                VerificationEvidenceRef,ReceivedAmount,VerifiedAtUtc)
            VALUES(@receipt,N'tenant-a',@Delivery,@Trip,@Customer,@CashLine,N'C6-CASH-WITNESS',100,@At);
            """, new { scene.Trip, scene.Customer, scene.Warehouse, scene.Vehicle, shipTo, receipt, At = at.UtcDateTime },
            tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return receipt;
    }
}
