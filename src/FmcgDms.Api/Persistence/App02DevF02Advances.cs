using System.Security.Claims;
using Dapper;
using static FmcgDms.Api.Persistence.C1Validation;

namespace FmcgDms.Api.Persistence;

public sealed record F02Command(Guid CommandId, string? Action, long Amount, string? Evidence,
    Guid? AdvanceId, Guid? ReferenceId, string? EmployeeId, string? DecisionReason) : IC1Command;

public sealed record F02Advance(Guid Id, string EmployeeId, long Issued, string Currency, string Evidence,
    long ApprovedExpenses, long AcceptedReturns, long PendingReturns, long Outstanding);

public sealed record F02Entry(Guid Id, string Kind, Guid? ReferenceId, long Amount, string ActorId, string Evidence,
    string? DecisionReason);
public sealed record F02Detail(Guid Id, long Outstanding, IReadOnlyList<F02Entry> Entries);
public sealed record F02Session(string TenantId, string ActorId, string SessionId, string Role);

internal sealed partial class App02c1Service
{
    private const string AdvancePrefix = "F02_";
    internal const string AdvanceSummarySql = """
        SELECT a.Id,a.EmployeeId,a.Issued,a.Currency,a.Evidence,
               totals.Expenses AS ApprovedExpenses,totals.Accepted AS AcceptedReturns,
               totals.Offered-totals.Accepted AS PendingReturns,
               a.Issued-totals.Expenses-totals.Accepted AS Outstanding
        FROM dbo.F02Advance a
        CROSS APPLY (
            SELECT COALESCE(SUM(CASE WHEN Kind='EXPENSE_APPROVE' THEN Amount END),0) AS Expenses,
                   COALESCE(SUM(CASE WHEN Kind='RETURN_ACCEPT' THEN Amount END),0) AS Accepted,
                   COALESCE(SUM(CASE WHEN Kind='RETURN_OFFER' THEN Amount END),0) AS Offered
            FROM dbo.F02AdvanceEntry WHERE TenantId=a.TenantId AND AdvanceId=a.Id
        ) totals
        WHERE a.TenantId=@Tenant AND (@Employee IS NULL OR a.EmployeeId=@Employee)
        """;

    public Task<F02Session> AdvanceIdentity(ClaimsPrincipal principal, CancellationToken ct) =>
        ReadAdvance(principal, async (s, role) => new F02Session(s.Tenant, s.Actor,
            await s.Db.QuerySingleAsync<string>(s.Sql("SELECT Nonce FROM dbo.App00ScratchMarker", null)), role), ct);

    public Task<IReadOnlyList<F02Advance>> Advances(ClaimsPrincipal principal, CancellationToken ct) =>
        ReadAdvance<IReadOnlyList<F02Advance>>(principal, async (s, role) =>
            (await s.Db.QueryAsync<F02Advance>(s.Sql(AdvanceSummarySql + " ORDER BY a.OccurredAtUtc DESC,a.Id",
                new { s.Tenant, Employee = role == "sales" ? s.Actor : null }))).AsList(), ct);

    public Task<IReadOnlyList<string>> AdvanceEmployees(ClaimsPrincipal principal, CancellationToken ct) =>
        InSession<IReadOnlyList<string>>(principal, async s =>
            (await s.Db.QueryAsync<string>(s.Sql("""
                SELECT ActorId FROM dbo.App02ActorRoleGrant
                WHERE TenantId=@Tenant AND RoleCode=N'sales' AND IsActive=1 AND ActorId<>@Actor
                ORDER BY ActorId
                """, new { s.Tenant, s.Actor }))).AsList(), ct, "finance");

    public Task<F02Detail?> AdvanceDetail(ClaimsPrincipal principal, Guid id, CancellationToken ct) =>
        ReadAdvance<F02Detail?>(principal, async (s, role) =>
        {
            var advance = await LoadAdvance(s, id, role == "sales" ? s.Actor : null);
            if (advance is null) return null;
            var entries = await s.Db.QueryAsync<F02Entry>(s.Sql("""
                SELECT Id,Kind,ReferenceId,Amount,ActorId,Evidence,DecisionReason FROM dbo.F02AdvanceEntry
                WHERE TenantId=@Tenant AND AdvanceId=@AdvanceId ORDER BY OccurredAtUtc,Id
                """, new { s.Tenant, AdvanceId = id }));
            return new F02Detail(id, advance.Outstanding, entries.AsList());
        }, ct);

    public Task<C1Outcome?> AdvanceOutcome(ClaimsPrincipal principal, Guid id, CancellationToken ct) =>
        ReadAdvance<C1Outcome?>(principal, async (s, _) =>
        {
            var old = await s.Outcome(id);
            if (old is null || !old.Kind.StartsWith(AdvancePrefix, StringComparison.Ordinal)) return null;
            string? commandRole = AdvanceActionRole(old.Kind[AdvancePrefix.Length..]);
            if (commandRole is null || !principal.HasClaim("synthetic_role", commandRole))
                throw new UnauthorizedAccessException("Advance command role required.");
            await App02a3Sql.RequireRole(s.Db, s.Tx, s.Tenant, s.Actor, commandRole, ct);
            return old.ToOutcome();
        }, ct);

    public Task<C1Outcome> RecordAdvance(ClaimsPrincipal principal, F02Command input, CancellationToken ct)
    {
        string? role = AdvanceActionRole(input.Action);
        return role is null
            ? Task.FromResult(new C1Outcome(400, "ADVANCE_FIELDS_INVALID"))
            : Run(principal, input, AdvancePrefix + input.Action, s => DecideAdvance(s, input), ct, role: role);
    }

    private Task<T> ReadAdvance<T>(ClaimsPrincipal principal, Func<Session, string, Task<T>> work,
        CancellationToken ct) => InBoundSession(principal, async s =>
    {
        var roles = await s.Db.QueryAsync<string>(s.Sql("""
            SELECT RoleCode FROM dbo.App02ActorRoleGrant
            WHERE TenantId=@Tenant AND ActorId=@Actor AND IsActive=1 AND RoleCode IN ('finance','cashier','sales')
            ORDER BY CASE RoleCode WHEN 'finance' THEN 1 WHEN 'cashier' THEN 2 ELSE 3 END
            """, new { s.Tenant, s.Actor }));
        string? role = roles.FirstOrDefault(candidate => principal.HasClaim("synthetic_role", candidate));
        if (role is null) throw new UnauthorizedAccessException("Current advance role required.");
        return await work(s, role);
    }, ct);

    private static string? AdvanceActionRole(string? action) => action switch
    {
        "ISSUE" or "EXPENSE_APPROVE" => "finance",
        "EXPENSE_REQUEST" or "RETURN_OFFER" => "sales",
        "RETURN_ACCEPT" => "cashier",
        _ => null
    };

    private static Task<F02Advance?> LoadAdvance(Session s, Guid id, string? employee) =>
        s.Db.QuerySingleOrDefaultAsync<F02Advance>(s.Sql(AdvanceSummarySql + " AND a.Id=@Id",
            new { s.Tenant, Employee = employee, Id = id }));

    private static async Task<Change> DecideAdvance(Session s, F02Command input)
    {
        string? evidence = Text(input.Evidence, 120);
        if (evidence is null || input.Amount is <= 0 or > 1000000000)
            return Reject(400, "ADVANCE_FIELDS_INVALID");
        if (input.Action == "ISSUE")
            return input.DecisionReason is null ? await IssueAdvance(s, input, evidence)
                : Reject(400, "ADVANCE_FIELDS_INVALID");
        if (input.AdvanceId is not Guid advanceId || advanceId == Guid.Empty || input.EmployeeId is not null)
            return Reject(400, "ADVANCE_FIELDS_INVALID");

        string action = input.Action!;
        bool employeeAction = action is "EXPENSE_REQUEST" or "RETURN_OFFER";
        string? reason = Text(input.DecisionReason, 200);
        if (employeeAction ? input.DecisionReason is not null : reason is null)
            return Reject(400, "ADVANCE_FIELDS_INVALID");
        var advance = await LoadAdvance(s, advanceId, employeeAction ? s.Actor : null);
        if (advance is null) return Reject(404, "ADVANCE_NOT_FOUND");
        if (!employeeAction && string.Equals(s.Actor, advance.EmployeeId, StringComparison.Ordinal))
            return Reject(409, "ADVANCE_SELF_CHECK");

        var args = new { s.Tenant, s.Actor, AdvanceId = advanceId, Kind = action, input.ReferenceId,
            input.Amount, Evidence = evidence, DecisionReason = reason, input.CommandId, Id = Guid.NewGuid() };
        if (await s.Db.ExecuteScalarAsync<int>(s.Sql("""
            SELECT COUNT(*) FROM dbo.F02AdvanceEntry WHERE TenantId=@Tenant AND Kind=@Kind AND Evidence=@Evidence
            """, args)) != 0)
            return Reject(409, "ADVANCE_EVIDENCE_IN_USE");

        long available = advance.Outstanding;
        if (employeeAction)
        {
            if (input.ReferenceId.HasValue) return Reject(400, "ADVANCE_FIELDS_INVALID");
            if (action == "RETURN_OFFER") available -= advance.PendingReturns;
        }
        else
        {
            if (input.ReferenceId is null || input.ReferenceId == Guid.Empty)
                return Reject(400, "ADVANCE_FIELDS_INVALID");
            string parentKind = action == "EXPENSE_APPROVE" ? "EXPENSE_REQUEST" : "RETURN_OFFER";
            long? remaining = await s.Db.QuerySingleOrDefaultAsync<long?>(s.Sql("""
                SELECT parent.Amount-COALESCE((
                    SELECT SUM(m.Amount) FROM dbo.F02AdvanceEntry m
                    WHERE m.TenantId=@Tenant AND m.AdvanceId=@AdvanceId AND m.ReferenceId=parent.Id
                ),0)
                FROM dbo.F02AdvanceEntry parent WHERE parent.TenantId=@Tenant AND parent.AdvanceId=@AdvanceId
                    AND parent.Id=@ReferenceId AND parent.Kind=@ParentKind
                """, new { s.Tenant, AdvanceId = advanceId, input.ReferenceId, ParentKind = parentKind }));
            if (remaining is null) return Reject(404, "ADVANCE_REFERENCE_NOT_FOUND");
            if (action == "EXPENSE_APPROVE") available -= advance.PendingReturns;
            available = Math.Min(available, remaining.Value);
        }

        if (input.Amount > available) return Reject(409, "ADVANCE_AMOUNT_EXCEEDS_BALANCE");
        return Applied(args.Id, () => s.Db.ExecuteAsync(s.Sql("""
            INSERT dbo.F02AdvanceEntry(Id,TenantId,AdvanceId,ActorId,CommandId,Kind,ReferenceId,Amount,Evidence,DecisionReason)
            VALUES(@Id,@Tenant,@AdvanceId,@Actor,@CommandId,@Kind,@ReferenceId,@Amount,@Evidence,@DecisionReason)
            """, args)));
    }

    private static async Task<Change> IssueAdvance(Session s, F02Command input, string evidence)
    {
        string? employee = Text(input.EmployeeId, 128);
        if (employee is null || input.AdvanceId.HasValue || input.ReferenceId.HasValue)
            return Reject(400, "ADVANCE_FIELDS_INVALID");
        if (string.Equals(employee, s.Actor, StringComparison.Ordinal)) return Reject(409, "ADVANCE_SELF_CHECK");
        var args = new { s.Tenant, s.Actor, Employee = employee, input.Amount, input.CommandId,
            Evidence = evidence, Id = Guid.NewGuid() };
        if (await s.Db.ExecuteScalarAsync<int>(s.Sql("""
            SELECT COUNT(*) FROM dbo.App02ActorRoleGrant
            WHERE TenantId=@Tenant AND ActorId=@Employee AND RoleCode=N'sales' AND IsActive=1
            """, args)) != 1)
            return Reject(404, "ADVANCE_EMPLOYEE_NOT_FOUND");
        if (await s.Db.ExecuteScalarAsync<int>(s.Sql("""
            SELECT COUNT(*) FROM dbo.F02Advance WHERE TenantId=@Tenant AND Evidence=@Evidence
            """, args)) != 0)
            return Reject(409, "ADVANCE_EVIDENCE_IN_USE");
        return Applied(args.Id, () => s.Db.ExecuteAsync(s.Sql("""
            INSERT dbo.F02Advance(Id,TenantId,EmployeeId,ActorId,CommandId,Issued,Evidence)
            VALUES(@Id,@Tenant,@Employee,@Actor,@CommandId,@Amount,@Evidence)
            """, args)));
    }
}
