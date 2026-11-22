using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using FmcgDms.Api.Authentication;
using FmcgDms.Api.Tenancy;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Persistence;

/// <summary>
/// APP-01C.3: atomic synthetic A partial-delivery source, physical
/// issue, moving-average COGS and due-dated AR. No client-selected price/
/// terms, B grant, receipt/custody/fund, legal invoice or production route.
/// </summary>
public sealed class App01c3APostingService(TenantConnections connections)
{
    private const decimal MaximumQuantity = 999999999999999.999m;
    private const decimal MaximumAmount = 999999999999999.9999m;
    private const string Kind = "A_DELIVERY";

    private static CommandDefinition Sql(string query, object? parameters, IDbTransaction tx, CancellationToken ct) =>
        new(query, parameters, tx, cancellationToken: ct);

    private static bool ValidKey(string? value, int max) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= max
        && string.Equals(value, value.Trim(), StringComparison.Ordinal)
        && !value.Any(char.IsControl);

    private static bool ValidQuantity(decimal value) =>
        value > 0 && value <= MaximumQuantity && decimal.Round(value, 3) == value;

    private static bool ValidMoney(decimal value) =>
        value > 0 && value <= MaximumAmount && decimal.Round(value, 4) == value;

    private sealed class ExistingCommand
    {
        public Guid Id { get; set; }
        public short PayloadHashVersion { get; set; }
        public byte[] PayloadHash { get; set; } = [];
        public string Status { get; set; } = "";
        public string OutcomeCode { get; set; } = "";
        public string? ReasonCode { get; set; }
    }

    private sealed class TripScope
    {
        public Guid VehicleLocationId { get; set; }
        public DateTime BusinessDate { get; set; }
        public string Status { get; set; } = "";
        public string VehicleKind { get; set; } = "";
        public bool VehicleActive { get; set; }
    }

    private sealed class FrozenRight
    {
        public Guid ManifestLineId { get; set; }
        public Guid LoadSourceLineId { get; set; }
        public Guid StockRightId { get; set; }
        public decimal LoadedBaseQuantity { get; set; }
        public decimal GrantedBaseQuantity { get; set; }
        public decimal ConsumedBaseQuantity { get; set; }
        public decimal ReleasedBaseQuantity { get; set; }
        public string RightStatus { get; set; } = "";
        public string OrderStatus { get; set; } = "";
        public decimal ConfirmedBaseQuantity { get; set; }
        public decimal UnitPriceSnapshot { get; set; }
        public int TermsDaysSnapshot { get; set; }
    }

    private sealed class Balance
    {
        public decimal OnHandBaseQuantity { get; set; }
        public decimal OnHandCostAmount { get; set; }
        public decimal SourceBaseQuantity { get; set; }
        public decimal SourceCostAmount { get; set; }
    }

    /// <summary>
    /// A valid command identity has exactly one persisted outcome (including
    /// business rejections). SQL faults roll back the command row as well.
    /// Cross-kind idempotency shares APP-01A's persisted key and app-lock name.
    /// </summary>
    public async Task<BusinessCommandOutcome> PostAPartialDeliveryAsync(
        ClaimsPrincipal principal, PostAPartialDeliveryCommand request, CancellationToken ct)
    {
        if (!ValidKey(request.ProducerId, 128) || !ValidKey(request.CommandId, 128)
            || !ValidKey(request.SourceNumber, 80))
            return BusinessCommandOutcome.Rejected("INVALID_COMMAND_IDENTITY");
        if (!principal.HasClaim("synthetic_role", "sales"))
            throw new UnauthorizedAccessException("Synthetic sales role required.");
        string tenant = SyntheticAuth.GetTenantId(principal);
        string actor = principal.FindFirst("actor_id")?.Value
            ?? throw new UnauthorizedAccessException("Validated actor identity required.");
        if (!ValidKey(actor, 128) || !string.Equals(actor, principal.FindFirst("sub")?.Value,
                StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Invalid actor identity.");

        byte[] hash = CommandPayloadHash.Compute(Kind, tenant, actor, request.SourceNumber,
            ("TripId", request.TripId.ToString("N")),
            ("CustomerId", request.CustomerId.ToString("N")),
            ("ShipToSnapshotId", request.ShipToSnapshotId.ToString("N")),
            ("AOrderId", request.AOrderId.ToString("N")),
            ("AOrderLineId", request.AOrderLineId.ToString("N")),
            ("TripManifestLineId", request.TripManifestLineId.ToString("N")),
            ("SkuId", request.SkuId.ToString("N")),
            ("BaseQuantity", CommandPayloadHash.Decimal(request.BaseQuantity)));

        await using SqlConnection db = await connections.OpenSyntheticScratchAsync(principal, ct);
        await using SqlTransaction tx =
            (SqlTransaction)await db.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            string key = "APP01A2:" + Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(tenant + "\0" + request.ProducerId + "\0" + request.CommandId)));
            await LockAsync(db, tx, key, ct);

            var existing = await db.QuerySingleOrDefaultAsync<ExistingCommand>(Sql("""
                SELECT Id,PayloadHashVersion,PayloadHash,Status,OutcomeCode,ReasonCode
                FROM dbo.BusinessCommand WITH (UPDLOCK,HOLDLOCK)
                WHERE TenantId=@Tenant AND ProducerId=@Producer AND CommandId=@Command
                """, new { Tenant = tenant, Producer = request.ProducerId,
                    Command = request.CommandId }, tx, ct));
            if (existing is not null)
            {
                if (existing.PayloadHashVersion != CommandPayloadHash.Version
                    || !CryptographicOperations.FixedTimeEquals(existing.PayloadHash, hash))
                {
                    await tx.RollbackAsync(ct);
                    return BusinessCommandOutcome.Conflict();
                }
                if (existing.Status is not ("APPLIED" or "REJECTED"))
                    throw new InvalidOperationException("Persisted command is incomplete.");
                Guid? source = await db.QuerySingleOrDefaultAsync<Guid?>(Sql("""
                    SELECT Id FROM dbo.BusinessSource
                    WHERE TenantId=@Tenant AND BusinessCommandId=@Command
                    """, new { Tenant = tenant, Command = existing.Id }, tx, ct));
                await tx.CommitAsync(ct);
                return new BusinessCommandOutcome(existing.Status, existing.OutcomeCode,
                    existing.ReasonCode, true, source);
            }

            Guid commandId = Guid.NewGuid();
            await db.ExecuteAsync(Sql("""
                INSERT dbo.BusinessCommand(Id,TenantId,ProducerId,CommandId,
                    PayloadHashVersion,PayloadHash,ActorId,Status)
                VALUES(@Id,@Tenant,@Producer,@Command,@Version,@Hash,@Actor,N'RECEIVED')
                """, new { Id = commandId, Tenant = tenant, Producer = request.ProducerId,
                    Command = request.CommandId, Version = CommandPayloadHash.Version,
                    Hash = hash, Actor = actor }, tx, ct));

            // Same per-tenant/Trip app-lock resource as APP-01C.2 B20:
            // concurrent A/A and A/B posts serialize before touching shared
            // vehicle stock, rights or source-ledger materialization.
            BusinessCommandOutcome result;
            if (request.TripId == Guid.Empty || request.CustomerId == Guid.Empty
                || request.ShipToSnapshotId == Guid.Empty || request.AOrderId == Guid.Empty
                || request.AOrderLineId == Guid.Empty || request.TripManifestLineId == Guid.Empty
                || request.SkuId == Guid.Empty)
                result = BusinessCommandOutcome.Rejected("INVALID_REFERENCE");
            else if (!ValidQuantity(request.BaseQuantity))
                result = BusinessCommandOutcome.Rejected("QUANTITY_INVALID");
            else
            {
                await LockAsync(db, tx, "APP01C:TRIP:" + tenant + ":" +
                    request.TripId.ToString("N"), ct);
                result = await PostAsync(db, tx, tenant, actor, commandId, request, ct);
            }

            if (result.Status is not ("APPLIED" or "REJECTED"))
                throw new InvalidOperationException("Command cannot persist an incomplete outcome.");
            if (await db.ExecuteAsync(Sql("""
                UPDATE dbo.BusinessCommand SET
                    Status=@Status,OutcomeCode=@Outcome,ReasonCode=@Reason,
                    CompletedAtUtc=SYSUTCDATETIME()
                WHERE TenantId=@Tenant AND Id=@Id AND Status=N'RECEIVED'
                """, new { Tenant = tenant, Id = commandId, result.Status,
                    Outcome = result.OutcomeCode, Reason = result.ReasonCode }, tx, ct)) != 1)
                throw new InvalidOperationException("Command completion was not unique.");
            await tx.CommitAsync(ct);
            return result;
        }
        catch
        {
            try { await tx.RollbackAsync(CancellationToken.None); }
            catch (InvalidOperationException) { /* SQL Server already rolled back. */ }
            throw;
        }
    }

    private static async Task LockAsync(SqlConnection db, SqlTransaction tx,
        string key, CancellationToken ct)
    {
        int result = await db.ExecuteScalarAsync<int>(Sql("""
            DECLARE @r int;
            EXEC @r=sys.sp_getapplock @Resource=@Resource,
                @LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000;
            SELECT @r;
            """, new { Resource = key }, tx, ct));
        if (result < 0) throw new TimeoutException("Posting app-lock was not granted.");
    }

    private static async Task<BusinessCommandOutcome> PostAsync(
        SqlConnection db, SqlTransaction tx, string tenant, string actor,
        Guid commandId, PostAPartialDeliveryCommand request, CancellationToken ct)
    {
        // One immutable source number per (tenant, A_DELIVERY). The canonical
        // cross-kind command namespace is checked before this preflight.
        bool sourceUsed = await db.ExecuteScalarAsync<bool>(Sql("""
            SELECT CAST(CASE WHEN EXISTS (
                SELECT 1 FROM dbo.BusinessSource WITH (UPDLOCK,HOLDLOCK)
                WHERE TenantId=@Tenant AND SourceKind=N'A_DELIVERY'
                    AND SourceNumber=@Number
            ) THEN 1 ELSE 0 END AS bit)
            """, new { Tenant = tenant, Number = request.SourceNumber }, tx, ct));
        if (sourceUsed)
            return BusinessCommandOutcome.Rejected("SOURCE_ALREADY_USED");

        var p = new
        {
            Tenant = tenant, Trip = request.TripId, Customer = request.CustomerId,
            ShipTo = request.ShipToSnapshotId, Order = request.AOrderId,
            OrderLine = request.AOrderLineId, ManifestLine = request.TripManifestLineId,
            Sku = request.SkuId
        };
        var trip = await db.QuerySingleOrDefaultAsync<TripScope>(Sql("""
            SELECT t.VehicleLocationId,t.BusinessDate,t.Status,
                v.Kind AS VehicleKind,v.IsActive AS VehicleActive
            FROM dbo.Trip t WITH (UPDLOCK,HOLDLOCK)
            JOIN dbo.StockLocation v WITH (HOLDLOCK)
                ON v.TenantId=t.TenantId AND v.Id=t.VehicleLocationId
            WHERE t.TenantId=@Tenant AND t.Id=@Trip
            """, p, tx, ct));
        if (trip is null || trip.Status is not ("PREPARED" or "IN_PROGRESS")
            || trip.VehicleKind != "VEHICLE" || !trip.VehicleActive)
            return BusinessCommandOutcome.Rejected("TRIP_NOT_ELIGIBLE");

        bool customerSkuEligible = await db.ExecuteScalarAsync<bool>(Sql("""
            SELECT CAST(CASE WHEN EXISTS (
                SELECT 1 FROM dbo.Customer c WITH (HOLDLOCK)
                JOIN dbo.ShipToSnapshot sh WITH (HOLDLOCK)
                    ON sh.TenantId=c.TenantId AND sh.CustomerId=c.Id
                JOIN dbo.Sku k WITH (HOLDLOCK)
                    ON k.TenantId=c.TenantId AND k.Id=@Sku
                WHERE c.TenantId=@Tenant AND c.Id=@Customer AND sh.Id=@ShipTo
                    AND c.IsActive=1 AND k.IsActive=1
            ) THEN 1 ELSE 0 END AS bit)
            """, p, tx, ct));
        if (!customerSkuEligible)
            return BusinessCommandOutcome.Rejected("CUSTOMER_SHIPTO_OR_SKU_NOT_ELIGIBLE");

        // Both the loaded physical line and A reservation must trace to the
        // same exact confirmed order line. No request price/terms is accepted.
        // The approval is a separate immutable witnessed row, not a flag from
        // the sales request or an inferred default for legacy confirmations.
        var right = await db.QuerySingleOrDefaultAsync<FrozenRight>(Sql("""
            SELECT l.Id AS ManifestLineId,l.LoadSourceLineId,l.StockRightId,
                l.LoadedBaseQuantity,r.GrantedBaseQuantity,
                r.ConsumedBaseQuantity,r.ReleasedBaseQuantity,r.Status AS RightStatus,
                o.Status AS OrderStatus,s.ConfirmedBaseQuantity,
                s.UnitPriceSnapshot,s.TermsDaysSnapshot
            FROM dbo.TripManifestLine l WITH (UPDLOCK,HOLDLOCK)
            JOIN dbo.TripManifest m WITH (HOLDLOCK)
                ON m.TenantId=l.TenantId AND m.Id=l.TripManifestId
                AND m.TripId=l.TripId AND m.Status=N'LOADED' AND m.IsC3=0
            JOIN dbo.StockRight r WITH (UPDLOCK,HOLDLOCK)
                ON r.TenantId=l.TenantId AND r.Id=l.StockRightId
                AND r.TripId=l.TripId AND r.SkuId=l.SkuId AND r.RightKind='A'
            JOIN dbo.AReservation res WITH (HOLDLOCK)
                ON res.TenantId=r.TenantId AND res.StockRightId=r.Id
                AND res.AOrderLineId=@OrderLine
            JOIN dbo.AOrderLine ol WITH (HOLDLOCK)
                ON ol.TenantId=r.TenantId AND ol.Id=res.AOrderLineId
                AND ol.AOrderId=@Order AND ol.SkuId=l.SkuId
            JOIN dbo.AOrder o WITH (UPDLOCK,HOLDLOCK)
                ON o.TenantId=ol.TenantId AND o.Id=ol.AOrderId
                AND o.TripId=l.TripId AND o.CustomerId=@Customer
                AND o.ShipToSnapshotId=@ShipTo
            JOIN dbo.AConfirmedLineSnapshot s WITH (HOLDLOCK)
                ON s.TenantId=ol.TenantId AND s.AOrderLineId=ol.Id
                AND s.AOrderId=ol.AOrderId AND s.SkuId=ol.SkuId
                AND s.TripId=l.TripId AND s.CustomerId=@Customer
            JOIN dbo.AOrderTermsApproval ap WITH (HOLDLOCK)
                ON ap.TenantId=s.TenantId AND ap.AOrderId=s.AOrderId
                AND ap.TripId=s.TripId AND ap.CustomerId=s.CustomerId
                AND ap.TermsDays=s.TermsDaysSnapshot AND ap.Decision=N'APPROVED'
                AND ap.ApprovedAtUtc<=s.ConfirmedAtUtc
            JOIN dbo.BusinessSource a WITH (HOLDLOCK)
                ON a.TenantId=r.TenantId AND a.TripId=l.TripId
                AND a.SourceKind=N'STOCK_ALLOC'
            JOIN dbo.BusinessSourceLine al WITH (HOLDLOCK)
                ON al.TenantId=r.TenantId AND al.Id=r.BusinessSourceLineId
                AND al.BusinessSourceId=a.Id AND al.SkuId=l.SkuId
                AND al.BaseQuantity=r.GrantedBaseQuantity
            JOIN dbo.PhysicalStockMovement mv WITH (HOLDLOCK)
                ON mv.TenantId=l.TenantId AND mv.TripManifestLineId=l.Id
                AND mv.LoadSourceLineId=l.LoadSourceLineId
                AND mv.BaseQuantity=l.LoadedBaseQuantity
                AND mv.CostAmount=l.LoadedCostAmount
            WHERE l.TenantId=@Tenant AND l.Id=@ManifestLine
                AND l.TripId=@Trip AND l.SkuId=@Sku
                AND l.RightKind='A' AND l.LoadSourceLineId IS NOT NULL
                AND l.LoadedBaseQuantity>0
                AND NOT EXISTS (SELECT 1 FROM dbo.C2Confirmation c2 WHERE c2.TenantId=o.TenantId AND c2.OrderId=o.Id)
            """, p, tx, ct));
        if (right is null)
            return BusinessCommandOutcome.Rejected("A_FROZEN_LOADED_RIGHT_NOT_FOUND");
        if (right.OrderStatus is not ("CONFIRMED" or "PARTIALLY_DELIVERED"))
            return BusinessCommandOutcome.Rejected("A_ORDER_NOT_DELIVERABLE");
        if (!ValidMoney(right.UnitPriceSnapshot)
            || right.TermsDaysSnapshot is < 1 or > 365)
            return BusinessCommandOutcome.Rejected("A_FROZEN_PRICE_OR_TERMS_INVALID");

        // Require a complete immutable confirmation and the full original A
        // reservation lineage. Sum per right AND per order line so a split
        // manifest cannot double-spend the same source.
        var frozen = await db.QuerySingleAsync<FrozenOrderTotals>(Sql("""
            SELECT
                (SELECT COUNT(*) FROM dbo.AOrderLine WITH (HOLDLOCK)
                 WHERE TenantId=@Tenant AND AOrderId=@Order) AS OrderLines,
                (SELECT COUNT(*) FROM dbo.AConfirmedLineSnapshot WITH (HOLDLOCK)
                 WHERE TenantId=@Tenant AND AOrderId=@Order) AS FrozenLines,
                (SELECT COALESCE(SUM(ConfirmedBaseQuantity),0)
                 FROM dbo.AConfirmedLineSnapshot WITH (HOLDLOCK)
                 WHERE TenantId=@Tenant AND AOrderId=@Order) AS ConfirmedQuantity,
                (SELECT COALESCE(SUM(d.BaseQuantity),0)
                 FROM dbo.SaleDelivery d WITH (UPDLOCK,HOLDLOCK)
                 WHERE d.TenantId=@Tenant AND d.AOrderId=@Order
                     AND d.RightKind='A') AS DeliveredQuantity,
                (SELECT COALESCE(SUM(r.GrantedBaseQuantity),0)
                 FROM dbo.AReservation ar WITH (HOLDLOCK)
                 JOIN dbo.StockRight r WITH (HOLDLOCK)
                   ON r.TenantId=ar.TenantId AND r.Id=ar.StockRightId
                 WHERE ar.TenantId=@Tenant AND ar.AOrderLineId=@OrderLine
                     AND r.TripId=@Trip AND r.SkuId=@Sku AND r.RightKind='A')
                    AS LineGranted,
                (SELECT COALESCE(SUM(BaseQuantity),0)
                 FROM dbo.SaleDelivery WITH (UPDLOCK,HOLDLOCK)
                 WHERE TenantId=@Tenant AND AOrderLineId=@OrderLine
                    AND RightKind='A') AS LineDelivered,
                (SELECT COALESCE(SUM(BaseQuantity),0)
                 FROM dbo.SaleDelivery WITH (UPDLOCK,HOLDLOCK)
                 WHERE TenantId=@Tenant AND StockRightId=@Right
                    AND RightKind='A') AS RightDelivered,
                (SELECT COALESCE(SUM(BaseQuantity),0)
                 FROM dbo.SaleDelivery WITH (UPDLOCK,HOLDLOCK)
                 WHERE TenantId=@Tenant AND TripManifestLineId=@ManifestLine)
                    AS ManifestDelivered
            """, new { p.Tenant, p.Order, p.OrderLine, p.Trip, p.Sku,
                Right = right.StockRightId, p.ManifestLine }, tx, ct));
        if (frozen.OrderLines < 1 || frozen.OrderLines != frozen.FrozenLines
            || frozen.ConfirmedQuantity < request.BaseQuantity)
            return BusinessCommandOutcome.Rejected("A_CONFIRM_SNAPSHOT_INCOMPLETE");
        if (frozen.LineGranted != right.ConfirmedBaseQuantity)
            return BusinessCommandOutcome.Rejected("A_CONFIRM_RIGHT_SOURCE_DRIFT");
        if (frozen.RightDelivered != right.ConsumedBaseQuantity)
            return BusinessCommandOutcome.Rejected("A_RIGHT_SOURCE_DRIFT");
        if (frozen.LineDelivered + request.BaseQuantity > right.ConfirmedBaseQuantity)
            return BusinessCommandOutcome.Rejected("A_LINE_OVERDELIVERY");
        if (right.LoadedBaseQuantity - frozen.ManifestDelivered < request.BaseQuantity)
            return BusinessCommandOutcome.Rejected("A_MANIFEST_INSUFFICIENT");
        if (right.RightStatus != "HELD"
            || right.GrantedBaseQuantity - right.ConsumedBaseQuantity
                - right.ReleasedBaseQuantity < request.BaseQuantity)
            return BusinessCommandOutcome.Rejected("A_HOLD_INSUFFICIENT");
        if (frozen.DeliveredQuantity + request.BaseQuantity > frozen.ConfirmedQuantity)
            return BusinessCommandOutcome.Rejected("A_ORDER_OVERDELIVERY");

        decimal payable;
        try { payable = checked(right.UnitPriceSnapshot * request.BaseQuantity); }
        catch (OverflowException) { payable = -1m; }
        if (!ValidMoney(payable))
            return BusinessCommandOutcome.Rejected("A_PAYABLE_INVALID");
        DateTime due;
        try { due = trip.BusinessDate.Date.AddDays(right.TermsDaysSnapshot); }
        catch (ArgumentOutOfRangeException)
        {
            return BusinessCommandOutcome.Rejected("A_DUE_DATE_INVALID");
        }

        // Both B20 and A25 derive cost from exactly the same source-backed
        // vehicle on-hand, with four-decimal moving-average rounding.
        var vehicle = await db.QuerySingleOrDefaultAsync<Balance>(Sql("""
            SELECT b.OnHandBaseQuantity,b.OnHandCostAmount,
                o.BaseQuantity + COALESCE(loaded.Qty,0) - COALESCE(issued.Qty,0)
                    - COALESCE(unloaded.Qty,0)
                    AS SourceBaseQuantity,
                o.CostAmount + COALESCE(loaded.Cost,0) - COALESCE(issued.Cost,0)
                    - COALESCE(unloaded.Cost,0)
                    AS SourceCostAmount
            FROM dbo.PhysicalStockBalance b WITH (UPDLOCK,HOLDLOCK)
            JOIN dbo.PhysicalStockOpening o WITH (HOLDLOCK)
                ON o.TenantId=b.TenantId AND o.StockLocationId=b.StockLocationId
                AND o.SkuId=b.SkuId
            OUTER APPLY (
                SELECT SUM(mv.BaseQuantity) AS Qty,SUM(mv.CostAmount) AS Cost
                FROM dbo.PhysicalStockMovement mv WITH (HOLDLOCK)
                JOIN dbo.TripManifestLine l WITH (HOLDLOCK)
                    ON l.TenantId=mv.TenantId AND l.Id=mv.TripManifestLineId
                WHERE mv.TenantId=b.TenantId AND l.VehicleLocationId=b.StockLocationId
                    AND l.SkuId=b.SkuId
            ) loaded
            OUTER APPLY (
                SELECT SUM(i.BaseQuantity) AS Qty,SUM(i.CostAmount) AS Cost
                FROM dbo.DeliveryStockIssue i WITH (HOLDLOCK)
                JOIN dbo.TripManifestLine l WITH (HOLDLOCK)
                    ON l.TenantId=i.TenantId AND l.Id=i.TripManifestLineId
                WHERE i.TenantId=b.TenantId AND l.VehicleLocationId=b.StockLocationId
                    AND i.SkuId=b.SkuId
            ) issued
            -- Reused vehicles must subtract completed T6 unloads from the
            -- immutable opening + load - delivery source reconciliation.
            OUTER APPLY (
                SELECT SUM(ul.BaseQuantity) AS Qty,SUM(ul.CostAmount) AS Cost
                FROM dbo.TripUnloadLine ul WITH (HOLDLOCK)
                JOIN dbo.TripUnload u WITH (HOLDLOCK)
                    ON u.TenantId=ul.TenantId AND u.Id=ul.TripUnloadId
                WHERE ul.TenantId=b.TenantId AND u.VehicleLocationId=b.StockLocationId
                    AND ul.SkuId=b.SkuId
            ) unloaded
            WHERE b.TenantId=@Tenant AND b.StockLocationId=@Vehicle AND b.SkuId=@Sku
            """, new { Tenant = tenant, Vehicle = trip.VehicleLocationId,
                Sku = request.SkuId }, tx, ct));
        if (vehicle is null || vehicle.OnHandBaseQuantity < request.BaseQuantity
            || vehicle.OnHandCostAmount < 0)
            return BusinessCommandOutcome.Rejected("VEHICLE_STOCK_INSUFFICIENT");
        if (vehicle.OnHandBaseQuantity != vehicle.SourceBaseQuantity
            || vehicle.OnHandCostAmount != vehicle.SourceCostAmount)
            return BusinessCommandOutcome.Rejected("VEHICLE_STOCK_SOURCE_DRIFT");
        decimal cost = request.BaseQuantity == vehicle.OnHandBaseQuantity
            ? vehicle.OnHandCostAmount
            : decimal.Round(vehicle.OnHandCostAmount / vehicle.OnHandBaseQuantity
                * request.BaseQuantity, 4, MidpointRounding.AwayFromZero);
        if (cost > vehicle.OnHandCostAmount) cost = vehicle.OnHandCostAmount;
        if (cost < 0 || cost > MaximumAmount)
            return BusinessCommandOutcome.Rejected("VEHICLE_COST_INVALID");

        // From here, every source, stock, AR, ledger and order-status mutation
        // is in the same SERIALIZABLE SQL transaction as the command envelope.
        Guid sourceId = Guid.NewGuid(), sourceLine = Guid.NewGuid();
        Guid deliveryId = Guid.NewGuid(), itemId = Guid.NewGuid();
        await db.ExecuteAsync(Sql("""
            INSERT dbo.BusinessSource(Id,TenantId,BusinessCommandId,SourceKind,
                SourceNumber,ActorId,CustomerId,TripId,OccurredAtUtc)
            VALUES(@Id,@Tenant,@Command,N'A_DELIVERY',@Number,@Actor,
                @Customer,@Trip,SYSUTCDATETIME());
            INSERT dbo.BusinessSourceLine(Id,TenantId,BusinessSourceId,LineNumber,
                SkuId,BaseQuantity,Amount)
            VALUES(@Line,@Tenant,@Id,1,@Sku,@Qty,@Payable);
            """, new { Id = sourceId, Line = sourceLine, Tenant = tenant,
                Command = commandId, Number = request.SourceNumber, Actor = actor,
                Customer = request.CustomerId, Trip = request.TripId,
                Sku = request.SkuId, Qty = request.BaseQuantity, Payable = payable }, tx, ct));

        if (await db.ExecuteAsync(Sql("""
            UPDATE dbo.StockRight SET ConsumedBaseQuantity=ConsumedBaseQuantity+@Qty,
                Status=CASE WHEN GrantedBaseQuantity=ConsumedBaseQuantity+ReleasedBaseQuantity+@Qty
                    THEN N'CONSUMED' ELSE N'HELD' END
            WHERE TenantId=@Tenant AND Id=@Right AND RightKind='A' AND Status=N'HELD'
                AND GrantedBaseQuantity-ConsumedBaseQuantity-ReleasedBaseQuantity>=@Qty
            """, new { Tenant = tenant, Right = right.StockRightId,
                Qty = request.BaseQuantity }, tx, ct)) != 1)
            throw new InvalidOperationException("A right changed under lock.");
        if (await db.ExecuteAsync(Sql("""
            UPDATE dbo.PhysicalStockBalance SET
                OnHandBaseQuantity=OnHandBaseQuantity-@Qty,
                OnHandCostAmount=OnHandCostAmount-@Cost
            WHERE TenantId=@Tenant AND StockLocationId=@Vehicle AND SkuId=@Sku
                AND OnHandBaseQuantity>=@Qty AND OnHandCostAmount>=@Cost
            """, new { Tenant = tenant, Vehicle = trip.VehicleLocationId,
                Sku = request.SkuId, Qty = request.BaseQuantity, Cost = cost }, tx, ct)) != 1)
            throw new InvalidOperationException("Vehicle custody changed under lock.");

        await db.ExecuteAsync(Sql("""
            INSERT dbo.SaleDelivery(Id,TenantId,TripId,CustomerId,ShipToSnapshotId,
                TripManifestLineId,LoadSourceLineId,StockRightId,RightKind,SkuId,
                AOrderId,AOrderLineId,BusinessSourceId,BusinessSourceKind,BusinessSourceLineId,
                BaseQuantity,PayableAmount,CostAmount,CostBasisVersion,BusinessDate,InvoiceEligible)
            VALUES(@Delivery,@Tenant,@Trip,@Customer,@ShipTo,@ManifestLine,
                @LoadLine,@Right,'A',@Sku,@Order,@OrderLine,@Source,N'A_DELIVERY',@SourceLine,
                @Qty,@Payable,@Cost,N'SYNTHETIC_MA_V1',@Date,0);
            INSERT dbo.DeliveryStockIssue(Id,TenantId,SaleDeliveryId,
                BusinessSourceLineId,TripManifestLineId,SkuId,BaseQuantity,CostAmount)
            VALUES(@Issue,@Tenant,@Delivery,@SourceLine,@ManifestLine,@Sku,@Qty,@Cost);
            INSERT dbo.ReceivableOpenItem(Id,TenantId,SaleDeliveryId,CustomerId,
                BusinessSourceLineId,RightKind,OriginalAmount,AppliedAmount,DueBusinessDate)
            VALUES(@Item,@Tenant,@Delivery,@Customer,@SourceLine,'A',@Payable,0,@Due);
            """, new { Delivery = deliveryId, Issue = Guid.NewGuid(), Item = itemId,
                Tenant = tenant, Trip = request.TripId, Customer = request.CustomerId,
                ShipTo = request.ShipToSnapshotId, Order = request.AOrderId,
                OrderLine = request.AOrderLineId, ManifestLine = right.ManifestLineId,
                LoadLine = right.LoadSourceLineId, Right = right.StockRightId,
                Sku = request.SkuId, Source = sourceId, SourceLine = sourceLine,
                Qty = request.BaseQuantity, Payable = payable, Cost = cost,
                Date = trip.BusinessDate, Due = due }, tx, ct));

        var effects = new[]
        {
            new PostingEffect("PHYSICAL-OUT", "PHYSICAL_STOCK", -request.BaseQuantity, -cost,
                null, request.SkuId, trip.VehicleLocationId),
            new PostingEffect("COGS", "COGS", 0m, cost,
                null, request.SkuId, null),
            new PostingEffect("AR-GROSS", "AR", 0m, payable,
                request.CustomerId, null, null),
            new PostingEffect("RIGHT-A-USED", "RIGHT_A_CONSUMED", -request.BaseQuantity, 0m,
                null, request.SkuId, null)
        };
        foreach (var effect in effects)
            await AddEffectAsync(db, tx, tenant, sourceLine, effect, ct);

        string newStatus = frozen.DeliveredQuantity + request.BaseQuantity
            == frozen.ConfirmedQuantity ? "FULFILLED" : "PARTIALLY_DELIVERED";
        if (await db.ExecuteAsync(Sql("""
            UPDATE dbo.AOrder SET Status=@NewStatus
            WHERE TenantId=@Tenant AND Id=@Order
                AND Status IN (N'CONFIRMED',N'PARTIALLY_DELIVERED')
            """, new { Tenant = tenant, Order = request.AOrderId,
                NewStatus = newStatus }, tx, ct)) != 1)
            throw new InvalidOperationException("A order changed under lock.");

        var result = await db.QuerySingleAsync<PostingTotals>(Sql("""
            SELECT
                (SELECT COUNT(*) FROM dbo.DeliveryStockIssue
                    WHERE TenantId=@Tenant AND SaleDeliveryId=@Delivery) AS Issues,
                (SELECT COUNT(*) FROM dbo.ReceivableOpenItem
                    WHERE TenantId=@Tenant AND SaleDeliveryId=@Delivery
                        AND OriginalAmount=@Payable AND AppliedAmount=0
                        AND DueBusinessDate=@Due) AS Receivables,
                (SELECT COUNT(*) FROM dbo.LedgerEffect
                    WHERE TenantId=@Tenant AND BusinessSourceLineId=@SourceLine) AS Effects,
                (SELECT COUNT(*) FROM dbo.CashReceipt
                    WHERE TenantId=@Tenant AND SaleDeliveryId=@Delivery) AS Receipts,
                (SELECT COUNT(*) FROM dbo.BGrantConsumption
                    WHERE TenantId=@Tenant AND SaleDeliveryId=@Delivery) AS BGrants
            """, new { Tenant = tenant, Delivery = deliveryId, Payable = payable,
                Due = due, SourceLine = sourceLine }, tx, ct));
        if (result.Issues != 1 || result.Receivables != 1 || result.Effects != 4
            || result.Receipts != 0 || result.BGrants != 0)
            throw new InvalidOperationException("A source-ledger partial posting is incomplete.");
        return BusinessCommandOutcome.Applied("A_PARTIAL_DELIVERY_POSTED", sourceId);
    }

    private sealed class FrozenOrderTotals
    {
        public int OrderLines { get; set; }
        public int FrozenLines { get; set; }
        public decimal ConfirmedQuantity { get; set; }
        public decimal DeliveredQuantity { get; set; }
        public decimal LineGranted { get; set; }
        public decimal LineDelivered { get; set; }
        public decimal RightDelivered { get; set; }
        public decimal ManifestDelivered { get; set; }
    }

    private sealed class PostingTotals
    {
        public int Issues { get; set; }
        public int Receivables { get; set; }
        public int Effects { get; set; }
        public int Receipts { get; set; }
        public int BGrants { get; set; }
    }

    private sealed record PostingEffect(string Key, string Kind,
        decimal Quantity, decimal Amount, Guid? Customer, Guid? Sku, Guid? Location);

    private static Task<int> AddEffectAsync(SqlConnection db, SqlTransaction tx,
        string tenant, Guid sourceLine, PostingEffect effect, CancellationToken ct) =>
        db.ExecuteAsync(Sql("""
            INSERT dbo.LedgerEffect(Id,TenantId,BusinessSourceLineId,EffectKey,
                LedgerKind,CustomerId,SkuId,StockLocationId,QuantityDelta,AmountDelta)
            VALUES(@Id,@Tenant,@Line,@Key,@Kind,@Customer,@Sku,@Location,@Qty,@Amount);
            """, new { Id = Guid.NewGuid(), Tenant = tenant, Line = sourceLine,
                effect.Key, effect.Kind, effect.Customer, effect.Sku,
                effect.Location, Qty = effect.Quantity, effect.Amount }, tx, ct));
}
