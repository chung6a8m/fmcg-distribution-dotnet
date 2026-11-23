using System.Data;
using System.Security.Claims;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Persistence;

public sealed record F08Command(Guid CommandId, string? Action, Guid? DeliveryId, Guid? CaseId, Guid? CreditId,
    Guid? RefundId, long? Amount, string? Reason, string? Evidence);
public sealed record F08DeliveryView(Guid Id, string Customer, string Sku, long Gross, decimal Outstanding);
public sealed record F08ReturnView(Guid Id, Guid DeliveryId, string Customer, string Sku, decimal Quantity, Guid? CreditId);
public sealed record F08CreditView(Guid Id, Guid CaseId, string Customer, long Net, long Tax, long Gross, long Discount, long Available);
public sealed record F08RefundView(Guid Id, Guid CreditId, string Customer, long Amount, string Status);

public sealed partial class App01a2CommandService
{
    private sealed record F08Sale(Guid Id, Guid CustomerId, Guid SkuId, Guid TripId, Guid ArId, decimal Quantity,
        decimal Outstanding, long NetAmount, long GrossAmount, long AllocatedDiscount);
    private sealed record F08Case(Guid DeliveryId, decimal Quantity);
    private sealed record F08Credit(Guid Id, Guid DeliveryId, long GrossAmount, long Paid, long Reserved);
    private sealed record F08Refund(Guid Id, Guid CreditId, long Amount, string Maker, string? Checker, bool Disbursed);
    private sealed record F08Witness(Guid Id, string ActorId);
    private static readonly string[] RefundRoles = ["finance", "cashier", "case_checker"];

    private const string RefundableSaleSql = """
        FROM dbo.SaleDelivery d
        JOIN dbo.F04DeliveryPricing p ON p.TenantId=d.TenantId AND p.DeliveryId=d.Id
        JOIN dbo.F04PriceLine f ON f.TenantId=p.TenantId AND f.OrderLineId=p.OrderLineId
        JOIN dbo.AOrderLine o ON o.TenantId=f.TenantId AND o.Id=f.OrderLineId AND o.BaseQuantity=d.BaseQuantity
        JOIN dbo.ReceivableOpenItem a ON a.TenantId=d.TenantId AND a.SaleDeliveryId=d.Id
        """;
    private const string RefundableSaleFilter = """
        d.TenantId=@Tenant AND d.RightKind='A' AND p.Currency='VND' AND p.GrossAmount=d.PayableAmount
            AND p.NetAmount=f.NetAmount AND p.TaxAmount=f.TaxAmount AND p.GrossAmount=f.GrossAmount AND d.InvoiceEligible=0
        """;

    public Task<F05Identity> RefundIdentity(ClaimsPrincipal user, CancellationToken ct) =>
        ReadF08(user, async (db, tx, tenant, actor, role) => new F05Identity(tenant, actor,
            await db.QuerySingleAsync<string>(Sql("SELECT Nonce FROM dbo.App00ScratchMarker", null, tx, ct)), role), ct);

    public Task<IEnumerable<F08DeliveryView>> RefundDeliveries(ClaimsPrincipal user, CancellationToken ct) =>
        ReadF08(user, (db, tx, tenant, _, _) => db.QueryAsync<F08DeliveryView>(Sql($"""
            SELECT d.Id,c.Name AS Customer,k.Code AS Sku,p.GrossAmount AS Gross,a.OutstandingAmount AS Outstanding
            {RefundableSaleSql}
            JOIN dbo.Customer c ON c.TenantId=d.TenantId AND c.Id=d.CustomerId
            JOIN dbo.Sku k ON k.TenantId=d.TenantId AND k.Id=d.SkuId
            WHERE {RefundableSaleFilter}
            ORDER BY c.Name,k.Code,d.Id
            """, new { Tenant = tenant }, tx, ct)), ct);

    public Task<IEnumerable<F08ReturnView>> RefundReturns(ClaimsPrincipal user, CancellationToken ct) =>
        ReadF08(user, (db, tx, tenant, _, _) => db.QueryAsync<F08ReturnView>(Sql($"""
            SELECT r.Id,d.Id AS DeliveryId,c.Name AS Customer,k.Code AS Sku,r.Quantity,x.Id AS CreditId
            {RefundableSaleSql}
            JOIN dbo.F05ReturnCase r ON r.TenantId=d.TenantId AND r.DeliveryId=d.Id
            JOIN dbo.F05ReturnStage s ON s.TenantId=r.TenantId AND s.CaseId=r.Id AND s.Stage='RELEASE'
            JOIN dbo.Customer c ON c.TenantId=d.TenantId AND c.Id=d.CustomerId
            JOIN dbo.Sku k ON k.TenantId=d.TenantId AND k.Id=d.SkuId
            LEFT JOIN dbo.F08ReturnCredit x ON x.TenantId=r.TenantId AND x.CaseId=r.Id
            WHERE {RefundableSaleFilter}
            ORDER BY c.Name,k.Code,r.Id
            """, new { Tenant = tenant }, tx, ct)), ct);

    public Task<IEnumerable<F08CreditView>> RefundCredits(ClaimsPrincipal user, CancellationToken ct) =>
        ReadF08(user, (db, tx, tenant, _, _) => db.QueryAsync<F08CreditView>(Sql("""
            SELECT x.Id,x.CaseId,c.Name AS Customer,x.NetAmount AS Net,x.TaxAmount AS Tax,x.GrossAmount AS Gross,
                x.DiscountAmount AS Discount,x.GrossAmount-COALESCE(paid.Amount,0) AS Available
            FROM dbo.F08ReturnCredit x JOIN dbo.SaleDelivery d ON d.TenantId=x.TenantId AND d.Id=x.DeliveryId
            JOIN dbo.Customer c ON c.TenantId=d.TenantId AND c.Id=d.CustomerId
            OUTER APPLY (SELECT SUM(s.Amount) AS Amount FROM dbo.F08Refund r JOIN dbo.F08Disbursement s
                ON s.TenantId=r.TenantId AND s.RefundId=r.Id WHERE r.TenantId=x.TenantId AND r.CreditId=x.Id) paid
            WHERE x.TenantId=@Tenant
            ORDER BY c.Name,x.Id
            """, new { Tenant = tenant }, tx, ct)), ct);

    public Task<IEnumerable<F08RefundView>> RefundRequests(ClaimsPrincipal user, CancellationToken ct) =>
        ReadF08(user, (db, tx, tenant, _, _) => db.QueryAsync<F08RefundView>(Sql("""
            SELECT r.Id,r.CreditId,c.Name AS Customer,r.Amount,
                CASE WHEN s.Id IS NOT NULL THEN 'DISBURSED' WHEN a.Id IS NOT NULL THEN 'APPROVED' ELSE 'PROPOSED' END AS Status
            FROM dbo.F08Refund r JOIN dbo.F08ReturnCredit x ON x.TenantId=r.TenantId AND x.Id=r.CreditId
            JOIN dbo.SaleDelivery d ON d.TenantId=x.TenantId AND d.Id=x.DeliveryId
            JOIN dbo.Customer c ON c.TenantId=d.TenantId AND c.Id=d.CustomerId
            LEFT JOIN dbo.F08RefundApproval a ON a.TenantId=r.TenantId AND a.RefundId=r.Id
            LEFT JOIN dbo.F08Disbursement s ON s.TenantId=r.TenantId AND s.RefundId=r.Id
            WHERE r.TenantId=@Tenant
            ORDER BY c.Name,r.Id
            """, new { Tenant = tenant }, tx, ct)), ct);

    private async Task<T> ReadF08<T>(ClaimsPrincipal user,
        Func<SqlConnection, SqlTransaction, string, string, string, Task<T>> read, CancellationToken ct)
    {
        if (!connections.UsesDevF08) throw new UnauthorizedAccessException("DEV-F08 disabled.");
        string tenant = App02a3Sql.Tenant(user);
        string actor = App02a3Sql.Actor(user);
        await using var db = await connections.OpenApp02c2Async(user, ct);
        await using var tx = (SqlTransaction)await db.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var active = await db.QueryAsync<string>(Sql("""
            SELECT scope.RoleCode FROM dbo.F08Scope scope
            JOIN dbo.App02ActorRoleGrant grantRow ON grantRow.TenantId=scope.TenantId AND grantRow.ActorId=scope.ActorId
                AND grantRow.RoleCode=scope.RoleCode AND grantRow.IsActive=1
            WHERE scope.TenantId=@Tenant AND scope.ActorId=@Actor
            """, new { Tenant = tenant, Actor = actor }, tx, ct));
        string role = RefundRoles.FirstOrDefault(role => active.Contains(role, StringComparer.Ordinal)
            && user.HasClaim("synthetic_role", role)) ?? throw new UnauthorizedAccessException("Current refund authority required.");
        T result = await read(db, tx, tenant, actor, role);
        await tx.CommitAsync(ct);
        return result;
    }

    private static async Task RequireRefundScope(SqlConnection db, SqlTransaction tx,
        string tenant, string actor, string role, CancellationToken ct)
    {
        if (await db.ExecuteScalarAsync<int>(Sql("""
            SELECT COUNT(*) FROM dbo.F08Scope WHERE TenantId=@Tenant AND ActorId=@Actor AND RoleCode=@Role
            """, new { Tenant = tenant, Actor = actor, Role = role }, tx, ct)) != 1)
            throw new UnauthorizedAccessException("Current refund authority required.");
    }

    private static C2CommandKey RefundKey(ClaimsPrincipal user, Guid id)
    {
        var key = C2Key(user, id);
        return new("F08-" + key.ProducerId[3..], key.CommandId, "F08-" + key.SourceNumber[3..]);
    }

    public Task<BusinessCommandOutcome> RecordRefund(ClaimsPrincipal user, F08Command input, CancellationToken ct)
    {
        if (!connections.UsesDevF08) throw new UnauthorizedAccessException("DEV-F08 disabled.");
        string? role = input.Action switch
        {
            "SETTLE" or "CREDIT" or "PROPOSE" or "APPROVE" => "finance",
            "DISBURSE" => "cashier",
            "WITNESS_PAYMENT" or "WITNESS_PAYOUT" => "case_checker",
            _ => null
        };
        if (role is null) return Task.FromResult(BusinessCommandOutcome.Rejected("F08_FIELDS_INVALID"));
        var key = RefundKey(user, input.CommandId);
        return ExecuteAsync(user, key, "F08_" + input.Action, [("Request", JsonSerializer.Serialize(input, C1Validation.Json))],
            (db, tx, tenant, actor, command, token) => ApplyRefund(db, tx, tenant, actor, command, key.SourceNumber,
                input, connections.UsesDevF06, token), ct, role, isF05Return: true);
    }

    public Task<BusinessCommandOutcome?> RefundOutcome(ClaimsPrincipal user, Guid id, CancellationToken ct) =>
        ReadF08<BusinessCommandOutcome?>(user, async (db, tx, tenant, actor, _) =>
        {
            var stored = await FindReturnCommandOutcome(db, tx, tenant, actor, RefundKey(user, id), ct);
            if (stored is null) return null;
            if (!user.HasClaim("synthetic_role", stored.RoleCode))
                throw new UnauthorizedAccessException("Current refund command role required.");
            await App02a3Sql.RequireRole(db, tx, tenant, actor, stored.RoleCode, ct);
            await RequireRefundScope(db, tx, tenant, actor, stored.RoleCode, ct);
            return new BusinessCommandOutcome(stored.Status, stored.OutcomeCode, stored.ReasonCode, true, stored.SourceId);
        }, ct);

    private static Task<F08Sale?> FindRefundableSale(SqlConnection db, SqlTransaction tx, string tenant,
        Guid delivery, CancellationToken ct) => db.QuerySingleOrDefaultAsync<F08Sale>(Sql($"""
            SELECT d.Id,d.CustomerId,d.SkuId,d.TripId,a.Id AS ArId,d.BaseQuantity AS Quantity,a.OutstandingAmount AS Outstanding,
                p.NetAmount,p.GrossAmount,f.AllocatedDiscount
            {RefundableSaleSql}
            WHERE {RefundableSaleFilter} AND d.Id=@Delivery
            """, new { Tenant = tenant, Delivery = delivery }, tx, ct));

    private static async Task<BusinessCommandOutcome> ApplyRefund(SqlConnection db, SqlTransaction tx,
        string tenant, string actor, Guid command, string number, F08Command input,
        bool enforcePeriod, CancellationToken ct)
    {
        string? reason = C1Validation.Text(input.Reason, 200);
        string? evidence = C1Validation.Text(input.Evidence, 120);
        if (reason is null || evidence is null || input.Amount is <= 0 or > 999999999999999)
            return BusinessCommandOutcome.Rejected("F08_FIELDS_INVALID");

        string? table = (input.Action, input.DeliveryId, input.CaseId, input.CreditId, input.RefundId, input.Amount) switch
        {
            ("WITNESS_PAYMENT", Guid, null, null, null, long) => "F08PaymentWitness",
            ("WITNESS_PAYOUT", null, null, null, Guid, long) => "F08PayoutWitness",
            ("SETTLE", Guid, null, null, null, long) => "F08Settlement",
            ("CREDIT", null, Guid, null, null, null) => "F08ReturnCredit",
            ("PROPOSE", null, null, Guid, null, long) => "F08Refund",
            ("APPROVE", null, null, null, Guid, null) => "F08RefundApproval",
            ("DISBURSE", null, null, null, Guid, long) => "F08Disbursement",
            _ => null
        };
        if (table is null) return BusinessCommandOutcome.Rejected("F08_FIELDS_INVALID");
        await App02a3Sql.Lock(db, tx, "DEVF08:REFUND:" + tenant, ct);
        if (enforcePeriod)
        {
            DateTime date = await db.ExecuteScalarAsync<DateTime>(Sql($"SELECT {LotBusinessDate}", null, tx, ct));
            if (await PeriodState(db, tx, tenant, MonthStart(date), ct) != "OPEN")
                return BusinessCommandOutcome.Rejected("F06_OPEN_PERIOD_REQUIRED");
        }
        if (await db.ExecuteScalarAsync<int>(Sql($"SELECT COUNT(*) FROM dbo.{table} WHERE TenantId=@Tenant AND Evidence=@Evidence",
            new { Tenant = tenant, Evidence = evidence }, tx, ct)) != 0)
            return BusinessCommandOutcome.Rejected("F08_EVIDENCE_USED");

        input = input with { Reason = reason, Evidence = evidence };
        if (input.Action == "WITNESS_PAYMENT")
            return await WitnessSalePayment(db, tx, tenant, actor, command, number, input, ct);
        if (input.Action == "SETTLE")
            return await SettleRefundSale(db, tx, tenant, actor, command, number, input, ct);
        if (input.Action == "CREDIT")
            return await PostReturnCredit(db, tx, tenant, actor, command, number, input, ct);
        return await ApplyCreditRefund(db, tx, tenant, actor, command, number, input, ct);
    }

    private static async Task<BusinessCommandOutcome> SettleRefundSale(SqlConnection db, SqlTransaction tx,
        string tenant, string actor, Guid command, string number, F08Command input, CancellationToken ct)
    {
        var sale = await FindRefundableSale(db, tx, tenant, input.DeliveryId!.Value, ct);
        if (sale is null) return BusinessCommandOutcome.Rejected("F08_SOURCE_NOT_FOUND");
        long amount = input.Amount!.Value;
        if (amount != sale.Outstanding) return BusinessCommandOutcome.Rejected("F08_PAYMENT_AMOUNT_MISMATCH");
        var proof = await db.QuerySingleOrDefaultAsync<F08Witness>(Sql("""
            SELECT Id,ActorId FROM dbo.F08PaymentWitness
            WHERE TenantId=@Tenant AND DeliveryId=@Delivery AND Amount=@Amount AND Evidence=@Evidence
            """, new { Tenant = tenant, Delivery = sale.Id, Amount = amount, input.Evidence }, tx, ct));
        if (proof is null) return BusinessCommandOutcome.Rejected("F08_WITNESS_REQUIRED");
        if (proof.ActorId == actor) return BusinessCommandOutcome.Rejected("F08_SEPARATION_REQUIRED");

        Guid id = await AddSource(db, tx, tenant, command, "F08_SETTLE", number, actor, sale.CustomerId, sale.TripId, ct);
        Guid line = await AddLine(db, tx, tenant, id, 1, sale.SkuId, null, amount, ct);
        if (await db.ExecuteAsync(Sql("""
            UPDATE dbo.ReceivableOpenItem SET AppliedAmount=AppliedAmount+@Amount
            WHERE TenantId=@Tenant AND Id=@ArId AND OutstandingAmount=@Amount
            """, new { Tenant = tenant, sale.ArId, Amount = amount }, tx, ct)) != 1)
            throw new InvalidOperationException("Receivable changed during settlement.");
        await db.ExecuteAsync(Sql("""
            INSERT dbo.F08Settlement(TenantId,Id,WitnessId,DeliveryId,Amount,ActorId,Evidence,Reason)
            VALUES(@Tenant,@Id,@Witness,@Delivery,@Amount,@Actor,@Evidence,@Reason)
            """, new { Tenant = tenant, Id = id, Witness = proof.Id, Delivery = sale.Id, Amount = amount, Actor = actor,
                input.Evidence, input.Reason }, tx, ct));
        await RefundEffect(db, tx, tenant, line, "F08-PAID-AR", "AR", sale.CustomerId, -amount, ct);
        await RefundEffect(db, tx, tenant, line, "F08-VERIFIED-PAYMENT", "BANK_CUSTOMER_RECEIPT", sale.CustomerId, amount, ct);
        return BusinessCommandOutcome.Applied("F08_SETTLE", id);
    }

    private static async Task<BusinessCommandOutcome> PostReturnCredit(SqlConnection db, SqlTransaction tx,
        string tenant, string actor, Guid command, string number, F08Command input, CancellationToken ct)
    {
        var origin = await db.QuerySingleOrDefaultAsync<F08Case>(Sql("""
            SELECT r.DeliveryId,r.Quantity FROM dbo.F05ReturnCase r JOIN dbo.F05ReturnStage s
                ON s.TenantId=r.TenantId AND s.CaseId=r.Id AND s.Stage='RELEASE'
            WHERE r.TenantId=@Tenant AND r.Id=@Case
            """, new { Tenant = tenant, Case = input.CaseId }, tx, ct));
        if (origin is null) return BusinessCommandOutcome.Rejected("F08_RETURN_NOT_RELEASED");
        var sale = await FindRefundableSale(db, tx, tenant, origin.DeliveryId, ct);
        if (sale is null) return BusinessCommandOutcome.Rejected("F08_SOURCE_NOT_FOUND");
        if (sale.Outstanding != 0 || await db.ExecuteScalarAsync<int>(Sql("""
            SELECT COUNT(*) FROM dbo.F08Settlement WHERE TenantId=@Tenant AND DeliveryId=@Delivery
            """, new { Tenant = tenant, Delivery = sale.Id }, tx, ct)) != 1)
            return BusinessCommandOutcome.Rejected("F08_PAYMENT_REQUIRED");
        if (await db.ExecuteScalarAsync<int>(Sql("""
            SELECT COUNT(*) FROM dbo.F08ReturnCredit WHERE TenantId=@Tenant AND CaseId=@Case
            """, new { Tenant = tenant, Case = input.CaseId }, tx, ct)) != 0)
            return BusinessCommandOutcome.Rejected("F08_RETURN_ALREADY_CREDITED");

        decimal prior = await db.ExecuteScalarAsync<decimal>(Sql("""
            SELECT COALESCE(SUM(Quantity),0) FROM dbo.F08ReturnCredit WHERE TenantId=@Tenant AND DeliveryId=@Delivery
            """, new { Tenant = tenant, Delivery = sale.Id }, tx, ct));
        var source = F04Pricing.Portion(sale.NetAmount, sale.GrossAmount, sale.Quantity, prior, origin.Quantity);
        long discount = F04Pricing.Portion(sale.AllocatedDiscount, sale.AllocatedDiscount, sale.Quantity, prior, origin.Quantity).Gross;
        Guid id = await AddSource(db, tx, tenant, command, "F08_CREDIT", number, actor, sale.CustomerId, sale.TripId, ct);
        Guid line = await AddLine(db, tx, tenant, id, 1, sale.SkuId, origin.Quantity, source.Gross, ct);
        await db.ExecuteAsync(Sql("""
            INSERT dbo.F08ReturnCredit(TenantId,Id,CaseId,DeliveryId,Quantity,NetAmount,TaxAmount,GrossAmount,DiscountAmount,ActorId,Evidence,Reason)
            VALUES(@Tenant,@Id,@Case,@Delivery,@Quantity,@Net,@Tax,@Gross,@Discount,@Actor,@Evidence,@Reason)
            """, new { Tenant = tenant, Id = id, Case = input.CaseId, Delivery = sale.Id, origin.Quantity,
                source.Net, source.Tax, source.Gross, Discount = discount, Actor = actor, input.Evidence, input.Reason }, tx, ct));
        if (source.Gross > 0)
            await RefundEffect(db, tx, tenant, line, "F08-CREDIT-ISSUED", "CUSTOMER_CREDIT", sale.CustomerId, source.Gross, ct);
        return BusinessCommandOutcome.Applied("F08_CREDIT", id);
    }

    private static async Task<BusinessCommandOutcome> ApplyCreditRefund(SqlConnection db, SqlTransaction tx,
        string tenant, string actor, Guid command, string number, F08Command input, CancellationToken ct)
    {
        F08Refund? refund = null;
        Guid creditId;
        if (input.Action == "PROPOSE")
        {
            creditId = input.CreditId!.Value;
        }
        else
        {
            refund = await db.QuerySingleOrDefaultAsync<F08Refund>(Sql("""
                SELECT r.Id,r.CreditId,r.Amount,r.ActorId AS Maker,a.ActorId AS Checker,
                    CONVERT(bit,CASE WHEN s.Id IS NULL THEN 0 ELSE 1 END) AS Disbursed
                FROM dbo.F08Refund r
                LEFT JOIN dbo.F08RefundApproval a ON a.TenantId=r.TenantId AND a.RefundId=r.Id
                LEFT JOIN dbo.F08Disbursement s ON s.TenantId=r.TenantId AND s.RefundId=r.Id
                WHERE r.TenantId=@Tenant AND r.Id=@Refund
                """, new { Tenant = tenant, Refund = input.RefundId }, tx, ct));
            if (refund is null) return BusinessCommandOutcome.Rejected("F08_SOURCE_NOT_FOUND");
            if (actor == refund.Maker || actor == refund.Checker)
                return BusinessCommandOutcome.Rejected("F08_SEPARATION_REQUIRED");
            creditId = refund.CreditId;
        }

        var credit = await db.QuerySingleOrDefaultAsync<F08Credit>(Sql("""
            SELECT x.Id,x.DeliveryId,x.GrossAmount,
                COALESCE(SUM(s.Amount),0) AS Paid,
                COALESCE(SUM(CASE WHEN a.Id IS NOT NULL AND s.Id IS NULL THEN r.Amount ELSE 0 END),0) AS Reserved
            FROM dbo.F08ReturnCredit x LEFT JOIN dbo.F08Refund r ON r.TenantId=x.TenantId AND r.CreditId=x.Id
            LEFT JOIN dbo.F08RefundApproval a ON a.TenantId=r.TenantId AND a.RefundId=r.Id
            LEFT JOIN dbo.F08Disbursement s ON s.TenantId=r.TenantId AND s.RefundId=r.Id
            WHERE x.TenantId=@Tenant AND x.Id=@Credit GROUP BY x.Id,x.DeliveryId,x.GrossAmount
            """, new { Tenant = tenant, Credit = creditId }, tx, ct));
        if (credit is null) return BusinessCommandOutcome.Rejected("F08_SOURCE_NOT_FOUND");
        var sale = await FindRefundableSale(db, tx, tenant, credit.DeliveryId, ct);
        if (sale is null) return BusinessCommandOutcome.Rejected("F08_SOURCE_NOT_FOUND");

        long amount = refund?.Amount ?? input.Amount!.Value;
        if (input.Action == "WITNESS_PAYOUT")
            return await WitnessPayout(db, tx, tenant, actor, command, number, input, refund!, sale, ct);

        F08Witness? proof = null;
        if (input.Action == "DISBURSE")
        {
            if (refund!.Checker is null || refund.Disbursed)
                return BusinessCommandOutcome.Rejected("F08_APPROVED_REFUND_REQUIRED");
            if (amount != input.Amount || amount > credit.GrossAmount - credit.Paid)
                return BusinessCommandOutcome.Rejected("F08_DISBURSEMENT_AMOUNT_MISMATCH");
            proof = await db.QuerySingleOrDefaultAsync<F08Witness>(Sql("""
                SELECT Id,ActorId FROM dbo.F08PayoutWitness
                WHERE TenantId=@Tenant AND RefundId=@Refund AND Amount=@Amount AND Evidence=@Evidence
                """, new { Tenant = tenant, Refund = refund.Id, Amount = amount, input.Evidence }, tx, ct));
            if (proof is null) return BusinessCommandOutcome.Rejected("F08_WITNESS_REQUIRED");
            if (proof.ActorId == actor) return BusinessCommandOutcome.Rejected("F08_SEPARATION_REQUIRED");
            if (await db.ExecuteAsync(Sql("""
                UPDATE dbo.F08Treasury SET AvailableAmount=AvailableAmount-@Amount
                WHERE TenantId=@Tenant AND AvailableAmount>=@Amount
                """, new { Tenant = tenant, Amount = amount }, tx, ct)) != 1)
                return BusinessCommandOutcome.Rejected("F08_TREASURY_UNAVAILABLE");
        }
        else
        {
            if (refund?.Checker is not null) return BusinessCommandOutcome.Rejected("F08_ALREADY_APPROVED");
            if (amount > credit.GrossAmount - credit.Paid - credit.Reserved)
                return BusinessCommandOutcome.Rejected("F08_CREDIT_EXCEEDED");
        }

        string kind = "F08_" + input.Action;
        Guid id = await AddSource(db, tx, tenant, command, kind, number, actor, sale.CustomerId, sale.TripId, ct);
        Guid line = await AddLine(db, tx, tenant, id, 1, sale.SkuId, null, amount, ct);
        string insert = input.Action switch
        {
            "PROPOSE" => """
                INSERT dbo.F08Refund(TenantId,Id,CreditId,Amount,ActorId,Evidence,Reason)
                VALUES(@Tenant,@Id,@Credit,@Amount,@Actor,@Evidence,@Reason)
                """,
            "APPROVE" => """
                INSERT dbo.F08RefundApproval(TenantId,Id,RefundId,ActorId,Evidence,Reason)
                VALUES(@Tenant,@Id,@Refund,@Actor,@Evidence,@Reason)
                """,
            _ => """
                INSERT dbo.F08Disbursement(TenantId,Id,WitnessId,RefundId,Amount,ActorId,Evidence,Reason)
                VALUES(@Tenant,@Id,@Witness,@Refund,@Amount,@Actor,@Evidence,@Reason)
                """
        };
        await db.ExecuteAsync(Sql(insert, new { Tenant = tenant, Id = id, Credit = creditId, Refund = refund?.Id,
            Witness = proof?.Id, Amount = amount, Actor = actor, input.Evidence, input.Reason }, tx, ct));
        if (input.Action == "DISBURSE")
        {
            await RefundEffect(db, tx, tenant, line, "F08-CREDIT-PAID", "CUSTOMER_CREDIT", sale.CustomerId, -amount, ct);
            await RefundEffect(db, tx, tenant, line, "F08-TREASURY-PAID", "REFUND_TREASURY", sale.CustomerId, -amount, ct);
        }
        return BusinessCommandOutcome.Applied(kind, id);
    }

    private static Task<int> RefundEffect(SqlConnection db, SqlTransaction tx, string tenant, Guid line, string key,
        string ledger, Guid customer, long amount, CancellationToken ct) => db.ExecuteAsync(Sql($"""
            INSERT dbo.LedgerEffect(Id,TenantId,BusinessSourceLineId,EffectKey,LedgerKind,CustomerId,QuantityDelta,AmountDelta,RecordedAtUtc)
            VALUES(NEWID(),@Tenant,@Line,@Key,@Ledger,@Customer,0,@Amount,{App02c6Clock.SqlUtcNow})
            """, new { Tenant = tenant, Line = line, Key = key, Ledger = ledger, Customer = customer, Amount = amount }, tx, ct));
}
