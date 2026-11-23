using System.Data;
using System.Security.Claims;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Persistence;

public sealed record F05Command(Guid CommandId, string? Action, Guid? CaseId, Guid? DeliveryId, Guid? LotId,
    decimal? Quantity, string? Reason, string? Evidence);
public sealed record F05Identity(string TenantId, string ActorId, string SessionId, string Role);
public sealed record F05DeliveryView(Guid DeliveryId, Guid LotId, string LotCode, string Customer, string Sku,
    decimal Issued, decimal Available);
public sealed record F05CaseView(Guid Id, Guid DeliveryId, Guid LotId, string LotCode, string Customer, string Sku,
    decimal Quantity, string State, decimal? ReleasedCost);

public sealed partial class App01a2CommandService
{
    private sealed record F05Source(Guid DeliveryId, Guid LotId, Guid TripId, Guid CustomerId, Guid SkuId,
        Guid StockRightId, Guid WarehouseId, decimal IssuedForLot, decimal Issued, decimal Cost,
        DateTime BusinessDate, DateTime ExpiresOn);
    private sealed record F05Case(Guid Id, Guid DeliveryId, Guid LotId, decimal Quantity, string RequestedBy);
    private sealed record F05Stage(string Stage, string ActorId, Guid? DestinationId);
    private sealed record F05Stock(decimal OnHandBaseQuantity, decimal OnHandCostAmount);
    private sealed record F05Stored(string Status, string OutcomeCode, string? ReasonCode, Guid? SourceId, string RoleCode);
    private static readonly string[] ReturnRoles = ["sales", "warehouse", "finance", "case_checker"];

    private static string? ReturnActionRole(string? action) => action switch
    {
        "REQUEST" => "sales", "RECEIVE" => "warehouse", "APPROVE" => "finance", "RELEASE" => "case_checker", _ => null
    };

    private static C2CommandKey ReturnKey(ClaimsPrincipal principal, Guid commandId)
    {
        var key = C2Key(principal, commandId);
        return new("F05-" + key.ProducerId[3..], key.CommandId, "F05-" + key.SourceNumber[3..]);
    }

    public Task<BusinessCommandOutcome> RecordReturn(ClaimsPrincipal principal, F05Command input, CancellationToken ct)
    {
        string? role = ReturnActionRole(input.Action);
        if (!connections.UsesDevF05) throw new UnauthorizedAccessException("DEV-F05 disabled.");
        if (role is null) return Task.FromResult(BusinessCommandOutcome.Rejected("F05_FIELDS_INVALID"));
        var key = ReturnKey(principal, input.CommandId);
        return ExecuteAsync(principal, key, "F05_" + input.Action,
            [("Request", JsonSerializer.Serialize(input, C1Validation.Json))],
            (db, tx, tenant, actor, command, token) => ApplyReturn(db, tx, tenant, actor, command,
                key.SourceNumber, input, token), ct, role, isF05Return: true);
    }

    public Task<F05Identity> ReturnIdentity(ClaimsPrincipal principal, CancellationToken ct) =>
        ReadF05(principal, async (db, tx, tenant, actor, role) => new F05Identity(tenant, actor,
            await db.QuerySingleAsync<string>(Sql("SELECT Nonce FROM dbo.App00ScratchMarker", null, tx, ct)), role), ct);

    public Task<IEnumerable<F05DeliveryView>> ReturnableDeliveries(ClaimsPrincipal principal, CancellationToken ct) =>
        ReadF05(principal, (db, tx, tenant, actor, role) => db.QueryAsync<F05DeliveryView>(Sql("""
            SELECT d.Id AS DeliveryId,i.LotId,l.Code AS LotCode,c.Name AS Customer,k.Code AS Sku,
                   i.Quantity AS Issued,i.Quantity-r.Used AS Available
            FROM dbo.SaleDelivery d JOIN dbo.F03LotIssue i ON i.TenantId=d.TenantId AND i.DeliveryId=d.Id
            JOIN dbo.F03Lot l ON l.TenantId=i.TenantId AND l.Id=i.LotId
            JOIN dbo.F04DeliveryPricing p ON p.TenantId=d.TenantId AND p.DeliveryId=d.Id
            JOIN dbo.Customer c ON c.TenantId=d.TenantId AND c.Id=d.CustomerId
            JOIN dbo.Sku k ON k.TenantId=d.TenantId AND k.Id=d.SkuId
            CROSS APPLY (SELECT COALESCE(SUM(x.Quantity),0) AS Used FROM dbo.F05ReturnCase x
                WHERE x.TenantId=d.TenantId AND x.DeliveryId=d.Id AND x.LotId=i.LotId) r
            WHERE d.TenantId=@Tenant AND d.RightKind='A' AND p.Currency='VND'
                AND (@Role<>'sales' OR EXISTS (
                    SELECT 1 FROM dbo.C4DeliveryWriter w WHERE w.TenantId=d.TenantId AND w.TripId=d.TripId AND w.ActorId=@Actor))
            ORDER BY d.PostedAtUtc DESC,d.Id,l.ExpiresOn,l.Code
            """, new { Tenant = tenant, Actor = actor, Role = role }, tx, ct)), ct);

    public Task<IEnumerable<F05CaseView>> ReadReturnCases(ClaimsPrincipal principal, CancellationToken ct) =>
        ReadF05(principal, (db, tx, tenant, actor, role) => db.QueryAsync<F05CaseView>(Sql("""
            SELECT x.Id,x.DeliveryId,x.LotId,l.Code AS LotCode,c.Name AS Customer,k.Code AS Sku,x.Quantity,
                   CASE WHEN released.SourceId IS NOT NULL THEN 'RELEASED'
                        WHEN approved.SourceId IS NOT NULL THEN 'APPROVED'
                        WHEN received.SourceId IS NOT NULL THEN 'QUARANTINED' ELSE 'REQUESTED' END AS State,
                   released.AcceptedCost AS ReleasedCost
            FROM dbo.F05ReturnCase x JOIN dbo.SaleDelivery d ON d.TenantId=x.TenantId AND d.Id=x.DeliveryId
            JOIN dbo.F03Lot l ON l.TenantId=x.TenantId AND l.Id=x.LotId
            JOIN dbo.Customer c ON c.TenantId=d.TenantId AND c.Id=d.CustomerId
            JOIN dbo.Sku k ON k.TenantId=d.TenantId AND k.Id=d.SkuId
            LEFT JOIN dbo.F05ReturnStage released ON released.TenantId=x.TenantId AND released.CaseId=x.Id AND released.Stage='RELEASE'
            LEFT JOIN dbo.F05ReturnStage approved ON approved.TenantId=x.TenantId AND approved.CaseId=x.Id AND approved.Stage='APPROVE'
            LEFT JOIN dbo.F05ReturnStage received ON received.TenantId=x.TenantId AND received.CaseId=x.Id AND received.Stage='RECEIVE'
            WHERE x.TenantId=@Tenant AND (@Role<>'sales' OR EXISTS (
                SELECT 1 FROM dbo.C4DeliveryWriter w WHERE w.TenantId=d.TenantId AND w.TripId=d.TripId AND w.ActorId=@Actor))
            ORDER BY d.PostedAtUtc DESC,x.Id
            """, new { Tenant = tenant, Actor = actor, Role = role }, tx, ct)), ct);

    public Task<BusinessCommandOutcome?> ReturnOutcome(ClaimsPrincipal principal, Guid id, CancellationToken ct) =>
        ReadF05<BusinessCommandOutcome?>(principal, async (db, tx, tenant, actor, _) =>
        {
            var key = ReturnKey(principal, id);
            var result = await db.QuerySingleOrDefaultAsync<F05Stored>(Sql("""
                SELECT b.Status,b.OutcomeCode,b.ReasonCode,s.Id AS SourceId,scope.RoleCode
                FROM dbo.BusinessCommand b
                JOIN dbo.C2CommandScope scope ON scope.TenantId=b.TenantId AND scope.CommandRowId=b.Id AND scope.IsF05Return=1
                LEFT JOIN dbo.BusinessSource s ON s.TenantId=b.TenantId AND s.BusinessCommandId=b.Id
                WHERE b.TenantId=@Tenant AND b.ActorId=@Actor AND b.ProducerId=@ProducerId AND b.CommandId=@CommandId
                    AND b.Status IN ('APPLIED','REJECTED')
                """, new { Tenant = tenant, Actor = actor, key.ProducerId, key.CommandId }, tx, ct));
            if (result is null) return null;
            if (!principal.HasClaim("synthetic_role", result.RoleCode))
                throw new UnauthorizedAccessException("Return command role no longer assigned.");
            await App02a3Sql.RequireRole(db, tx, tenant, actor, result.RoleCode, ct);
            if (result.RoleCode == "sales" && result.SourceId is Guid caseId)
                await RequireReturnRequestAssignment(db, tx, tenant, actor, caseId, ct);
            return new BusinessCommandOutcome(result.Status, result.OutcomeCode, result.ReasonCode, true, result.SourceId);
        }, ct);

    private async Task<T> ReadF05<T>(ClaimsPrincipal principal,
        Func<SqlConnection, SqlTransaction, string, string, string, Task<T>> read, CancellationToken ct)
    {
        if (!connections.UsesDevF05) throw new UnauthorizedAccessException("DEV-F05 disabled.");
        string tenant = App02a3Sql.Tenant(principal);
        string actor = App02a3Sql.Actor(principal);
        await using var db = await connections.OpenApp02c2Async(principal, ct);
        await using var tx = (SqlTransaction)await db.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var active = (await db.QueryAsync<string>(Sql("""
            SELECT RoleCode FROM dbo.App02ActorRoleGrant WHERE TenantId=@Tenant AND ActorId=@Actor AND IsActive=1
            """, new { Tenant = tenant, Actor = actor }, tx, ct))).ToHashSet(StringComparer.Ordinal);
        string role = ReturnRoles.FirstOrDefault(role => active.Contains(role) && principal.HasClaim("synthetic_role", role))
            ?? throw new UnauthorizedAccessException("Current returns role required.");
        var result = await read(db, tx, tenant, actor, role);
        await tx.CommitAsync(ct);
        return result;
    }

    private static async Task RequireReturnRequestAssignment(SqlConnection db, SqlTransaction tx,
        string tenant, string actor, Guid caseId, CancellationToken ct)
    {
        if (await db.ExecuteScalarAsync<int>(Sql("""
            SELECT COUNT(*) FROM dbo.F05ReturnCase r
            JOIN dbo.SaleDelivery d ON d.TenantId=r.TenantId AND d.Id=r.DeliveryId
            JOIN dbo.C4DeliveryWriter w ON w.TenantId=d.TenantId AND w.TripId=d.TripId AND w.ActorId=@Actor
            WHERE r.TenantId=@Tenant AND r.Id=@CaseId
            """, new { Tenant = tenant, Actor = actor, CaseId = caseId }, tx, ct)) != 1)
            throw new UnauthorizedAccessException("Current delivery assignment required.");
    }

    private static Task<F05Source?> FindReturnSource(SqlConnection db, SqlTransaction tx, string tenant,
        Guid delivery, Guid lot, CancellationToken ct) => db.QuerySingleOrDefaultAsync<F05Source>(Sql("""
            SELECT d.Id AS DeliveryId,i.LotId,d.TripId,d.CustomerId,d.SkuId,d.StockRightId,
                   r.StockLocationId AS WarehouseId,i.Quantity AS IssuedForLot,d.BaseQuantity AS Issued,d.CostAmount AS Cost,
                   d.BusinessDate,l.ExpiresOn
            FROM dbo.SaleDelivery d
            JOIN dbo.F03LotIssue i ON i.TenantId=d.TenantId AND i.DeliveryId=d.Id
            JOIN dbo.F03Lot l ON l.TenantId=i.TenantId AND l.Id=i.LotId AND l.SkuId=d.SkuId
            JOIN dbo.F04DeliveryPricing p ON p.TenantId=d.TenantId AND p.DeliveryId=d.Id AND p.Currency='VND'
            JOIN dbo.StockRight r ON r.TenantId=d.TenantId AND r.Id=d.StockRightId
            WHERE d.TenantId=@Tenant AND d.Id=@Delivery AND i.LotId=@Lot AND d.RightKind='A'
                AND d.CostAmount=ROUND(d.CostAmount,0)
            """, new { Tenant = tenant, Delivery = delivery, Lot = lot }, tx, ct));

    private static async Task<BusinessCommandOutcome> ApplyReturn(SqlConnection db, SqlTransaction tx,
        string tenant, string actor, Guid command, string sourceNumber, F05Command input, CancellationToken ct)
    {
        string? reason = C1Validation.Text(input.Reason, 200);
        if (reason is null) return BusinessCommandOutcome.Rejected("F05_FIELDS_INVALID");
        await App02a3Sql.Lock(db, tx, "DEVF05:RETURN:" + tenant, ct);
        if (input.Action == "REQUEST")
            return await RequestReturn(db, tx, tenant, actor, command, sourceNumber, input, reason, ct);

        string? evidence = C1Validation.Text(input.Evidence, 120);
        if (input.CaseId is not Guid caseId || caseId == Guid.Empty || input.DeliveryId.HasValue || input.LotId.HasValue
            || input.Quantity.HasValue || evidence is null)
            return BusinessCommandOutcome.Rejected("F05_FIELDS_INVALID");
        string action = input.Action!;
        var item = await db.QuerySingleOrDefaultAsync<F05Case>(Sql("""
            SELECT Id,DeliveryId,LotId,Quantity,RequestedBy FROM dbo.F05ReturnCase
            WHERE TenantId=@Tenant AND Id=@CaseId
            """, new { Tenant = tenant, CaseId = caseId }, tx, ct));
        if (item is null) return BusinessCommandOutcome.Rejected("F05_CASE_NOT_FOUND");
        var source = await FindReturnSource(db, tx, tenant, item.DeliveryId, item.LotId, ct);
        if (source is null) return BusinessCommandOutcome.Rejected("F05_SOURCE_NOT_FOUND");
        var stages = (await db.QueryAsync<F05Stage>(Sql("""
            SELECT Stage,ActorId,DestinationId FROM dbo.F05ReturnStage WHERE TenantId=@Tenant AND CaseId=@CaseId
            """, new { Tenant = tenant, CaseId = caseId }, tx, ct))).ToDictionary(row => row.Stage, StringComparer.Ordinal);
        if (stages.ContainsKey(action)) return BusinessCommandOutcome.Rejected("F05_STAGE_ALREADY_RECORDED");
        if (string.Equals(item.RequestedBy, actor, StringComparison.Ordinal)
            || stages.Values.Any(stage => string.Equals(stage.ActorId, actor, StringComparison.Ordinal)))
            return BusinessCommandOutcome.Rejected("F05_SEPARATION_REQUIRED");
        if (await db.ExecuteScalarAsync<int>(Sql("""
            SELECT COUNT(*) FROM dbo.F05ReturnStage WHERE TenantId=@Tenant AND Stage=@Stage AND Evidence=@Evidence
            """, new { Tenant = tenant, Stage = action, Evidence = evidence }, tx, ct)) != 0)
            return BusinessCommandOutcome.Rejected("F05_EVIDENCE_IN_USE");

        Guid? destination = null;
        decimal? cost = null;
        if (action == "RECEIVE")
        {
            destination = await FindReturnQuarantine(db, tx, tenant, source.SkuId, ct);
            if (destination is not Guid qa) return BusinessCommandOutcome.Rejected("F05_QUARANTINE_UNAVAILABLE");
            await UpdateReturnBalance(db, tx, tenant, source.SkuId, qa, item.Quantity, 0, ct);
        }
        else
        {
            if (stages.GetValueOrDefault("RECEIVE")?.DestinationId is not Guid qa)
                return BusinessCommandOutcome.Rejected("F05_RECEIPT_REQUIRED");
            if (action == "RELEASE")
            {
                if (!stages.ContainsKey("APPROVE")) return BusinessCommandOutcome.Rejected("F05_APPROVAL_REQUIRED");
                DateTime date = await db.ExecuteScalarAsync<DateTime>(Sql($"SELECT {LotBusinessDate}", null, tx, ct));
                if (date > source.ExpiresOn) return BusinessCommandOutcome.Rejected("F05_LOT_EXPIRED");
                if (date.Year != source.BusinessDate.Year || date.Month != source.BusinessDate.Month)
                    return BusinessCommandOutcome.Rejected("F05_PERIOD_REVIEW_REQUIRED");
                await App02a3Sql.Lock(db, tx, $"DEVF03:RESERVE:{source.SkuId:N}:{source.WarehouseId:N}", ct);
                decimal previous = await db.ExecuteScalarAsync<decimal>(Sql("""
                    SELECT COALESCE(SUM(r.Quantity),0) FROM dbo.F05ReturnCase r JOIN dbo.F05ReturnStage s
                        ON s.TenantId=r.TenantId AND s.CaseId=r.Id AND s.Stage='RELEASE'
                    WHERE r.TenantId=@Tenant AND r.DeliveryId=@DeliveryId
                    """, new { Tenant = tenant, source.DeliveryId }, tx, ct));
                cost = F04Pricing.Portion((long)source.Cost, (long)source.Cost, source.Issued, previous, item.Quantity).Net;
                destination = source.WarehouseId;

                var stock = await db.QuerySingleOrDefaultAsync<F05Stock>(Sql("""
                    SELECT b.OnHandBaseQuantity,b.OnHandCostAmount FROM dbo.PhysicalStockBalance b WITH (UPDLOCK,HOLDLOCK)
                    JOIN dbo.StockPool p WITH (UPDLOCK,HOLDLOCK) ON p.TenantId=b.TenantId AND p.StockLocationId=b.StockLocationId
                      AND p.SkuId=b.SkuId AND p.OnHandBaseQuantity=b.OnHandBaseQuantity
                    JOIN dbo.StockLocation l ON l.TenantId=b.TenantId AND l.Id=b.StockLocationId AND l.IsActive=1 AND l.Kind='WAREHOUSE'
                    WHERE b.TenantId=@Tenant AND b.SkuId=@SkuId AND b.StockLocationId=@WarehouseId
                    """, new { Tenant = tenant, source.SkuId, source.WarehouseId }, tx, ct));
                if (stock is null || !ValidQuantity(stock.OnHandBaseQuantity + item.Quantity)
                    || stock.OnHandCostAmount + cost > 999999999999999.9999m)
                    return BusinessCommandOutcome.Rejected("F05_WAREHOUSE_STOCK_INVALID");
                await UpdateReturnBalance(db, tx, tenant, source.SkuId, qa, -item.Quantity, 0, ct);
                await UpdateReturnBalance(db, tx, tenant, source.SkuId, source.WarehouseId, item.Quantity, cost.Value, ct);
                await db.ExecuteAsync(Sql("""
                    UPDATE dbo.StockPool SET OnHandBaseQuantity=OnHandBaseQuantity+@Quantity
                    WHERE TenantId=@Tenant AND SkuId=@SkuId AND StockLocationId=@WarehouseId
                    """, new { Tenant = tenant, source.SkuId, source.WarehouseId, item.Quantity }, tx, ct));
            }
        }

        Guid stageSource = await AddSource(db, tx, tenant, command, "F05_RETURN_" + action, sourceNumber,
            actor, source.CustomerId, source.TripId, ct);
        Guid sourceLine = await AddLine(db, tx, tenant, stageSource, 1, source.SkuId, item.Quantity, cost, ct);
        await db.ExecuteAsync(Sql("""
            INSERT dbo.F05ReturnStage(TenantId,CaseId,Stage,SourceId,ActorId,Evidence,Reason,AcceptedCost,DestinationId)
            VALUES(@Tenant,@CaseId,@Stage,@SourceId,@Actor,@Evidence,@Reason,@Cost,@Destination)
            """, new { Tenant = tenant, CaseId = caseId, Stage = action, SourceId = stageSource, Actor = actor,
                Evidence = evidence, Reason = reason, Cost = cost, Destination = destination }, tx, ct));

        if (action == "RECEIVE")
            await ReturnEffect(db, tx, tenant, sourceLine, "RETURN-QA-IN", "PHYSICAL_STOCK", source.SkuId,
                destination, item.Quantity, 0, ct);
        if (cost is decimal acceptedCost)
        {
            await ReturnEffect(db, tx, tenant, sourceLine, "RETURN-QA-OUT", "PHYSICAL_STOCK", source.SkuId,
                stages["RECEIVE"].DestinationId, -item.Quantity, 0, ct);
            await ReturnEffect(db, tx, tenant, sourceLine, "RETURN-MAIN-IN", "PHYSICAL_STOCK", source.SkuId,
                destination, item.Quantity, acceptedCost, ct);
            if (acceptedCost != 0)
                await ReturnEffect(db, tx, tenant, sourceLine, "RETURN-COGS", "COGS", source.SkuId,
                    null, 0, -acceptedCost, ct);
        }
        return BusinessCommandOutcome.Applied("F05_" + action, stageSource);
    }

    private static async Task<BusinessCommandOutcome> RequestReturn(SqlConnection db, SqlTransaction tx,
        string tenant, string actor, Guid command, string sourceNumber, F05Command input, string reason, CancellationToken ct)
    {
        if (input.CaseId.HasValue || input.DeliveryId is not Guid delivery || input.LotId is not Guid lot
            || input.Quantity is not decimal quantity || !ValidQuantity(quantity) || input.Evidence is not null)
            return BusinessCommandOutcome.Rejected("F05_FIELDS_INVALID");
        var source = await FindReturnSource(db, tx, tenant, delivery, lot, ct);
        if (source is null || await db.ExecuteScalarAsync<int>(Sql("""
            SELECT COUNT(*) FROM dbo.C4DeliveryWriter
            WHERE TenantId=@Tenant AND TripId=@TripId AND ActorId=@Actor
            """, new { Tenant = tenant, source.TripId, Actor = actor }, tx, ct)) != 1)
            return BusinessCommandOutcome.Rejected("F05_SOURCE_NOT_FOUND");
        if (!await MeetsLotStep(db, tx, tenant, source.StockRightId, quantity, ct))
            return BusinessCommandOutcome.Rejected("F05_FIELDS_INVALID");
        decimal used = await db.ExecuteScalarAsync<decimal>(Sql("""
            SELECT COALESCE(SUM(Quantity),0) FROM dbo.F05ReturnCase
            WHERE TenantId=@Tenant AND DeliveryId=@Delivery AND LotId=@Lot
            """, new { Tenant = tenant, Delivery = delivery, Lot = lot }, tx, ct));
        if (quantity > source.IssuedForLot - used) return BusinessCommandOutcome.Rejected("F05_RETURN_QUANTITY_EXCEEDED");

        Guid id = await AddSource(db, tx, tenant, command, "F05_RETURN_REQUEST", sourceNumber, actor,
            source.CustomerId, source.TripId, ct);
        await AddLine(db, tx, tenant, id, 1, source.SkuId, quantity, null, ct);
        await db.ExecuteAsync(Sql("""
            INSERT dbo.F05ReturnCase(TenantId,Id,DeliveryId,LotId,Quantity,RequestedBy,Reason)
            VALUES(@Tenant,@Id,@Delivery,@Lot,@Quantity,@Actor,@Reason)
            """, new { Tenant = tenant, Id = id, Delivery = delivery, Lot = lot, Quantity = quantity,
                Actor = actor, Reason = reason }, tx, ct));
        return BusinessCommandOutcome.Applied("F05_REQUEST", id);
    }

    private static Task<Guid?> FindReturnQuarantine(SqlConnection db, SqlTransaction tx, string tenant, Guid sku,
        CancellationToken ct) => db.QuerySingleOrDefaultAsync<Guid?>(Sql("""
            SELECT l.Id FROM dbo.StockLocation l
            JOIN dbo.PhysicalStockBalance b ON b.TenantId=l.TenantId AND b.StockLocationId=l.Id AND b.SkuId=@Sku
            WHERE l.TenantId=@Tenant AND l.Code=N'F05-QA' AND l.Kind=N'QUARANTINE' AND l.IsActive=1
                AND NOT EXISTS (SELECT 1 FROM dbo.StockPool p WHERE p.TenantId=l.TenantId AND p.StockLocationId=l.Id AND p.SkuId=@Sku)
            """, new { Tenant = tenant, Sku = sku }, tx, ct));

    private static async Task UpdateReturnBalance(SqlConnection db, SqlTransaction tx, string tenant, Guid sku,
        Guid location, decimal quantity, decimal cost, CancellationToken ct)
    {
        if (await db.ExecuteAsync(Sql("""
            UPDATE dbo.PhysicalStockBalance SET OnHandBaseQuantity=OnHandBaseQuantity+@Quantity,
                OnHandCostAmount=OnHandCostAmount+@Cost
            WHERE TenantId=@Tenant AND SkuId=@Sku AND StockLocationId=@Location
                AND OnHandBaseQuantity+@Quantity>=0
            """, new { Tenant = tenant, Sku = sku, Location = location, Quantity = quantity, Cost = cost }, tx, ct)) != 1)
            throw new InvalidOperationException("Return stock balance changed under lock.");
    }

    private static Task<int> ReturnEffect(SqlConnection db, SqlTransaction tx, string tenant, Guid line, string key,
        string kind, Guid sku, Guid? location, decimal quantity, decimal amount, CancellationToken ct) => db.ExecuteAsync(Sql($"""
            INSERT dbo.LedgerEffect(Id,TenantId,BusinessSourceLineId,EffectKey,LedgerKind,SkuId,StockLocationId,
                QuantityDelta,AmountDelta,RecordedAtUtc)
            VALUES(NEWID(),@Tenant,@Line,@Key,@Kind,@Sku,@Location,@Quantity,@Amount,{App02c6Clock.SqlUtcNow})
            """, new { Tenant = tenant, Line = line, Key = key, Kind = kind, Sku = sku,
                Location = location, Quantity = quantity, Amount = amount }, tx, ct));
}
