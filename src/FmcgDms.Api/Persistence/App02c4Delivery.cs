using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using static FmcgDms.Api.Persistence.App01c3APostingService;

namespace FmcgDms.Api.Persistence;

public sealed record C4DeliveryInput(Guid CommandId, Guid TripId, Guid ManifestLineId, int ShipToVersion, decimal Quantity);
public sealed record C4AmendInput(Guid CommandId, Guid TripId, Guid OrderId, Guid SelectionId, int ExpectedVersion, string? Reason);

public sealed partial class App01a2CommandService
{
    private sealed record C4Evidence(Guid Id, Guid TripId, Guid ManifestLineId, int ShipToVersion,
        decimal Quantity, DateTime OccurredAtUtc, DateTime BusinessDate);
    private sealed record C4Receipt(C4Evidence? Evidence, string? Rejection = null, bool Conflict = false);
    private sealed record C4Address(Guid SnapshotId, Guid? AmendmentId, int Version);
    private sealed record C4FrozenPolicy(string Seller, Guid CustomerId, Guid SkuId, string BaseUom, int Factor,
        string Currency, bool InvoiceEligible, C2CommercialPolicy Policy);
    private sealed class C4PostingLine
    {
        public Guid OrderId { get; set; }
        public Guid OrderLineId { get; set; }
        public Guid CustomerId { get; set; }
        public Guid ShipToSnapshotId { get; set; }
        public Guid SkuId { get; set; }
        public Guid LoadSourceLineId { get; set; }
        public Guid StockRightId { get; set; }
        public decimal Loaded { get; set; }
        public decimal Granted { get; set; }
        public decimal Consumed { get; set; }
        public decimal Released { get; set; }
        public string RightStatus { get; set; } = "";
        public string OrderStatus { get; set; } = "";
        public decimal Confirmed { get; set; }
        public decimal UnitPriceSnapshot { get; set; }
        public int TermsDaysSnapshot { get; set; }
        public DateTime ConfirmedAtUtc { get; set; }
        public string ConfirmActor { get; set; } = "";
        public Guid PolicyId { get; set; }
        public int PolicyVersion { get; set; }
        public string BaseUom { get; set; } = "";
        public string PolicyPayload { get; set; } = "";
        public byte[] SourceHash { get; set; } = [];
        public decimal GrossAmount { get; set; }
    }

    private sealed record C4Totals(decimal OnOrder, decimal ConfirmedOrder, decimal OnLine, decimal LinePayable,
        decimal OnRight, decimal OnManifest);

    public async Task<BusinessCommandOutcome> DeliverC4(ClaimsPrincipal principal, C4DeliveryInput input, CancellationToken ct)
    {
        if (input.CommandId == Guid.Empty || input.TripId == Guid.Empty || input.ManifestLineId == Guid.Empty
            || input.ShipToVersion < 1 || !ValidQuantity(input.Quantity))
            return BusinessCommandOutcome.Rejected("INVALID_COMMAND_IDENTITY");
        var key = C2Key(principal, input.CommandId);
        var receipt = await WithC4Authority(principal, input.TripId, async (db, tx, tenant, actor) =>
        {
            await LockCommand(db, tx, tenant, key, ct);
            await FindTrip(db, tx, tenant, input.TripId, ct);
            var args = new { Tenant = tenant, Actor = actor, input.CommandId, input.TripId,
                input.ManifestLineId, input.ShipToVersion, input.Quantity };
            var previous = await db.QuerySingleOrDefaultAsync<C4Evidence>(Sql("""
                SELECT Id,TripId,ManifestLineId,ShipToVersion,Quantity,OccurredAtUtc,
                    CAST((OccurredAtUtc AT TIME ZONE 'UTC') AT TIME ZONE 'SE Asia Standard Time' AS date) AS BusinessDate
                FROM dbo.C4DeliveryEvidence WITH (UPDLOCK,HOLDLOCK)
                WHERE TenantId=@Tenant AND ActorId=@Actor AND ClientCommandId=@CommandId
                """, args, tx, ct));
            if (previous is not null) return new C4Receipt(previous);
            if (await db.ExecuteScalarAsync<int>(Sql("""
                SELECT COUNT(*) FROM dbo.BusinessCommand WITH (UPDLOCK,HOLDLOCK)
                WHERE TenantId=@Tenant AND ProducerId=@ProducerId AND CommandId=@CommandId
                """, new { Tenant = tenant, key.ProducerId, key.CommandId }, tx, ct)) != 0)
                return new C4Receipt(null, Conflict: true);
            if (await db.ExecuteScalarAsync<int>(Sql("""
                SELECT COUNT(*) FROM dbo.TripManifestLine l
                JOIN dbo.PhysicalStockMovement m ON m.TenantId=l.TenantId AND m.TripManifestLineId=l.Id
                WHERE l.TenantId=@Tenant AND l.Id=@ManifestLineId AND l.TripId=@TripId AND l.IsC3=1 AND l.RightKind='A'
                """, args, tx, ct)) != 1)
                return new C4Receipt(null, "C4_LOADED_LINE_NOT_FOUND");
            if (await db.ExecuteScalarAsync<int>(Sql("""
                SELECT COUNT(*) FROM dbo.C4DeliveryEvidence e
                JOIN dbo.BusinessCommand b ON b.TenantId=e.TenantId AND b.Id=e.Id AND b.Status='RECEIVED'
                WHERE e.TenantId=@Tenant AND e.TripId=@TripId AND e.ManifestLineId=@ManifestLineId
                """, args, tx, ct)) != 0)
                return new C4Receipt(null, "C4_EXISTING_DELIVERY_PENDING");

            Guid id = Guid.NewGuid();
            byte[] hash = CommandPayloadHash.Compute("C4_DELIVERY", tenant, actor, key.SourceNumber,
                ("Evidence", id.ToString("N")));
            await InsertReceivedCommand(db, tx, tenant, actor, key, id, hash, "sales", null, input.TripId, ct);
            var captured = await db.QuerySingleAsync<C4Evidence>(Sql($"""
                INSERT dbo.C4DeliveryEvidence(TenantId,Id,ActorId,ClientCommandId,TripId,ManifestLineId,
                    ShipToVersion,Quantity,OccurredAtUtc)
                OUTPUT inserted.Id,inserted.TripId,inserted.ManifestLineId,inserted.ShipToVersion,
                    inserted.Quantity,inserted.OccurredAtUtc,
                    CAST((inserted.OccurredAtUtc AT TIME ZONE 'UTC') AT TIME ZONE 'SE Asia Standard Time' AS date) AS BusinessDate
                VALUES(@Tenant,@Id,@Actor,@CommandId,@TripId,@ManifestLineId,@ShipToVersion,@Quantity,{App02c6Clock.SqlUtcNow})
                """, new { args.Tenant, Id = id, args.Actor, args.CommandId, args.TripId, args.ManifestLineId,
                    args.ShipToVersion, args.Quantity }, tx, ct));
            return new C4Receipt(captured);
        }, ct);
        if (receipt.Conflict) return BusinessCommandOutcome.Conflict();
        if (receipt.Evidence is not C4Evidence evidence)
            return BusinessCommandOutcome.Rejected(receipt.Rejection ?? throw new InvalidOperationException("Missing receipt outcome."));
        if (evidence.TripId != input.TripId || evidence.ManifestLineId != input.ManifestLineId
            || evidence.ShipToVersion != input.ShipToVersion || evidence.Quantity != input.Quantity)
            return BusinessCommandOutcome.Conflict();

        return await ExecuteAsync(principal, key, "C4_DELIVERY",
            [("Evidence", evidence.Id.ToString("N"))],
            (db, tx, tenant, actor, command, token) =>
                ApplyDeliveryC4(db, tx, tenant, actor, command, key, evidence, token),
            ct, "sales", deliveryTrip: input.TripId, pendingCommand: evidence.Id);
    }

    private static async Task<BusinessCommandOutcome> ApplyDeliveryC4(SqlConnection db, SqlTransaction tx,
        string tenant, string actor, Guid command, C2CommandKey key, C4Evidence evidence, CancellationToken ct)
    {
        var trip = await FindTrip(db, tx, tenant, evidence.TripId, ct);
        if (trip is not { Status: "PREPARED" or "IN_PROGRESS", VehicleLocationId: Guid location,
            VehicleKind: "VEHICLE", VehicleActive: true })
            return BusinessCommandOutcome.Rejected("TRIP_NOT_ELIGIBLE");
        if (await SourceUsed(db, tx, tenant, "A_DELIVERY", key.SourceNumber, ct))
            return BusinessCommandOutcome.Rejected("SOURCE_ALREADY_USED");
        var line = await db.QuerySingleOrDefaultAsync<C4PostingLine>(Sql("""
            SELECT o.Id AS OrderId,s.AOrderLineId AS OrderLineId,o.CustomerId,c.ShipToSnapshotId,
                l.SkuId,l.LoadSourceLineId,l.StockRightId,l.LoadedBaseQuantity AS Loaded,
                r.GrantedBaseQuantity AS Granted,r.ConsumedBaseQuantity AS Consumed,r.ReleasedBaseQuantity AS Released,
                r.Status AS RightStatus,o.Status AS OrderStatus,s.ConfirmedBaseQuantity AS Confirmed,
                s.UnitPriceSnapshot,s.TermsDaysSnapshot,s.ConfirmedAtUtc,cs.ActorId AS ConfirmActor,
                p.PolicyId,p.PolicyVersion,p.PolicyPayload,p.SourceHash,p.BaseUom,p.GrossAmount
            FROM dbo.TripManifestLine l
            JOIN dbo.TripManifest m ON m.TenantId=l.TenantId AND m.Id=l.TripManifestId AND m.Status='LOADED' AND m.IsC3=1
            JOIN dbo.PhysicalStockMovement mv ON mv.TenantId=l.TenantId AND mv.TripManifestLineId=l.Id
            JOIN dbo.BusinessSource loadSource ON loadSource.TenantId=m.TenantId AND loadSource.Id=m.LoadBusinessSourceId
            JOIN dbo.C2CommandScope loadScope ON loadScope.TenantId=loadSource.TenantId
                AND loadScope.CommandRowId=loadSource.BusinessCommandId AND loadScope.LoadTripId=l.TripId
            JOIN dbo.BusinessCommand loadCommand ON loadCommand.TenantId=loadSource.TenantId
                AND loadCommand.Id=loadSource.BusinessCommandId AND loadCommand.Status='APPLIED'
            JOIN dbo.StockRight r WITH (UPDLOCK,HOLDLOCK) ON r.TenantId=l.TenantId AND r.Id=l.StockRightId
            JOIN dbo.AReservation rs ON rs.TenantId=r.TenantId AND rs.StockRightId=r.Id
            JOIN dbo.AConfirmedLineSnapshot s ON s.TenantId=rs.TenantId AND s.AOrderLineId=rs.AOrderLineId
                AND s.TripId=l.TripId AND s.SkuId=l.SkuId
            JOIN dbo.AOrder o WITH (UPDLOCK,HOLDLOCK) ON o.TenantId=s.TenantId AND o.Id=s.AOrderId
            JOIN dbo.Customer customer ON customer.TenantId=o.TenantId AND customer.Id=o.CustomerId AND customer.IsActive=1
            JOIN dbo.C2Confirmation c ON c.TenantId=o.TenantId AND c.OrderId=o.Id AND c.ConfirmSourceId=s.ConfirmBusinessSourceId
                AND c.CustomerId=o.CustomerId AND c.ShipToSnapshotId=o.ShipToSnapshotId
            JOIN dbo.BusinessSource cs ON cs.TenantId=c.TenantId AND cs.Id=c.ConfirmSourceId
            JOIN dbo.BusinessCommand cc ON cc.TenantId=cs.TenantId AND cc.Id=cs.BusinessCommandId AND cc.Status='APPLIED'
            JOIN dbo.C2CommandScope confirmScope ON confirmScope.TenantId=cc.TenantId
                AND confirmScope.CommandRowId=cc.Id AND confirmScope.RoleCode='sales'
                AND confirmScope.LoadTripId IS NULL AND confirmScope.DeliveryTripId IS NULL
            JOIN dbo.C2LinePolicy p ON p.TenantId=s.TenantId AND p.OrderLineId=s.AOrderLineId
            JOIN dbo.Sku sku ON sku.TenantId=l.TenantId AND sku.Id=l.SkuId AND sku.IsActive=1
                AND CONVERT(varbinary(48),sku.BaseUom)=CONVERT(varbinary(48),p.BaseUom)
            WHERE l.TenantId=@Tenant AND l.Id=@ManifestLineId AND l.TripId=@TripId
                AND l.VehicleLocationId=@Location AND l.RightKind='A' AND l.LoadedBaseQuantity>0
            """, new { Tenant = tenant, evidence.ManifestLineId, evidence.TripId, Location = location }, tx, ct));
        if (line is null || line.OrderStatus is not ("CONFIRMED" or "PARTIALLY_DELIVERED"))
            return BusinessCommandOutcome.Rejected("C4_LOADED_LINE_NOT_DELIVERABLE");
        var address = await CurrentC4Address(db, tx, tenant, line.OrderId, line.ShipToSnapshotId, ct);
        if (address.Version != evidence.ShipToVersion)
            return BusinessCommandOutcome.Rejected("C4_STALE_SHIPTO");

        var policy = await ValidateC4Policy(db, tx, tenant, line, ct);
        if (policy is null) return BusinessCommandOutcome.Rejected("C4_FROZEN_POLICY_INVALID");
        var totals = await db.QuerySingleAsync<C4Totals>(Sql("""
            SELECT
                (SELECT COALESCE(SUM(BaseQuantity),0) FROM dbo.SaleDelivery WHERE TenantId=@Tenant AND AOrderId=@OrderId) AS OnOrder,
                (SELECT COALESCE(SUM(ConfirmedBaseQuantity),0) FROM dbo.AConfirmedLineSnapshot
                    WHERE TenantId=@Tenant AND AOrderId=@OrderId) AS ConfirmedOrder,
                (SELECT COALESCE(SUM(BaseQuantity),0) FROM dbo.SaleDelivery WHERE TenantId=@Tenant AND AOrderLineId=@OrderLineId) AS OnLine,
                (SELECT COALESCE(SUM(PayableAmount),0) FROM dbo.SaleDelivery WHERE TenantId=@Tenant AND AOrderLineId=@OrderLineId) AS LinePayable,
                (SELECT COALESCE(SUM(BaseQuantity),0) FROM dbo.SaleDelivery WHERE TenantId=@Tenant AND StockRightId=@StockRightId) AS OnRight,
                (SELECT COALESCE(SUM(BaseQuantity),0) FROM dbo.SaleDelivery WHERE TenantId=@Tenant AND TripManifestLineId=@ManifestLineId) AS OnManifest
            """, new { Tenant = tenant, line.OrderId, line.OrderLineId, line.StockRightId, evidence.ManifestLineId }, tx, ct));
        decimal qty = evidence.Quantity;
        if (line.RightStatus != "HELD" || totals.OnRight != line.Consumed
            || qty > line.Granted - line.Consumed - line.Released || qty > line.Loaded - totals.OnManifest
            || qty > line.Confirmed - totals.OnLine || qty > totals.ConfirmedOrder - totals.OnOrder)
            return BusinessCommandOutcome.Rejected("C4_QUANTITY_NOT_AVAILABLE");

        decimal unit = (policy.UnitPrice!.Value - policy.DiscountPerUnit!.Value) * (1 + policy.TaxRate!.Value);
        decimal Gross(decimal quantity) => decimal.Round(unit * quantity, 4, MidpointRounding.AwayFromZero);
        if (totals.LinePayable != Gross(totals.OnLine))
            return BusinessCommandOutcome.Rejected("C4_AR_SOURCE_DRIFT");
        decimal payable = Gross(totals.OnLine + qty) - totals.LinePayable;
        DateTime due = evidence.BusinessDate.AddDays(policy.TermsDays!.Value);
        var request = new PostAPartialDeliveryCommand(key.ProducerId, key.CommandId, key.SourceNumber,
            evidence.TripId, line.CustomerId, address.SnapshotId, line.OrderId, line.OrderLineId,
            evidence.ManifestLineId, line.SkuId, qty);
        return await PostValidatedDelivery(db, tx, tenant, actor, command, request,
            new TripScope { VehicleLocationId = location, BusinessDate = evidence.BusinessDate },
            new FrozenRight { ManifestLineId = evidence.ManifestLineId, LoadSourceLineId = line.LoadSourceLineId,
                StockRightId = line.StockRightId },
            new FrozenOrderTotals { ConfirmedQuantity = totals.ConfirmedOrder, DeliveredQuantity = totals.OnOrder },
            payable, due, ct, evidence.Id, address.AmendmentId, evidence.OccurredAtUtc, totals.OnLine);
    }

    private static async Task<C2CommercialPolicy?> ValidateC4Policy(SqlConnection db, SqlTransaction tx,
        string tenant, C4PostingLine line, CancellationToken ct)
    {
        if (!CryptographicOperations.FixedTimeEquals(line.SourceHash, SHA256.HashData(Encoding.UTF8.GetBytes(line.PolicyPayload))))
            return null;
        var recorded = JsonSerializer.Deserialize<C4FrozenPolicy>(line.PolicyPayload, C1Validation.Json);
        if (recorded is null || recorded.Seller != tenant || recorded.CustomerId != line.CustomerId || recorded.SkuId != line.SkuId
            || recorded.BaseUom != line.BaseUom || recorded.Factor != 1 || recorded.Currency != "SYNTHETIC_UNITLESS"
            || recorded.InvoiceEligible)
            return null;
        var policy = await db.QuerySingleOrDefaultAsync<C2CommercialPolicy>(Sql("""
            SELECT Id,Version,Decision,EffectiveFromUtc,EffectiveUntilUtc,Maker,Checker,
                ApprovedAtUtc,EvidenceRef,UnitPrice,DiscountPerUnit,TaxRate,TermsDays,CreditCap,SellerTimeZone
            FROM dbo.C2CommercialPolicy WHERE TenantId=@Tenant AND Id=@PolicyId AND CustomerId=@CustomerId AND SkuId=@SkuId
            """, new { Tenant = tenant, line.PolicyId, line.CustomerId, line.SkuId }, tx, ct));
        if (policy is null || recorded.Policy != policy || policy.Version != line.PolicyVersion
            || policy.UnitPrice != line.UnitPriceSnapshot || policy.TermsDays != line.TermsDaysSnapshot
            || policy.EffectiveFromUtc > line.ConfirmedAtUtc || !ValidC2Policy(policy, line.ConfirmActor, line.ConfirmedAtUtc))
            return null;
        decimal unit = (policy.UnitPrice!.Value - policy.DiscountPerUnit!.Value) * (1 + policy.TaxRate!.Value);
        if (unit > 0 && line.Confirmed > 999999999999999.9999m / unit) return null;
        return decimal.Round(unit * line.Confirmed, 4, MidpointRounding.AwayFromZero) == line.GrossAmount ? policy : null;
    }

    private static async Task<C4Address> CurrentC4Address(SqlConnection db, SqlTransaction tx,
        string tenant, Guid orderId, Guid original, CancellationToken ct) =>
        await db.QuerySingleOrDefaultAsync<C4Address>(Sql("""
            SELECT TOP(1) ShipToSnapshotId AS SnapshotId,Id AS AmendmentId,Version
            FROM dbo.C4ShipToAmendment WHERE TenantId=@Tenant AND OrderId=@OrderId ORDER BY Version DESC
            """, new { Tenant = tenant, OrderId = orderId }, tx, ct)) ?? new(original, null, 1);
}
