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
/// APP-01B.2 synthetic-only: atomic Dapper manifest load and non-posting B
/// quota preflight. Shares APP-01A's persisted command/idempotency namespace.
/// PhysicalStockBalance is custody; StockPool/StockRight remain entitlements.
/// </summary>
public sealed class App01b2CommandService(TenantConnections connections)
{
    private static CommandDefinition Sql(string text, object? args, IDbTransaction tx, CancellationToken ct) =>
        new(text, args, tx, cancellationToken: ct);

    private static bool ValidKey(string? key, int max) =>
        !string.IsNullOrWhiteSpace(key) && key.Length <= max && !key.Any(char.IsControl);

    private static bool ValidQuantity(decimal value) =>
        value > 0 && value <= 999999999999999.999m && decimal.Round(value, 3) == value;

    private static bool ValidAmount(decimal value) =>
        value >= 0 && value <= 999999999999999.9999m && decimal.Round(value, 4) == value;

    private sealed class ExistingCommand
    {
        public Guid Id { get; set; }
        public short PayloadHashVersion { get; set; }
        public byte[] PayloadHash { get; set; } = [];
        public string Status { get; set; } = "";
        public string OutcomeCode { get; set; } = "";
        public string? ReasonCode { get; set; }
    }

    internal sealed class Manifest
    {
        public Guid Id { get; set; }
        public Guid TripId { get; set; }
        public Guid WarehouseLocationId { get; set; }
        public Guid VehicleLocationId { get; set; }
        public string Status { get; set; } = "";
        public string TripStatus { get; set; } = "";
        public string WarehouseKind { get; set; } = "";
        public bool WarehouseActive { get; set; }
        public string VehicleKind { get; set; } = "";
        public bool VehicleActive { get; set; }
    }

    internal sealed class ManifestLine
    {
        public Guid Id { get; set; }
        public int LineNumber { get; set; }
        public Guid SkuId { get; set; }
        public Guid StockRightId { get; set; }
        public string RightKind { get; set; } = "";
        public decimal PlannedBaseQuantity { get; set; }
        public decimal LoadedBaseQuantity { get; set; }
        public Guid? LoadSourceLineId { get; set; }
    }

    private sealed class Right
    {
        public Guid Id { get; set; }
        public string RightKind { get; set; } = "";
        public string Status { get; set; } = "";
        public Guid TripId { get; set; }
        public Guid StockLocationId { get; set; }
        public Guid SkuId { get; set; }
        public decimal GrantedBaseQuantity { get; set; }
        public decimal ConsumedBaseQuantity { get; set; }
        public decimal ReleasedBaseQuantity { get; set; }
        public bool Eligible { get; set; }
        public bool SourceEligible { get; set; }
    }

    private sealed class TripState
    {
        public string Status { get; set; } = "";
        public Guid? VehicleId { get; set; }
        public string VehicleKind { get; set; } = "";
        public bool VehicleActive { get; set; }
    }

    internal sealed class PhysicalBalance
    {
        public decimal OnHandBaseQuantity { get; set; }
        public decimal OnHandCostAmount { get; set; }
    }

    internal sealed record SkuTransfer(
        Guid SkuId, decimal BaseQuantity, decimal CostAmount,
        IReadOnlyList<(ManifestLine Line, decimal Cost)> Lines);

    private async Task<BusinessCommandOutcome> ExecuteAsync(
        ClaimsPrincipal principal, IApp01aCommand request, string kind,
        (string Key, string Value)[] fields,
        Func<SqlConnection, SqlTransaction, string, string, Guid, CancellationToken,
            Task<BusinessCommandOutcome>> apply, CancellationToken ct)
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
        string requiredRole = kind == "LOAD_MANIFEST" ? "warehouse" : "sales";
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
        await using SqlConnection db = await connections.OpenSyntheticScratchAsync(principal, ct);
        await using SqlTransaction tx =
            (SqlTransaction)await db.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            // Lock by command identity BEFORE the read. A second request for the
            // same key blocks until the first commits, then sees its final row.
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

            var existing = await db.QuerySingleOrDefaultAsync<ExistingCommand>(Sql("""
                SELECT Id,PayloadHashVersion,PayloadHash,Status,OutcomeCode,ReasonCode
                FROM dbo.BusinessCommand WITH (UPDLOCK,HOLDLOCK)
                WHERE TenantId=@Tenant AND ProducerId=@Producer AND CommandId=@Command
                """, new { Tenant = tenant, Producer = request.ProducerId, Command = request.CommandId }, tx, ct));
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
                Guid? oldSource = await db.QuerySingleOrDefaultAsync<Guid?>(Sql("""
                    SELECT Id FROM dbo.BusinessSource
                    WHERE TenantId=@Tenant AND BusinessCommandId=@Command
                    """, new { Tenant = tenant, Command = existing.Id }, tx, ct));
                await tx.CommitAsync(ct);
                return new BusinessCommandOutcome(existing.Status, existing.OutcomeCode,
                    existing.ReasonCode, true, oldSource);
            }

            Guid commandRowId = Guid.NewGuid();
            await db.ExecuteAsync(Sql("""
                INSERT dbo.BusinessCommand(Id,TenantId,ProducerId,CommandId,
                    PayloadHashVersion,PayloadHash,ActorId,Status)
                VALUES(@Id,@Tenant,@Producer,@Command,@Version,@Hash,@Actor,N'RECEIVED')
                """, new
                {
                    Id = commandRowId, Tenant = tenant, Producer = request.ProducerId,
                    Command = request.CommandId, Version = CommandPayloadHash.Version, Hash = hash, Actor = actor
                }, tx, ct));

            BusinessCommandOutcome result = await apply(db, tx, tenant, actor, commandRowId, ct);
            if (result.Status is not ("APPLIED" or "REJECTED"))
                throw new InvalidOperationException("Only completed outcomes can be persisted.");
            await db.ExecuteAsync(Sql("""
                UPDATE dbo.BusinessCommand
                SET Status=@Status,OutcomeCode=@Outcome,ReasonCode=@Reason,
                    CompletedAtUtc=SYSUTCDATETIME()
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


    private static async Task<bool> LoadSourceUsed(SqlConnection db, SqlTransaction tx,
        string tenant, string sourceNumber, CancellationToken ct) =>
        (await db.QuerySingleOrDefaultAsync<Guid?>(Sql("""
            SELECT Id FROM dbo.BusinessSource WITH (UPDLOCK,HOLDLOCK)
            WHERE TenantId=@Tenant AND SourceKind=N'MANIFEST_LOAD' AND SourceNumber=@Number
            """, new { Tenant = tenant, Number = sourceNumber }, tx, ct))).HasValue;

    /// <summary>
    /// Posts every staged manifest line exactly once. A rejected command has only
    /// its completed BusinessCommand row; failure mid-post rolls back *all* rows.
    /// </summary>
    public Task<BusinessCommandOutcome> LoadManifestAsync(
        ClaimsPrincipal principal, LoadManifestCommand request, CancellationToken ct) =>
        ExecuteAsync(principal, request, "LOAD_MANIFEST",
            [("ManifestId", request.ManifestId.ToString("N"))],
            async (db, tx, tenant, actor, commandRowId, token) =>
            {
                if (request.ManifestId == Guid.Empty)
                    return BusinessCommandOutcome.Rejected("INVALID_REFERENCE");

                var manifest = await db.QuerySingleOrDefaultAsync<Manifest>(Sql("""
                    SELECT m.Id,m.TripId,m.WarehouseLocationId,m.VehicleLocationId,m.Status,
                        t.Status AS TripStatus, w.Kind AS WarehouseKind,
                        w.IsActive AS WarehouseActive, v.Kind AS VehicleKind,
                        v.IsActive AS VehicleActive
                    FROM dbo.TripManifest m WITH (UPDLOCK,HOLDLOCK)
                    JOIN dbo.Trip t WITH (UPDLOCK,HOLDLOCK)
                        ON t.TenantId=m.TenantId AND t.Id=m.TripId
                    JOIN dbo.StockLocation w WITH (HOLDLOCK)
                        ON w.TenantId=m.TenantId AND w.Id=m.WarehouseLocationId
                    JOIN dbo.StockLocation v WITH (HOLDLOCK)
                        ON v.TenantId=m.TenantId AND v.Id=m.VehicleLocationId
                    WHERE m.TenantId=@Tenant AND m.Id=@Manifest AND m.IsC3=0
                        AND NOT EXISTS(SELECT 1 FROM dbo.TripManifest c
                            WHERE c.TenantId=m.TenantId AND c.TripId=m.TripId AND c.IsC3=1)
                    """, new { Tenant = tenant, Manifest = request.ManifestId }, tx, token));
                if (manifest is null) return BusinessCommandOutcome.Rejected("MANIFEST_NOT_FOUND");
                if (manifest.Status != "DRAFT") return BusinessCommandOutcome.Rejected("MANIFEST_ALREADY_LOADED");
                if (manifest.TripStatus != "PREPARED" || !manifest.VehicleActive
                    || manifest.VehicleKind != "VEHICLE")
                    return BusinessCommandOutcome.Rejected("TRIP_NOT_PREPARED");
                if (manifest.WarehouseKind != "WAREHOUSE" || !manifest.WarehouseActive)
                    return BusinessCommandOutcome.Rejected("WAREHOUSE_NOT_ELIGIBLE");

                var lines = (await db.QueryAsync<ManifestLine>(Sql("""
                    SELECT Id,LineNumber,SkuId,StockRightId,RightKind,
                        PlannedBaseQuantity,LoadedBaseQuantity,LoadSourceLineId
                    FROM dbo.TripManifestLine WITH (UPDLOCK,HOLDLOCK)
                    WHERE TenantId=@Tenant AND TripManifestId=@Manifest
                    ORDER BY LineNumber
                    """, new { Tenant = tenant, Manifest = manifest.Id }, tx, token))).AsList();
                if (lines.Count == 0) return BusinessCommandOutcome.Rejected("MANIFEST_EMPTY");
                if (lines.Any(l => l.LoadSourceLineId is not null
                    || l.LoadedBaseQuantity != 0
                    || !ValidQuantity(l.PlannedBaseQuantity)))
                    return BusinessCommandOutcome.Rejected("MANIFEST_LINE_NOT_STAGED");

                // Rights are never debited for a mere physical custody transfer.
                // They must nevertheless be held, subtype-backed and available.
                foreach (var line in lines.OrderBy(l => l.StockRightId))
                {
                    var right = await db.QuerySingleOrDefaultAsync<Right>(Sql("""
                        SELECT r.Id,r.RightKind,r.Status,r.TripId,r.StockLocationId,r.SkuId,
                            r.GrantedBaseQuantity,r.ConsumedBaseQuantity,r.ReleasedBaseQuantity,
                            CAST(CASE WHEN allocation.SourceKind=N'STOCK_ALLOC'
                                AND allocation.TripId=r.TripId
                                AND allocationLine.SkuId=r.SkuId
                                AND allocationLine.BaseQuantity=r.GrantedBaseQuantity
                                THEN 1 ELSE 0 END AS bit) AS SourceEligible,
                            CAST(CASE
                                WHEN r.RightKind='A' AND ao.Status=N'CONFIRMED'
                                    AND ao.TripId=@Trip AND al.SkuId=r.SkuId
                                    AND NOT EXISTS (SELECT 1 FROM dbo.C2Confirmation c2
                                        WHERE c2.TenantId=ao.TenantId AND c2.OrderId=ao.Id)
                                    -- ConfirmAOrder requires the whole A-order line
                                    -- to be reserved. Recheck the *sum* (one line
                                    -- may have several independent A reservations)
                                    -- before the physical transfer, rather than
                                    -- accepting a changed post-confirmation quantity.
                                    AND al.BaseQuantity = (
                                        SELECT COALESCE(SUM(rAll.GrantedBaseQuantity), 0)
                                        FROM dbo.AReservation aAll
                                        JOIN dbo.StockRight rAll WITH (HOLDLOCK)
                                            ON rAll.TenantId=aAll.TenantId
                                            AND rAll.Id=aAll.StockRightId
                                        WHERE aAll.TenantId=r.TenantId
                                            AND aAll.AOrderLineId=al.Id
                                            AND rAll.RightKind='A'
                                            AND rAll.TripId=@Trip
                                            AND rAll.SkuId=al.SkuId
                                    ) THEN 1
                                WHEN r.RightKind='B' AND b.Id IS NOT NULL THEN 1
                                ELSE 0 END AS bit) AS Eligible
                        FROM dbo.StockRight r WITH (UPDLOCK,HOLDLOCK)
                        LEFT JOIN dbo.BusinessSourceLine allocationLine
                            ON allocationLine.TenantId=r.TenantId
                            AND allocationLine.Id=r.BusinessSourceLineId
                        LEFT JOIN dbo.BusinessSource allocation
                            ON allocation.TenantId=allocationLine.TenantId
                            AND allocation.Id=allocationLine.BusinessSourceId
                        LEFT JOIN dbo.AReservation a ON a.TenantId=r.TenantId
                            AND a.StockRightId=r.Id
                        LEFT JOIN dbo.AOrderLine al ON al.TenantId=a.TenantId
                            AND al.Id=a.AOrderLineId
                        LEFT JOIN dbo.AOrder ao ON ao.TenantId=al.TenantId
                            AND ao.Id=al.AOrderId
                        LEFT JOIN dbo.BQuota b ON b.TenantId=r.TenantId
                            AND b.StockRightId=r.Id
                        WHERE r.TenantId=@Tenant AND r.Id=@Right
                        """, new { Tenant = tenant, Right = line.StockRightId,
                            Trip = manifest.TripId }, tx, token));
                    if (right is null || right.TripId != manifest.TripId
                        || right.StockLocationId != manifest.WarehouseLocationId
                        || right.SkuId != line.SkuId || right.RightKind != line.RightKind
                        || !right.Eligible || !right.SourceEligible || right.Status != "HELD")
                        return BusinessCommandOutcome.Rejected("MANIFEST_RIGHT_NOT_ELIGIBLE");
                    if (right.GrantedBaseQuantity - right.ConsumedBaseQuantity
                        - right.ReleasedBaseQuantity < line.PlannedBaseQuantity)
                        return BusinessCommandOutcome.Rejected("MANIFEST_RIGHT_INSUFFICIENT");
                }

                // Lock physical resources in deterministic SKU order. Both opening
                // rows are mandatory; StockPool is NEVER decremented here.
                var transfers = new List<SkuTransfer>();
                foreach (var group in lines.GroupBy(l => l.SkuId).OrderBy(g => g.Key))
                {
                    var sku = group.Key;
                    bool skuActive = await db.ExecuteScalarAsync<bool>(Sql("""
                        SELECT IsActive FROM dbo.Sku WITH (HOLDLOCK)
                        WHERE TenantId=@Tenant AND Id=@Sku
                        """, new { Tenant = tenant, Sku = sku }, tx, token));
                    if (!skuActive) return BusinessCommandOutcome.Rejected("SKU_NOT_ELIGIBLE");

                    var warehouse = await db.QuerySingleOrDefaultAsync<PhysicalBalance>(Sql("""
                        SELECT OnHandBaseQuantity,OnHandCostAmount
                        FROM dbo.PhysicalStockBalance WITH (UPDLOCK,HOLDLOCK)
                        WHERE TenantId=@Tenant AND StockLocationId=@Location AND SkuId=@Sku
                        """, new { Tenant = tenant,
                            Location = manifest.WarehouseLocationId, Sku = sku }, tx, token));
                    var vehicle = await db.QuerySingleOrDefaultAsync<PhysicalBalance>(Sql("""
                        SELECT OnHandBaseQuantity,OnHandCostAmount
                        FROM dbo.PhysicalStockBalance WITH (UPDLOCK,HOLDLOCK)
                        WHERE TenantId=@Tenant AND StockLocationId=@Location AND SkuId=@Sku
                        """, new { Tenant = tenant,
                            Location = manifest.VehicleLocationId, Sku = sku }, tx, token));
                    if (warehouse is null || vehicle is null)
                        return BusinessCommandOutcome.Rejected("PHYSICAL_OPENING_REQUIRED");

                    decimal quantity = group.Sum(l => l.PlannedBaseQuantity);
                    if (warehouse.OnHandBaseQuantity < quantity)
                        return BusinessCommandOutcome.Rejected("PHYSICAL_STOCK_INSUFFICIENT");

                    transfers.Add(PriceTransfer(sku, warehouse, quantity, group));
                }

                if (await LoadSourceUsed(db, tx, tenant, request.SourceNumber, token))
                    return BusinessCommandOutcome.Rejected("SOURCE_ALREADY_USED");

                Guid sourceId = Guid.NewGuid();
                await db.ExecuteAsync(Sql("""
                    INSERT dbo.BusinessSource(Id,TenantId,BusinessCommandId,SourceKind,
                        SourceNumber,ActorId,TripId,OccurredAtUtc)
                    VALUES(@Id,@Tenant,@Command,N'MANIFEST_LOAD',@Number,
                        @Actor,@Trip,SYSUTCDATETIME())
                    """, new { Id = sourceId, Tenant = tenant, Command = commandRowId,
                        Number = request.SourceNumber, Actor = actor, Trip = manifest.TripId }, tx, token));

                await PostTransfers(db, tx, tenant, manifest, sourceId, transfers, token);

                return BusinessCommandOutcome.Applied("MANIFEST_LOADED", sourceId);
            }, ct);

    internal static SkuTransfer PriceTransfer(Guid sku, PhysicalBalance warehouse,
        decimal quantity, IEnumerable<ManifestLine> lines)
    {
        // SQL decimal(19,4) custody cost. Move exact remaining cost
        // on a full depletion; distribute rounded partial cost and
        // assign the residual deterministically to the last line.
        decimal cost = quantity == warehouse.OnHandBaseQuantity
            ? warehouse.OnHandCostAmount
            : decimal.Round((warehouse.OnHandCostAmount / warehouse.OnHandBaseQuantity)
                * quantity, 4, MidpointRounding.AwayFromZero);
        if (cost > warehouse.OnHandCostAmount) cost = warehouse.OnHandCostAmount;
        var ordered = lines.OrderBy(l => l.LineNumber).ToArray();
        var portions = new List<(ManifestLine Line, decimal Cost)>();
        decimal assigned = 0m;
        for (int i = 0; i < ordered.Length; i++)
        {
            decimal portion = i == ordered.Length - 1
                ? cost - assigned
                : decimal.Round((cost / quantity) * ordered[i].PlannedBaseQuantity,
                    4, MidpointRounding.AwayFromZero);
            if (portion > cost - assigned) portion = cost - assigned;
            portions.Add((ordered[i], portion));
            assigned += portion;
        }
        return new SkuTransfer(sku, quantity, cost, portions);
    }

    internal static async Task PostTransfers(SqlConnection db, SqlTransaction tx, string tenant,
        Manifest manifest, Guid sourceId, IEnumerable<SkuTransfer> transfers, CancellationToken token)
    {
        // Header must transition before its lines: the composite FK
        // requires each loaded line's source to belong to the header.
        if (await db.ExecuteAsync(Sql("""
            UPDATE dbo.TripManifest SET Status=N'LOADED',
                LoadBusinessSourceId=@Source,LoadedAtUtc=SYSUTCDATETIME()
            WHERE TenantId=@Tenant AND Id=@Manifest AND Status=N'DRAFT'
            """, new { Tenant = tenant, Manifest = manifest.Id, Source = sourceId }, tx, token)) != 1)
            throw new InvalidOperationException("Manifest changed under transactional lock.");

        foreach (var transfer in transfers)
        {
            // Paired, guarded updates: a physical movement is a transfer,
            // never an additional entitlement allocation or a sale.
            if (await db.ExecuteAsync(Sql("""
                UPDATE dbo.PhysicalStockBalance SET
                    OnHandBaseQuantity=OnHandBaseQuantity-@Quantity,
                    OnHandCostAmount=OnHandCostAmount-@Cost
                WHERE TenantId=@Tenant AND StockLocationId=@Location AND SkuId=@Sku
                    AND OnHandBaseQuantity>=@Quantity AND OnHandCostAmount>=@Cost
                """, new { Tenant = tenant, Location = manifest.WarehouseLocationId,
                    Sku = transfer.SkuId, Quantity = transfer.BaseQuantity,
                    Cost = transfer.CostAmount }, tx, token)) != 1)
                throw new InvalidOperationException("Warehouse balance changed under lock.");
            if (await db.ExecuteAsync(Sql("""
                UPDATE dbo.PhysicalStockBalance SET
                    OnHandBaseQuantity=OnHandBaseQuantity+@Quantity,
                    OnHandCostAmount=OnHandCostAmount+@Cost
                WHERE TenantId=@Tenant AND StockLocationId=@Location AND SkuId=@Sku
                """, new { Tenant = tenant, Location = manifest.VehicleLocationId,
                    Sku = transfer.SkuId, Quantity = transfer.BaseQuantity,
                    Cost = transfer.CostAmount }, tx, token)) != 1)
                throw new InvalidOperationException("Vehicle balance changed under lock.");

            foreach (var (line, lineCost) in transfer.Lines)
            {
                Guid sourceLine = Guid.NewGuid();
                await db.ExecuteAsync(Sql("""
                    INSERT dbo.BusinessSourceLine(Id,TenantId,BusinessSourceId,
                        LineNumber,SkuId,BaseQuantity,Amount)
                    VALUES(@Id,@Tenant,@Source,@Number,@Sku,@Quantity,@Cost)
                    """, new { Id = sourceLine, Tenant = tenant, Source = sourceId,
                        Number = line.LineNumber, Sku = line.SkuId,
                        Quantity = line.PlannedBaseQuantity, Cost = lineCost }, tx, token));

                if (await db.ExecuteAsync(Sql("""
                    UPDATE dbo.TripManifestLine SET LoadBusinessSourceId=@Source,
                        LoadSourceLineId=@SourceLine,
                        LoadedBaseQuantity=@Quantity,LoadedCostAmount=@Cost
                    WHERE TenantId=@Tenant AND Id=@Line AND LoadSourceLineId IS NULL
                        AND LoadedBaseQuantity=0 AND PlannedBaseQuantity=@Quantity
                    """, new { Tenant = tenant, Line = line.Id, Source = sourceId,
                        SourceLine = sourceLine, Quantity = line.PlannedBaseQuantity,
                        Cost = lineCost }, tx, token)) != 1)
                    throw new InvalidOperationException("Manifest line changed under lock.");

                await db.ExecuteAsync(Sql("""
                    INSERT dbo.PhysicalStockMovement(Id,TenantId,TripManifestLineId,
                        LoadSourceLineId,BaseQuantity,CostAmount)
                    VALUES(@Id,@Tenant,@Line,@SourceLine,@Quantity,@Cost)
                    """, new { Id = Guid.NewGuid(), Tenant = tenant, Line = line.Id,
                        SourceLine = sourceLine, Quantity = line.PlannedBaseQuantity,
                        Cost = lineCost }, tx, token));

                // Two balanced custody ledger legs, same immutable source.
                // Not COGS, revenue, cash, AR or a receipt.
                foreach (var leg in new[]
                {
                    (Key: "PHYSICAL-OUT", Location: manifest.WarehouseLocationId, Sign: -1m),
                    (Key: "PHYSICAL-IN", Location: manifest.VehicleLocationId, Sign: 1m)
                })
                    await db.ExecuteAsync(Sql("""
                        INSERT dbo.LedgerEffect(Id,TenantId,BusinessSourceLineId,
                            EffectKey,LedgerKind,SkuId,StockLocationId,
                            QuantityDelta,AmountDelta)
                        VALUES(@Id,@Tenant,@SourceLine,@Key,N'PHYSICAL_STOCK',
                            @Sku,@Location,@Quantity,@Cost)
                        """, new { Id = Guid.NewGuid(), Tenant = tenant,
                            SourceLine = sourceLine, leg.Key, Sku = line.SkuId,
                            leg.Location, Quantity = leg.Sign * line.PlannedBaseQuantity,
                            Cost = leg.Sign * lineCost }, tx, token));
            }
        }
    }

    /// <summary>
    /// APP-01B T2 preflight only. No sale/cash receipt is ever created here.
    /// Even a within-quota request is rejected until APP-01C is implemented.
    /// </summary>
    public Task<BusinessCommandOutcome> PreflightBSaleAsync(
        ClaimsPrincipal principal, PreflightBSaleCommand request, CancellationToken ct) =>
        ExecuteAsync(principal, request, "PREFLIGHT_B_SALE",
            [
                ("TripId", request.TripId.ToString("N")),
                ("SkuId", request.SkuId.ToString("N")),
                ("BaseQuantity", CommandPayloadHash.Decimal(request.BaseQuantity)),
                ("UnitPrice", CommandPayloadHash.Decimal(request.UnitPrice)),
                ("OfferedCashAmount", CommandPayloadHash.Decimal(request.OfferedCashAmount))
            ],
            async (db, tx, tenant, actor, commandRowId, token) =>
            {
                if (request.TripId == Guid.Empty || request.SkuId == Guid.Empty)
                    return BusinessCommandOutcome.Rejected("INVALID_REFERENCE");
                if (!ValidQuantity(request.BaseQuantity)
                    || !ValidAmount(request.UnitPrice) || !ValidAmount(request.OfferedCashAmount))
                    return BusinessCommandOutcome.Rejected("AMOUNT_OR_QUANTITY_INVALID");
                decimal expectedOffer;
                try { expectedOffer = checked(request.BaseQuantity * request.UnitPrice); }
                catch (OverflowException)
                {
                    return BusinessCommandOutcome.Rejected("AMOUNT_OR_QUANTITY_INVALID");
                }
                if (expectedOffer != request.OfferedCashAmount)
                    return BusinessCommandOutcome.Rejected("OFFER_AMOUNT_MISMATCH");

                var trip = await db.QuerySingleOrDefaultAsync<TripState>(Sql("""
                    SELECT t.Status,t.VehicleLocationId AS VehicleId,
                        v.Kind AS VehicleKind,v.IsActive AS VehicleActive
                    FROM dbo.Trip t WITH (UPDLOCK,HOLDLOCK)
                    LEFT JOIN dbo.StockLocation v WITH (HOLDLOCK)
                        ON v.TenantId=t.TenantId AND v.Id=t.VehicleLocationId
                    WHERE t.TenantId=@Tenant AND t.Id=@Trip
                    """, new { Tenant = tenant, Trip = request.TripId }, tx, token));
                if (trip is null || trip.VehicleId is null || trip.Status != "PREPARED"
                    || trip.VehicleKind != "VEHICLE" || !trip.VehicleActive)
                    return BusinessCommandOutcome.Rejected("TRIP_NOT_PREPARED");

                bool loaded = await db.ExecuteScalarAsync<bool>(Sql("""
                    SELECT CAST(CASE WHEN EXISTS (
                        SELECT 1 FROM dbo.TripManifest WITH (UPDLOCK,HOLDLOCK)
                        WHERE TenantId=@Tenant AND TripId=@Trip AND Status=N'LOADED' AND IsC3=0
                    ) THEN 1 ELSE 0 END AS bit)
                    """, new { Tenant = tenant, Trip = request.TripId }, tx, token));
                if (!loaded) return BusinessCommandOutcome.Rejected("MANIFEST_NOT_LOADED");

                decimal available = await db.ExecuteScalarAsync<decimal>(Sql("""
                    SELECT COALESCE(SUM(CASE WHEN l.LoadedBaseQuantity >
                        r.ConsumedBaseQuantity+r.ReleasedBaseQuantity
                        THEN l.LoadedBaseQuantity-r.ConsumedBaseQuantity-r.ReleasedBaseQuantity
                        ELSE CONVERT(decimal(18,3),0) END),0)
                    FROM dbo.TripManifestLine l WITH (UPDLOCK,HOLDLOCK)
                    JOIN dbo.TripManifest m ON m.TenantId=l.TenantId
                        AND m.Id=l.TripManifestId AND m.Status=N'LOADED' AND m.IsC3=0
                    JOIN dbo.StockRight r WITH (UPDLOCK,HOLDLOCK)
                        ON r.TenantId=l.TenantId AND r.Id=l.StockRightId
                        AND r.RightKind='B' AND r.Status=N'HELD'
                    JOIN dbo.BQuota b ON b.TenantId=r.TenantId AND b.StockRightId=r.Id
                    WHERE l.TenantId=@Tenant AND l.TripId=@Trip AND l.SkuId=@Sku
                        AND l.RightKind='B' AND l.LoadSourceLineId IS NOT NULL
                    """, new { Tenant = tenant, Trip = request.TripId, Sku = request.SkuId }, tx, token));
                if (available < request.BaseQuantity)
                    return new BusinessCommandOutcome("REJECTED", "REJECTED_QUOTA",
                        "B_QUOTA_INSUFFICIENT", false, null);

                // Deliberate fail-closed non-posting path until APP-01C.
                return BusinessCommandOutcome.Rejected("B_SALE_NOT_IMPLEMENTED");
            }, ct);
}
