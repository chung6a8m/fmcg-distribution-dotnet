using System.Data;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Dapper;
using Microsoft.Data.SqlClient;
using static FmcgDms.Api.Persistence.App01b2CommandService;

namespace FmcgDms.Api.Persistence;

public sealed record C3LoadLine(Guid RightId,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] decimal ExpectedWarehouseHeld,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] decimal Quantity);
public sealed record C3LoadInput(Guid CommandId, Guid TripId, C3LoadLine?[]? Lines);
public sealed record C3TripView(Guid Id, string Code);
public sealed record C3RightView(Guid Id, Guid? OrderId, Guid WarehouseId, string WarehouseCode, Guid SkuId,
    string SkuCode, string BaseUom,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] decimal Granted,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] decimal Loaded,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] decimal WarehouseHeld)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<F03LotView>? Lots { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonNumberHandling(JsonNumberHandling.WriteAsString)]
    public decimal? EligibleWarehouseHeld { get; init; }
}

public sealed partial class App01a2CommandService
{
    private const string ManifestLoadSource = "MANIFEST_LOAD";

    public Task<BusinessCommandOutcome> LoadC3(ClaimsPrincipal principal, C3LoadInput input, CancellationToken ct) =>
        LoadManifest(principal, input, ct, d1Load: false);

    public Task<BusinessCommandOutcome> LoadD1(ClaimsPrincipal principal, C3LoadInput input, CancellationToken ct)
    {
        RequireD1Mode();
        return LoadManifest(principal, input, ct, d1Load: true);
    }

    private Task<BusinessCommandOutcome> LoadManifest(ClaimsPrincipal principal, C3LoadInput input,
        CancellationToken ct, bool d1Load)
    {
        const int maxLines = 100;
        if (input.Lines is { Length: > maxLines })
            return Task.FromResult(BusinessCommandOutcome.Rejected("INVALID_LOAD_SHAPE"));
        var lines = (input.Lines ?? []).OfType<C3LoadLine>().OrderBy(l => l.RightId).ToArray();
        bool validShape = input.Lines is { Length: > 0 } && input.Lines.Length == lines.Length;
        var key = C2Key(principal, input.CommandId);
        return ExecuteAsync(principal, key, d1Load ? "D1_LOAD_MANIFEST" : "C3_LOAD_MANIFEST",
            [("Trip", input.TripId.ToString("N")),
             ("SubmittedLines", input.Lines?.Length.ToString(CultureInfo.InvariantCulture) ?? "missing"),
             ..lines.SelectMany((l, i) => new[]
            {
                ($"Right{i}", l.RightId.ToString("N")),
                ($"Held{i}", CommandPayloadHash.Decimal(l.ExpectedWarehouseHeld)),
                ($"Quantity{i}", CommandPayloadHash.Decimal(l.Quantity))
            })],
            (db, tx, tenant, actor, command, token) =>
                validShape
                    ? ApplyPartialLoad(db, tx, tenant, actor, command, key.SourceNumber, input.TripId, lines, d1Load,
                        connections.UsesDevF03, token)
                    : Task.FromResult(BusinessCommandOutcome.Rejected("INVALID_LOAD_LINES")),
            ct, "warehouse", input.TripId, d1Load: d1Load);
    }

    private static async Task<BusinessCommandOutcome> ApplyPartialLoad(SqlConnection db, SqlTransaction tx,
        string tenant, string actor, Guid command, string number, Guid tripId, C3LoadLine[] lines, bool d1Load, bool lots,
        CancellationToken ct)
    {
        if (lines.Select(l => l.RightId).Distinct().Count() != lines.Length
            || lines.Any(l => l.RightId == Guid.Empty || !ValidQuantity(l.Quantity)
                || !ValidQuantity(l.ExpectedWarehouseHeld) || l.Quantity > l.ExpectedWarehouseHeld))
            return BusinessCommandOutcome.Rejected("INVALID_LOAD_LINES");
        var trip = await FindTrip(db, tx, tenant, tripId, ct);
        if (!IsPreparedTrip(trip)) return BusinessCommandOutcome.Rejected("TRIP_NOT_PREPARED");
        if (await HasLegacyManifest(db, tx, tenant, tripId, ct))
            return BusinessCommandOutcome.Rejected("LEGACY_MANIFEST_EXISTS");
        if (await SourceUsed(db, tx, tenant, ManifestLoadSource, number, ct))
            return BusinessCommandOutcome.Rejected("SOURCE_ALREADY_USED");

        Guid[] rightIds = lines.Select(l => l.RightId).ToArray();
        var rights = (d1Load
                ? await EligibleD1Rights(db, tx, tenant, tripId, rightIds, ct)
                : await EligibleC3Rights(db, tx, tenant, [tripId], rightIds, ct))
            .ToDictionary(right => right.Id);
        if (rights.Count != lines.Length)
            return BusinessCommandOutcome.Rejected(d1Load ? "D1_QUOTA_NOT_ELIGIBLE" : "C3_RESERVATION_NOT_ELIGIBLE");
        if (lines.Any(l => l.ExpectedWarehouseHeld != rights[l.RightId].WarehouseHeld))
            return BusinessCommandOutcome.Rejected("C3_STALE_WAREHOUSE_HELD");
        Guid warehouse = rights[lines[0].RightId].WarehouseId;
        if (rights.Values.Any(r => r.WarehouseId != warehouse))
            return BusinessCommandOutcome.Rejected("C3_MIXED_WAREHOUSES");

        var manifest = new Manifest
        {
            Id = Guid.NewGuid(), TripId = tripId, WarehouseLocationId = warehouse,
            VehicleLocationId = trip!.VehicleLocationId!.Value
        };
        var manifestLines = lines.Select((l, i) => new ManifestLine
        {
            Id = Guid.NewGuid(), LineNumber = i + 1, SkuId = rights[l.RightId].SkuId,
            StockRightId = l.RightId, RightKind = d1Load ? "B" : "A", PlannedBaseQuantity = l.Quantity
        }).ToArray();
        var transfers = new List<SkuTransfer>();
        foreach (var group in manifestLines.GroupBy(l => l.SkuId).OrderBy(g => g.Key))
        {
            decimal quantity = group.Sum(l => l.PlannedBaseQuantity);
            var main = await PhysicalC3Balance(db, tx, tenant, warehouse, group.Key, ct);
            var vehicle = await PhysicalC3Balance(db, tx, tenant, manifest.VehicleLocationId, group.Key, ct);
            if (main is null || vehicle is null) return BusinessCommandOutcome.Rejected("PHYSICAL_OPENING_REQUIRED");
            var pool = await db.QuerySingleOrDefaultAsync<StockBalance>(Sql("""
                SELECT OnHandBaseQuantity,AllocatedBaseQuantity FROM dbo.StockPool WITH (UPDLOCK,HOLDLOCK)
                WHERE TenantId=@Tenant AND StockLocationId=@Warehouse AND SkuId=@Sku
                """, new { Tenant = tenant, Warehouse = warehouse, Sku = group.Key }, tx, ct));
            if (pool is null || pool.OnHandBaseQuantity != main.OnHandBaseQuantity)
                return BusinessCommandOutcome.Rejected("C3_WAREHOUSE_COUNTER_MISMATCH");
            if (pool.AllocatedBaseQuantity < quantity || main.OnHandBaseQuantity < quantity)
                return BusinessCommandOutcome.Rejected("C3_WAREHOUSE_STOCK_INSUFFICIENT");
            var transfer = PriceTransfer(group.Key, main, quantity, group);
            if (!ValidQuantity(vehicle.OnHandBaseQuantity + quantity)
                || vehicle.OnHandCostAmount + transfer.CostAmount > 999999999999999.9999m)
                return BusinessCommandOutcome.Rejected("C3_VEHICLE_BALANCE_LIMIT");
            transfers.Add(transfer);
        }

        var lotPicks = new Dictionary<Guid, IReadOnlyList<F03Pick>>();
        if (lots)
        {
            foreach (var line in manifestLines)
            {
                if (!await IsLotTracked(db, tx, tenant, line.StockRightId, byRight: true, ct)) continue;
                var picks = await PlanLotLoad(db, tx, tenant, line.StockRightId, line.PlannedBaseQuantity, ct);
                if (picks.Count == 0) return BusinessCommandOutcome.Rejected(LotUnavailable);
                lotPicks.Add(line.Id, picks);
            }
        }
        Guid source = await AddSource(db, tx, tenant, command, ManifestLoadSource, number, actor, null, tripId, ct);
        await db.ExecuteAsync(Sql("""
            INSERT dbo.TripManifest(Id,TenantId,ManifestNumber,TripId,WarehouseLocationId,VehicleLocationId,IsC3)
            VALUES(@Id,@Tenant,@Number,@TripId,@WarehouseLocationId,@VehicleLocationId,1)
            """, new { manifest.Id, Tenant = tenant, Number = number, manifest.TripId,
                manifest.WarehouseLocationId, manifest.VehicleLocationId }, tx, ct));
        foreach (var line in manifestLines)
            await db.ExecuteAsync(Sql("""
                INSERT dbo.TripManifestLine(Id,TenantId,TripManifestId,TripId,WarehouseLocationId,VehicleLocationId,
                    LineNumber,SkuId,StockRightId,RightKind,PlannedBaseQuantity,IsC3)
                VALUES(@Id,@Tenant,@Manifest,@TripId,@WarehouseLocationId,@VehicleLocationId,
                    @LineNumber,@SkuId,@StockRightId,@RightKind,@PlannedBaseQuantity,1)
                """, new { line.Id, Tenant = tenant, Manifest = manifest.Id, manifest.TripId, manifest.WarehouseLocationId,
                    manifest.VehicleLocationId, line.LineNumber, line.SkuId, line.StockRightId, line.RightKind,
                    line.PlannedBaseQuantity }, tx, ct));
        foreach (var transfer in transfers)
        {
            int changed = await db.ExecuteAsync(Sql("""
                UPDATE dbo.StockPool SET OnHandBaseQuantity=OnHandBaseQuantity-@BaseQuantity,
                    AllocatedBaseQuantity=AllocatedBaseQuantity-@BaseQuantity
                WHERE TenantId=@Tenant AND StockLocationId=@Warehouse AND SkuId=@SkuId
                    AND AllocatedBaseQuantity>=@BaseQuantity
                """, new { Tenant = tenant, Warehouse = warehouse, transfer.SkuId, transfer.BaseQuantity }, tx, ct));
            if (changed != 1) throw new InvalidOperationException("Warehouse pool changed under lock.");
        }
        await PostTransfers(db, tx, tenant, manifest, source, transfers, ct);
        foreach (var (lineId, picks) in lotPicks)
            await RecordLotPicks(db, tx, tenant, lineId, picks, "F03LotLoad", "ManifestLineId", ct);
        return BusinessCommandOutcome.Applied("MANIFEST_LOADED", source);
    }

    private static Task<PhysicalBalance?> PhysicalC3Balance(SqlConnection db, SqlTransaction tx,
        string tenant, Guid location, Guid sku, CancellationToken ct) =>
        db.QuerySingleOrDefaultAsync<PhysicalBalance>(Sql("""
            SELECT OnHandBaseQuantity,OnHandCostAmount
            FROM dbo.PhysicalStockBalance WITH (UPDLOCK,HOLDLOCK)
            WHERE TenantId=@Tenant AND StockLocationId=@Location AND SkuId=@Sku
            """, new { Tenant = tenant, Location = location, Sku = sku }, tx, ct));

    private static Task<bool> HasLegacyManifest(SqlConnection db, SqlTransaction tx, string tenant, Guid trip, CancellationToken ct) =>
        db.ExecuteScalarAsync<bool>(Sql("""
            SELECT CONVERT(bit,CASE WHEN EXISTS(SELECT 1 FROM dbo.TripManifest
                WHERE TenantId=@Tenant AND TripId=@Trip AND IsC3=0) THEN 1 ELSE 0 END)
            """, new { Tenant = tenant, Trip = trip }, tx, ct));

    private static Task<IEnumerable<C3RightView>> EligibleC3Rights(SqlConnection db, SqlTransaction tx,
        string tenant, Guid[] trips, Guid[]? rightIds, CancellationToken ct)
    {
        const string sql = """
            SELECT r.Id,o.Id AS OrderId,r.StockLocationId AS WarehouseId,w.Code AS WarehouseCode,
                r.SkuId,sku.Code AS SkuCode,p.BaseUom,r.GrantedBaseQuantity AS Granted,
                totals.Loaded,r.GrantedBaseQuantity-totals.Loaded AS WarehouseHeld
            FROM dbo.StockRight r
            JOIN dbo.AReservation a ON a.TenantId=r.TenantId AND a.StockRightId=r.Id
            JOIN dbo.AOrderLine ol ON ol.TenantId=a.TenantId AND ol.Id=a.AOrderLineId AND ol.SkuId=r.SkuId
            JOIN dbo.AOrder o ON o.TenantId=ol.TenantId AND o.Id=ol.AOrderId AND o.TripId=r.TripId
            JOIN dbo.C2Confirmation c ON c.TenantId=o.TenantId AND c.OrderId=o.Id
            JOIN dbo.AConfirmedLineSnapshot s ON s.TenantId=c.TenantId AND s.AOrderId=c.OrderId
                AND s.AOrderLineId=ol.Id AND s.ConfirmBusinessSourceId=c.ConfirmSourceId AND s.SkuId=r.SkuId
            JOIN dbo.C2LinePolicy p ON p.TenantId=s.TenantId AND p.OrderLineId=s.AOrderLineId
            JOIN dbo.Sku sku ON sku.TenantId=r.TenantId AND sku.Id=r.SkuId AND sku.IsActive=1
                AND CONVERT(varbinary(48),sku.BaseUom)=CONVERT(varbinary(48),p.BaseUom)
            JOIN dbo.StockLocation w ON w.TenantId=r.TenantId AND w.Id=r.StockLocationId AND w.Kind='WAREHOUSE' AND w.IsActive=1
            JOIN dbo.BusinessSourceLine sl ON sl.TenantId=r.TenantId AND sl.Id=r.BusinessSourceLineId
                AND sl.SkuId=r.SkuId AND sl.BaseQuantity=r.GrantedBaseQuantity
            JOIN dbo.BusinessSource bs ON bs.TenantId=sl.TenantId AND bs.Id=sl.BusinessSourceId
                AND bs.SourceKind='STOCK_ALLOC' AND bs.TripId=r.TripId AND bs.CustomerId=o.CustomerId
            JOIN dbo.BusinessCommand bc ON bc.TenantId=bs.TenantId AND bc.Id=bs.BusinessCommandId AND bc.Status='APPLIED'
            JOIN dbo.C2CommandScope cs ON cs.TenantId=bc.TenantId AND cs.CommandRowId=bc.Id
                AND cs.RoleCode='warehouse' AND cs.LoadTripId IS NULL
            CROSS APPLY (
                SELECT COALESCE(SUM(l.LoadedBaseQuantity),0) AS Loaded
                FROM dbo.TripManifestLine l
                JOIN dbo.PhysicalStockMovement m ON m.TenantId=l.TenantId AND m.TripManifestLineId=l.Id
                WHERE l.TenantId=r.TenantId AND l.StockRightId=r.Id AND l.IsC3=1
            ) totals
            WHERE r.TenantId=@Tenant AND r.TripId IN @Trips AND r.RightKind='A' AND r.Status='HELD'
                AND r.ReleasedBaseQuantity=0 AND o.Status IN ('CONFIRMED','PARTIALLY_DELIVERED')
                AND r.ConsumedBaseQuantity<=totals.Loaded AND totals.Loaded<=r.GrantedBaseQuantity
            """;
        string rightFilter = rightIds is null ? "" : " AND r.Id IN @Ids";
        return db.QueryAsync<C3RightView>(Sql(sql + rightFilter + " ORDER BY w.Code,sku.Code,r.Id",
            new { Tenant = tenant, Trips = trips, Ids = rightIds }, tx, ct));
    }
}
