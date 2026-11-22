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
/// APP-01C.2: one synthetic B sale+delivery+verified receipt+AR/COGS/stock/
/// entitlement transaction. No offline writes, real cash API, legal invoice,
/// tax/revenue accounting, A partial delivery or production authorization.
/// </summary>
public sealed partial class App01c2BPostingService(TenantConnections connections)
{
    private const decimal MaximumQuantity = 999999999999999.999m;
    private const decimal MaximumAmount = 999999999999999.9999m;
    private const string Kind = "B_SALE_DELIVERY";

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

    private sealed class ManifestRight
    {
        public Guid ManifestLineId { get; set; }
        public Guid LoadSourceLineId { get; set; }
        public Guid StockRightId { get; set; }
        public Guid SkuId { get; set; }
        public decimal LoadedBaseQuantity { get; set; }
        public decimal GrantedBaseQuantity { get; set; }
        public decimal ConsumedBaseQuantity { get; set; }
        public decimal ReleasedBaseQuantity { get; set; }
        public string RightStatus { get; set; } = "";
    }

    private sealed class Balance
    {
        public decimal OnHandBaseQuantity { get; set; }
        public decimal OnHandCostAmount { get; set; }
        public decimal SourceBaseQuantity { get; set; }
        public decimal SourceCostAmount { get; set; }
    }

    private sealed class VerifiedCash
    {
        public Guid TripId { get; set; }
        public Guid CustomerId { get; set; }
        public Guid ShipToSnapshotId { get; set; }
        public decimal VerifiedAmount { get; set; }
        public string PaymentMethod { get; set; } = "";
        public string VerificationStatus { get; set; } = "";
        public string? VerifiedBy { get; set; }
        public DateTime? VerifiedAtUtc { get; set; }
    }

    private sealed class CreditGrant
    {
        public Guid Id { get; set; }
        public decimal GrantedAmount { get; set; }
        public decimal ConsumedAmount { get; set; }
        public decimal ReleasedAmount { get; set; }
        public decimal RecordedConsumption { get; set; }
        public string Status { get; set; } = "";
        public bool Approved { get; set; }
    }

    private sealed record GrantUse(Guid GrantId, decimal Amount);

    /// <summary>
    /// A valid command identity has exactly one persisted outcome (including
    /// business rejections). SQL faults roll back the command row as well.
    /// Cross-kind idempotency shares APP-01A's persisted key and app-lock name.
    /// </summary>
    public async Task<BusinessCommandOutcome> PostBSaleDeliveryReceiptAsync(
        ClaimsPrincipal principal, PostBSaleDeliveryReceiptCommand request, CancellationToken ct)
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

        var fields = new List<(string Key, string Value)>
        {
            ("TripId", request.TripId.ToString("N")),
            ("CustomerId", request.CustomerId.ToString("N")),
            ("ShipToSnapshotId", request.ShipToSnapshotId.ToString("N")),
            ("TripManifestLineId", request.TripManifestLineId.ToString("N")),
            ("SkuId", request.SkuId.ToString("N")),
            ("BaseQuantity", CommandPayloadHash.Decimal(request.BaseQuantity)),
            ("UnitPrice", CommandPayloadHash.Decimal(request.UnitPrice)),
            ("ReceivedCashAmount", CommandPayloadHash.Decimal(request.ReceivedCashAmount)),
            ("VerificationEvidenceRef", request.VerificationEvidenceRef ?? "")
        };
        if (request.PendingBankEvidenceRef is not null)
            fields.Add(("PendingBankEvidenceRef", request.PendingBankEvidenceRef));
        if (request.CustomerCreditReservationId is Guid reservationId)
            fields.Add(("CustomerCreditReservationId", reservationId.ToString("N")));
        byte[] hash = CommandPayloadHash.Compute(Kind, tenant, actor, request.SourceNumber, fields.ToArray());

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

            // The Trip lock is shared by all B20 commands regardless of command
            // key; APP-01C.3 must adopt this same lock for A/B vehicle races.
            // Other APP-01A/B commands also lock Trip rows via UPDLOCK/HOLDLOCK.
            BusinessCommandOutcome result;
            if (request.TripId == Guid.Empty || request.CustomerId == Guid.Empty
                || request.ShipToSnapshotId == Guid.Empty || request.TripManifestLineId == Guid.Empty
                || request.SkuId == Guid.Empty)
                result = BusinessCommandOutcome.Rejected("INVALID_REFERENCE");
            else if (request.PendingBankEvidenceRef is not null
                && (!ValidKey(request.PendingBankEvidenceRef, 128)
                    || request.ReceivedCashAmount != 0 || !string.IsNullOrEmpty(request.VerificationEvidenceRef)
                    || request.CustomerCreditReservationId is not null))
                result = BusinessCommandOutcome.Rejected("BANK_PENDING_INPUT_INVALID");
            else if (request.PendingBankEvidenceRef is null && !ValidKey(request.VerificationEvidenceRef, 128))
                result = BusinessCommandOutcome.Rejected("CASH_EVIDENCE_INVALID");
            else if (!ValidQuantity(request.BaseQuantity) || !ValidMoney(request.UnitPrice)
                || (request.PendingBankEvidenceRef is null && !ValidMoney(request.ReceivedCashAmount)))
                result = BusinessCommandOutcome.Rejected("AMOUNT_OR_QUANTITY_INVALID");
            else
            {
                decimal payable;
                try { payable = checked(request.BaseQuantity * request.UnitPrice); }
                catch (OverflowException) { payable = -1m; }
                if (!ValidMoney(payable))
                    result = BusinessCommandOutcome.Rejected("PAYABLE_OR_RECEIPT_INVALID");
                else
                {
                    await LockAsync(db, tx, "APP01C:TRIP:" + tenant + ":" +
                        request.TripId.ToString("N"), ct);
                    result = await PostAsync(db, tx, tenant, actor, commandId,
                        request, payable, ct);
                }
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
        Guid commandId, PostBSaleDeliveryReceiptCommand request,
        decimal payable, CancellationToken ct)
    {
        // Source identity is globally unique by tenant/kind/number; check it
        // before mutable quota/credit preflight so a reused source never gets
        // misreported as an ordinary stock or grant insufficiency.
        bool sourceUsed = await db.ExecuteScalarAsync<bool>(Sql("""
            SELECT CAST(CASE WHEN EXISTS (
                SELECT 1 FROM dbo.BusinessSource WITH (UPDLOCK,HOLDLOCK)
                WHERE TenantId=@Tenant AND SourceKind=N'B_SALE_DELIVERY'
                    AND SourceNumber=@Number
            ) THEN 1 ELSE 0 END AS bit)
            """, new { Tenant = tenant, Number = request.SourceNumber }, tx, ct));
        if (sourceUsed)
            return BusinessCommandOutcome.Rejected("SOURCE_ALREADY_USED");

        var p = new
        {
            Tenant = tenant, Trip = request.TripId, Customer = request.CustomerId,
            ShipTo = request.ShipToSnapshotId, ManifestLine = request.TripManifestLineId,
            request.SkuId, Evidence = request.VerificationEvidenceRef
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
                JOIN dbo.Sku k WITH (HOLDLOCK) ON k.TenantId=c.TenantId
                    AND k.Id=@SkuId
                WHERE c.TenantId=@Tenant AND c.Id=@Customer AND sh.Id=@ShipTo
                    AND c.IsActive=1 AND k.IsActive=1
            ) THEN 1 ELSE 0 END AS bit)
            """, p, tx, ct));
        if (!customerSkuEligible)
            return BusinessCommandOutcome.Rejected("CUSTOMER_SHIPTO_OR_SKU_NOT_ELIGIBLE");

        var right = await db.QuerySingleOrDefaultAsync<ManifestRight>(Sql("""
            SELECT l.Id AS ManifestLineId,l.LoadSourceLineId,l.StockRightId,
                l.SkuId,l.LoadedBaseQuantity,
                r.GrantedBaseQuantity,r.ConsumedBaseQuantity,r.ReleasedBaseQuantity,
                r.Status AS RightStatus
            FROM dbo.TripManifestLine l WITH (UPDLOCK,HOLDLOCK)
            JOIN dbo.TripManifest m WITH (HOLDLOCK)
                ON m.TenantId=l.TenantId AND m.Id=l.TripManifestId
                AND m.TripId=l.TripId AND m.Status=N'LOADED' AND m.IsC3=0
            JOIN dbo.StockRight r WITH (UPDLOCK,HOLDLOCK)
                ON r.TenantId=l.TenantId AND r.Id=l.StockRightId
                AND r.TripId=l.TripId AND r.SkuId=l.SkuId AND r.RightKind='B'
            JOIN dbo.BQuota b WITH (HOLDLOCK)
                ON b.TenantId=r.TenantId AND b.StockRightId=r.Id
            JOIN dbo.BusinessSourceLine al WITH (HOLDLOCK)
                ON al.TenantId=r.TenantId AND al.Id=r.BusinessSourceLineId
                AND al.SkuId=l.SkuId AND al.BaseQuantity=r.GrantedBaseQuantity
            JOIN dbo.BusinessSource a WITH (HOLDLOCK)
                ON a.TenantId=al.TenantId AND a.Id=al.BusinessSourceId
                AND a.SourceKind=N'STOCK_ALLOC' AND a.TripId=l.TripId
            JOIN dbo.PhysicalStockMovement mv WITH (HOLDLOCK)
                ON mv.TenantId=l.TenantId AND mv.TripManifestLineId=l.Id
                AND mv.LoadSourceLineId=l.LoadSourceLineId
                AND mv.BaseQuantity=l.LoadedBaseQuantity
                AND mv.CostAmount=l.LoadedCostAmount
            WHERE l.TenantId=@Tenant AND l.Id=@ManifestLine AND l.TripId=@Trip
                AND l.SkuId=@SkuId AND l.RightKind='B'
                AND l.LoadSourceLineId IS NOT NULL AND l.LoadedBaseQuantity>0
            """, p, tx, ct));
        if (right is null)
            return BusinessCommandOutcome.Rejected("B_LOADED_RIGHT_NOT_FOUND");

        // Reconcile the mutable right counter with immutable source-backed
        // deliveries. Serializable predicate locks prevent a parallel oversale.
        decimal postedQty = await db.ExecuteScalarAsync<decimal>(Sql("""
            SELECT COALESCE(SUM(BaseQuantity),0)
            FROM dbo.SaleDelivery WITH (UPDLOCK,HOLDLOCK)
            WHERE TenantId=@Tenant AND TripManifestLineId=@ManifestLine
            """, p, tx, ct));
        if (postedQty != right.ConsumedBaseQuantity)
            return BusinessCommandOutcome.Rejected("B_RIGHT_SOURCE_DRIFT");
        if (right.RightStatus != "HELD"
            || right.LoadedBaseQuantity - postedQty < request.BaseQuantity
            || right.GrantedBaseQuantity - right.ConsumedBaseQuantity
                - right.ReleasedBaseQuantity < request.BaseQuantity)
            return BusinessCommandOutcome.Rejected("B_QUOTA_INSUFFICIENT");

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
        // A positive balance alone is not proof of stock. Reconcile the
        // mutable vehicle counter with immutable T0, loads and posted issues.
        // Both quantity and four-decimal custody cost must match exactly.
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

        var (payment, rejection) = await ResolvePaymentAsync(db, tx, tenant, actor, request, payable, ct);
        if (payment is null)
            return BusinessCommandOutcome.Rejected(rejection!);
        decimal creditDue = payable - payment.TotalApplied;
        var grants = (await db.QueryAsync<CreditGrant>(Sql("""
            SELECT g.Id,g.GrantedAmount,g.ConsumedAmount,g.ReleasedAmount,g.Status,
                (SELECT COALESCE(SUM(c.ConsumedAmount),0)
                 FROM dbo.BGrantConsumption c WITH (HOLDLOCK)
                 WHERE c.TenantId=g.TenantId AND c.BCreditGrantId=g.Id)
                 AS RecordedConsumption,
                CAST(CASE WHEN a.Id IS NOT NULL
                        AND a.Decision=N'APPROVED' AND a.EvidenceRef IS NOT NULL
                        AND LEN(LTRIM(RTRIM(a.EvidenceRef)))>0
                        AND a.ProposedBy<>a.CheckedBy
                    THEN 1 ELSE 0 END AS bit) AS Approved
            FROM dbo.BCreditGrant g WITH (UPDLOCK,HOLDLOCK)
            LEFT JOIN dbo.CreditPolicyApproval a WITH (HOLDLOCK)
                ON a.TenantId=g.TenantId AND a.Id=g.ApprovalId
            WHERE g.TenantId=@Tenant AND g.TripId=@Trip
                AND g.CustomerId=@Customer AND g.ShipToSnapshotId=@ShipTo
                AND g.PoolCode=N'B' AND g.CurrencyUnit=N'SYNTHETIC_UNITLESS'
            ORDER BY g.Id
            """, p, tx, ct))).AsList();
        if (grants.Any(g => g.ConsumedAmount != g.RecordedConsumption))
            return BusinessCommandOutcome.Rejected("B_CREDIT_SOURCE_DRIFT");

        // Deterministically spend only the necessary residual AR, across
        // independent source grants; no held+AR double count.
        var uses = new List<GrantUse>();
        decimal uncovered = creditDue;
        foreach (var g in grants)
        {
            if (uncovered == 0) break;
            if (g.Status != "HELD" || !g.Approved) continue;
            decimal remaining = g.GrantedAmount - g.ConsumedAmount - g.ReleasedAmount;
            if (remaining <= 0) continue;
            decimal spend = Math.Min(remaining, uncovered);
            uses.Add(new GrantUse(g.Id, spend));
            uncovered -= spend;
        }
        if (uncovered > 0)
            return BusinessCommandOutcome.Rejected("B_CREDIT_INSUFFICIENT");

        // All writes below are covered by the SAME explicit Serializable tx.
        Guid sourceId = Guid.NewGuid(), sourceLine = Guid.NewGuid();
        Guid deliveryId = Guid.NewGuid(), itemId = Guid.NewGuid(), receiptId = Guid.NewGuid();
        await db.ExecuteAsync(Sql("""
            INSERT dbo.BusinessSource(Id,TenantId,BusinessCommandId,SourceKind,
                SourceNumber,ActorId,CustomerId,TripId,OccurredAtUtc)
            VALUES(@Id,@Tenant,@Command,N'B_SALE_DELIVERY',@Number,@Actor,
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
            WHERE TenantId=@Tenant AND Id=@Right AND RightKind='B' AND Status=N'HELD'
                AND GrantedBaseQuantity-ConsumedBaseQuantity-ReleasedBaseQuantity>=@Qty;
            """, new { Tenant = tenant, Right = right.StockRightId,
                Qty = request.BaseQuantity }, tx, ct)) != 1)
            throw new InvalidOperationException("B right changed under lock.");

        if (await db.ExecuteAsync(Sql("""
            UPDATE dbo.PhysicalStockBalance SET
                OnHandBaseQuantity=OnHandBaseQuantity-@Qty,
                OnHandCostAmount=OnHandCostAmount-@Cost
            WHERE TenantId=@Tenant AND StockLocationId=@Vehicle AND SkuId=@Sku
                AND OnHandBaseQuantity>=@Qty AND OnHandCostAmount>=@Cost;
            """, new { Tenant = tenant, Vehicle = trip.VehicleLocationId,
                Sku = request.SkuId, Qty = request.BaseQuantity, Cost = cost }, tx, ct)) != 1)
            throw new InvalidOperationException("Vehicle custody changed under lock.");

        await db.ExecuteAsync(Sql("""
            INSERT dbo.SaleDelivery(Id,TenantId,TripId,CustomerId,ShipToSnapshotId,
                TripManifestLineId,LoadSourceLineId,StockRightId,RightKind,SkuId,
                BusinessSourceId,BusinessSourceKind,BusinessSourceLineId,
                BaseQuantity,PayableAmount,CostAmount,CostBasisVersion,BusinessDate,InvoiceEligible)
            VALUES(@Delivery,@Tenant,@Trip,@Customer,@ShipTo,@ManifestLine,
                @LoadLine,@Right,'B',@Sku,@Source,N'B_SALE_DELIVERY',@SourceLine,
                @Qty,@Payable,@Cost,N'SYNTHETIC_MA_V1',@Date,0);
            INSERT dbo.DeliveryStockIssue(Id,TenantId,SaleDeliveryId,
                BusinessSourceLineId,TripManifestLineId,SkuId,BaseQuantity,CostAmount)
            VALUES(@Issue,@Tenant,@Delivery,@SourceLine,@ManifestLine,@Sku,@Qty,@Cost);
            INSERT dbo.ReceivableOpenItem(Id,TenantId,SaleDeliveryId,CustomerId,
                BusinessSourceLineId,RightKind,OriginalAmount,AppliedAmount)
            VALUES(@Item,@Tenant,@Delivery,@Customer,@SourceLine,'B',@Payable,0);
            """, new { Delivery = deliveryId, Issue = Guid.NewGuid(), Item = itemId,
                Tenant = tenant, Trip = request.TripId, Customer = request.CustomerId,
                ShipTo = request.ShipToSnapshotId, ManifestLine = right.ManifestLineId,
                LoadLine = right.LoadSourceLineId, Right = right.StockRightId,
                Sku = request.SkuId, Source = sourceId, SourceLine = sourceLine,
                Qty = request.BaseQuantity, Payable = payable, Cost = cost,
                Date = trip.BusinessDate }, tx, ct));

        if (payment.CashReceived > 0)
        {
            await db.ExecuteAsync(Sql("""
                INSERT dbo.CashReceipt(Id,TenantId,SaleDeliveryId,TripId,CustomerId,
                    BusinessSourceLineId,VerificationEvidenceRef,ReceivedAmount,AppliedAmount,VerifiedAtUtc)
                VALUES(@Receipt,@Tenant,@Delivery,@Trip,@Customer,@SourceLine,
                    @Evidence,@CashReceived,0,@VerifiedAt);
                INSERT dbo.ReceiptAllocation(Id,TenantId,CashReceiptId,
                    ReceivableOpenItemId,CustomerId,AppliedAmount)
                VALUES(NEWID(),@Tenant,@Receipt,@Item,@Customer,@CashApplied);
                """, new { Receipt = receiptId, Tenant = tenant, Delivery = deliveryId, Trip = request.TripId,
                    Customer = request.CustomerId, SourceLine = sourceLine, Item = itemId,
                    Evidence = request.VerificationEvidenceRef, payment.CashReceived, payment.CashApplied,
                    VerifiedAt = payment.CashVerifiedAt }, tx, ct));
            if (await db.ExecuteAsync(Sql("""
                UPDATE dbo.CashReceipt SET AppliedAmount=AppliedAmount+@Cash
                WHERE TenantId=@Tenant AND Id=@Receipt AND ReceivedAmount-AppliedAmount>=@Cash;
                """, new { Tenant = tenant, Receipt = receiptId, Cash = payment.CashApplied }, tx, ct)) != 1)
                throw new InvalidOperationException("Receipt allocation counter changed.");
        }
        if (payment.TotalApplied > 0 && await db.ExecuteAsync(Sql("""
                UPDATE dbo.ReceivableOpenItem SET AppliedAmount=AppliedAmount+@Applied
                WHERE TenantId=@Tenant AND Id=@Item AND OutstandingAmount>=@Applied;
                """, new { Tenant = tenant, Item = itemId, Applied = payment.TotalApplied }, tx, ct)) != 1)
            throw new InvalidOperationException("AR allocation counter changed.");

        if (payment.IsExtended)
            await db.ExecuteAsync(Sql("""
                INSERT dbo.BLocalSalePayment(TenantId,SaleDeliveryId,PendingBankEvidenceRef,
                    CreditReservationId,CreditApplied,CreditIssued)
                VALUES(@Tenant,@Delivery,@Bank,@Reservation,@CreditApplied,@CreditIssued);
                """, new { Tenant = tenant, Delivery = deliveryId, Bank = request.PendingBankEvidenceRef,
                    Reservation = request.CustomerCreditReservationId, payment.CreditApplied, payment.CreditIssued }, tx, ct));

        foreach (var use in uses)
        {
            if (await db.ExecuteAsync(Sql("""
                UPDATE dbo.BCreditGrant SET ConsumedAmount=ConsumedAmount+@Amount,
                    Status=CASE WHEN GrantedAmount=ConsumedAmount+ReleasedAmount+@Amount
                        THEN N'CONSUMED' ELSE N'HELD' END
                WHERE TenantId=@Tenant AND Id=@Grant AND Status=N'HELD'
                    AND GrantedAmount-ConsumedAmount-ReleasedAmount>=@Amount;
                """, new { Tenant = tenant, Grant = use.GrantId, use.Amount }, tx, ct)) != 1)
                throw new InvalidOperationException("Credit grant changed under lock.");
            await db.ExecuteAsync(Sql("""
                INSERT dbo.BGrantConsumption(Id,TenantId,SaleDeliveryId,
                    BCreditGrantId,TripId,CustomerId,ShipToSnapshotId,ConsumedAmount)
                VALUES(@Id,@Tenant,@Delivery,@Grant,@Trip,@Customer,@ShipTo,@Amount);
                """, new { Id = Guid.NewGuid(), Tenant = tenant, Delivery = deliveryId,
                    Grant = use.GrantId, Trip = request.TripId, Customer = request.CustomerId,
                    ShipTo = request.ShipToSnapshotId, use.Amount }, tx, ct));
        }

        var effects = new List<PostingEffect>
        {
            new("PHYSICAL-OUT", "PHYSICAL_STOCK", -request.BaseQuantity, -cost,
                null, request.SkuId, trip.VehicleLocationId),
            new("COGS", "COGS", 0m, cost, null, request.SkuId, null),
            new("AR-GROSS", "AR", 0m, payable, request.CustomerId, null, null),
            new("RIGHT-B-USED", "RIGHT_B_CONSUMED", -request.BaseQuantity, 0m, null, request.SkuId, null)
        };
        if (payment.CashReceived > 0)
        {
            effects.Add(new("AR-CASH-ALLOC", "AR", 0m, -payment.CashApplied, request.CustomerId, null, null));
            effects.Add(new("CASH-CUSTODY", "CUSTODY", 0m, payment.CashReceived, request.CustomerId, null, null));
        }
        if (payment.CreditApplied > 0)
        {
            effects.Add(new("AR-CREDIT-ALLOC", "AR", 0m, -payment.CreditApplied, request.CustomerId, null, null));
            effects.Add(new("CUSTOMER-CREDIT-USED", "CUSTOMER_CREDIT", 0m, -payment.CreditApplied, request.CustomerId, null, null));
        }
        if (payment.CreditIssued > 0)
            effects.Add(new("CUSTOMER-CREDIT-ISSUED", "CUSTOMER_CREDIT", 0m, payment.CreditIssued, request.CustomerId, null, null));
        if (creditDue > 0)
            effects.Add(new("CREDIT-B-USED", "RIGHT_CREDIT_B_CONSUMED", 0m, -creditDue, request.CustomerId, null, null));
        foreach (var effect in effects)
            await AddEffectAsync(db, tx, tenant, sourceLine, effect, ct);

        // Compare physical/AR/cash/grant source aggregates with their mutable
        // counters *before* committing. A later APP-01C.4 oracle is independent.
        var posted = await db.QuerySingleAsync<PostingTotals>(Sql("""
            SELECT
                (SELECT COUNT(*) FROM dbo.DeliveryStockIssue
                    WHERE TenantId=@Tenant AND SaleDeliveryId=@Delivery) AS Issues,
                (SELECT COALESCE(SUM(AppliedAmount),0) FROM dbo.ReceiptAllocation
                    WHERE TenantId=@Tenant AND CashReceiptId=@Receipt
                        AND ReceivableOpenItemId=@Item) AS Allocated,
                (SELECT COALESCE(SUM(AppliedAmount),0) FROM dbo.CashReceipt
                    WHERE TenantId=@Tenant AND Id=@Receipt) AS ReceiptApplied,
                (SELECT AppliedAmount FROM dbo.ReceivableOpenItem
                    WHERE TenantId=@Tenant AND Id=@Item) AS ArApplied,
                (SELECT COALESCE(SUM(ConsumedAmount),0) FROM dbo.BGrantConsumption
                    WHERE TenantId=@Tenant AND SaleDeliveryId=@Delivery) AS CreditUsed,
                (SELECT COUNT(*) FROM dbo.LedgerEffect
                    WHERE TenantId=@Tenant AND BusinessSourceLineId=@SourceLine) AS EffectCount
            """, new { Tenant = tenant, Delivery = deliveryId, Receipt = receiptId,
                Item = itemId, SourceLine = sourceLine }, tx, ct));
        if (posted.Issues != 1 || posted.Allocated != payment.CashApplied
            || posted.ReceiptApplied != payment.CashApplied
            || posted.ArApplied != payment.TotalApplied
            || posted.CreditUsed != creditDue || posted.EffectCount != effects.Count)
            throw new InvalidOperationException("B source-ledger posting is incomplete.");

        return BusinessCommandOutcome.Applied("B_SALE_DELIVERY_POSTED", sourceId);
    }

    private sealed record PostingEffect(string Key, string Kind,
        decimal Quantity, decimal Amount, Guid? Customer, Guid? Sku, Guid? Location);

    private sealed class PostingTotals
    {
        public int Issues { get; set; }
        public decimal Allocated { get; set; }
        public decimal ReceiptApplied { get; set; }
        public decimal ArApplied { get; set; }
        public decimal CreditUsed { get; set; }
        public int EffectCount { get; set; }
    }

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
