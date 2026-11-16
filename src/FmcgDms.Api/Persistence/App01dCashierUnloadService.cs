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
/// APP-01D synthetic LocalDB-only source-bound T5/T6 commands. Canonical
/// cross-kind idempotency and the shared APP01C:TRIP lock serialize T3..T6.
/// No production cash handling, legal document, or accounting integration.
/// </summary>
public sealed class App01dCashierUnloadService(TenantConnections connections)
{
    private const decimal MaximumAmount = 999999999999999.9999m;
    private const decimal MaximumQuantity = 999999999999999.999m;

    private static CommandDefinition Sql(string query, object? values,
        IDbTransaction tx, CancellationToken ct) => new(query, values, tx, cancellationToken: ct);

    private static bool ValidKey(string? value, int max) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= max
        && value == value.Trim() && !value.Any(char.IsControl);

    private static bool ValidAmount(decimal amount) => amount > 0 && amount <= MaximumAmount;

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
        public string Status { get; set; } = "";
        public Guid VehicleLocationId { get; set; }
        public bool VehicleActive { get; set; }
        public string VehicleKind { get; set; } = "";
    }

    private sealed class OfferScope
    {
        public Guid Id { get; set; }
        public Guid TripId { get; set; }
        public decimal OfferedAmount { get; set; }
        public string OfferedBy { get; set; } = "";
        public string SourceKind { get; set; } = "";
        public int SourceLines { get; set; }
    }

    private sealed class CountScope
    {
        public Guid TripId { get; set; }
        public decimal CountedAmount { get; set; }
        public string CountedBy { get; set; } = "";
        public string VerificationStatus { get; set; } = "";
    }

    private sealed class ManifestScope
    {
        public Guid WarehouseLocationId { get; set; }
        public Guid VehicleLocationId { get; set; }
        public string Status { get; set; } = "";
        public string WarehouseKind { get; set; } = "";
        public string VehicleKind { get; set; } = "";
        public bool WarehouseActive { get; set; }
        public bool VehicleActive { get; set; }
    }

    private sealed class LoadedRemainder
    {
        public Guid ManifestLineId { get; set; }
        public Guid LoadSourceLineId { get; set; }
        public Guid StockRightId { get; set; }
        public Guid SkuId { get; set; }
        public int LineNumber { get; set; }
        public string RightKind { get; set; } = "";
        public string RightStatus { get; set; } = "";
        public decimal LoadedBaseQuantity { get; set; }
        public decimal GrantedBaseQuantity { get; set; }
        public decimal ConsumedBaseQuantity { get; set; }
        public decimal ReleasedBaseQuantity { get; set; }
        public decimal IssuedBaseQuantity { get; set; }
        public decimal IssueCostAmount { get; set; }
        public decimal Remaining => LoadedBaseQuantity - IssuedBaseQuantity;
    }

    private sealed class LocationBalance
    {
        public decimal OnHandBaseQuantity { get; set; }
        public decimal OnHandCostAmount { get; set; }
        public decimal SourceBaseQuantity { get; set; }
        public decimal SourceCostAmount { get; set; }
    }

    private sealed record ReturnLine(LoadedRemainder Loaded, decimal Cost);

    public Task<BusinessCommandOutcome> OfferCashHandoverAsync(
        ClaimsPrincipal principal, OfferCashHandoverCommand request, CancellationToken ct) =>
        ExecuteAsync(principal, request, "CASH_HANDOVER_OFFER", "sales",
            request.TripId,
            [("TripId", request.TripId.ToString("N")),
             ("OfferedAmount", CommandPayloadHash.Decimal(request.OfferedAmount))],
            OfferAsync, ct);

    public Task<BusinessCommandOutcome> AcceptCashHandoverAsync(
        ClaimsPrincipal principal, AcceptCashHandoverCommand request, CancellationToken ct) =>
        ExecuteAsync(principal, request, "CASH_HANDOVER_ACCEPT", "cashier",
            request.TripId,
            [("TripId", request.TripId.ToString("N")),
             ("OfferId", request.OfferId.ToString("N")),
             ("CountEvidenceRef", request.CountEvidenceRef ?? "")],
            AcceptAsync, ct);

    public Task<BusinessCommandOutcome> UnloadTripRemainderAsync(
        ClaimsPrincipal principal, UnloadTripRemainderCommand request, CancellationToken ct) =>
        ExecuteAsync(principal, request, "TRIP_UNLOAD", "warehouse",
            request.TripId,
            [("TripId", request.TripId.ToString("N")),
             ("ManifestId", request.ManifestId.ToString("N"))],
            UnloadAsync, ct);

    private async Task<BusinessCommandOutcome> ExecuteAsync<T>(
        ClaimsPrincipal principal, T request, string kind, string requiredRole,
        Guid tripId, (string Key, string Value)[] fields,
        Func<SqlConnection, SqlTransaction, string, string, Guid, T,
            CancellationToken, Task<BusinessCommandOutcome>> handler, CancellationToken ct)
        where T : IApp01aCommand
    {
        if (!ValidKey(request.ProducerId, 128) || !ValidKey(request.CommandId, 128)
            || !ValidKey(request.SourceNumber, 80))
            return BusinessCommandOutcome.Rejected("INVALID_COMMAND_IDENTITY");
        if (!principal.HasClaim("synthetic_role", requiredRole))
            throw new UnauthorizedAccessException("Synthetic " + requiredRole + " role required.");
        string tenant = SyntheticAuth.GetTenantId(principal);
        string actor = principal.FindFirst("actor_id")?.Value
            ?? throw new UnauthorizedAccessException("Validated actor identity required.");
        if (!ValidKey(actor, 128)
            || !string.Equals(actor, principal.FindFirst("sub")?.Value, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Invalid actor identity.");

        byte[] hash = CommandPayloadHash.Compute(kind, tenant, actor, request.SourceNumber, fields);
        await using SqlConnection db = await connections.OpenSyntheticScratchAsync(principal, ct);
        await using SqlTransaction tx =
            (SqlTransaction)await db.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            string commandLock = "APP01A2:" + Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(tenant + "\0" + request.ProducerId + "\0" + request.CommandId)));
            await LockAsync(db, tx, commandLock, ct);
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
                Guid? existingSource = await db.QuerySingleOrDefaultAsync<Guid?>(Sql("""
                    SELECT Id FROM dbo.BusinessSource
                    WHERE TenantId=@Tenant AND BusinessCommandId=@Command
                    """, new { Tenant = tenant, Command = existing.Id }, tx, ct));
                await tx.CommitAsync(ct);
                return new BusinessCommandOutcome(existing.Status, existing.OutcomeCode,
                    existing.ReasonCode, true, existingSource);
            }

            Guid commandId = Guid.NewGuid();
            await db.ExecuteAsync(Sql("""
                INSERT dbo.BusinessCommand(Id,TenantId,ProducerId,CommandId,
                    PayloadHashVersion,PayloadHash,ActorId,Status)
                VALUES(@Id,@Tenant,@Producer,@Command,@Version,@Hash,@Actor,N'RECEIVED');
                """, new { Id = commandId, Tenant = tenant,
                    Producer = request.ProducerId, Command = request.CommandId,
                    Version = CommandPayloadHash.Version, Hash = hash, Actor = actor }, tx, ct));

            BusinessCommandOutcome outcome;
            if (tripId == Guid.Empty)
                outcome = BusinessCommandOutcome.Rejected("TRIP_REFERENCE_INVALID");
            else
            {
                // Exactly the same lock resource as APP-01C.2 and C.3 delivery
                // posting: a delivery cannot race cashier settlement/unload.
                await LockAsync(db, tx,
                    "APP01C:TRIP:" + tenant + ":" + tripId.ToString("N"), ct);
                outcome = await handler(db, tx, tenant, actor, commandId, request, ct);
            }

            if (outcome.Status is not ("APPLIED" or "REJECTED"))
                throw new InvalidOperationException("Cannot persist an incomplete command outcome.");
            if (await db.ExecuteAsync(Sql("""
                UPDATE dbo.BusinessCommand SET Status=@Status,
                    OutcomeCode=@Outcome,ReasonCode=@Reason,
                    CompletedAtUtc=SYSUTCDATETIME()
                WHERE TenantId=@Tenant AND Id=@Id AND Status=N'RECEIVED';
                """, new { Tenant = tenant, Id = commandId, outcome.Status,
                    Outcome = outcome.OutcomeCode, Reason = outcome.ReasonCode }, tx, ct)) != 1)
                throw new InvalidOperationException("Command completion was not unique.");
            await tx.CommitAsync(ct);
            return outcome;
        }
        catch
        {
            try { await tx.RollbackAsync(CancellationToken.None); }
            catch (InvalidOperationException) { /* SQL Server may already have rolled back. */ }
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
        if (result < 0) throw new TimeoutException("APP01D app-lock was not granted.");
    }

    private static async Task<bool> SourceUsed(SqlConnection db, SqlTransaction tx,
        string tenant, string kind, string sourceNumber, CancellationToken ct) =>
        await db.ExecuteScalarAsync<bool>(Sql("""
            SELECT CAST(CASE WHEN EXISTS (
                SELECT 1 FROM dbo.BusinessSource WITH (UPDLOCK,HOLDLOCK)
                WHERE TenantId=@Tenant AND SourceKind=@Kind AND SourceNumber=@Number
            ) THEN 1 ELSE 0 END AS bit);
            """, new { Tenant = tenant, Kind = kind, Number = sourceNumber }, tx, ct));

    private static Task<int> AddEffect(SqlConnection db, SqlTransaction tx,
        string tenant, Guid sourceLine, string key, string kind,
        decimal quantity, decimal amount, Guid? sku, Guid? location, CancellationToken ct) =>
        db.ExecuteAsync(Sql("""
            INSERT dbo.LedgerEffect(Id,TenantId,BusinessSourceLineId,EffectKey,
                LedgerKind,SkuId,StockLocationId,QuantityDelta,AmountDelta)
            VALUES(@Id,@Tenant,@Line,@Key,@Kind,@Sku,@Location,@Qty,@Amount);
            """, new { Id = Guid.NewGuid(), Tenant = tenant, Line = sourceLine,
                Key = key, Kind = kind, Sku = sku, Location = location,
                Qty = quantity, Amount = amount }, tx, ct));

    private static Task<TripScope?> FindTrip(SqlConnection db, SqlTransaction tx,
        string tenant, Guid tripId, CancellationToken ct) =>
        db.QuerySingleOrDefaultAsync<TripScope>(Sql("""
            SELECT t.Status,t.VehicleLocationId,
                v.Kind AS VehicleKind,v.IsActive AS VehicleActive
            FROM dbo.Trip t WITH (UPDLOCK,HOLDLOCK)
            JOIN dbo.StockLocation v WITH (HOLDLOCK)
                ON v.TenantId=t.TenantId AND v.Id=t.VehicleLocationId
            WHERE t.TenantId=@Tenant AND t.Id=@Trip;
            """, new { Tenant = tenant, Trip = tripId }, tx, ct));

    private static async Task<decimal> Custody(SqlConnection db, SqlTransaction tx,
        string tenant, Guid trip, CancellationToken ct) =>
        await db.ExecuteScalarAsync<decimal>(Sql("""
            SELECT COALESCE(SUM(e.AmountDelta),0)
            FROM dbo.LedgerEffect e WITH (UPDLOCK,HOLDLOCK)
            JOIN dbo.BusinessSourceLine sl ON sl.TenantId=e.TenantId
                AND sl.Id=e.BusinessSourceLineId
            JOIN dbo.BusinessSource src ON src.TenantId=sl.TenantId
                AND src.Id=sl.BusinessSourceId
            WHERE src.TenantId=@Tenant AND src.TripId=@Trip
                AND e.LedgerKind=N'CUSTODY';
            """, new { Tenant = tenant, Trip = trip }, tx, ct));

    private static async Task<bool> ReconcileCustody(
        SqlConnection db, SqlTransaction tx, string tenant,
        Guid trip, decimal custody, CancellationToken ct)
    {
        var (receipts, accepted, fund) = await db.QuerySingleAsync<
            (decimal Receipts, decimal Accepted, decimal Fund)>(Sql("""
            SELECT
                (SELECT COALESCE(SUM(ReceivedAmount),0) FROM dbo.CashReceipt WITH (HOLDLOCK)
                    WHERE TenantId=@Tenant AND TripId=@Trip),
                (SELECT COALESCE(SUM(AcceptedAmount),0) FROM dbo.CashHandoverAcceptance WITH (HOLDLOCK)
                    WHERE TenantId=@Tenant AND TripId=@Trip),
                (SELECT COALESCE(SUM(e.AmountDelta),0) FROM dbo.LedgerEffect e WITH (HOLDLOCK)
                    JOIN dbo.BusinessSourceLine sl ON sl.TenantId=e.TenantId
                        AND sl.Id=e.BusinessSourceLineId
                    JOIN dbo.BusinessSource src ON src.TenantId=sl.TenantId
                        AND src.Id=sl.BusinessSourceId
                    WHERE src.TenantId=@Tenant AND src.TripId=@Trip
                        AND e.LedgerKind=N'FUND');
            """, new { Tenant = tenant, Trip = trip }, tx, ct));
        return custody >= 0 && receipts - accepted == custody && accepted == fund;
    }

    private static async Task<BusinessCommandOutcome> OfferAsync(
        SqlConnection db, SqlTransaction tx, string tenant, string actor,
        Guid commandId, OfferCashHandoverCommand req, CancellationToken ct)
    {
        if (!ValidAmount(req.OfferedAmount))
            return BusinessCommandOutcome.Rejected("OFFER_AMOUNT_INVALID");
        if (await SourceUsed(db, tx, tenant, "CASH_HANDOVER_OFFER", req.SourceNumber, ct))
            return BusinessCommandOutcome.Rejected("SOURCE_ALREADY_USED");
        TripScope? trip = await FindTrip(db, tx, tenant, req.TripId, ct);
        if (trip is null || trip.Status is not ("PREPARED" or "IN_PROGRESS")
            || trip.VehicleKind != "VEHICLE" || !trip.VehicleActive)
            return BusinessCommandOutcome.Rejected("TRIP_NOT_ELIGIBLE");
        decimal custody = await Custody(db, tx, tenant, req.TripId, ct);
        if (!await ReconcileCustody(db, tx, tenant, req.TripId, custody, ct))
            return BusinessCommandOutcome.Rejected("CUSTODY_SOURCE_DRIFT");
        if (custody < req.OfferedAmount)
            return BusinessCommandOutcome.Rejected("CUSTODY_INSUFFICIENT");

        Guid source = Guid.NewGuid(), line = Guid.NewGuid();
        await db.ExecuteAsync(Sql("""
            INSERT dbo.BusinessSource(Id,TenantId,BusinessCommandId,SourceKind,
                SourceNumber,ActorId,TripId,OccurredAtUtc)
            VALUES(@Source,@Tenant,@Command,N'CASH_HANDOVER_OFFER',
                @Number,@Actor,@Trip,SYSUTCDATETIME());
            INSERT dbo.BusinessSourceLine(Id,TenantId,BusinessSourceId,LineNumber,Amount)
            VALUES(@Line,@Tenant,@Source,1,@Amount);
            INSERT dbo.CashHandoverOffer(Id,TenantId,TripId,SourceLineId,OfferedAmount,OfferedBy)
            VALUES(@Source,@Tenant,@Trip,@Line,@Amount,@Actor);
            """, new { Source = source, Line = line, Tenant = tenant,
                Command = commandId, Number = req.SourceNumber,
                Actor = actor, Trip = req.TripId, Amount = req.OfferedAmount }, tx, ct));
        if (await db.ExecuteScalarAsync<int>(Sql("""
            SELECT COUNT(*) FROM dbo.LedgerEffect
            WHERE TenantId=@Tenant AND BusinessSourceLineId=@Line
            """, new { Tenant = tenant, Line = line }, tx, ct)) != 0)
            throw new InvalidOperationException("Offer unexpectedly posted a ledger effect.");
        return BusinessCommandOutcome.Applied("CASH_HANDOVER_OFFERED", source);
    }

    private static async Task<BusinessCommandOutcome> AcceptAsync(
        SqlConnection db, SqlTransaction tx, string tenant, string actor,
        Guid commandId, AcceptCashHandoverCommand req, CancellationToken ct)
    {
        if (req.OfferId == Guid.Empty || !ValidKey(req.CountEvidenceRef, 128))
            return BusinessCommandOutcome.Rejected("ACCEPT_REFERENCE_INVALID");
        if (await SourceUsed(db, tx, tenant, "CASH_HANDOVER_ACCEPT", req.SourceNumber, ct))
            return BusinessCommandOutcome.Rejected("SOURCE_ALREADY_USED");
        TripScope? trip = await FindTrip(db, tx, tenant, req.TripId, ct);
        if (trip is null || trip.Status is not ("PREPARED" or "IN_PROGRESS")
            || trip.VehicleKind != "VEHICLE" || !trip.VehicleActive)
            return BusinessCommandOutcome.Rejected("TRIP_NOT_ELIGIBLE");

        var offer = await db.QuerySingleOrDefaultAsync<OfferScope>(Sql("""
            SELECT o.Id,o.TripId,o.OfferedAmount,o.OfferedBy,o.SourceKind,
                (SELECT COUNT(*) FROM dbo.BusinessSourceLine sl WITH (HOLDLOCK)
                    WHERE sl.TenantId=o.TenantId AND sl.Id=o.SourceLineId
                        AND sl.BusinessSourceId=o.Id AND sl.Amount=o.OfferedAmount
                        AND sl.SkuId IS NULL AND sl.BaseQuantity IS NULL) SourceLines
            FROM dbo.CashHandoverOffer o WITH (UPDLOCK,HOLDLOCK)
            WHERE o.TenantId=@Tenant AND o.Id=@OfferId AND o.TripId=@Trip;
            """, new { Tenant = tenant, req.OfferId, Trip = req.TripId }, tx, ct));
        if (offer is null || offer.SourceKind != "CASH_HANDOVER_OFFER"
            || offer.SourceLines != 1)
            return BusinessCommandOutcome.Rejected("OFFER_NOT_FOUND_OR_INVALID");
        if (offer.OfferedBy == actor)
            return BusinessCommandOutcome.Rejected("CASHIER_MUST_BE_INDEPENDENT");
        if (await db.ExecuteScalarAsync<int>(Sql("""
            SELECT COUNT(*) FROM dbo.CashHandoverAcceptance WITH (UPDLOCK,HOLDLOCK)
            WHERE TenantId=@Tenant AND OfferId=@Offer;
            """, new { Tenant = tenant, Offer = req.OfferId }, tx, ct)) != 0)
            return BusinessCommandOutcome.Rejected("OFFER_ALREADY_ACCEPTED");

        var witness = await db.QuerySingleOrDefaultAsync<CountScope>(Sql("""
            SELECT TripId,CountedAmount,CountedBy,VerificationStatus
            FROM dbo.CashierCountEvidence WITH (UPDLOCK,HOLDLOCK)
            WHERE TenantId=@Tenant AND EvidenceRef=@Evidence;
            """, new { Tenant = tenant, Evidence = req.CountEvidenceRef }, tx, ct));
        if (witness is null || witness.TripId != req.TripId
            || witness.CountedBy != actor || witness.VerificationStatus != "VERIFIED"
            || witness.CountedAmount != offer.OfferedAmount)
            return BusinessCommandOutcome.Rejected("CASHIER_COUNT_NOT_VERIFIED");
        if (await db.ExecuteScalarAsync<int>(Sql("""
            SELECT COUNT(*) FROM dbo.CashHandoverAcceptance WITH (UPDLOCK,HOLDLOCK)
            WHERE TenantId=@Tenant AND EvidenceRef=@Evidence;
            """, new { Tenant = tenant, Evidence = req.CountEvidenceRef }, tx, ct)) != 0)
            return BusinessCommandOutcome.Rejected("CASHIER_COUNT_ALREADY_USED");

        decimal custody = await Custody(db, tx, tenant, req.TripId, ct);
        if (!await ReconcileCustody(db, tx, tenant, req.TripId, custody, ct))
            return BusinessCommandOutcome.Rejected("CUSTODY_SOURCE_DRIFT");
        if (custody < offer.OfferedAmount)
            return BusinessCommandOutcome.Rejected("CUSTODY_INSUFFICIENT");

        Guid source = Guid.NewGuid(), line = Guid.NewGuid();
        await db.ExecuteAsync(Sql("""
            INSERT dbo.BusinessSource(Id,TenantId,BusinessCommandId,SourceKind,
                SourceNumber,ActorId,TripId,OccurredAtUtc)
            VALUES(@Source,@Tenant,@Command,N'CASH_HANDOVER_ACCEPT',
                @Number,@Actor,@Trip,SYSUTCDATETIME());
            INSERT dbo.BusinessSourceLine(Id,TenantId,BusinessSourceId,LineNumber,Amount)
            VALUES(@Line,@Tenant,@Source,1,@Amount);
            INSERT dbo.CashHandoverAcceptance(Id,TenantId,TripId,OfferId,
                SourceLineId,EvidenceRef,AcceptedBy,AcceptedAmount)
            VALUES(@Source,@Tenant,@Trip,@Offer,@Line,@Evidence,@Actor,@Amount);
            """, new { Source = source, Line = line, Tenant = tenant, Command = commandId,
                Number = req.SourceNumber, Actor = actor, Trip = req.TripId,
                Offer = req.OfferId, Evidence = req.CountEvidenceRef,
                Amount = offer.OfferedAmount }, tx, ct));
        await AddEffect(db, tx, tenant, line, "CUSTODY-OUT", "CUSTODY",
            0m, -offer.OfferedAmount, null, null, ct);
        await AddEffect(db, tx, tenant, line, "FUND-IN", "FUND",
            0m, offer.OfferedAmount, null, null, ct);
        if (await db.ExecuteScalarAsync<int>(Sql("""
            SELECT COUNT(*) FROM dbo.LedgerEffect
            WHERE TenantId=@Tenant AND BusinessSourceLineId=@Line;
            """, new { Tenant = tenant, Line = line }, tx, ct)) != 2
            || !await ReconcileCustody(db, tx, tenant, req.TripId,
                custody - offer.OfferedAmount, ct))
            throw new InvalidOperationException("Cashier source/ledger conservation failed.");
        return BusinessCommandOutcome.Applied("CASH_HANDOVER_ACCEPTED", source);
    }

    private static async Task<BusinessCommandOutcome> UnloadAsync(
        SqlConnection db, SqlTransaction tx, string tenant, string actor,
        Guid commandId, UnloadTripRemainderCommand req, CancellationToken ct)
    {
        if (req.ManifestId == Guid.Empty)
            return BusinessCommandOutcome.Rejected("MANIFEST_REFERENCE_INVALID");
        if (await SourceUsed(db, tx, tenant, "TRIP_UNLOAD", req.SourceNumber, ct))
            return BusinessCommandOutcome.Rejected("SOURCE_ALREADY_USED");
        TripScope? trip = await FindTrip(db, tx, tenant, req.TripId, ct);
        if (trip is null || trip.Status is not ("PREPARED" or "IN_PROGRESS")
            || trip.VehicleKind != "VEHICLE" || !trip.VehicleActive)
            return BusinessCommandOutcome.Rejected("TRIP_NOT_ELIGIBLE");
        if (await db.ExecuteScalarAsync<int>(Sql("""
            SELECT COUNT(*) FROM dbo.TripUnload WITH (UPDLOCK,HOLDLOCK)
            WHERE TenantId=@Tenant AND TripId=@Trip;
            """, new { Tenant = tenant, Trip = req.TripId }, tx, ct)) != 0)
            return BusinessCommandOutcome.Rejected("TRIP_ALREADY_UNLOADED");

        var manifest = await db.QuerySingleOrDefaultAsync<ManifestScope>(Sql("""
            SELECT m.WarehouseLocationId,m.VehicleLocationId,m.Status,
                wh.Kind AS WarehouseKind,wh.IsActive AS WarehouseActive,
                v.Kind AS VehicleKind,v.IsActive AS VehicleActive
            FROM dbo.TripManifest m WITH (UPDLOCK,HOLDLOCK)
            JOIN dbo.StockLocation wh WITH (HOLDLOCK)
                ON wh.TenantId=m.TenantId AND wh.Id=m.WarehouseLocationId
            JOIN dbo.StockLocation v WITH (HOLDLOCK)
                ON v.TenantId=m.TenantId AND v.Id=m.VehicleLocationId
            WHERE m.TenantId=@Tenant AND m.Id=@Manifest
                AND m.TripId=@Trip;
            """, new { Tenant = tenant, Manifest = req.ManifestId, Trip = req.TripId }, tx, ct));
        if (manifest is null || manifest.Status != "LOADED"
            || manifest.VehicleLocationId != trip.VehicleLocationId
            || manifest.WarehouseKind != "WAREHOUSE" || !manifest.WarehouseActive
            || manifest.VehicleKind != "VEHICLE" || !manifest.VehicleActive)
            return BusinessCommandOutcome.Rejected("LOADED_MANIFEST_NOT_ELIGIBLE");

        decimal custody = await Custody(db, tx, tenant, req.TripId, ct);
        if (!await ReconcileCustody(db, tx, tenant, req.TripId, custody, ct))
            return BusinessCommandOutcome.Rejected("CUSTODY_SOURCE_DRIFT");
        if (custody != 0)
            return BusinessCommandOutcome.Rejected("CUSTODY_NOT_SETTLED");

        var lines = (await db.QueryAsync<LoadedRemainder>(Sql("""
            SELECT l.Id AS ManifestLineId,l.LoadSourceLineId,l.StockRightId,
                l.SkuId,l.LineNumber,l.RightKind,l.LoadedBaseQuantity,
                r.GrantedBaseQuantity,r.ConsumedBaseQuantity,
                r.ReleasedBaseQuantity,r.Status AS RightStatus,
                COALESCE(delivery.BaseQuantity,0) AS IssuedBaseQuantity,
                COALESCE(delivery.CostAmount,0) AS IssueCostAmount
            FROM dbo.TripManifestLine l WITH (UPDLOCK,HOLDLOCK)
            JOIN dbo.StockRight r WITH (UPDLOCK,HOLDLOCK)
                ON r.TenantId=l.TenantId AND r.Id=l.StockRightId
                AND r.TripId=l.TripId AND r.SkuId=l.SkuId
                AND r.RightKind=l.RightKind
            JOIN dbo.PhysicalStockMovement mv WITH (HOLDLOCK)
                ON mv.TenantId=l.TenantId AND mv.TripManifestLineId=l.Id
                AND mv.LoadSourceLineId=l.LoadSourceLineId
                AND mv.BaseQuantity=l.LoadedBaseQuantity
                AND mv.CostAmount=l.LoadedCostAmount
            OUTER APPLY (
                SELECT SUM(d.BaseQuantity) BaseQuantity,SUM(i.CostAmount) CostAmount
                FROM dbo.SaleDelivery d WITH (UPDLOCK,HOLDLOCK)
                JOIN dbo.DeliveryStockIssue i WITH (HOLDLOCK)
                    ON i.TenantId=d.TenantId AND i.SaleDeliveryId=d.Id
                    AND i.BusinessSourceLineId=d.BusinessSourceLineId
                WHERE d.TenantId=l.TenantId AND d.TripManifestLineId=l.Id
                    AND d.StockRightId=l.StockRightId
            ) delivery
            WHERE l.TenantId=@Tenant AND l.TripId=@Trip
                AND l.TripManifestId=@Manifest AND l.LoadSourceLineId IS NOT NULL
                AND l.LoadedBaseQuantity>0
            ORDER BY l.LineNumber;
            """, new { Tenant = tenant, Manifest = req.ManifestId, Trip = req.TripId },
                tx, ct))).ToList();
        if (lines.Count == 0)
            return BusinessCommandOutcome.Rejected("MANIFEST_EMPTY");
        int totalLoadedLines = await db.ExecuteScalarAsync<int>(Sql("""
            SELECT COUNT(*) FROM dbo.TripManifestLine WITH (HOLDLOCK)
            WHERE TenantId=@Tenant AND TripManifestId=@Manifest;
            """, new { Tenant = tenant, Manifest = req.ManifestId }, tx, ct));
        if (totalLoadedLines != lines.Count)
            return BusinessCommandOutcome.Rejected("MANIFEST_SOURCE_DRIFT");
        foreach (var line in lines)
        {
            if (line.RightKind is not ("A" or "B")
                || line.IssuedBaseQuantity < 0
                || line.IssuedBaseQuantity != line.ConsumedBaseQuantity
                || line.Remaining < 0
                || line.Remaining > line.GrantedBaseQuantity - line.ConsumedBaseQuantity
                    - line.ReleasedBaseQuantity
                || line.RightStatus != "HELD" || line.ReleasedBaseQuantity != 0
                || line.GrantedBaseQuantity != line.LoadedBaseQuantity)
                return BusinessCommandOutcome.Rejected("REMAINDER_RIGHT_SOURCE_DRIFT");
        }
        if (lines.All(l => l.Remaining == 0))
            return BusinessCommandOutcome.Rejected("NOTHING_TO_UNLOAD");

        var returns = new List<ReturnLine>();
        var skuTotals = new List<(Guid Sku, decimal Qty, decimal Cost)>();
        foreach (var group in lines.GroupBy(l => l.SkuId).OrderBy(g => g.Key))
        {
            decimal qty = group.Sum(l => l.Remaining);
            var vehicle = await ReadPhysical(db, tx, tenant,
                manifest.VehicleLocationId, group.Key, ct);
            var warehouse = await ReadPhysical(db, tx, tenant,
                manifest.WarehouseLocationId, group.Key, ct);
            if (vehicle is null || warehouse is null
                || vehicle.OnHandBaseQuantity != vehicle.SourceBaseQuantity
                || vehicle.OnHandCostAmount != vehicle.SourceCostAmount
                || warehouse.OnHandBaseQuantity != warehouse.SourceBaseQuantity
                || warehouse.OnHandCostAmount != warehouse.SourceCostAmount)
                return BusinessCommandOutcome.Rejected("PHYSICAL_SOURCE_DRIFT");
            if (qty <= 0 || qty != vehicle.OnHandBaseQuantity
                || vehicle.OnHandCostAmount < 0 || vehicle.OnHandCostAmount > MaximumAmount
                || warehouse.OnHandBaseQuantity + qty > MaximumQuantity
                || warehouse.OnHandCostAmount + vehicle.OnHandCostAmount > MaximumAmount)
                return BusinessCommandOutcome.Rejected("VEHICLE_REMAINDER_MISMATCH");
            decimal assigned = 0m;
            var remaining = group.Where(l => l.Remaining > 0).OrderBy(l => l.LineNumber).ToArray();
            for (int i = 0; i < remaining.Length; i++)
            {
                decimal cost = i == remaining.Length - 1
                    ? vehicle.OnHandCostAmount - assigned
                    : decimal.Round(vehicle.OnHandCostAmount * remaining[i].Remaining / qty,
                        4, MidpointRounding.AwayFromZero);
                if (cost < 0 || cost > vehicle.OnHandCostAmount - assigned)
                    return BusinessCommandOutcome.Rejected("UNLOAD_COST_INVALID");
                assigned += cost;
                returns.Add(new ReturnLine(remaining[i], cost));
            }
            if (assigned != vehicle.OnHandCostAmount)
                return BusinessCommandOutcome.Rejected("UNLOAD_COST_MISMATCH");
            skuTotals.Add((group.Key, qty, vehicle.OnHandCostAmount));
        }

        Guid source = Guid.NewGuid();
        await db.ExecuteAsync(Sql("""
            INSERT dbo.BusinessSource(Id,TenantId,BusinessCommandId,SourceKind,
                SourceNumber,ActorId,TripId,OccurredAtUtc)
            VALUES(@Source,@Tenant,@Command,N'TRIP_UNLOAD',
                @Number,@Actor,@Trip,SYSUTCDATETIME());
            INSERT dbo.TripUnload(Id,TenantId,TripId,TripManifestId,
                WarehouseLocationId,VehicleLocationId)
            VALUES(@Source,@Tenant,@Trip,@Manifest,@Warehouse,@Vehicle);
            """, new { Source = source, Tenant = tenant, Command = commandId,
                Number = req.SourceNumber, Actor = actor, Trip = req.TripId,
                Manifest = req.ManifestId, Warehouse = manifest.WarehouseLocationId,
                Vehicle = manifest.VehicleLocationId }, tx, ct));

        int n = 0;
        foreach (var entry in returns)
        {
            var line = entry.Loaded;
            Guid sourceLine = Guid.NewGuid();
            await db.ExecuteAsync(Sql("""
                INSERT dbo.BusinessSourceLine(Id,TenantId,BusinessSourceId,
                    LineNumber,SkuId,BaseQuantity,Amount)
                VALUES(@Line,@Tenant,@Source,@Number,@Sku,@Qty,@Cost);
                INSERT dbo.TripUnloadLine(Id,TenantId,TripUnloadId,TripId,
                    TripManifestLineId,LoadSourceLineId,StockRightId,RightKind,
                    SkuId,SourceLineId,BaseQuantity,CostAmount,ReleasedBQuantity)
                VALUES(@Id,@Tenant,@Source,@Trip,@ManifestLine,@LoadLine,@Right,
                    @Kind,@Sku,@Line,@Qty,@Cost,@BReleased);
                """, new { Id = Guid.NewGuid(), Tenant = tenant, Source = source,
                    Trip = req.TripId, Line = sourceLine, Number = ++n,
                    Sku = line.SkuId, Qty = line.Remaining, Cost = entry.Cost,
                    ManifestLine = line.ManifestLineId, LoadLine = line.LoadSourceLineId,
                    Right = line.StockRightId, Kind = line.RightKind,
                    BReleased = line.RightKind == "B" ? line.Remaining : 0m }, tx, ct));
            await AddEffect(db, tx, tenant, sourceLine, "VEHICLE-OUT",
                "PHYSICAL_STOCK", -line.Remaining, -entry.Cost,
                line.SkuId, manifest.VehicleLocationId, ct);
            await AddEffect(db, tx, tenant, sourceLine, "WAREHOUSE-IN",
                "PHYSICAL_STOCK", line.Remaining, entry.Cost,
                line.SkuId, manifest.WarehouseLocationId, ct);
            if (line.RightKind == "B")
            {
                if (await db.ExecuteAsync(Sql("""
                    UPDATE dbo.StockRight SET ReleasedBaseQuantity=ReleasedBaseQuantity+@Qty,
                        Status=CASE WHEN GrantedBaseQuantity=
                            ConsumedBaseQuantity+ReleasedBaseQuantity+@Qty
                            THEN N'RELEASED' ELSE N'HELD' END
                    WHERE TenantId=@Tenant AND Id=@Right AND RightKind='B'
                        AND Status=N'HELD' AND ReleasedBaseQuantity=0
                        AND GrantedBaseQuantity-ConsumedBaseQuantity>=@Qty;
                    """, new { Tenant = tenant, Right = line.StockRightId,
                        Qty = line.Remaining }, tx, ct)) != 1)
                    throw new InvalidOperationException("B release lost its right lock.");
                await AddEffect(db, tx, tenant, sourceLine, "RIGHT-B-RELEASE",
                    "RIGHT_B_RELEASED", -line.Remaining, 0m,
                    line.SkuId, null, ct);
            }
        }
        foreach (var sku in skuTotals)
        {
            if (await db.ExecuteAsync(Sql("""
                UPDATE dbo.PhysicalStockBalance
                SET OnHandBaseQuantity=OnHandBaseQuantity-@Qty,
                    OnHandCostAmount=OnHandCostAmount-@Cost
                WHERE TenantId=@Tenant AND StockLocationId=@Vehicle AND SkuId=@Sku
                    AND OnHandBaseQuantity=@Qty AND OnHandCostAmount=@Cost;
                """, new { Tenant = tenant, Vehicle = manifest.VehicleLocationId,
                    sku.Sku, sku.Qty, sku.Cost }, tx, ct)) != 1)
                throw new InvalidOperationException("Vehicle balance changed under lock.");
            if (await db.ExecuteAsync(Sql("""
                UPDATE dbo.PhysicalStockBalance
                SET OnHandBaseQuantity=OnHandBaseQuantity+@Qty,
                    OnHandCostAmount=OnHandCostAmount+@Cost
                WHERE TenantId=@Tenant AND StockLocationId=@Warehouse AND SkuId=@Sku
                    AND OnHandBaseQuantity+@Qty<=@MaxQty
                    AND OnHandCostAmount+@Cost<=@MaxCost;
                """, new { Tenant = tenant, Warehouse = manifest.WarehouseLocationId,
                    sku.Sku, sku.Qty, sku.Cost, MaxQty = MaximumQuantity,
                    MaxCost = MaximumAmount }, tx, ct)) != 1)
                throw new InvalidOperationException("Warehouse balance changed under lock.");
        }
        if (await db.ExecuteAsync(Sql("""
            UPDATE dbo.Trip SET Status=N'RECONCILING'
            WHERE TenantId=@Tenant AND Id=@Trip
                AND Status IN (N'PREPARED',N'IN_PROGRESS');
            """, new { Tenant = tenant, Trip = req.TripId }, tx, ct)) != 1)
            throw new InvalidOperationException("Trip status changed under lock.");

        var totals = await db.QuerySingleAsync<
            (decimal Qty, decimal Cost, decimal BReleased, int Lines, int Effects)>(Sql("""
            SELECT
                COALESCE((SELECT SUM(BaseQuantity) FROM dbo.TripUnloadLine
                    WHERE TenantId=@Tenant AND TripUnloadId=@Source),0),
                COALESCE((SELECT SUM(CostAmount) FROM dbo.TripUnloadLine
                    WHERE TenantId=@Tenant AND TripUnloadId=@Source),0),
                COALESCE((SELECT SUM(ReleasedBQuantity) FROM dbo.TripUnloadLine
                    WHERE TenantId=@Tenant AND TripUnloadId=@Source),0),
                (SELECT COUNT(*) FROM dbo.TripUnloadLine
                    WHERE TenantId=@Tenant AND TripUnloadId=@Source),
                (SELECT COUNT(*) FROM dbo.LedgerEffect e
                    JOIN dbo.BusinessSourceLine sl ON sl.TenantId=e.TenantId
                        AND sl.Id=e.BusinessSourceLineId
                    WHERE sl.TenantId=@Tenant AND sl.BusinessSourceId=@Source);
            """, new { Tenant = tenant, Source = source }, tx, ct));
        if (totals.Qty != skuTotals.Sum(x => x.Qty)
            || totals.Cost != skuTotals.Sum(x => x.Cost)
            || totals.BReleased != returns.Where(x => x.Loaded.RightKind == "B")
                .Sum(x => x.Loaded.Remaining)
            || totals.Lines != returns.Count
            || totals.Effects != returns.Count * 2
                + returns.Count(x => x.Loaded.RightKind == "B"))
            throw new InvalidOperationException("Unload source/ledger incomplete.");
        return BusinessCommandOutcome.Applied("TRIP_REMAINDER_UNLOADED", source);
    }

    private static Task<LocationBalance?> ReadPhysical(SqlConnection db, SqlTransaction tx,
        string tenant, Guid location, Guid sku, CancellationToken ct) =>
        db.QuerySingleOrDefaultAsync<LocationBalance>(Sql("""
            SELECT b.OnHandBaseQuantity,b.OnHandCostAmount,
                o.BaseQuantity
                    + CASE WHEN loc.Kind=N'VEHICLE' THEN COALESCE(load.Qty,0)
                        - COALESCE(issued.Qty,0) - COALESCE(unload.Qty,0)
                      ELSE -COALESCE(load.Qty,0)+COALESCE(unload.Qty,0) END
                    AS SourceBaseQuantity,
                o.CostAmount
                    + CASE WHEN loc.Kind=N'VEHICLE' THEN COALESCE(load.Cost,0)
                        - COALESCE(issued.Cost,0) - COALESCE(unload.Cost,0)
                      ELSE -COALESCE(load.Cost,0)+COALESCE(unload.Cost,0) END
                    AS SourceCostAmount
            FROM dbo.PhysicalStockBalance b WITH (UPDLOCK,HOLDLOCK)
            JOIN dbo.PhysicalStockOpening o WITH (HOLDLOCK)
                ON o.TenantId=b.TenantId AND o.StockLocationId=b.StockLocationId
                AND o.SkuId=b.SkuId
            JOIN dbo.StockLocation loc WITH (HOLDLOCK)
                ON loc.TenantId=b.TenantId AND loc.Id=b.StockLocationId
            OUTER APPLY (
                SELECT SUM(mv.BaseQuantity) AS Qty,SUM(mv.CostAmount) AS Cost
                FROM dbo.PhysicalStockMovement mv WITH (HOLDLOCK)
                JOIN dbo.TripManifestLine ml WITH (HOLDLOCK)
                    ON ml.TenantId=mv.TenantId AND ml.Id=mv.TripManifestLineId
                WHERE mv.TenantId=b.TenantId AND ml.SkuId=b.SkuId
                    AND (ml.VehicleLocationId=b.StockLocationId
                         OR ml.WarehouseLocationId=b.StockLocationId)
            ) load
            OUTER APPLY (
                SELECT SUM(di.BaseQuantity) AS Qty,SUM(di.CostAmount) AS Cost
                FROM dbo.DeliveryStockIssue di WITH (HOLDLOCK)
                JOIN dbo.TripManifestLine ml WITH (HOLDLOCK)
                    ON ml.TenantId=di.TenantId AND ml.Id=di.TripManifestLineId
                WHERE di.TenantId=b.TenantId AND di.SkuId=b.SkuId
                    AND ml.VehicleLocationId=b.StockLocationId
            ) issued
            OUTER APPLY (
                SELECT SUM(ul.BaseQuantity) AS Qty,SUM(ul.CostAmount) AS Cost
                FROM dbo.TripUnloadLine ul WITH (HOLDLOCK)
                JOIN dbo.TripUnload u WITH (HOLDLOCK)
                    ON u.TenantId=ul.TenantId AND u.Id=ul.TripUnloadId
                WHERE ul.TenantId=b.TenantId AND ul.SkuId=b.SkuId
                    AND (u.VehicleLocationId=b.StockLocationId
                         OR u.WarehouseLocationId=b.StockLocationId)
            ) unload
            WHERE b.TenantId=@Tenant AND b.StockLocationId=@Location AND b.SkuId=@Sku;
            """, new { Tenant = tenant, Location = location, Sku = sku }, tx, ct));
}
