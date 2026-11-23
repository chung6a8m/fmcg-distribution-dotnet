using System.Security.Claims;
using Dapper;
using static FmcgDms.Api.Persistence.C1Validation;

namespace FmcgDms.Api.Persistence;

public sealed record F01VisitInput(Guid CommandId, Guid CustomerId, Guid? OutletId, string? Outcome,
    string? Reason, string? Observation) : IC1Command;

public sealed record F01Visit(Guid Id, Guid CustomerId, Guid? OutletId, string ActorId,
    string Outcome, string Reason, string? Observation, DateTime CompletedAtUtc);

internal sealed partial class App02c1Service
{
    private const string VisitCommand = "VISIT_COMPLETE";

    public Task<IReadOnlyList<C1Customer>> VisitCustomers(ClaimsPrincipal principal, CancellationToken ct) =>
        InSession<IReadOnlyList<C1Customer>>(principal, async s =>
            (await s.Db.QueryAsync<C1Customer>(s.Sql($"""
                SELECT {CustomerColumns} FROM dbo.Customer c
                WHERE c.TenantId=@Tenant AND EXISTS (
                    SELECT 1 FROM dbo.F01CustomerScope grantScope
                    WHERE grantScope.TenantId=c.TenantId AND grantScope.CustomerId=c.Id AND grantScope.ActorId=@Actor)
                ORDER BY Code,Id
                """, new { s.Tenant, s.Actor }))).AsList(), ct);

    public Task<C1Outcome?> VisitOutcome(ClaimsPrincipal principal, Guid commandId, CancellationToken ct) =>
        InSession<C1Outcome?>(principal, async s =>
        {
            if (await s.Outcome(commandId) is not { Kind: VisitCommand } old) return null;
            if (old.Id is not Guid id) return old.ToOutcome();
            Guid customerId = await s.Db.QuerySingleAsync<Guid>(s.Sql("""
                SELECT CustomerId FROM dbo.F01Visit WHERE TenantId=@Tenant AND ActorId=@Actor AND Id=@Id
                """, new { s.Tenant, s.Actor, Id = id }));
            if (!await Assigned(s, customerId)) throw new UnauthorizedAccessException("Customer assignment required.");
            return old.ToOutcome();
        }, ct);

    public Task<IReadOnlyList<F01Visit>> Visits(ClaimsPrincipal principal, Guid customerId, CancellationToken ct) =>
        InSession<IReadOnlyList<F01Visit>>(principal, async s =>
        {
            if (!await Assigned(s, customerId)) return [];
            var rows = await s.Db.QueryAsync<F01Visit>(s.Sql("""
                SELECT Id,CustomerId,OutletId,ActorId,Outcome,Reason,Observation,CompletedAtUtc
                FROM dbo.F01Visit
                WHERE TenantId=@Tenant AND CustomerId=@CustomerId
                ORDER BY CompletedAtUtc DESC,Id
                """, new { s.Tenant, CustomerId = customerId }));
            return rows.Select(row => row with
                { CompletedAtUtc = DateTime.SpecifyKind(row.CompletedAtUtc, DateTimeKind.Utc) }).ToArray();
        }, ct);

    public Task<C1Outcome> CompleteVisit(ClaimsPrincipal principal, F01VisitInput input, CancellationToken ct) =>
        Run(principal, input, VisitCommand, async s =>
        {
            string? reason = Text(input.Reason, 160);
            string? observation = Text(input.Observation, 500);
            if (reason is null || input.Outcome is not ("NO_ORDER" or "NOT_MET")
                || (input.Observation is not null && observation is null) || input.CustomerId == Guid.Empty)
                return Reject(400, "VISIT_FIELDS_INVALID");
            if (!await Assigned(s, input.CustomerId) || !await s.ActiveCustomer(input.CustomerId))
                return Reject(404, "CUSTOMER_NOT_FOUND");
            if (!await s.OutletMatches(input.CustomerId, input.OutletId)) return Reject(404, "OUTLET_NOT_FOUND");

            Guid id = Guid.NewGuid();
            return Applied(id, () => s.Db.ExecuteAsync(s.Sql($"""
                INSERT dbo.F01Visit(Id,TenantId,ActorId,CommandId,CustomerId,OutletId,Outcome,Reason,Observation,CompletedAtUtc)
                VALUES(@Id,@Tenant,@Actor,@CommandId,@CustomerId,@OutletId,@Outcome,@Reason,@Observation,{App02c6Clock.SqlUtcNow})
                """, new
            {
                Id = id, s.Tenant, s.Actor, input.CommandId, input.CustomerId, input.OutletId,
                input.Outcome, Reason = reason, Observation = observation
            })));
        }, ct, canReplay: s => Assigned(s, input.CustomerId));

    private static Task<bool> Assigned(Session s, Guid customerId) =>
        s.Db.ExecuteScalarAsync<bool>(s.Sql("""
            SELECT CAST(CASE WHEN EXISTS (
                SELECT 1 FROM dbo.F01CustomerScope WHERE TenantId=@Tenant AND ActorId=@Actor AND CustomerId=@CustomerId
            ) THEN 1 ELSE 0 END AS bit)
            """, new { s.Tenant, s.Actor, CustomerId = customerId }));
}
