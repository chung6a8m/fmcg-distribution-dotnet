using System.Data;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Preprod.Authorization;

public static class B5Schema
{
    public const string Version = "B5_20261118_001";
}

// Actor/session must come from a B.3-validated JWT or a revalidated durable Worker
// source. ResourceTenantId is server-derived (never from a client connection hint).
// The registry binding/epoch must come from an authoritative current B.4 read.
public sealed record AuthorizationContext(
    Guid TenantId, Guid ResourceTenantId, Guid ActorId, Guid SessionId,
    Guid BindingId, long RegistryEpoch, long FenceEpoch, long MembershipEpoch,
    string PermissionCode, string ResourceKey, Guid? MakerId = null)
{
    public void Validate()
    {
        if (TenantId == Guid.Empty || ResourceTenantId != TenantId ||
            ActorId == Guid.Empty || SessionId == Guid.Empty || BindingId == Guid.Empty ||
            RegistryEpoch < 1 || FenceEpoch < 1 || MembershipEpoch < 1 ||
            string.IsNullOrWhiteSpace(PermissionCode) || PermissionCode.Length > 96 ||
            string.IsNullOrWhiteSpace(ResourceKey) || ResourceKey.Length > 160 ||
            PermissionCode.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')) ||
            ResourceKey.Any(char.IsControl) ||
            (MakerId.HasValue && (MakerId.Value == Guid.Empty || MakerId == ActorId)))
            throw new AuthorizationDeniedException("B5_ENVELOPE_DENIED");
    }
}

public sealed class AuthorizationDeniedException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

// A real session check is mandatory. In API, B.3 JwtBearer checks current
// SQL session; in future Worker, a separate durable-session recheck is required.
// The preflight is NOT a substitute for the B.5 SQL commit fence.
public delegate Task<bool> CurrentSessionCheck(Guid actorId, Guid sessionId,
    CancellationToken cancellationToken);

/// <summary>
/// Runs the authorization assertion and any protected effect in ONE SQL Server
/// transaction on ONE SqlConnection. Callers may use only a server-resolved,
/// quoted three-part tenant target on this same instance. A second connection,
/// deferred job or HTTP call is NOT fenced and must never be dispatched here.
/// The protected workload remains disabled in PREPROD until B.4 / B.3 / B.7
/// authoritative integration and physical operator acceptance.
/// </summary>
public sealed class SqlAuthorizationFence(string fenceConnectionString)
{
    // TEST ONLY: disposable LocalDB login to verify real SQL permission checks.
    internal Func<SqlConnection, CancellationToken, Task>? InitializeCiSession { get; init; }

    public async Task<T> ExecuteAsync<T>(AuthorizationContext authority,
        CurrentSessionCheck currentSession,
        Func<SqlConnection, SqlTransaction, CancellationToken, Task<T>> effect,
        CancellationToken cancellationToken)
    {
        authority.Validate();
        if (!await currentSession(authority.ActorId, authority.SessionId, cancellationToken))
            throw new AuthorizationDeniedException("B5_SESSION_DENIED");
        await using var connection = new SqlConnection(fenceConnectionString);
        await connection.OpenAsync(cancellationToken);
        if (InitializeCiSession is not null)
            await InitializeCiSession(connection, cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        try
        {
            await using var command = new SqlCommand("dbo.b5_AssertAuthority",
                connection, transaction) { CommandType = CommandType.StoredProcedure, CommandTimeout = 15 };
            command.Parameters.AddRange(
            [
                Parameter("@TenantId", authority.TenantId),
                Parameter("@ResourceTenantId", authority.ResourceTenantId),
                Parameter("@ActorId", authority.ActorId),
                Parameter("@SessionId", authority.SessionId),
                Parameter("@BindingId", authority.BindingId),
                Parameter("@RegistryEpoch", authority.RegistryEpoch),
                Parameter("@FenceEpoch", authority.FenceEpoch),
                Parameter("@MembershipEpoch", authority.MembershipEpoch),
                Parameter("@PermissionCode", authority.PermissionCode),
                Parameter("@ResourceKey", authority.ResourceKey),
                Parameter("@MakerId", authority.MakerId)
            ]);
            await command.ExecuteNonQueryAsync(cancellationToken);
            var result = await effect(connection, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (SqlException)
        {
            // No SQL infrastructure/authorization detail crosses this boundary.
            throw new AuthorizationDeniedException("B5_SQL_AUTHORITY_DENIED");
        }
    }

    internal static SqlParameter Parameter(string name, object? value) => new(
        name, value switch
        {
            Guid or Guid? => SqlDbType.UniqueIdentifier,
            long => SqlDbType.BigInt,
            _ => SqlDbType.NVarChar
        })
    {
        Value = value ?? DBNull.Value,
        Size = name == "@PermissionCode" ? 96 : name == "@ResourceKey" ? 160 : 0
    };
}

/// <summary>Security operator connection is separately provisioned; no API/Worker
/// registration of this class or automatic grant to the runtime SQL principal.</summary>
public sealed class SqlRevocationOperator(string securityConnectionString)
{
    public Task RevokeMemberAsync(Guid tenant, Guid actor, long expectedFenceEpoch,
        string evidenceRef, CancellationToken ct) => InvokeAsync("dbo.b5_RevokeMember",
            tenant, expectedFenceEpoch, evidenceRef, ct, actor);

    public Task SuspendTenantAsync(Guid tenant, long expectedFenceEpoch,
        string evidenceRef, CancellationToken ct) => InvokeAsync("dbo.b5_SuspendTenant",
            tenant, expectedFenceEpoch, evidenceRef, ct, null);

    private async Task InvokeAsync(string procedure, Guid tenant, long expectedFenceEpoch,
        string evidenceRef, CancellationToken ct, Guid? actor)
    {
        if (tenant == Guid.Empty || actor == Guid.Empty || expectedFenceEpoch < 1 ||
            string.IsNullOrWhiteSpace(evidenceRef) || evidenceRef.Length > 160)
            throw new AuthorizationDeniedException("B5_REVOKE_INPUT_DENIED");
        await using var connection = new SqlConnection(securityConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(procedure, connection)
        { CommandType = CommandType.StoredProcedure };
        command.Parameters.AddRange([
            SqlAuthorizationFence.Parameter("@TenantId", tenant),
            SqlAuthorizationFence.Parameter("@ExpectedFenceEpoch", expectedFenceEpoch),
            SqlAuthorizationFence.Parameter("@EvidenceRef", evidenceRef)
        ]);
        if (actor.HasValue)
            command.Parameters.Add(SqlAuthorizationFence.Parameter("@ActorId", actor.Value));
        try { await command.ExecuteNonQueryAsync(ct); }
        catch (SqlException) { throw new AuthorizationDeniedException("B5_REVOKE_DENIED"); }
    }
}
