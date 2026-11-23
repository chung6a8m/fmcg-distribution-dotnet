using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using FmcgDms.Api.Tenancy;
using Microsoft.Data.SqlClient;
using static FmcgDms.Api.Persistence.C1Validation;

namespace FmcgDms.Api.Persistence;

internal sealed partial class App02c1Service(TenantConnections connections)
{
    private const string VersionSql = "CONVERT(varchar(16),CONVERT(binary(8),RowVersion),2)";
    private const string CustomerColumns = "Id,Code,Name,IsActive," + VersionSql + " AS Version";
    private const string OutcomeSql = """
        SELECT CAST(HttpStatus AS int) AS HttpStatus,Code,ResultId AS Id,Revision,
               CAST(1 AS bit) AS Replayed,HashVersion,RequestHash,Kind
        FROM dbo.C1Command WHERE TenantId=@Tenant AND ActorId=@Actor AND CommandId=@CommandId
        """;
    private sealed record StoredOutcome(int HttpStatus, string Code, Guid? Id, int? Revision,
        bool Replayed, byte HashVersion, byte[] RequestHash, string Kind)
    {
        public C1Outcome ToOutcome() => new(HttpStatus, Code, Id, Revision, Replayed);
    }
    private sealed record Change(C1Outcome Outcome, Func<Task>? Apply = null);
    private sealed record RegisteredRow(Guid Id, int Revision, Guid? OutletId, string Payload);
    private sealed record SelectionRow(Guid Id, string Origin, Guid? OutletId, Guid? RegisteredId,
        int? RegisteredRevision, string Payload, DateTime CapturedAtUtc);

    private sealed record Session(SqlConnection Db, SqlTransaction Tx, string Tenant, string Actor, CancellationToken Ct)
    {
        public CommandDefinition Sql(string sql, object? args) => App02a3Sql.Cmd(sql, args, Tx, Ct);

        public Task<bool> ActiveCustomer(Guid customerId) => Db.ExecuteScalarAsync<bool>(Sql("""
            SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM dbo.Customer
                WHERE TenantId=@Tenant AND Id=@CustomerId AND IsActive=1) THEN 1 ELSE 0 END AS bit)
            """, new { Tenant, CustomerId = customerId }));

        public Task<bool> OutletMatches(Guid customerId, Guid? outletId) =>
            outletId is null ? Task.FromResult(true) : Db.ExecuteScalarAsync<bool>(Sql("""
                SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM dbo.C1Outlet
                    WHERE TenantId=@Tenant AND CustomerId=@CustomerId AND Id=@OutletId) THEN 1 ELSE 0 END AS bit)
                """, new { Tenant, CustomerId = customerId, OutletId = outletId }));

        public Task<RegisteredRow?> CurrentAddress(Guid customerId, Guid registeredId) =>
            Db.QuerySingleOrDefaultAsync<RegisteredRow>(Sql("""
                SELECT TOP(1) RegisteredId AS Id,Revision,OutletId,Payload
                FROM dbo.C1RegisteredShipToRevision
                WHERE TenantId=@Tenant AND CustomerId=@CustomerId AND RegisteredId=@RegisteredId
                ORDER BY Revision DESC
                """, new { Tenant, CustomerId = customerId, RegisteredId = registeredId }));

        public Task<StoredOutcome?> Outcome(Guid commandId) => Db.QuerySingleOrDefaultAsync<StoredOutcome>(
            Sql(OutcomeSql, new { Tenant, Actor, CommandId = commandId }));
    }

    public Task<IReadOnlyList<C1Customer>> Customers(ClaimsPrincipal principal, CancellationToken ct) =>
        InSession<IReadOnlyList<C1Customer>>(principal, async s =>
            (await s.Db.QueryAsync<C1Customer>(s.Sql(
                $"SELECT {CustomerColumns} FROM dbo.Customer WHERE TenantId=@Tenant ORDER BY Code,Id",
                new { s.Tenant }))).AsList(), ct);

    public Task<C1Detail?> Detail(ClaimsPrincipal principal, Guid id, CancellationToken ct) =>
        InSession<C1Detail?>(principal, async s =>
        {
            var scope = new { s.Tenant, Id = id };
            var customer = await s.Db.QuerySingleOrDefaultAsync<C1Customer>(s.Sql(
                $"SELECT {CustomerColumns} FROM dbo.Customer WHERE TenantId=@Tenant AND Id=@Id", scope));
            if (customer is null) return null;
            var outlets = await s.Db.QueryAsync<C1Outlet>(s.Sql($"""
                SELECT Id,Name,{VersionSql} AS Version
                FROM dbo.C1Outlet WHERE TenantId=@Tenant AND CustomerId=@Id ORDER BY Name,Id
                """, scope));
            var registered = await s.Db.QueryAsync<RegisteredRow>(s.Sql("""
                SELECT latest.RegisteredId AS Id,latest.Revision,latest.OutletId,latest.Payload
                FROM dbo.C1RegisteredShipTo r
                CROSS APPLY (
                    SELECT TOP(1) RegisteredId,Revision,OutletId,Payload FROM dbo.C1RegisteredShipToRevision v
                    WHERE v.TenantId=r.TenantId AND v.CustomerId=r.CustomerId AND v.RegisteredId=r.Id
                    ORDER BY Revision DESC
                ) latest
                WHERE r.TenantId=@Tenant AND r.CustomerId=@Id ORDER BY latest.RegisteredId
                """, scope));
            var selections = await s.Db.QueryAsync<SelectionRow>(s.Sql("""
                SELECT Id,Origin,OutletId,RegisteredId,RegisteredRevision,Payload,CapturedAtUtc
                FROM dbo.C1ShipToSelection WHERE TenantId=@Tenant AND CustomerId=@Id ORDER BY CapturedAtUtc DESC,Id
                """, scope));
            return new C1Detail(customer, outlets.AsList(), registered.Select(r =>
                new C1Registered(r.Id, r.Revision, r.OutletId, ReadAddress(r.Payload))).ToArray(), selections.Select(r =>
                new C1Selection(r.Id, r.Origin, r.OutletId, r.RegisteredId, r.RegisteredRevision, ReadAddress(r.Payload),
                    DateTime.SpecifyKind(r.CapturedAtUtc, DateTimeKind.Utc))).ToArray());
        }, ct);

    public Task<C1Outcome?> Outcome(ClaimsPrincipal principal, Guid commandId, CancellationToken ct) =>
        InSession(principal, async s => await s.Outcome(commandId) is { Kind: not VisitCommand } old
            && !old.Kind.StartsWith(AdvancePrefix, StringComparison.Ordinal) ? old.ToOutcome() : null, ct);

    public Task<C1Identity> Identity(ClaimsPrincipal principal, CancellationToken ct) =>
        InSession(principal, async s => new C1Identity(s.Tenant, s.Actor, await s.Db.QuerySingleAsync<string>(
            s.Sql("SELECT Nonce FROM dbo.App00ScratchMarker", null))), ct);

    public Task<C1Outcome> SaveCustomer(ClaimsPrincipal principal, C1CustomerInput input, CancellationToken ct) =>
        Run(principal, input, "CUSTOMER_SAVE", async s =>
        {
            string? code = Text(input.Code, 32)?.ToUpperInvariant();
            string? name = Text(input.Name, 256);
            if (name is null || !ValidEdit(input.Id, input.Version))
                return Reject(400, "CUSTOMER_FIELDS_INVALID");
            Guid id = input.Id ?? Guid.NewGuid();
            if (input.Id.HasValue)
            {
                var current = await s.Db.QuerySingleOrDefaultAsync<C1Customer>(s.Sql($"""
                    SELECT {CustomerColumns} FROM dbo.Customer WITH (UPDLOCK)
                    WHERE TenantId=@Tenant AND Id=@Id
                    """, new { s.Tenant, Id = id }));
                if (current is null) return Reject(404, "CUSTOMER_NOT_FOUND");
                if (!string.Equals(current.Version, input.Version, StringComparison.OrdinalIgnoreCase))
                    return Reject(409, "STALE_VERSION");
                if (string.Equals(current.Code, input.Code, StringComparison.Ordinal)) code = current.Code;
                else if (code is null || !CustomerCode().IsMatch(code)) return Reject(400, "CUSTOMER_FIELDS_INVALID");
            }
            else if (code is null || !CustomerCode().IsMatch(code)) return Reject(400, "CUSTOMER_FIELDS_INVALID");
            var args = new { s.Tenant, Id = id, Code = code, Name = name };
            if (await s.Db.ExecuteScalarAsync<int>(s.Sql("""
                SELECT COUNT(*) FROM dbo.Customer WITH (UPDLOCK,HOLDLOCK)
                WHERE TenantId=@Tenant AND Code=@Code AND Id<>@Id
                """, args)) != 0)
                return Reject(409, "CUSTOMER_CODE_IN_USE");
            string sql = input.Id.HasValue
                ? "UPDATE dbo.Customer SET Code=@Code,Name=@Name WHERE TenantId=@Tenant AND Id=@Id"
                : "INSERT dbo.Customer(Id,TenantId,Code,Name) VALUES(@Id,@Tenant,@Code,@Name)";
            return Applied(id, () => s.Db.ExecuteAsync(s.Sql(sql, args)));
        }, ct);

    public Task<C1Outcome> SaveOutlet(ClaimsPrincipal principal, C1OutletInput input, CancellationToken ct) =>
        Run(principal, input, "OUTLET_SAVE", async s =>
        {
            string? name = Text(input.Name, 256);
            if (name is null || !ValidEdit(input.Id, input.Version))
                return Reject(400, "OUTLET_FIELDS_INVALID");
            if (!await s.ActiveCustomer(input.CustomerId)) return Reject(404, "CUSTOMER_NOT_FOUND");
            Guid id = input.Id ?? Guid.NewGuid();
            var args = new { s.Tenant, input.CustomerId, Id = id, Name = name };
            if (input.Id.HasValue)
            {
                string? current = await s.Db.QuerySingleOrDefaultAsync<string>(s.Sql($"""
                    SELECT {VersionSql} FROM dbo.C1Outlet WITH (UPDLOCK)
                    WHERE TenantId=@Tenant AND CustomerId=@CustomerId AND Id=@Id
                    """, args));
                if (current is null) return Reject(404, "OUTLET_NOT_FOUND");
                if (!string.Equals(current, input.Version, StringComparison.OrdinalIgnoreCase))
                    return Reject(409, "STALE_VERSION");
            }
            string sql = input.Id.HasValue
                ? "UPDATE dbo.C1Outlet SET Name=@Name WHERE TenantId=@Tenant AND CustomerId=@CustomerId AND Id=@Id"
                : "INSERT dbo.C1Outlet(Id,TenantId,CustomerId,Name) VALUES(@Id,@Tenant,@CustomerId,@Name)";
            return Applied(id, () => s.Db.ExecuteAsync(s.Sql(sql, args)));
        }, ct);

    public Task<C1Outcome> SaveRegistered(ClaimsPrincipal principal, C1RegisteredInput input, CancellationToken ct) =>
        Run(principal, input, "REGISTERED_SAVE", async s =>
        {
            string? payload = AddressPayload(input.Address);
            if (payload is null) return Reject(400, "ADDRESS_INVALID");
            if (input.Id == Guid.Empty || (input.Id.HasValue ? input.Revision is null or <= 0 : input.Revision.HasValue))
                return Reject(400, "REVISION_INVALID");
            if (!await s.ActiveCustomer(input.CustomerId)) return Reject(404, "CUSTOMER_NOT_FOUND");
            if (!await s.OutletMatches(input.CustomerId, input.OutletId)) return Reject(404, "OUTLET_NOT_FOUND");
            Guid id = input.Id ?? Guid.NewGuid();
            int revision = 1;
            if (input.Id.HasValue)
            {
                var current = await s.CurrentAddress(input.CustomerId, id);
                if (current is null) return Reject(404, "REGISTERED_SHIP_TO_NOT_FOUND");
                if (current.Revision != input.Revision) return Reject(409, "STALE_VERSION");
                revision = checked(current.Revision + 1);
            }
            var args = new
            {
                s.Tenant, s.Actor, input.CustomerId, Id = id, input.CommandId,
                input.OutletId, Revision = revision, Payload = payload
            };
            return Applied(id, async () =>
            {
                if (!input.Id.HasValue)
                    await s.Db.ExecuteAsync(s.Sql("""
                        INSERT dbo.C1RegisteredShipTo(Id,TenantId,CustomerId) VALUES(@Id,@Tenant,@CustomerId)
                        """, args));
                await s.Db.ExecuteAsync(s.Sql("""
                    INSERT dbo.C1RegisteredShipToRevision
                        (TenantId,CustomerId,RegisteredId,Revision,OutletId,Payload,ActorId,CommandId)
                    VALUES(@Tenant,@CustomerId,@Id,@Revision,@OutletId,@Payload,@Actor,@CommandId)
                    """, args));
            }, revision);
        }, ct);

    public Task<C1Outcome> SelectAddress(ClaimsPrincipal principal, C1SelectionInput input, CancellationToken ct) =>
        Run(principal, input, "SHIP_TO_SELECT", async s =>
        {
            if (!await s.ActiveCustomer(input.CustomerId)) return Reject(404, "CUSTOMER_NOT_FOUND");
            string? payload;
            Guid? outletId = input.OutletId;
            string origin;
            if (input.RegisteredId is Guid registeredId)
            {
                if (input.Revision is null or <= 0 || input.Address is not null || input.OutletId.HasValue)
                    return Reject(400, "SELECTION_INVALID");
                var current = await s.CurrentAddress(input.CustomerId, registeredId);
                if (current is null) return Reject(404, "REGISTERED_SHIP_TO_NOT_FOUND");
                if (current.Revision != input.Revision) return Reject(409, "STALE_VERSION");
                payload = current.Payload;
                outletId = current.OutletId;
                origin = "REGISTERED";
            }
            else
            {
                if (input.Revision.HasValue) return Reject(400, "SELECTION_INVALID");
                payload = AddressPayload(input.Address);
                if (payload is null) return Reject(400, "ADDRESS_INVALID");
                if (!await s.OutletMatches(input.CustomerId, outletId)) return Reject(404, "OUTLET_NOT_FOUND");
                origin = "DIRECT";
            }
            Guid id = Guid.NewGuid();
            return Applied(id, () => s.Db.ExecuteAsync(s.Sql("""
                INSERT dbo.C1ShipToSelection
                    (Id,TenantId,CustomerId,OutletId,RegisteredId,RegisteredRevision,Origin,
                     ValidatorVersion,Payload,PayloadHash,ActorId,CommandId)
                VALUES(@Id,@Tenant,@CustomerId,@OutletId,@RegisteredId,@Revision,@Origin,
                       1,@Payload,@Hash,@Actor,@CommandId)
                """, new
            {
                Id = id, s.Tenant, s.Actor, input.CustomerId, input.RegisteredId, input.Revision, input.CommandId,
                OutletId = outletId, Origin = origin, Payload = payload, Hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload))
            })));
        }, ct);

    private Task<C1Outcome> Run<T>(ClaimsPrincipal principal, T input, string kind, Func<Session, Task<Change>> decide,
        CancellationToken ct, Func<Session, Task<bool>>? canReplay = null,
        string role = "sales") where T : IC1Command =>
        InSession(principal, async s =>
        {
            if (input.CommandId == Guid.Empty) return new C1Outcome(400, "COMMAND_ID_INVALID");
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(kind + "\n" + JsonSerializer.Serialize(input, Json)));

            // Local synthetic DEV has one writer at a time per tenant DB. A
            // retry sees the committed result before rechecking master versions.
            await App02a3Sql.Lock(s.Db, s.Tx, "APP02C1:WRITE", ct);
            var old = await s.Outcome(input.CommandId);
            if (old is not null)
            {
                if (old.HashVersion != 1 || !CryptographicOperations.FixedTimeEquals(old.RequestHash, hash))
                    return new C1Outcome(409, "COMMAND_PAYLOAD_CONFLICT");
                if (old.HttpStatus == 200 && canReplay is not null && !await canReplay(s))
                    throw new UnauthorizedAccessException("Customer assignment required.");
                return old.ToOutcome();
            }

            var change = await decide(s);
            var result = change.Outcome;
            await s.Db.ExecuteAsync(s.Sql("""
                INSERT dbo.C1Command(TenantId,ActorId,CommandId,Kind,HashVersion,RequestHash,HttpStatus,Code,ResultId,Revision)
                VALUES(@Tenant,@Actor,@CommandId,@Kind,1,@Hash,@HttpStatus,@Code,@Id,@Revision)
                """, new
            {
                s.Tenant, s.Actor, input.CommandId, Kind = kind, Hash = hash,
                result.HttpStatus, result.Code, result.Id, result.Revision
            }));
            if (change.Apply is not null) await change.Apply();
            return result;
        }, ct, role);

    private Task<T> InSession<T>(ClaimsPrincipal principal, Func<Session, Task<T>> work, CancellationToken ct,
        string role = "sales")
    {
        if (!principal.HasClaim("synthetic_role", role))
            throw new UnauthorizedAccessException("Current synthetic role required.");
        return InBoundSession(principal, async s =>
        {
            await App02a3Sql.RequireRole(s.Db, s.Tx, s.Tenant, s.Actor, role, ct);
            return await work(s);
        }, ct);
    }

    private async Task<T> InBoundSession<T>(ClaimsPrincipal principal, Func<Session, Task<T>> work, CancellationToken ct)
    {
        string tenant = App02a3Sql.Tenant(principal);
        string actor = App02a3Sql.Actor(principal);
        await using var db = await connections.OpenApp02c1Async(principal, ct);
        await using var tx = (SqlTransaction)await db.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var result = await work(new Session(db, tx, tenant, actor, ct));
        await tx.CommitAsync(ct);
        return result;
    }

    private static bool ValidEdit(Guid? id, string? version) =>
        id.HasValue ? id != Guid.Empty && version is not null && RowVersion().IsMatch(version) : version is null;

    private static C1Address ReadAddress(string payload) =>
        JsonSerializer.Deserialize<C1Address>(payload, Json) ?? throw new InvalidOperationException("Address payload missing.");

    private static Change Applied(Guid id, Func<Task> apply, int? revision = null) =>
        new(new C1Outcome(200, "APPLIED", id, revision), apply);

    private static Change Reject(int status, string code) => new(new C1Outcome(status, code));
}
