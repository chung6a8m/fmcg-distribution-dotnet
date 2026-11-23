using System.Security.Claims;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Persistence;

public sealed record F07Command(Guid CommandId, string? Action, Guid? CustomerId, Guid? IssueId, Guid? WaiverId,
    int? Quantity, string? Reason, string? Evidence);
public sealed record F07Position(Guid CustomerId, string Customer, decimal NetPurchases, decimal Entitlement,
    long GiftHeld, long WaivedHeld, decimal ToDeliver);
public sealed record F07IssueView(Guid Id, Guid CustomerId, string Customer, int Quantity, long Returned, long WaivedHeld);
public sealed record F07WaiverView(Guid Id, Guid IssueId, string Customer, int Quantity, string Status, string Reason, string Evidence);

public sealed partial class App01a2CommandService
{
    private sealed record F07Program(Guid Id, Guid PurchaseSkuId, Guid GiftSkuId, Guid GiftLocationId, Guid QuarantineLocationId);
    private sealed record F07Issue(Guid Id, Guid CustomerId, int Quantity, long CostAmount, long Returned, long ReturnedCost);
    private sealed record F07Waiver(Guid IssueId, int Quantity, string ActorId, bool Approved);
    private static readonly string[] GiftRoles = ["sales", "warehouse", "finance"];

    private const string GiftAssignment = """
        (@Role<>'sales' OR EXISTS (SELECT 1 FROM dbo.SaleDelivery a
            JOIN dbo.C4DeliveryWriter w ON w.TenantId=a.TenantId AND w.TripId=a.TripId AND w.ActorId=@Actor
            WHERE a.TenantId=c.TenantId AND a.CustomerId=c.Id AND a.SkuId=p.PurchaseSkuId AND a.RightKind='A'))
        """;

    private const string GiftCounts = """
        SELECT i.Id,i.CustomerId,i.Quantity,i.CostAmount,back.Returned,back.ReturnedCost,
            CASE WHEN i.Quantity-back.Returned<waiver.Quantity THEN i.Quantity-back.Returned
                 ELSE waiver.Quantity END AS WaivedHeld
        FROM dbo.F07Issue i
        CROSS APPLY (SELECT COALESCE(SUM(CONVERT(bigint,q.Quantity)),0) AS Quantity
            FROM dbo.F07Waiver q JOIN dbo.F07WaiverDecision approved
                ON approved.TenantId=q.TenantId AND approved.WaiverId=q.Id
            WHERE q.TenantId=i.TenantId AND q.IssueId=i.Id) waiver
        CROSS APPLY (SELECT COALESCE(SUM(CONVERT(bigint,r.Quantity)),0) AS Returned,
            COALESCE(SUM(r.CostAmount),0) AS ReturnedCost FROM dbo.F07GiftReturn r
            WHERE r.TenantId=i.TenantId AND r.IssueId=i.Id) back
        WHERE i.TenantId=p.TenantId AND i.ProgramId=p.Id AND i.CustomerId=c.Id
        """;

    private const string PositionQuery = $"""
        SELECT c.Id AS CustomerId,c.Name AS Customer,purchases.Net AS NetPurchases,calc.Entitlement,
               g.Held AS GiftHeld,g.Waived AS WaivedHeld,
               CASE WHEN calc.Entitlement>g.Held THEN calc.Entitlement-g.Held ELSE 0 END AS ToDeliver
        FROM dbo.Customer c JOIN dbo.F07Program p ON p.TenantId=c.TenantId
        CROSS APPLY (
            SELECT CONVERT(decimal(30,3),COALESCE(SUM(d.BaseQuantity-r.Accepted),0)) AS Net,COUNT_BIG(*) AS Deliveries
            FROM dbo.SaleDelivery d
            CROSS APPLY (SELECT CONVERT(decimal(18,3),COALESCE(SUM(x.Quantity),0)) AS Accepted
                FROM dbo.F05ReturnCase x JOIN dbo.F05ReturnStage s
                ON s.TenantId=x.TenantId AND s.CaseId=x.Id AND s.Stage='RELEASE'
                WHERE x.TenantId=d.TenantId AND x.DeliveryId=d.Id) r
            WHERE d.TenantId=c.TenantId AND d.CustomerId=c.Id AND d.SkuId=p.PurchaseSkuId AND d.RightKind='A') purchases
        CROSS APPLY (SELECT ((purchases.Net-purchases.Net%p.PurchaseThreshold)/p.PurchaseThreshold)*p.GiftsPerThreshold AS Entitlement) calc
        CROSS APPLY (SELECT COALESCE(SUM(q.Quantity-q.Returned),0) AS Held,COALESCE(SUM(q.WaivedHeld),0) AS Waived
            FROM ({GiftCounts}) q) g
        WHERE c.TenantId=@Tenant AND purchases.Deliveries>0 AND {GiftAssignment}
        """;

    public Task<F05Identity> GiftIdentity(ClaimsPrincipal user, CancellationToken ct) =>
        ReadF07(user, async (db, tx, tenant, actor, role) => new F05Identity(tenant, actor,
            await db.QuerySingleAsync<string>(Sql("SELECT Nonce FROM dbo.App00ScratchMarker", null, tx, ct)), role), ct);

    public Task<IEnumerable<F07Position>> GiftPositions(ClaimsPrincipal user, CancellationToken ct) =>
        ReadF07(user, (db, tx, tenant, actor, role) => db.QueryAsync<F07Position>(
            Sql(PositionQuery + " ORDER BY c.Name,c.Id", new { Tenant = tenant, Actor = actor, Role = role }, tx, ct)), ct);

    public Task<IEnumerable<F07IssueView>> GiftIssues(ClaimsPrincipal user, CancellationToken ct) =>
        ReadF07(user, (db, tx, tenant, actor, role) => db.QueryAsync<F07IssueView>(Sql($"""
            SELECT q.Id,q.CustomerId,c.Name AS Customer,q.Quantity,q.Returned,q.WaivedHeld
            FROM dbo.Customer c JOIN dbo.F07Program p ON p.TenantId=c.TenantId
            CROSS APPLY ({GiftCounts}) q
            WHERE c.TenantId=@Tenant AND {GiftAssignment}
            ORDER BY c.Name,q.Id
            """, new { Tenant = tenant, Actor = actor, Role = role }, tx, ct)), ct);

    public Task<IEnumerable<F07WaiverView>> GiftWaivers(ClaimsPrincipal user, CancellationToken ct) =>
        ReadF07(user, (db, tx, tenant, actor, role) => db.QueryAsync<F07WaiverView>(Sql($"""
            SELECT q.Id,q.IssueId,c.Name AS Customer,q.Quantity,
                CASE WHEN w.Id IS NOT NULL THEN 'APPROVED'
                     WHEN q.Quantity>i.Quantity-back.Returned THEN 'EXPIRED' ELSE 'PROPOSED' END AS Status,q.Reason,q.Evidence
            FROM dbo.F07Waiver q JOIN dbo.F07Issue i ON i.TenantId=q.TenantId AND i.Id=q.IssueId
            JOIN dbo.F07Program p ON p.TenantId=i.TenantId AND p.Id=i.ProgramId
            JOIN dbo.Customer c ON c.TenantId=i.TenantId AND c.Id=i.CustomerId
            LEFT JOIN dbo.F07WaiverDecision w ON w.TenantId=q.TenantId AND w.WaiverId=q.Id
            CROSS APPLY (SELECT COALESCE(SUM(CONVERT(bigint,r.Quantity)),0) AS Returned
                FROM dbo.F07GiftReturn r WHERE r.TenantId=i.TenantId AND r.IssueId=i.Id) back
            WHERE q.TenantId=@Tenant AND {GiftAssignment}
            ORDER BY c.Name,q.Id
            """, new { Tenant = tenant, Actor = actor, Role = role }, tx, ct)), ct);

    private Task<T> ReadF07<T>(ClaimsPrincipal user,
        Func<SqlConnection, SqlTransaction, string, string, string, Task<T>> read, CancellationToken ct)
    {
        if (!connections.UsesDevF07) throw new UnauthorizedAccessException("DEV-F07 disabled.");
        return ReadF05(user, async (db, tx, tenant, actor, role) =>
        {
            var program = await FindGiftProgram(db, tx, tenant, ct);
            if (program is not null)
                await RequireGiftProgramScope(db, tx, tenant, actor, role, program, ct);
            return await read(db, tx, tenant, actor, role);
        }, ct, GiftRoles);
    }

    private static C2CommandKey GiftKey(ClaimsPrincipal user, Guid id)
    {
        var key = C2Key(user, id);
        return new("F07-" + key.ProducerId[3..], key.CommandId, "F07-" + key.SourceNumber[3..]);
    }

    public Task<BusinessCommandOutcome> RecordGift(ClaimsPrincipal user, F07Command input, CancellationToken ct)
    {
        if (!connections.UsesDevF07) throw new UnauthorizedAccessException("DEV-F07 disabled.");
        string? role = input.Action switch
        {
            "ISSUE" or "PROPOSE" => "sales",
            "RETURN" => "warehouse",
            "APPROVE" => "finance",
            _ => null
        };
        if (role is null) return Task.FromResult(BusinessCommandOutcome.Rejected("F07_FIELDS_INVALID"));
        var key = GiftKey(user, input.CommandId);
        return ExecuteAsync(user, key, "F07_" + input.Action, [("Request", JsonSerializer.Serialize(input, C1Validation.Json))],
            (db, tx, tenant, actor, command, token) => ApplyGift(db, tx, tenant, actor, command, key.SourceNumber,
                role, input, connections.UsesDevF06, token), ct, role, isF05Return: true);
    }

    public Task<BusinessCommandOutcome?> GiftOutcome(ClaimsPrincipal user, Guid id, CancellationToken ct) =>
        ReadF07<BusinessCommandOutcome?>(user, async (db, tx, tenant, actor, _) =>
        {
            var stored = await FindReturnCommandOutcome(db, tx, tenant, actor, GiftKey(user, id), ct);
            if (stored is null) return null;
            if (!user.HasClaim("synthetic_role", stored.RoleCode))
                throw new UnauthorizedAccessException("Current gift command role required.");
            await App02a3Sql.RequireRole(db, tx, tenant, actor, stored.RoleCode, ct);
            if (stored.SourceId is Guid source)
                await RequireGiftRecoveryScope(db, tx, tenant, actor, stored.RoleCode, source, ct);
            return new BusinessCommandOutcome(stored.Status, stored.OutcomeCode, stored.ReasonCode, true, stored.SourceId);
        }, ct);

    private static Task<F07Program?> FindGiftProgram(SqlConnection db, SqlTransaction tx, string tenant, CancellationToken ct) =>
        db.QuerySingleOrDefaultAsync<F07Program>(Sql("""
            SELECT Id,PurchaseSkuId,GiftSkuId,GiftLocationId,QuarantineLocationId FROM dbo.F07Program
            WHERE TenantId=@Tenant
            """, new { Tenant = tenant }, tx, ct));

    private static Task<F07Position?> FindGiftPosition(SqlConnection db, SqlTransaction tx, string tenant,
        string actor, string role, Guid customer, CancellationToken ct) =>
        db.QuerySingleOrDefaultAsync<F07Position>(Sql(PositionQuery + " AND c.Id=@Customer",
            new { Tenant = tenant, Actor = actor, Role = role, Customer = customer }, tx, ct));

    private static async Task RequireGiftProgramScope(SqlConnection db, SqlTransaction tx, string tenant,
        string actor, string role, F07Program program, CancellationToken ct)
    {
        if (role == "warehouse")
        {
            await RequireQaScope(db, tx, tenant, actor, program.GiftLocationId, ct);
            return;
        }

        if (await db.ExecuteScalarAsync<int>(Sql("""
            SELECT COUNT(*) FROM dbo.F07ProgramGrant
            WHERE TenantId=@Tenant AND ProgramId=@Program AND ActorId=@Actor AND RoleCode=@Role
            """, new { Tenant = tenant, Program = program.Id, Actor = actor, Role = role }, tx, ct)) != 1)
            throw new UnauthorizedAccessException("Current gift program assignment required.");
    }

    private static async Task RequireGiftRecoveryScope(SqlConnection db, SqlTransaction tx, string tenant,
        string actor, string role, Guid source, CancellationToken ct)
    {
        var program = await FindGiftProgram(db, tx, tenant, ct)
            ?? throw new UnauthorizedAccessException("Gift program unavailable.");
        await RequireGiftProgramScope(db, tx, tenant, actor, role, program, ct);
        if (role == "sales")
        {
            Guid customer = await db.QuerySingleAsync<Guid>(Sql("""
                SELECT CustomerId FROM dbo.BusinessSource WHERE TenantId=@Tenant AND Id=@Source
                """, new { Tenant = tenant, Source = source }, tx, ct));
            if (await FindGiftPosition(db, tx, tenant, actor, role, customer, ct) is null)
                throw new UnauthorizedAccessException("Current qualification assignment required.");
        }
    }

    private static async Task<BusinessCommandOutcome> ApplyGift(SqlConnection db, SqlTransaction tx,
        string tenant, string actor, Guid command, string sourceNumber, string role, F07Command input,
        bool enforcePeriod, CancellationToken ct)
    {
        string? reason = C1Validation.Text(input.Reason, 200);
        string? evidence = C1Validation.Text(input.Evidence, 120);
        if (reason is null || evidence is null) return BusinessCommandOutcome.Rejected("F07_FIELDS_INVALID");
        F07Command request = input with { Reason = reason, Evidence = evidence };
        await App02a3Sql.Lock(db, tx, "DEVF07:GIFT:" + tenant, ct);
        if (enforcePeriod)
        {
            DateTime date = await db.ExecuteScalarAsync<DateTime>(Sql($"SELECT {LotBusinessDate}", null, tx, ct));
            if (await PeriodState(db, tx, tenant, MonthStart(date), ct) != "OPEN")
                return BusinessCommandOutcome.Rejected("F06_OPEN_PERIOD_REQUIRED");
        }
        var program = await FindGiftProgram(db, tx, tenant, ct);
        if (program is null) return BusinessCommandOutcome.Rejected("F07_SOURCE_NOT_FOUND");
        await RequireGiftProgramScope(db, tx, tenant, actor, role, program, ct);

        if (input.Action == "ISSUE")
        {
            if (input.CustomerId is not Guid customer || input.Quantity is not > 0 || input.IssueId.HasValue || input.WaiverId.HasValue)
                return BusinessCommandOutcome.Rejected("F07_FIELDS_INVALID");
            return await IssueGift(db, tx, tenant, actor, command, sourceNumber, program, customer, request, ct);
        }

        F07Waiver? waiver = null;
        Guid issueId;
        if (input.Action == "APPROVE")
        {
            if (input.WaiverId is not Guid waiverId || input.IssueId.HasValue || input.CustomerId.HasValue || input.Quantity.HasValue)
                return BusinessCommandOutcome.Rejected("F07_FIELDS_INVALID");
            waiver = await db.QuerySingleOrDefaultAsync<F07Waiver>(Sql("""
                SELECT q.IssueId,q.Quantity,q.ActorId,CONVERT(bit,CASE WHEN d.Id IS NULL THEN 0 ELSE 1 END) AS Approved
                FROM dbo.F07Waiver q LEFT JOIN dbo.F07WaiverDecision d ON d.TenantId=q.TenantId AND d.WaiverId=q.Id
                WHERE q.TenantId=@Tenant AND q.Id=@Waiver
                """, new { Tenant = tenant, Waiver = waiverId }, tx, ct));
            if (waiver is null) return BusinessCommandOutcome.Rejected("F07_SOURCE_NOT_FOUND");
            if (waiver.ActorId == actor) return BusinessCommandOutcome.Rejected("F07_SEPARATION_REQUIRED");
            if (waiver.Approved) return BusinessCommandOutcome.Rejected("F07_WAIVER_EXISTS");
            issueId = waiver.IssueId;
        }
        else
        {
            if (input.IssueId is not Guid selected || input.Quantity is not > 0 || input.CustomerId.HasValue || input.WaiverId.HasValue)
                return BusinessCommandOutcome.Rejected("F07_FIELDS_INVALID");
            issueId = selected;
        }

        var issue = await db.QuerySingleOrDefaultAsync<F07Issue>(Sql($"""
            SELECT q.Id,q.CustomerId,q.Quantity,q.CostAmount,q.Returned,q.ReturnedCost
            FROM dbo.Customer c JOIN dbo.F07Program p ON p.TenantId=c.TenantId
            CROSS APPLY ({GiftCounts}) q
            WHERE c.TenantId=@Tenant AND q.Id=@Issue AND {GiftAssignment}
            """, new { Tenant = tenant, Issue = issueId, Role = role, Actor = actor }, tx, ct));
        if (issue is null) return BusinessCommandOutcome.Rejected("F07_SOURCE_NOT_FOUND");
        int quantity = waiver?.Quantity ?? input.Quantity!.Value;
        if (quantity > issue.Quantity - issue.Returned) return BusinessCommandOutcome.Rejected("F07_GIFT_QUANTITY_EXCEEDED");

        if (input.Action == "RETURN")
            return await ReceiveGift(db, tx, tenant, actor, command, sourceNumber, program, issue, quantity, request, ct);

        if (waiver is null && await db.ExecuteScalarAsync<int>(Sql("""
            SELECT COUNT(*) FROM dbo.F07Waiver q LEFT JOIN dbo.F07WaiverDecision d
                ON d.TenantId=q.TenantId AND d.WaiverId=q.Id
            WHERE q.TenantId=@Tenant AND q.IssueId=@Issue AND (d.Id IS NOT NULL OR q.Quantity<=@Held)
            """, new { Tenant = tenant, Issue = issueId, Held = issue.Quantity - issue.Returned }, tx, ct)) != 0)
            return BusinessCommandOutcome.Rejected("F07_WAIVER_EXISTS");

        var position = await FindGiftPosition(db, tx, tenant, actor, role, issue.CustomerId, ct);
        if (position is null || quantity > position.GiftHeld - position.Entitlement - position.WaivedHeld)
            return BusinessCommandOutcome.Rejected("F07_WAIVER_SCOPE_EXCEEDED");

        bool approve = waiver is not null;
        string table = approve ? "F07WaiverDecision" : "F07Waiver";
        if (await GiftEvidenceExists(db, tx, tenant, table, evidence, ct))
            return BusinessCommandOutcome.Rejected("F07_EVIDENCE_USED");
        Guid id = await AddSource(db, tx, tenant, command, "F07_" + input.Action, sourceNumber, actor, issue.CustomerId, null, ct);
        await AddLine(db, tx, tenant, id, 1, program.GiftSkuId, quantity, null, ct);
        string insert = approve
            ? "INSERT dbo.F07WaiverDecision(TenantId,Id,WaiverId,ActorId,Evidence,Reason) VALUES(@Tenant,@Id,@Waiver,@Actor,@Evidence,@Reason)"
            : "INSERT dbo.F07Waiver(TenantId,Id,IssueId,Quantity,ActorId,Evidence,Reason) VALUES(@Tenant,@Id,@Issue,@Quantity,@Actor,@Evidence,@Reason)";
        await db.ExecuteAsync(Sql(insert, new { Tenant = tenant, Id = id, Issue = issueId,
            Waiver = input.WaiverId, Quantity = quantity, Actor = actor, Evidence = evidence, Reason = reason }, tx, ct));
        return BusinessCommandOutcome.Applied("F07_" + input.Action, id);
    }

    private static async Task<bool> GiftEvidenceExists(SqlConnection db, SqlTransaction tx, string tenant, string table,
        string evidence, CancellationToken ct) => await db.ExecuteScalarAsync<int>(Sql(
            $"SELECT COUNT(*) FROM dbo.{table} WHERE TenantId=@Tenant AND Evidence=@Evidence",
            new { Tenant = tenant, Evidence = evidence }, tx, ct)) != 0;

    private static async Task<BusinessCommandOutcome> IssueGift(SqlConnection db, SqlTransaction tx, string tenant,
        string actor, Guid command, string sourceNumber, F07Program program, Guid customer, F07Command input, CancellationToken ct)
    {
        var position = await FindGiftPosition(db, tx, tenant, actor, "sales", customer, ct);
        if (position is null || await db.ExecuteScalarAsync<int>(Sql("""
            SELECT COUNT(*) FROM dbo.Customer WHERE TenantId=@Tenant AND Id=@Customer AND IsActive=1
            """, new { Tenant = tenant, Customer = customer }, tx, ct)) != 1)
            return BusinessCommandOutcome.Rejected("F07_SOURCE_NOT_FOUND");
        int quantity = input.Quantity!.Value;
        if (quantity > position.ToDeliver) return BusinessCommandOutcome.Rejected("F07_ENTITLEMENT_EXCEEDED");

        if (await GiftEvidenceExists(db, tx, tenant, "F07Issue", input.Evidence!, ct))
            return BusinessCommandOutcome.Rejected("F07_EVIDENCE_USED");
        var stock = await GiftStock(db, tx, tenant, program.GiftSkuId, program.GiftLocationId, "WAREHOUSE", ct);
        if (stock is null || quantity > stock.OnHandBaseQuantity)
            return BusinessCommandOutcome.Rejected("F07_STOCK_UNAVAILABLE");
        // The dedicated program uses whole gifts and VND. The last issue consumes the exact residual.
        long cost = (long)decimal.Round(stock.OnHandCostAmount * quantity / stock.OnHandBaseQuantity,
            0, MidpointRounding.AwayFromZero);
        Guid id = await AddSource(db, tx, tenant, command, "F07_ISSUE", sourceNumber, actor, customer, null, ct);
        Guid line = await AddLine(db, tx, tenant, id, 1, program.GiftSkuId, quantity, cost, ct);
        await UpdateReturnBalance(db, tx, tenant, program.GiftSkuId, program.GiftLocationId, -quantity, -cost, ct);
        await ReturnEffect(db, tx, tenant, line, "GIFT-OUT", "PHYSICAL_STOCK", program.GiftSkuId,
            program.GiftLocationId, -quantity, -cost, ct);
        await db.ExecuteAsync(Sql("""
            INSERT dbo.F07Issue(TenantId,Id,ProgramId,CustomerId,Quantity,CostAmount,ActorId,Evidence,Reason)
            VALUES(@Tenant,@Id,@Program,@Customer,@Quantity,@Cost,@Actor,@Evidence,@Reason)
            """, new { Tenant = tenant, Id = id, Program = program.Id, Customer = customer, Quantity = quantity,
                Cost = cost, Actor = actor, input.Evidence, input.Reason }, tx, ct));
        return BusinessCommandOutcome.Applied("F07_ISSUE", id);
    }

    private static async Task<BusinessCommandOutcome> ReceiveGift(SqlConnection db, SqlTransaction tx, string tenant,
        string actor, Guid command, string sourceNumber, F07Program program, F07Issue issue, int quantity,
        F07Command input, CancellationToken ct)
    {
        if (await GiftEvidenceExists(db, tx, tenant, "F07GiftReturn", input.Evidence!, ct))
            return BusinessCommandOutcome.Rejected("F07_EVIDENCE_USED");
        if (await GiftStock(db, tx, tenant, program.GiftSkuId, program.QuarantineLocationId, "QUARANTINE", ct) is null)
            return BusinessCommandOutcome.Rejected("F07_STOCK_UNAVAILABLE");

        long cumulative = (long)decimal.Round((decimal)issue.CostAmount * (issue.Returned + quantity) / issue.Quantity,
            0, MidpointRounding.AwayFromZero);
        long cost = cumulative - issue.ReturnedCost;
        Guid id = await AddSource(db, tx, tenant, command, "F07_RETURN", sourceNumber, actor, issue.CustomerId, null, ct);
        Guid line = await AddLine(db, tx, tenant, id, 1, program.GiftSkuId, quantity, cost, ct);
        await UpdateReturnBalance(db, tx, tenant, program.GiftSkuId, program.QuarantineLocationId, quantity, cost, ct);
        await ReturnEffect(db, tx, tenant, line, "GIFT-QA-IN", "PHYSICAL_STOCK", program.GiftSkuId,
            program.QuarantineLocationId, quantity, cost, ct);
        await db.ExecuteAsync(Sql("""
            INSERT dbo.F07GiftReturn(TenantId,Id,IssueId,Quantity,CostAmount,ActorId,Evidence,Reason)
            VALUES(@Tenant,@Id,@Issue,@Quantity,@Cost,@Actor,@Evidence,@Reason)
            """, new { Tenant = tenant, Id = id, Issue = issue.Id, Quantity = quantity, Cost = cost,
                Actor = actor, input.Evidence, input.Reason }, tx, ct));
        return BusinessCommandOutcome.Applied("F07_RETURN", id);
    }

    private static Task<F05Stock?> GiftStock(SqlConnection db, SqlTransaction tx, string tenant, Guid sku,
        Guid location, string kind, CancellationToken ct) => db.QuerySingleOrDefaultAsync<F05Stock>(Sql("""
            SELECT b.OnHandBaseQuantity,b.OnHandCostAmount
            FROM dbo.PhysicalStockBalance b WITH (UPDLOCK,HOLDLOCK)
            JOIN dbo.StockLocation l ON l.TenantId=b.TenantId AND l.Id=b.StockLocationId
            WHERE b.TenantId=@Tenant AND b.SkuId=@Sku AND b.StockLocationId=@Location AND l.Kind=@Kind AND l.IsActive=1
                AND b.OnHandBaseQuantity=ROUND(b.OnHandBaseQuantity,0) AND b.OnHandCostAmount=ROUND(b.OnHandCostAmount,0)
                AND NOT EXISTS (SELECT 1 FROM dbo.StockPool p WHERE p.TenantId=b.TenantId AND p.SkuId=b.SkuId
                    AND p.StockLocationId=b.StockLocationId)
            """, new { Tenant = tenant, Sku = sku, Location = location, Kind = kind }, tx, ct));
}
