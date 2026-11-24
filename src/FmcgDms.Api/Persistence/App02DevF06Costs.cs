using System.Security.Claims;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Persistence;

public sealed record F06Command(Guid CommandId, string? Action, Guid? DeliveryId, Guid? ProposalId,
    long? AdjustedCost, string? Reason, string? Evidence);
public sealed record F06SourceView(Guid DeliveryId, string Sku, long OriginalCost, decimal PriorReturned,
    long PreviousRefund, DateTime Period, long? AdjustedCost, string ReturnHandling);
public sealed record F06ProposalView(Guid Id, Guid DeliveryId, string Sku, long OriginalCost, long AdjustedCost,
    long PreviousRefund, long? PriorAdjustment, string Status, string ReturnHandling);

public sealed partial class App01a2CommandService
{
    private sealed record F06Origin(Guid DeliveryId, Guid SkuId, Guid WarehouseId, Guid CustomerId, Guid TripId,
        decimal Quantity, long OriginalCost, DateTime BusinessDate);
    private sealed record F06Proposal(Guid DeliveryId, long NewCost, string ActorId, DateTime PeriodStart);
    private sealed record F06Prior(decimal Quantity, long Refund, int Releases);
    private sealed record F06Physical(decimal OnHandBaseQuantity, decimal OnHandCostAmount, bool WitnessMatches);
    private const string AdjustmentSourceSql = """
        FROM dbo.SaleDelivery d JOIN dbo.F04DeliveryPricing p ON p.TenantId=d.TenantId AND p.DeliveryId=d.Id
        JOIN dbo.Sku k ON k.TenantId=d.TenantId AND k.Id=d.SkuId
        JOIN dbo.StockRight rightScope ON rightScope.TenantId=d.TenantId AND rightScope.Id=d.StockRightId
        JOIN dbo.F06Period pe ON pe.TenantId=d.TenantId AND pe.MonthStart=DATEFROMPARTS(YEAR(d.BusinessDate),MONTH(d.BusinessDate),1)
        CROSS APPLY (SELECT COALESCE(SUM(r.Quantity),0) AS Quantity,CONVERT(bigint,COALESCE(SUM(s.AcceptedCost),0)) AS Refund,
            COUNT(*) AS Releases,COUNT(w.ReturnSourceId) AS CurrentWitnesses
            FROM dbo.F05ReturnCase r JOIN dbo.F05ReturnStage s ON s.TenantId=r.TenantId AND s.CaseId=r.Id AND s.Stage='RELEASE'
            LEFT JOIN dbo.PhysicalStockBalance b ON b.TenantId=r.TenantId AND b.SkuId=d.SkuId
                AND b.StockLocationId=rightScope.StockLocationId
            LEFT JOIN dbo.F06ReturnWarehouseWitness w ON w.TenantId=s.TenantId AND w.ReturnSourceId=s.SourceId
                AND w.WarehouseVersion=b.RowVersion
            WHERE r.TenantId=d.TenantId AND r.DeliveryId=d.Id) receipts
        """;

    private const string AdjustmentHandling = """
        CASE WHEN NOT EXISTS(SELECT 1 FROM dbo.F03LotIssue i WHERE i.TenantId=d.TenantId AND i.DeliveryId=d.Id)
                THEN 'NO_LOT_SOURCE'
             WHEN receipts.Releases>1 THEN 'MULTIPLE_RELEASES'
             WHEN receipts.Releases=1 AND receipts.CurrentWitnesses=0 THEN 'WAREHOUSE_WITNESS_MISSING'
             ELSE 'SUPPORTED' END AS ReturnHandling
        """;

    public Task<F05Identity> AdjustmentIdentity(ClaimsPrincipal principal, CancellationToken ct) =>
        ReadF06(principal, async (db, tx, tenant, actor, role) => new F05Identity(tenant, actor,
            await db.QuerySingleAsync<string>(Sql("SELECT Nonce FROM dbo.App00ScratchMarker", null, tx, ct)), role), ct);

    public Task<IEnumerable<F06SourceView>> AdjustmentSources(ClaimsPrincipal principal, CancellationToken ct) =>
        ReadF06(principal, (db, tx, tenant, actor, role) => db.QueryAsync<F06SourceView>(Sql($"""
            SELECT d.Id AS DeliveryId,k.Code AS Sku,CONVERT(bigint,d.CostAmount) AS OriginalCost,
                   receipts.Quantity AS PriorReturned,receipts.Refund AS PreviousRefund,pe.MonthStart AS Period,
                   a.AdjustedCost,{AdjustmentHandling}
            {AdjustmentSourceSql}
            LEFT JOIN dbo.F06CostAdjustment a ON a.TenantId=d.TenantId AND a.DeliveryId=d.Id
            WHERE d.TenantId=@Tenant AND pe.State='LOCKED' AND p.Currency='VND'
                AND d.CostAmount=ROUND(d.CostAmount,0) AND d.CostAmount>0
                AND (@Role='finance' OR EXISTS (SELECT 1 FROM dbo.F05QaScope qa
                    WHERE qa.TenantId=d.TenantId AND qa.WarehouseId=rightScope.StockLocationId AND qa.ActorId=@Actor))
            ORDER BY d.BusinessDate DESC,d.Id
            """, new { Tenant = tenant, Actor = actor, Role = role }, tx, ct)), ct);

    public Task<IEnumerable<F06ProposalView>> AdjustmentProposals(ClaimsPrincipal principal, CancellationToken ct) =>
        ReadF06(principal, (db, tx, tenant, actor, role) => db.QueryAsync<F06ProposalView>(Sql($"""
            SELECT q.Id,q.DeliveryId,k.Code AS Sku,CONVERT(bigint,d.CostAmount) AS OriginalCost,
                   q.NewCost AS AdjustedCost,COALESCE(a.PriorRefund,receipts.Refund) AS PreviousRefund,
                   a.PriorAdjustment,CASE WHEN a.Id IS NULL THEN 'PROPOSED' ELSE 'APPROVED' END AS Status,
                   {AdjustmentHandling}
            {AdjustmentSourceSql}
            JOIN dbo.F06CostProposal q ON q.TenantId=d.TenantId AND q.DeliveryId=d.Id
            LEFT JOIN dbo.F06CostAdjustment a ON a.TenantId=q.TenantId AND a.ProposalId=q.Id
            WHERE d.TenantId=@Tenant AND (@Role='finance' OR EXISTS (SELECT 1 FROM dbo.F05QaScope qa
                WHERE qa.TenantId=d.TenantId AND qa.WarehouseId=rightScope.StockLocationId AND qa.ActorId=@Actor))
            ORDER BY q.PeriodStart DESC,q.Id
            """, new { Tenant = tenant, Actor = actor, Role = role }, tx, ct)), ct);

    public Task<BusinessCommandOutcome> RecordAdjustment(ClaimsPrincipal principal, F06Command input, CancellationToken ct)
    {
        if (!connections.UsesDevF06) throw new UnauthorizedAccessException("DEV-F06 disabled.");
        string? role = input.Action switch { "PROPOSE" => "finance", "APPROVE" => "case_checker", _ => null };
        if (role is null) return Task.FromResult(BusinessCommandOutcome.Rejected("F06_FIELDS_INVALID"));
        var key = AdjustmentKey(principal, input.CommandId);
        return ExecuteAsync(principal, key, "F06_" + input.Action,
            [("Request", JsonSerializer.Serialize(input, C1Validation.Json))],
            (db, tx, tenant, actor, command, token) =>
                ApplyAdjustment(db, tx, tenant, actor, command, key.SourceNumber, input, token),
            ct, role, isF05Return: true);
    }

    public Task<BusinessCommandOutcome?> AdjustmentOutcome(ClaimsPrincipal principal, Guid id, CancellationToken ct) =>
        ReadF06<BusinessCommandOutcome?>(principal, async (db, tx, tenant, actor, _) =>
        {
            var key = AdjustmentKey(principal, id);
            var stored = await FindReturnCommandOutcome(db, tx, tenant, actor, key, ct);
            if (stored is null) return null;
            if (!principal.HasClaim("synthetic_role", stored.RoleCode))
                throw new UnauthorizedAccessException("Current correction role required.");
            await App02a3Sql.RequireRole(db, tx, tenant, actor, stored.RoleCode, ct);
            if (stored.RoleCode == "case_checker" && stored.SourceId is Guid approved)
                await RequireAdjustmentWarehouseScope(db, tx, tenant, actor, approved, ct);
            return new BusinessCommandOutcome(stored.Status, stored.OutcomeCode, stored.ReasonCode, true, stored.SourceId);
        }, ct);

    private static C2CommandKey AdjustmentKey(ClaimsPrincipal principal, Guid commandId)
    {
        var key = C2Key(principal, commandId);
        return new("F06-" + key.ProducerId[3..], key.CommandId, "F06-" + key.SourceNumber[3..]);
    }

    private Task<T> ReadF06<T>(ClaimsPrincipal principal,
        Func<SqlConnection, SqlTransaction, string, string, string, Task<T>> read, CancellationToken ct)
    {
        if (!connections.UsesDevF06) throw new UnauthorizedAccessException("DEV-F06 disabled.");
        return ReadF05(principal, read, ct, ["finance", "case_checker"]);
    }

    private static async Task<BusinessCommandOutcome> ApplyAdjustment(SqlConnection db, SqlTransaction tx,
        string tenant, string actor, Guid command, string sourceNumber, F06Command input, CancellationToken ct)
    {
        string? reason = C1Validation.Text(input.Reason, 200);
        string? evidence = C1Validation.Text(input.Evidence, 120);
        if (reason is null || evidence is null) return BusinessCommandOutcome.Rejected("F06_FIELDS_INVALID");
        await App02a3Sql.Lock(db, tx, "DEVF05:RETURN:" + tenant, ct);
        DateTime current = await db.ExecuteScalarAsync<DateTime>(Sql($"SELECT {LotBusinessDate}", null, tx, ct));
        DateTime month = MonthStart(current);
        if (await PeriodState(db, tx, tenant, month, ct) != "OPEN")
            return BusinessCommandOutcome.Rejected("F06_OPEN_PERIOD_REQUIRED");

        F06Proposal? proposal = null;
        Guid delivery;
        if (input.Action == "PROPOSE")
        {
            if (input.DeliveryId is not Guid id || id == Guid.Empty || input.ProposalId.HasValue
                || input.AdjustedCost is not long requested || requested is <= 0 or > 999999999999999)
                return BusinessCommandOutcome.Rejected("F06_FIELDS_INVALID");
            delivery = id;
        }
        else
        {
            if (input.ProposalId is not Guid id || id == Guid.Empty || input.DeliveryId.HasValue || input.AdjustedCost.HasValue)
                return BusinessCommandOutcome.Rejected("F06_FIELDS_INVALID");
            proposal = await db.QuerySingleOrDefaultAsync<F06Proposal>(Sql("""
                SELECT DeliveryId,NewCost,ActorId,PeriodStart FROM dbo.F06CostProposal
                WHERE TenantId=@Tenant AND Id=@Id
                """, new { Tenant = tenant, Id = id }, tx, ct));
            if (proposal is null) return BusinessCommandOutcome.Rejected("F06_SOURCE_NOT_FOUND");
            if (string.Equals(actor, proposal.ActorId, StringComparison.Ordinal))
                return BusinessCommandOutcome.Rejected("F06_SEPARATION_REQUIRED");
            if (proposal.PeriodStart != month) return BusinessCommandOutcome.Rejected("F06_OPEN_PERIOD_REQUIRED");
            delivery = proposal.DeliveryId;
        }

        var origin = await db.QuerySingleOrDefaultAsync<F06Origin>(Sql("""
            SELECT d.Id AS DeliveryId,d.SkuId,r.StockLocationId AS WarehouseId,d.CustomerId,d.TripId,
                   d.BaseQuantity AS Quantity,CONVERT(bigint,d.CostAmount) AS OriginalCost,d.BusinessDate
            FROM dbo.SaleDelivery d JOIN dbo.F04DeliveryPricing p ON p.TenantId=d.TenantId AND p.DeliveryId=d.Id
            JOIN dbo.StockRight r ON r.TenantId=d.TenantId AND r.Id=d.StockRightId
            WHERE d.TenantId=@Tenant AND d.Id=@Delivery AND p.Currency='VND' AND d.RightKind='A'
                AND d.CostAmount=ROUND(d.CostAmount,0) AND d.CostAmount>0 AND EXISTS (
                    SELECT 1 FROM dbo.F03LotIssue i WHERE i.TenantId=d.TenantId AND i.DeliveryId=d.Id)
            """, new { Tenant = tenant, Delivery = delivery }, tx, ct));
        if (origin is null) return BusinessCommandOutcome.Rejected("F06_SOURCE_NOT_FOUND");
        if (MonthStart(origin.BusinessDate) >= month || await PeriodState(db, tx, tenant, MonthStart(origin.BusinessDate), ct) != "LOCKED")
            return BusinessCommandOutcome.Rejected("F06_SOURCE_NOT_LOCKED");
        if (await db.ExecuteScalarAsync<int>(Sql("""
            SELECT COUNT(*) FROM dbo.F06CostAdjustment WHERE TenantId=@Tenant AND DeliveryId=@Delivery
            """, new { Tenant = tenant, Delivery = delivery }, tx, ct)) != 0)
            return BusinessCommandOutcome.Rejected("F06_ADJUSTMENT_EXISTS");

        long newCost = proposal?.NewCost ?? input.AdjustedCost!.Value;
        if (newCost == origin.OriginalCost) return BusinessCommandOutcome.Rejected("F06_FIELDS_INVALID");
        if (proposal is null)
        {
            if (await AdjustmentEvidenceExists(db, tx, tenant, evidence, "F06CostProposal", ct))
                return BusinessCommandOutcome.Rejected("F06_EVIDENCE_IN_USE");
            Guid id = await AddSource(db, tx, tenant, command, "F06_COST_PROPOSAL", sourceNumber, actor,
                origin.CustomerId, origin.TripId, ct);
            await db.ExecuteAsync(Sql("""
                INSERT dbo.F06CostProposal(TenantId,Id,DeliveryId,NewCost,Reason,Evidence,ActorId,PeriodStart)
                VALUES(@Tenant,@Id,@Delivery,@NewCost,@Reason,@Evidence,@Actor,@Period)
                """, new { Tenant = tenant, Id = id, Delivery = delivery, NewCost = newCost, Reason = reason,
                    Evidence = evidence, Actor = actor, Period = month }, tx, ct));
            return BusinessCommandOutcome.Applied("F06_PROPOSED", id);
        }

        await RequireQaScope(db, tx, tenant, actor, origin.WarehouseId, ct);
        if (await AdjustmentEvidenceExists(db, tx, tenant, evidence, "F06CostAdjustment", ct))
            return BusinessCommandOutcome.Rejected("F06_EVIDENCE_IN_USE");
        await App02a3Sql.Lock(db, tx, $"DEVF03:RESERVE:{origin.SkuId:N}:{origin.WarehouseId:N}", ct);
        var prior = await db.QuerySingleAsync<F06Prior>(Sql("""
            SELECT COALESCE(SUM(r.Quantity),0) AS Quantity,CONVERT(bigint,COALESCE(SUM(s.AcceptedCost),0)) AS Refund,
                   COUNT(*) AS Releases
            FROM dbo.F05ReturnCase r JOIN dbo.F05ReturnStage s
                ON s.TenantId=r.TenantId AND s.CaseId=r.Id AND s.Stage='RELEASE'
            WHERE r.TenantId=@Tenant AND r.DeliveryId=@Delivery
            """, new { Tenant = tenant, Delivery = delivery }, tx, ct));
        long revisedPrior = prior.Quantity > 0 ? F04Pricing.Portion(newCost, newCost, origin.Quantity, 0, prior.Quantity).Net : 0;
        long priorDelta = revisedPrior - prior.Refund;
        if (priorDelta != 0)
        {
            // A single row version can prove one release still owns the value in this profile.
            if (prior.Releases != 1) return BusinessCommandOutcome.Rejected("F06_RETURN_STOCK_CHANGED");
            var balance = await db.QuerySingleOrDefaultAsync<F06Physical>(Sql("""
                SELECT b.OnHandBaseQuantity,b.OnHandCostAmount,CAST(CASE WHEN EXISTS (
                    SELECT 1 FROM dbo.F05ReturnCase r JOIN dbo.F05ReturnStage s
                        ON s.TenantId=r.TenantId AND s.CaseId=r.Id AND s.Stage='RELEASE'
                    JOIN dbo.F06ReturnWarehouseWitness w ON w.TenantId=s.TenantId AND w.ReturnSourceId=s.SourceId
                    WHERE r.TenantId=b.TenantId AND r.DeliveryId=@Delivery AND w.WarehouseVersion=b.RowVersion
                ) THEN 1 ELSE 0 END AS bit) AS WitnessMatches
                FROM dbo.PhysicalStockBalance b WITH (UPDLOCK,HOLDLOCK)
                WHERE b.TenantId=@Tenant AND b.StockLocationId=@WarehouseId AND b.SkuId=@SkuId
                """, new { Tenant = tenant, Delivery = delivery, origin.WarehouseId, origin.SkuId }, tx, ct));
            if (balance is null || !balance.WitnessMatches || balance.OnHandBaseQuantity < prior.Quantity
                || balance.OnHandCostAmount + priorDelta is < 0 or > 999999999999999.9999m)
                return BusinessCommandOutcome.Rejected("F06_RETURN_STOCK_CHANGED");
            await UpdateReturnBalance(db, tx, tenant, origin.SkuId, origin.WarehouseId, 0, priorDelta, ct);
        }
        long sourceDelta = newCost - origin.OriginalCost;
        Guid adjustment = await AddSource(db, tx, tenant, command, "F06_COST_ADJUSTMENT", sourceNumber, actor,
            origin.CustomerId, origin.TripId, ct);
        Guid line = await AddLine(db, tx, tenant, adjustment, 1, origin.SkuId, null, sourceDelta, ct);
        await db.ExecuteAsync(Sql("""
            INSERT dbo.F06CostAdjustment(TenantId,Id,ProposalId,DeliveryId,OriginalCost,AdjustedCost,PriorReturnedQuantity,
                PriorRefund,PriorAdjustment,PeriodStart,ActorId,Reason,Evidence,WarehouseId)
            VALUES(@Tenant,@Id,@ProposalId,@Delivery,@OriginalCost,@NewCost,@Quantity,
                @Refund,@PriorDelta,@Period,@Actor,@Reason,@Evidence,@WarehouseId)
            """, new { Tenant = tenant, Id = adjustment, input.ProposalId, Delivery = delivery, origin.OriginalCost,
                NewCost = newCost, prior.Quantity, prior.Refund, PriorDelta = priorDelta, Period = month, Actor = actor,
                Reason = reason, Evidence = evidence, origin.WarehouseId }, tx, ct));
        await ReturnEffect(db, tx, tenant, line, "COST-SOURCE-DELTA", "COGS", origin.SkuId, null, 0, sourceDelta, ct);
        if (priorDelta != 0)
        {
            await ReturnEffect(db, tx, tenant, line, "COST-RETURN-DELTA", "COGS", origin.SkuId, null, 0, -priorDelta, ct);
            await ReturnEffect(db, tx, tenant, line, "COST-RETURN-STOCK", "PHYSICAL_STOCK", origin.SkuId,
                origin.WarehouseId, 0, priorDelta, ct);
        }
        return BusinessCommandOutcome.Applied("F06_APPROVED", adjustment);
    }

    private static Task<bool> AdjustmentEvidenceExists(SqlConnection db, SqlTransaction tx, string tenant,
        string evidence, string table, CancellationToken ct) => db.ExecuteScalarAsync<bool>(Sql($"""
            SELECT CAST(IIF(EXISTS(SELECT 1 FROM dbo.[{table}] WHERE TenantId=@Tenant AND Evidence=@Evidence),1,0) AS bit)
            """, new { Tenant = tenant, Evidence = evidence }, tx, ct));

    private static async Task<(long Cost, string? Error)> ReturnCostForPeriod(SqlConnection db, SqlTransaction tx,
        string tenant, F05Source source, DateTime date, CancellationToken ct)
    {
        DateTime month = MonthStart(date);
        if (await PeriodState(db, tx, tenant, month, ct) != "OPEN")
            return (0, "F06_OPEN_PERIOD_REQUIRED");
        DateTime sourceMonth = MonthStart(source.BusinessDate);
        if (month == sourceMonth) return ((long)source.Cost, null);
        if (month < sourceMonth || await PeriodState(db, tx, tenant, sourceMonth, ct) != "LOCKED")
            return (0, "F06_SOURCE_NOT_LOCKED");
        long? adjusted = await db.QuerySingleOrDefaultAsync<long?>(Sql("""
            SELECT AdjustedCost FROM dbo.F06CostAdjustment
            WHERE TenantId=@Tenant AND DeliveryId=@DeliveryId AND PeriodStart<=@Month
            """, new { Tenant = tenant, source.DeliveryId, Month = month }, tx, ct));
        if (adjusted is long cost) return (cost, null);
        bool pendingProposal = await db.ExecuteScalarAsync<bool>(Sql("""
            SELECT CAST(IIF(EXISTS(SELECT 1 FROM dbo.F06CostProposal WHERE TenantId=@Tenant AND DeliveryId=@DeliveryId),
                1,0) AS bit)
            """, new { Tenant = tenant, source.DeliveryId }, tx, ct));
        return pendingProposal ? (0, "F06_ADJUSTMENT_REQUIRED") : ((long)source.Cost, null);
    }

    private static DateTime MonthStart(DateTime date) => new(date.Year, date.Month, 1);

    private static Task<string?> PeriodState(SqlConnection db, SqlTransaction tx, string tenant, DateTime month,
        CancellationToken ct) => db.QuerySingleOrDefaultAsync<string>(Sql("""
            SELECT State FROM dbo.F06Period WITH (HOLDLOCK) WHERE TenantId=@Tenant AND MonthStart=@Month
            """, new { Tenant = tenant, Month = month }, tx, ct));

    private static async Task RequireAdjustmentWarehouseScope(SqlConnection db, SqlTransaction tx, string tenant,
        string actor, Guid sourceId, CancellationToken ct)
    {
        Guid warehouse = await db.QuerySingleAsync<Guid>(Sql("""
            SELECT WarehouseId FROM dbo.F06CostAdjustment WHERE TenantId=@Tenant AND Id=@SourceId
            """, new { Tenant = tenant, SourceId = sourceId }, tx, ct));
        await RequireQaScope(db, tx, tenant, actor, warehouse, ct);
    }
}
