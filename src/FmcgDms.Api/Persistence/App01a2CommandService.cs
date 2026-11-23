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
/// APP-01A.2: synthetic-only source/rights commands. Every decision, source,
/// ledger effect and pool allocation commits in one explicit SQL transaction.
/// Not a sale, stock transfer, real customer credit approval or production API.
/// </summary>
public sealed partial class App01a2CommandService(TenantConnections connections)
{
    private const string StockSourceKind = "STOCK_ALLOC"; // common A/B source namespace
    private const string CreditSourceKind = "B_CREDIT_GRANT";

    private static CommandDefinition Sql(string text, object? args, IDbTransaction tx, CancellationToken ct) =>
        new(text, args, tx, cancellationToken: ct);

    private static bool ValidKey(string? key, int max) =>
        !string.IsNullOrWhiteSpace(key) && key.Length <= max && !key.Any(char.IsControl);

    private static bool ValidQuantity(decimal value) =>
        value > 0 && value <= 999999999999999.999m && decimal.Round(value, 3) == value;

    private static bool ValidAmount(decimal value) =>
        value > 0 && value <= 999999999999999.9999m && decimal.Round(value, 4) == value;

    private sealed class ExistingCommand
    {
        public Guid Id { get; set; }
        public short PayloadHashVersion { get; set; }
        public byte[] PayloadHash { get; set; } = [];
        public string Status { get; set; } = "";
        public string OutcomeCode { get; set; } = "";
        public string? ReasonCode { get; set; }
    }

    private sealed class Order
    {
        public Guid Id { get; set; }
        public Guid CustomerId { get; set; }
        public Guid ShipToSnapshotId { get; set; }
        public Guid? TripId { get; set; }
        public string Status { get; set; } = "";
        public bool CustomerActive { get; set; }
    }

    private sealed class OrderLine
    {
        public Guid Id { get; set; }
        public int LineNumber { get; set; }
        public Guid SkuId { get; set; }
        public decimal BaseQuantity { get; set; }
        public decimal? UnitPrice { get; set; }
    }

    private sealed class Trip
    {
        public string Status { get; set; } = "";
        public Guid? VehicleLocationId { get; set; }
        public string VehicleKind { get; set; } = "";
        public bool VehicleActive { get; set; }
    }

    private sealed class StockLocationSku
    {
        public string Kind { get; set; } = "";
        public bool LocationActive { get; set; }
        public bool SkuActive { get; set; }
    }

    private sealed class StockBalance
    {
        public decimal OnHandBaseQuantity { get; set; }
        public decimal AllocatedBaseQuantity { get; set; }
    }

    private sealed class CreditBalance
    {
        public decimal ApprovedLimit { get; set; }
        public decimal GrantedAmount { get; set; }
    }

    private sealed class CheckedCreditApproval
    {
        public Guid Id { get; set; }
        public decimal ApprovedLimit { get; set; }
        public string ProposedBy { get; set; } = "";
        public string CheckedBy { get; set; } = "";
    }

    private sealed class ApprovedATerms
    {
        public int TermsDays { get; set; }
        public string Decision { get; set; } = "";
        public string PolicyVersion { get; set; } = "";
        public string ProposedBy { get; set; } = "";
        public string? CheckedBy { get; set; }
        public string? EvidenceRef { get; set; }
        public DateTime? ApprovedAtUtc { get; set; }
        public DateTime EffectiveFromUtc { get; set; }
        public DateTime EffectiveUntilUtc { get; set; }
    }

    private async Task<BusinessCommandOutcome> ExecuteAsync(
        ClaimsPrincipal principal, IApp01aCommand request, string kind,
        (string Key, string Value)[] fields,
        Func<SqlConnection, SqlTransaction, string, string, Guid, CancellationToken,
            Task<BusinessCommandOutcome>> apply, CancellationToken ct, string? c2Role = null,
        Guid? loadTrip = null, Guid? deliveryTrip = null, bool amendment = false, Guid? pendingCommand = null,
        Guid? d1GrantTrip = null, bool d1Load = false)
    {
        // Never persist a malformed producer/command identity: the scope cannot
        // be deduplicated reliably. All valid identities, including business
        // rejects, receive a durable outcome.
        if (!ValidKey(request.ProducerId, 128) || !ValidKey(request.CommandId, 128)
            || !ValidKey(request.SourceNumber, 80))
            return BusinessCommandOutcome.Rejected("INVALID_COMMAND_IDENTITY");

        // Even when called without HTTP endpoint policies, business operations
        // require a signed, role-scoped synthetic actor. Never infer roles from
        // tenant identity, warehouse assignment, headers or request payload.
        string requiredRole = c2Role ?? (kind == "CONFIRM_A_ORDER" ? "sales" : "warehouse");
        if (!principal.HasClaim("synthetic_role", requiredRole))
            throw new UnauthorizedAccessException("Insufficient synthetic role.");
        string tenant = SyntheticAuth.GetTenantId(principal);
        string actor = principal.FindFirst("actor_id")?.Value
            ?? throw new UnauthorizedAccessException("Validated actor identity required.");
        if (!ValidKey(actor, 128)
            || !string.Equals(actor, actor.Trim(), StringComparison.Ordinal)
            || !string.Equals(actor, principal.FindFirst("sub")?.Value, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Invalid actor identity.");

        byte[] hash = CommandPayloadHash.Compute(
            kind, tenant, actor, request.SourceNumber, fields);
        // This opens only server-routed nonce-owned LocalDB with an exact
        // AppTenantBinding match. Real/non-scratch databases fail closed.
        await using SqlConnection db = (deliveryTrip.HasValue, loadTrip.HasValue, c2Role is not null) switch
        {
            (true, _, _) => await connections.OpenApp02c4Async(principal, ct),
            (_, true, _) => await connections.OpenApp02c3Async(principal, ct),
            (_, _, true) => await connections.OpenApp02c2Async(principal, ct),
            _ => await connections.OpenSyntheticScratchAsync(principal, ct)
        };
        await using SqlTransaction tx =
            (SqlTransaction)await db.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            if (c2Role is not null)
                await App02a3Sql.RequireRole(db, tx, tenant, actor, c2Role, ct);
            if (loadTrip is Guid trip)
                await RequireC3Writer(db, tx, tenant, actor, trip, ct);
            if (d1GrantTrip is Guid grantTrip)
            {
                if (!connections.UsesApp02d1)
                    throw new UnauthorizedAccessException("D.1 DEV commands are disabled.");
                if (c2Role == "warehouse")
                    await RequireC3Writer(db, tx, tenant, actor, grantTrip, ct);
                else if (await db.ExecuteScalarAsync<int>(Sql("""
                    SELECT COUNT(*) FROM dbo.Trip WITH (HOLDLOCK) WHERE TenantId=@Tenant AND Id=@Trip
                    """, new { Tenant = tenant, Trip = grantTrip }, tx, ct)) != 1)
                    throw new UnauthorizedAccessException("D.1 grant trip is unavailable.");
            }
            if (deliveryTrip is Guid c4Trip)
                await RequireC4Authority(principal, db, tx, tenant, actor, c4Trip, amendment, ct);
            await LockCommand(db, tx, tenant, request, ct);
            if (connections.UsesApp02d3 && (loadTrip ?? deliveryTrip ?? d1GrantTrip) is Guid mixedTrip)
                await App02a3Sql.Lock(db, tx, "APP01C:TRIP:" + tenant + ":" + mixedTrip.ToString("N"), ct);

            var existing = await db.QuerySingleOrDefaultAsync<ExistingCommand>(Sql("""
                SELECT Id,PayloadHashVersion,PayloadHash,Status,OutcomeCode,ReasonCode
                FROM dbo.BusinessCommand WITH (UPDLOCK,HOLDLOCK)
                WHERE TenantId=@Tenant AND ProducerId=@Producer AND CommandId=@Command
                """, new { Tenant = tenant, Producer = request.ProducerId, Command = request.CommandId }, tx, ct));
            if (existing is not null)
            {
                if (c2Role is not null && await db.ExecuteScalarAsync<int>(Sql("""
                    SELECT COUNT(*) FROM dbo.C2CommandScope WHERE TenantId=@Tenant AND CommandRowId=@Id AND RoleCode=@Role
                        AND (LoadTripId=@LoadTrip OR (LoadTripId IS NULL AND @LoadTrip IS NULL))
                        AND (DeliveryTripId=@DeliveryTrip OR (DeliveryTripId IS NULL AND @DeliveryTrip IS NULL))
                        AND (D1GrantTripId=@D1GrantTrip OR (D1GrantTripId IS NULL AND @D1GrantTrip IS NULL))
                        AND IsD1Load=@IsD1Load
                    """, new { Tenant = tenant, existing.Id, Role = c2Role, LoadTrip = loadTrip, DeliveryTrip = deliveryTrip,
                        D1GrantTrip = d1GrantTrip, IsD1Load = d1Load }, tx, ct)) == 0)
                    return BusinessCommandOutcome.Conflict();
                if (existing.PayloadHashVersion != CommandPayloadHash.Version
                    || !CryptographicOperations.FixedTimeEquals(existing.PayloadHash, hash))
                {
                    await tx.RollbackAsync(ct);
                    return BusinessCommandOutcome.Conflict();
                }
                if (existing.Status is "APPLIED" or "REJECTED")
                {
                    Guid? oldSource = await db.QuerySingleOrDefaultAsync<Guid?>(Sql("""
                        SELECT Id FROM dbo.BusinessSource
                        WHERE TenantId=@Tenant AND BusinessCommandId=@Command
                        """, new { Tenant = tenant, Command = existing.Id }, tx, ct));
                    await tx.CommitAsync(ct);
                    return new BusinessCommandOutcome(existing.Status, existing.OutcomeCode,
                        existing.ReasonCode, true, oldSource);
                }
                if (existing.Status != "RECEIVED" || existing.Id != pendingCommand)
                    throw new InvalidOperationException("Persisted command is incomplete.");
            }
            else if (pendingCommand.HasValue)
                throw new InvalidOperationException("Durable delivery command is missing.");

            Guid commandRowId = existing?.Id ?? Guid.NewGuid();
            if (existing is null)
                await InsertReceivedCommand(db, tx, tenant, actor, request, commandRowId, hash, c2Role, loadTrip, deliveryTrip, ct, d1GrantTrip, d1Load);

            BusinessCommandOutcome result = await apply(db, tx, tenant, actor, commandRowId, ct);
            if (result.Status is not ("APPLIED" or "REJECTED"))
                throw new InvalidOperationException("Only completed outcomes can be persisted.");
            await db.ExecuteAsync(Sql($"""
                UPDATE dbo.BusinessCommand
                SET Status=@Status,OutcomeCode=@Outcome,ReasonCode=@Reason,
                    CompletedAtUtc={App02c6Clock.SqlUtcNow}
                WHERE TenantId=@Tenant AND Id=@Id AND Status=N'RECEIVED'
                """, new
                {
                    Tenant = tenant, Id = commandRowId, result.Status,
                    Outcome = result.OutcomeCode, Reason = result.ReasonCode
                }, tx, ct));
            await tx.CommitAsync(ct);
            return result;
        }
        catch
        {
            try { await tx.RollbackAsync(CancellationToken.None); }
            catch (InvalidOperationException) { /* Already rolled back by SQL Server. */ }
            throw;
        }
    }

    private static async Task LockCommand(SqlConnection db, SqlTransaction tx, string tenant,
        IApp01aCommand request, CancellationToken ct)
    {
        string key = "APP01A2:" + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(tenant + "\0" + request.ProducerId + "\0" + request.CommandId)));
        int lockCode = await db.ExecuteScalarAsync<int>(Sql("""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource=@Key,
                @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=10000;
            SELECT @result;
            """, new { Key = key }, tx, ct));
        if (lockCode < 0)
            throw new TimeoutException("Could not serialize this command identity.");
    }

    private static async Task InsertReceivedCommand(SqlConnection db, SqlTransaction tx, string tenant,
        string actor, IApp01aCommand request, Guid id, byte[] hash, string? role, Guid? loadTrip, Guid? deliveryTrip,
        CancellationToken ct, Guid? d1GrantTrip = null, bool d1Load = false)
    {
        await db.ExecuteAsync(Sql($"""
            INSERT dbo.BusinessCommand(Id,TenantId,ProducerId,CommandId,
                PayloadHashVersion,PayloadHash,ActorId,Status,SubmittedAtUtc)
            VALUES(@Id,@Tenant,@Producer,@Command,@Version,@Hash,@Actor,N'RECEIVED',{App02c6Clock.SqlUtcNow})
            """, new { Id = id, Tenant = tenant, Producer = request.ProducerId, Command = request.CommandId,
                Version = CommandPayloadHash.Version, Hash = hash, Actor = actor }, tx, ct));
        if (role is not null)
            await db.ExecuteAsync(Sql("""
                INSERT dbo.C2CommandScope(TenantId,CommandRowId,RoleCode,LoadTripId,DeliveryTripId,D1GrantTripId,IsD1Load)
                VALUES(@Tenant,@Id,@Role,@LoadTrip,@DeliveryTrip,@D1GrantTrip,@IsD1Load)
                """, new { Tenant = tenant, Id = id, Role = role, LoadTrip = loadTrip, DeliveryTrip = deliveryTrip,
                    D1GrantTrip = d1GrantTrip, IsD1Load = d1Load }, tx, ct));
    }

    private static async Task<bool> SourceUsed(SqlConnection db, SqlTransaction tx,
        string tenant, string kind, string number, CancellationToken ct)
    {
        // The unique (TenantId,SourceKind,SourceNumber) index gives a
        // SERIALIZABLE key-range lock even when the source does not yet exist.
        var existing = await db.QuerySingleOrDefaultAsync<Guid?>(Sql("""
            SELECT Id FROM dbo.BusinessSource WITH (UPDLOCK,HOLDLOCK)
            WHERE TenantId=@Tenant AND SourceKind=@Kind AND SourceNumber=@Number
            """, new { Tenant = tenant, Kind = kind, Number = number }, tx, ct));
        return existing.HasValue;
    }

    private static Task<Order?> FindOrder(SqlConnection db, SqlTransaction tx,
        string tenant, Guid orderId, CancellationToken ct) =>
        db.QuerySingleOrDefaultAsync<Order>(Sql("""
            SELECT o.Id,o.Status,o.CustomerId,o.ShipToSnapshotId,o.TripId,
                c.IsActive AS CustomerActive
            FROM dbo.AOrder o WITH (UPDLOCK,HOLDLOCK)
            JOIN dbo.Customer c ON c.TenantId=o.TenantId AND c.Id=o.CustomerId
            WHERE o.TenantId=@Tenant AND o.Id=@OrderId
            """, new { Tenant = tenant, OrderId = orderId }, tx, ct));

    private static Task<Trip?> FindTrip(SqlConnection db, SqlTransaction tx,
        string tenant, Guid tripId, CancellationToken ct) =>
        db.QuerySingleOrDefaultAsync<Trip>(Sql("""
            SELECT t.Status,t.VehicleLocationId,
                v.Kind AS VehicleKind,v.IsActive AS VehicleActive
            FROM dbo.Trip t WITH (UPDLOCK,HOLDLOCK)
            LEFT JOIN dbo.StockLocation v WITH (HOLDLOCK)
                ON v.TenantId=t.TenantId AND v.Id=t.VehicleLocationId
            WHERE t.TenantId=@Tenant AND t.Id=@TripId
            """, new { Tenant = tenant, TripId = tripId }, tx, ct));

    private static bool IsPreparedTrip(Trip? trip) =>
        trip is { Status: "PREPARED", VehicleLocationId: not null,
            VehicleKind: "VEHICLE", VehicleActive: true };

    private static async Task<string?> AllocateStock(SqlConnection db, SqlTransaction tx,
        string tenant, Guid locationId, Guid skuId, decimal quantity, CancellationToken ct)
    {
        var eligible = await db.QuerySingleOrDefaultAsync<StockLocationSku>(Sql("""
            SELECT l.Kind,l.IsActive AS LocationActive,s.IsActive AS SkuActive
            FROM dbo.StockLocation l CROSS JOIN dbo.Sku s
            WHERE l.TenantId=@Tenant AND l.Id=@LocationId
                AND s.TenantId=@Tenant AND s.Id=@SkuId
            """, new { Tenant = tenant, LocationId = locationId, SkuId = skuId }, tx, ct));
        if (eligible is null || !eligible.LocationActive || !eligible.SkuActive
            || eligible.Kind != "WAREHOUSE")
            return "STOCK_LOCATION_OR_SKU_NOT_ELIGIBLE";

        var pool = await db.QuerySingleOrDefaultAsync<StockBalance>(Sql("""
            SELECT OnHandBaseQuantity,AllocatedBaseQuantity
            FROM dbo.StockPool WITH (UPDLOCK,HOLDLOCK)
            WHERE TenantId=@Tenant AND StockLocationId=@LocationId AND SkuId=@SkuId
            """, new { Tenant = tenant, LocationId = locationId, SkuId = skuId }, tx, ct));
        if (pool is null) return "STOCK_POOL_NOT_FOUND";
        if (pool.OnHandBaseQuantity - pool.AllocatedBaseQuantity < quantity)
            return "STOCK_INSUFFICIENT";

        int updated = await db.ExecuteAsync(Sql("""
            UPDATE dbo.StockPool SET AllocatedBaseQuantity=AllocatedBaseQuantity+@Quantity
            WHERE TenantId=@Tenant AND StockLocationId=@LocationId AND SkuId=@SkuId
                AND OnHandBaseQuantity-AllocatedBaseQuantity >= @Quantity
            """, new { Tenant = tenant, LocationId = locationId, SkuId = skuId, Quantity = quantity }, tx, ct));
        return updated == 1 ? null : "STOCK_ALLOCATION_CONFLICT";
    }

    private static async Task<Guid> AddSource(SqlConnection db, SqlTransaction tx,
        string tenant, Guid commandRowId, string kind, string sourceNumber,
        string actor, Guid? customerId, Guid? tripId, CancellationToken ct)
    {
        Guid id = Guid.NewGuid();
        await db.ExecuteAsync(Sql($"""
            INSERT dbo.BusinessSource(Id,TenantId,BusinessCommandId,SourceKind,
                SourceNumber,ActorId,CustomerId,TripId,OccurredAtUtc,RecordedAtUtc)
            VALUES(@Id,@Tenant,@Command,@Kind,@Number,@Actor,@Customer,@Trip,{App02c6Clock.SqlUtcNow},{App02c6Clock.SqlUtcNow})
            """, new
            {
                Id = id, Tenant = tenant, Command = commandRowId, Kind = kind,
                Number = sourceNumber, Actor = actor, Customer = customerId, Trip = tripId
            }, tx, ct));
        return id;
    }

    private static async Task<Guid> AddLine(SqlConnection db, SqlTransaction tx,
        string tenant, Guid sourceId, int number, Guid? skuId,
        decimal? quantity, decimal? amount, CancellationToken ct)
    {
        Guid id = Guid.NewGuid();
        await db.ExecuteAsync(Sql("""
            INSERT dbo.BusinessSourceLine(Id,TenantId,BusinessSourceId,LineNumber,
                SkuId,BaseQuantity,Amount)
            VALUES(@Id,@Tenant,@Source,@Number,@Sku,@Quantity,@Amount)
            """, new
            {
                Id = id, Tenant = tenant, Source = sourceId, Number = number,
                Sku = skuId, Quantity = quantity, Amount = amount
            }, tx, ct));
        return id;
    }

    private static Task<int> AddRightEffect(SqlConnection db, SqlTransaction tx,
        string tenant, Guid sourceLine, string ledgerKind, Guid? customerId,
        Guid? skuId, Guid? locationId, decimal quantity, decimal amount, CancellationToken ct) =>
        db.ExecuteAsync(Sql("""
            INSERT dbo.LedgerEffect(Id,TenantId,BusinessSourceLineId,EffectKey,
                LedgerKind,CustomerId,SkuId,StockLocationId,QuantityDelta,AmountDelta)
            VALUES(@Id,@Tenant,@Line,N'RIGHT-ALLOCATED',@Kind,@Customer,@Sku,@Location,@Quantity,@Amount)
            """, new
            {
                Id = Guid.NewGuid(), Tenant = tenant, Line = sourceLine,
                Kind = ledgerKind, Customer = customerId, Sku = skuId,
                Location = locationId, Quantity = quantity, Amount = amount
            }, tx, ct));

    public Task<BusinessCommandOutcome> ReserveAStockAsync(
        ClaimsPrincipal principal, ReserveAStockCommand request, CancellationToken ct) =>
        ReserveAStockCore(principal, request, null, ct);

    private Task<BusinessCommandOutcome> ReserveAStockCore(
        ClaimsPrincipal principal, ReserveAStockCommand request, Guid? expectedConfirmSource, CancellationToken ct) =>
        ExecuteAsync(principal, request, expectedConfirmSource.HasValue ? "C2_RESERVE_A" : "RESERVE_A_STOCK",
            [
                ("OrderId", request.OrderId.ToString("N")),
                ("OrderLineId", request.OrderLineId.ToString("N")),
                ("StockLocationId", request.StockLocationId.ToString("N")),
                ("BaseQuantity", CommandPayloadHash.Decimal(request.BaseQuantity)),
                ..(expectedConfirmSource.HasValue
                    ? new[] { ("ConfirmSource", expectedConfirmSource.Value.ToString("N")) } : [])
            ],
            async (db, tx, tenant, actor, commandId, token) =>
            {
                if (request.OrderId == Guid.Empty || request.OrderLineId == Guid.Empty
                    || request.StockLocationId == Guid.Empty)
                    return BusinessCommandOutcome.Rejected("INVALID_REFERENCE");
                if (!ValidQuantity(request.BaseQuantity))
                    return BusinessCommandOutcome.Rejected("QUANTITY_INVALID");
                if (await SourceUsed(db, tx, tenant, StockSourceKind, request.SourceNumber, token))
                    return BusinessCommandOutcome.Rejected("SOURCE_ALREADY_USED");

                Guid? draftTrip = expectedConfirmSource.HasValue ? await db.QuerySingleOrDefaultAsync<Guid?>(Sql("""
                    SELECT TripId FROM dbo.C2OrderDraft WHERE TenantId=@Tenant AND Id=@OrderId
                    """, new { Tenant = tenant, request.OrderId }, tx, token)) : null;
                var preparedTrip = draftTrip is Guid c2Trip ? await FindTrip(db, tx, tenant, c2Trip, token) : null;
                var order = await FindOrder(db, tx, tenant, request.OrderId, token);
                if (order is null) return BusinessCommandOutcome.Rejected("ORDER_NOT_FOUND");
                if (!order.CustomerActive) return BusinessCommandOutcome.Rejected("CUSTOMER_INACTIVE");
                if (expectedConfirmSource is Guid expected)
                {
                    if (order.Status is not ("CONFIRMED" or "PARTIALLY_DELIVERED"))
                        return BusinessCommandOutcome.Rejected("C2_ORDER_NOT_CONFIRMED");
                    Guid? actual = await db.QuerySingleOrDefaultAsync<Guid?>(Sql("""
                        SELECT ConfirmSourceId FROM dbo.C2Confirmation WHERE TenantId=@Tenant AND OrderId=@OrderId
                        """, new { Tenant = tenant, OrderId = order.Id }, tx, token));
                    if (actual != expected) return BusinessCommandOutcome.Rejected("C2_STALE_CONFIRM");
                }
                else if (order.Status != "DRAFT") return BusinessCommandOutcome.Rejected("ORDER_NOT_DRAFT");
                if (!order.TripId.HasValue) return BusinessCommandOutcome.Rejected("ORDER_TRIP_REQUIRED");
                var trip = expectedConfirmSource.HasValue
                    ? order.TripId == draftTrip ? preparedTrip : null
                    : await FindTrip(db, tx, tenant, order.TripId.Value, token);
                if (!IsPreparedTrip(trip))
                    return BusinessCommandOutcome.Rejected("TRIP_NOT_PREPARED");

                var line = await db.QuerySingleOrDefaultAsync<OrderLine>(Sql("""
                    SELECT Id,LineNumber,SkuId,BaseQuantity,UnitPrice
                    FROM dbo.AOrderLine WITH (UPDLOCK,HOLDLOCK)
                    WHERE TenantId=@Tenant AND AOrderId=@Order AND Id=@Line
                    """, new { Tenant = tenant, Order = order.Id, Line = request.OrderLineId }, tx, token));
                if (line is null) return BusinessCommandOutcome.Rejected("ORDER_LINE_NOT_FOUND");
                if (expectedConfirmSource.HasValue && await db.ExecuteScalarAsync<int>(Sql("""
                    SELECT COUNT(*) FROM dbo.AConfirmedLineSnapshot s
                    JOIN dbo.C2LinePolicy p ON p.TenantId=s.TenantId AND p.OrderLineId=s.AOrderLineId
                    JOIN dbo.Sku sku ON sku.TenantId=s.TenantId AND sku.Id=s.SkuId
                    WHERE s.TenantId=@Tenant AND s.AOrderId=@OrderId AND s.AOrderLineId=@LineId
                        AND s.ConfirmBusinessSourceId=@Expected
                        AND CONVERT(varbinary(48),p.BaseUom)=CONVERT(varbinary(48),sku.BaseUom)
                    """, new { Tenant = tenant, OrderId = order.Id, LineId = line.Id, Expected = expectedConfirmSource },
                        tx, token)) != 1)
                    return BusinessCommandOutcome.Rejected("C2_FROZEN_LINE_INVALID");

                decimal held = await db.ExecuteScalarAsync<decimal>(Sql("""
                    SELECT COALESCE(SUM(r.GrantedBaseQuantity-r.ConsumedBaseQuantity-r.ReleasedBaseQuantity),0)
                    FROM dbo.AReservation a JOIN dbo.StockRight r
                        ON r.TenantId=a.TenantId AND r.Id=a.StockRightId
                    WHERE a.TenantId=@Tenant AND a.AOrderLineId=@Line
                        AND r.RightKind='A' AND r.Status=N'HELD'
                        AND r.TripId=@Trip AND r.SkuId=@Sku
                    """, new
                    {
                        Tenant = tenant, Line = line.Id, Trip = order.TripId.Value, Sku = line.SkuId
                    }, tx, token));
                decimal delivered = expectedConfirmSource.HasValue ? await db.ExecuteScalarAsync<decimal>(Sql("""
                    SELECT COALESCE(SUM(BaseQuantity),0) FROM dbo.SaleDelivery
                    WHERE TenantId=@Tenant AND AOrderLineId=@Line AND RightKind='A'
                    """, new { Tenant = tenant, Line = line.Id }, tx, token)) : 0m;
                if (held + delivered + request.BaseQuantity > line.BaseQuantity)
                    return BusinessCommandOutcome.Rejected("A_LINE_OVER_RESERVED");

                IReadOnlyList<F03Pick>? lotPicks = null;
                if (connections.UsesDevF03 && await IsLotTracked(db, tx, tenant, line.Id, byRight: false, token))
                {
                    lotPicks = await PlanLotHold(db, tx, tenant, line.Id, line.SkuId,
                        request.StockLocationId, request.BaseQuantity, token);
                    if (lotPicks.Count == 0) return BusinessCommandOutcome.Rejected(LotUnavailable);
                }
                string? stockError = await AllocateStock(db, tx, tenant,
                    request.StockLocationId, line.SkuId, request.BaseQuantity, token);
                if (stockError is not null) return BusinessCommandOutcome.Rejected(stockError);

                Guid source = await AddSource(db, tx, tenant, commandId, StockSourceKind,
                    request.SourceNumber, actor, order.CustomerId, order.TripId, token);
                Guid sourceLine = await AddLine(db, tx, tenant, source, 1,
                    line.SkuId, request.BaseQuantity, null, token);
                Guid rightId = Guid.NewGuid();
                await db.ExecuteAsync(Sql("""
                    INSERT dbo.StockRight(Id,TenantId,TripId,StockLocationId,SkuId,
                        BusinessSourceLineId,RightKind,GrantedBaseQuantity)
                    VALUES(@Id,@Tenant,@Trip,@Location,@Sku,@Line,'A',@Quantity)
                    """, new
                    {
                        Id = rightId, Tenant = tenant, Trip = order.TripId.Value,
                        Location = request.StockLocationId, Sku = line.SkuId,
                        Line = sourceLine, Quantity = request.BaseQuantity
                    }, tx, token));
                await db.ExecuteAsync(Sql("""
                    INSERT dbo.AReservation(Id,TenantId,StockRightId,AOrderLineId)
                    VALUES(@Id,@Tenant,@Right,@Line)
                    """, new
                    {
                        Id = Guid.NewGuid(), Tenant = tenant, Right = rightId, Line = line.Id
                    }, tx, token));
                if (lotPicks is not null)
                    await RecordLotPicks(db, tx, tenant, rightId, lotPicks, "F03LotHold", "StockRightId", token);
                await AddRightEffect(db, tx, tenant, sourceLine, "RIGHT_A",
                    order.CustomerId, line.SkuId, request.StockLocationId, request.BaseQuantity, 0, token);
                return BusinessCommandOutcome.Applied("A_STOCK_RESERVED", source);
            }, ct, expectedConfirmSource.HasValue ? "warehouse" : null);

    public Task<BusinessCommandOutcome> ConfirmAOrderAsync(
        ClaimsPrincipal principal, ConfirmAOrderCommand request, CancellationToken ct) =>
        ExecuteAsync(principal, request, "CONFIRM_A_ORDER",
            [("OrderId", request.OrderId.ToString("N"))],
            async (db, tx, tenant, actor, commandId, token) =>
            {
                if (request.OrderId == Guid.Empty)
                    return BusinessCommandOutcome.Rejected("INVALID_REFERENCE");
                if (await SourceUsed(db, tx, tenant, "A_CONFIRM", request.SourceNumber, token))
                    return BusinessCommandOutcome.Rejected("SOURCE_ALREADY_USED");
                var order = await FindOrder(db, tx, tenant, request.OrderId, token);
                if (order is null) return BusinessCommandOutcome.Rejected("ORDER_NOT_FOUND");
                if (!order.CustomerActive) return BusinessCommandOutcome.Rejected("CUSTOMER_INACTIVE");
                if (order.Status != "DRAFT") return BusinessCommandOutcome.Rejected("ORDER_NOT_DRAFT");
                if (!order.TripId.HasValue) return BusinessCommandOutcome.Rejected("ORDER_TRIP_REQUIRED");
                var trip = await FindTrip(db, tx, tenant, order.TripId.Value, token);
                if (!IsPreparedTrip(trip))
                    return BusinessCommandOutcome.Rejected("TRIP_NOT_PREPARED");

                // Terms are NOT self-approved by the sales request. Only an
                // independently witnessed, effective A-order approval can be
                // frozen. Older confirmed orders lacking this witness/snapshot
                // are never silently backfilled.
                var approved = await db.QuerySingleOrDefaultAsync<ApprovedATerms>(Sql("""
                    SELECT TermsDays,Decision,PolicyVersion,ProposedBy,CheckedBy,
                        EvidenceRef,ApprovedAtUtc,EffectiveFromUtc,EffectiveUntilUtc
                    FROM dbo.AOrderTermsApproval WITH (UPDLOCK,HOLDLOCK)
                    WHERE TenantId=@Tenant AND AOrderId=@Order
                    """, new { Tenant = tenant, Order = order.Id }, tx, token));
                DateTime nowUtc = DateTime.UtcNow;
                if (approved is null || approved.Decision != "APPROVED"
                    || approved.TermsDays is < 1 or > 365
                    || string.IsNullOrWhiteSpace(approved.PolicyVersion)
                    || string.IsNullOrWhiteSpace(approved.EvidenceRef)
                    || string.IsNullOrWhiteSpace(approved.CheckedBy)
                    || string.Equals(approved.ProposedBy.Trim(), approved.CheckedBy.Trim(),
                        StringComparison.OrdinalIgnoreCase)
                    || string.Equals(actor, approved.CheckedBy.Trim(), StringComparison.OrdinalIgnoreCase)
                    || approved.ApprovedAtUtc is null || approved.ApprovedAtUtc > nowUtc
                    || approved.EffectiveFromUtc > nowUtc || approved.EffectiveUntilUtc <= nowUtc)
                    return BusinessCommandOutcome.Rejected("A_TERMS_APPROVAL_REQUIRED");

                var lines = (await db.QueryAsync<OrderLine>(Sql("""
                    SELECT Id,LineNumber,SkuId,BaseQuantity,UnitPrice
                    FROM dbo.AOrderLine WITH (UPDLOCK,HOLDLOCK)
                    WHERE TenantId=@Tenant AND AOrderId=@Order ORDER BY LineNumber
                    """, new { Tenant = tenant, Order = order.Id }, tx, token))).AsList();
                if (lines.Count == 0) return BusinessCommandOutcome.Rejected("ORDER_EMPTY");
                if (lines.Any(l => !l.UnitPrice.HasValue || !ValidAmount(l.UnitPrice.Value)))
                    return BusinessCommandOutcome.Rejected("ORDER_PRICE_INCOMPLETE");

                foreach (var line in lines)
                {
                    decimal held = await db.ExecuteScalarAsync<decimal>(Sql("""
                        SELECT COALESCE(SUM(r.GrantedBaseQuantity-r.ConsumedBaseQuantity-r.ReleasedBaseQuantity),0)
                        FROM dbo.AReservation a JOIN dbo.StockRight r
                            ON r.TenantId=a.TenantId AND r.Id=a.StockRightId
                        WHERE a.TenantId=@Tenant AND a.AOrderLineId=@Line
                            AND r.RightKind='A' AND r.Status=N'HELD'
                            AND r.TripId=@Trip AND r.SkuId=@Sku
                        """, new
                        {
                            Tenant = tenant, Line = line.Id, Trip = order.TripId.Value, Sku = line.SkuId
                        }, tx, token));
                    if (held != line.BaseQuantity)
                        return BusinessCommandOutcome.Rejected("ORDER_NOT_FULLY_RESERVED");
                }

                if (await db.ExecuteAsync(Sql("""
                    UPDATE dbo.AOrder SET Status=N'CONFIRMED',ConfirmedAtUtc=SYSUTCDATETIME()
                    WHERE TenantId=@Tenant AND Id=@Order AND Status=N'DRAFT'
                    """, new { Tenant = tenant, Order = order.Id }, tx, token)) != 1)
                    throw new InvalidOperationException("A confirm state changed under lock.");
                Guid source = await AddSource(db, tx, tenant, commandId, "A_CONFIRM",
                    request.SourceNumber, actor, order.CustomerId, order.TripId, token);
                foreach (var line in lines)
                {
                    Guid sourceLine = await AddLine(db, tx, tenant, source, line.LineNumber,
                        line.SkuId, line.BaseQuantity, null, token);
                    await db.ExecuteAsync(Sql("""
                        INSERT dbo.AConfirmedLineSnapshot(TenantId,AOrderLineId,AOrderId,
                            TripId,CustomerId,SkuId,ConfirmBusinessSourceId,ConfirmSourceLineId,
                            ConfirmedBaseQuantity,UnitPriceSnapshot,TermsDaysSnapshot)
                        VALUES(@Tenant,@OrderLine,@Order,@Trip,@Customer,@Sku,@Source,
                            @SourceLine,@Qty,@Price,@Terms);
                        """, new { Tenant = tenant, OrderLine = line.Id, Order = order.Id,
                            Trip = order.TripId.Value, Customer = order.CustomerId,
                            Sku = line.SkuId, Source = source, SourceLine = sourceLine,
                            Qty = line.BaseQuantity, Price = line.UnitPrice!.Value,
                            Terms = approved.TermsDays }, tx, token));
                }
                // Confirm freezes price and N-day terms; no stock movement,
                // AR, revenue, cash or COGS is posted until actual delivery.
                return BusinessCommandOutcome.Applied("A_ORDER_CONFIRMED", source);
            }, ct);

    public Task<BusinessCommandOutcome> GrantBQuotaAsync(
        ClaimsPrincipal principal, GrantBQuotaCommand request, CancellationToken ct) =>
        GrantQuota(principal, request, d1: false, ct);

    private Task<BusinessCommandOutcome> GrantQuota(
        ClaimsPrincipal principal, GrantBQuotaCommand request, bool d1, CancellationToken ct) =>
        ExecuteAsync(principal, request, d1 ? "D1_GRANT_B_QUOTA" : "GRANT_B_QUOTA",
            [
                ("TripId", request.TripId.ToString("N")),
                ("StockLocationId", request.StockLocationId.ToString("N")),
                ("SkuId", request.SkuId.ToString("N")),
                ("BaseQuantity", CommandPayloadHash.Decimal(request.BaseQuantity))
            ],
            async (db, tx, tenant, actor, commandId, token) =>
            {
                if (request.TripId == Guid.Empty || request.StockLocationId == Guid.Empty
                    || request.SkuId == Guid.Empty)
                    return BusinessCommandOutcome.Rejected("INVALID_REFERENCE");
                if (!ValidQuantity(request.BaseQuantity))
                    return BusinessCommandOutcome.Rejected("QUANTITY_INVALID");
                if (await SourceUsed(db, tx, tenant, StockSourceKind, request.SourceNumber, token))
                    return BusinessCommandOutcome.Rejected("SOURCE_ALREADY_USED");
                var trip = await FindTrip(db, tx, tenant, request.TripId, token);
                if (!IsPreparedTrip(trip))
                    return BusinessCommandOutcome.Rejected("TRIP_NOT_PREPARED");
                if (d1 && await HasLegacyManifest(db, tx, tenant, request.TripId, token))
                    return BusinessCommandOutcome.Rejected("LEGACY_MANIFEST_EXISTS");
                string? stockError = await AllocateStock(db, tx, tenant, request.StockLocationId,
                    request.SkuId, request.BaseQuantity, token);
                if (stockError is not null) return BusinessCommandOutcome.Rejected(stockError);

                Guid source = await AddSource(db, tx, tenant, commandId, StockSourceKind,
                    request.SourceNumber, actor, null, request.TripId, token);
                Guid sourceLine = await AddLine(db, tx, tenant, source, 1,
                    request.SkuId, request.BaseQuantity, null, token);
                Guid rightId = Guid.NewGuid();
                await db.ExecuteAsync(Sql("""
                    INSERT dbo.StockRight(Id,TenantId,TripId,StockLocationId,SkuId,
                        BusinessSourceLineId,RightKind,GrantedBaseQuantity)
                    VALUES(@Id,@Tenant,@Trip,@Location,@Sku,@Line,'B',@Quantity)
                    """, new
                    {
                        Id = rightId, Tenant = tenant, Trip = request.TripId,
                        Location = request.StockLocationId, Sku = request.SkuId,
                        Line = sourceLine, Quantity = request.BaseQuantity
                    }, tx, token));
                await db.ExecuteAsync(Sql("""
                    INSERT dbo.BQuota(Id,TenantId,StockRightId)
                    VALUES(@Id,@Tenant,@Right)
                    """, new { Id = Guid.NewGuid(), Tenant = tenant, Right = rightId }, tx, token));
                await AddRightEffect(db, tx, tenant, sourceLine, "RIGHT_B", null,
                    request.SkuId, request.StockLocationId, request.BaseQuantity, 0, token);
                return BusinessCommandOutcome.Applied("B_QUOTA_GRANTED", source);
            }, ct, d1 || connections.UsesApp02c6Clock ? "warehouse" : null,
            d1GrantTrip: d1 ? request.TripId : null);

    public Task<BusinessCommandOutcome> GrantBCreditAsync(
        ClaimsPrincipal principal, GrantBCreditCommand request, CancellationToken ct) =>
        GrantCredit(principal, request, d1: false, ct);

    private Task<BusinessCommandOutcome> GrantCredit(
        ClaimsPrincipal principal, GrantBCreditCommand request, bool d1, CancellationToken ct) =>
        ExecuteAsync(principal, request, d1 ? "D1_GRANT_B_CREDIT" : "GRANT_B_CREDIT",
            [
                ("TripId", request.TripId.ToString("N")),
                ("CustomerId", request.CustomerId.ToString("N")),
                ("ShipToSnapshotId", request.ShipToSnapshotId.ToString("N")),
                ("PoolCode", request.PoolCode ?? ""),
                ("CurrencyUnit", request.CurrencyUnit ?? ""),
                ("Amount", CommandPayloadHash.Decimal(request.Amount))
            ],
            async (db, tx, tenant, actor, commandId, token) =>
            {
                if (request.TripId == Guid.Empty || request.CustomerId == Guid.Empty
                    || request.ShipToSnapshotId == Guid.Empty)
                    return BusinessCommandOutcome.Rejected("INVALID_REFERENCE");
                if (!ValidAmount(request.Amount))
                    return BusinessCommandOutcome.Rejected("AMOUNT_INVALID");
                if (request.PoolCode != "B" || request.CurrencyUnit != "SYNTHETIC_UNITLESS")
                    return BusinessCommandOutcome.Rejected("POOL_UNSUPPORTED");
                if (await SourceUsed(db, tx, tenant, CreditSourceKind, request.SourceNumber, token))
                    return BusinessCommandOutcome.Rejected("SOURCE_ALREADY_USED");
                var trip = await FindTrip(db, tx, tenant, request.TripId, token);
                if (!IsPreparedTrip(trip))
                    return BusinessCommandOutcome.Rejected("TRIP_NOT_PREPARED");
                if (d1 && await HasLegacyManifest(db, tx, tenant, request.TripId, token))
                    return BusinessCommandOutcome.Rejected("LEGACY_MANIFEST_EXISTS");
                int matched = await db.ExecuteScalarAsync<int>(Sql("""
                    SELECT COUNT(*) FROM dbo.Customer c
                    JOIN dbo.ShipToSnapshot s ON s.TenantId=c.TenantId
                        AND s.CustomerId=c.Id
                    WHERE c.TenantId=@Tenant AND c.Id=@Customer AND s.Id=@ShipTo
                        AND c.IsActive=1
                    """, new
                    {
                        Tenant = tenant, Customer = request.CustomerId, ShipTo = request.ShipToSnapshotId
                    }, tx, token));
                if (matched != 1)
                    return BusinessCommandOutcome.Rejected("CUSTOMER_SHIPTO_MISMATCH");

                var pool = await db.QuerySingleOrDefaultAsync<CreditBalance>(Sql("""
                    SELECT ApprovedLimit,GrantedAmount FROM dbo.BCreditPool WITH (UPDLOCK,HOLDLOCK)
                    WHERE TenantId=@Tenant AND TripId=@Trip AND CustomerId=@Customer
                        AND PoolCode=N'B' AND CurrencyUnit=N'SYNTHETIC_UNITLESS'
                    """, new
                    {
                        Tenant = tenant, Trip = request.TripId, Customer = request.CustomerId
                    }, tx, token));
                if (pool is null) return BusinessCommandOutcome.Rejected("CREDIT_POOL_NOT_FOUND");

                // A seeded pool alone is never proof of credit approval. Require
                // one unexpired, versioned AP-01 decision with a different checker.
                // Multiple active approvals are ambiguous: fail closed instead of
                // silently selecting a more permissive limit.
                var approvals = (await db.QueryAsync<CheckedCreditApproval>(Sql($"""
                    DECLARE @NowUtc datetime2(7) = {(d1 ? App02c6Clock.SqlUtcNow : "SYSUTCDATETIME()")};
                    SELECT TOP (2) Id,ApprovedLimit,ProposedBy,CheckedBy
                    FROM dbo.CreditPolicyApproval WITH (UPDLOCK,HOLDLOCK)
                    WHERE TenantId=@Tenant AND TripId=@Trip AND CustomerId=@Customer
                        AND PoolCode=N'B' AND CurrencyUnit=N'SYNTHETIC_UNITLESS'
                        AND Decision=N'APPROVED'
                        AND EffectiveFromUtc <= @NowUtc
                        AND EffectiveUntilUtc > @NowUtc
                        AND ReviewedAtUtc <= @NowUtc
                    ORDER BY EffectiveFromUtc DESC,Id
                    """, new { Tenant = tenant, Trip = request.TripId,
                        Customer = request.CustomerId }, tx, token))).AsList();
                if (approvals.Count == 0)
                    return BusinessCommandOutcome.Rejected("CREDIT_APPROVAL_REQUIRED");
                if (approvals.Count != 1)
                    return BusinessCommandOutcome.Rejected("CREDIT_APPROVAL_AMBIGUOUS");
                var approval = approvals[0];
                if (!ValidKey(approval.ProposedBy, 128) || !ValidKey(approval.CheckedBy, 128)
                    || !string.Equals(approval.ProposedBy, approval.ProposedBy.Trim(), StringComparison.Ordinal)
                    || !string.Equals(approval.CheckedBy, approval.CheckedBy.Trim(), StringComparison.Ordinal)
                    || string.Equals(approval.ProposedBy, approval.CheckedBy, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(actor, approval.CheckedBy, StringComparison.OrdinalIgnoreCase))
                    return BusinessCommandOutcome.Rejected("CREDIT_CHECKER_CONFLICT");
                if (d1 && await db.ExecuteScalarAsync<int>(Sql("""
                    SELECT COUNT(*) FROM dbo.App02ActorRoleGrant WITH (HOLDLOCK)
                    WHERE TenantId=@Tenant AND IsActive=1 AND
                        ((ActorId=@Maker AND RoleCode=N'credit_maker')
                         OR (ActorId=@Checker AND RoleCode=N'credit_checker'))
                    """, new { Tenant = tenant, Maker = approval.ProposedBy, Checker = approval.CheckedBy }, tx, token)) != 2)
                    return BusinessCommandOutcome.Rejected("CREDIT_APPROVAL_AUTHORITY_REQUIRED");
                if (pool.ApprovedLimit > approval.ApprovedLimit)
                    return BusinessCommandOutcome.Rejected("CREDIT_POLICY_LIMIT_MISMATCH");
                if (pool.ApprovedLimit - pool.GrantedAmount < request.Amount)
                    return BusinessCommandOutcome.Rejected("CREDIT_LIMIT_INSUFFICIENT");
                int updated = await db.ExecuteAsync(Sql("""
                    UPDATE dbo.BCreditPool SET GrantedAmount=GrantedAmount+@Amount
                    WHERE TenantId=@Tenant AND TripId=@Trip AND CustomerId=@Customer
                        AND PoolCode=N'B' AND CurrencyUnit=N'SYNTHETIC_UNITLESS'
                        AND ApprovedLimit-GrantedAmount >= @Amount
                    """, new
                    {
                        Tenant = tenant, Trip = request.TripId,
                        Customer = request.CustomerId, request.Amount
                    }, tx, token));
                if (updated != 1)
                    return BusinessCommandOutcome.Rejected("CREDIT_ALLOCATION_CONFLICT");

                Guid source = await AddSource(db, tx, tenant, commandId, CreditSourceKind,
                    request.SourceNumber, actor, request.CustomerId, request.TripId, token);
                Guid sourceLine = await AddLine(db, tx, tenant, source, 1,
                    null, null, request.Amount, token);
                await db.ExecuteAsync(Sql("""
                    INSERT dbo.BCreditGrant(Id,TenantId,TripId,CustomerId,ShipToSnapshotId,
                        PoolCode,CurrencyUnit,BusinessSourceLineId,GrantedAmount,ApprovalId)
                    VALUES(@Id,@Tenant,@Trip,@Customer,@ShipTo,N'B',N'SYNTHETIC_UNITLESS',@Line,@Amount,@Approval)
                    """, new
                    {
                        Id = Guid.NewGuid(), Tenant = tenant, Trip = request.TripId,
                        Customer = request.CustomerId, ShipTo = request.ShipToSnapshotId,
                        Line = sourceLine, request.Amount, Approval = approval.Id
                    }, tx, token));
                await AddRightEffect(db, tx, tenant, sourceLine, "RIGHT_CREDIT_B",
                    request.CustomerId, null, null, 0, request.Amount, token);
                return BusinessCommandOutcome.Applied("B_CREDIT_GRANTED", source);
            }, ct, d1 ? "finance" : null, d1GrantTrip: d1 ? request.TripId : null);
}
