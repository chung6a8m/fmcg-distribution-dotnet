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
    Guid BindingId, long RegistryEpoch, long FenceEpoch, long MembershipEpoch, long SessionEpoch,
    string PermissionCode, string ResourceKey, Guid? ApprovalSourceId = null)
{
    public void Validate()
    {
        if (TenantId == Guid.Empty || ResourceTenantId != TenantId ||
            ActorId == Guid.Empty || SessionId == Guid.Empty || BindingId == Guid.Empty ||
            RegistryEpoch < 1 || FenceEpoch < 1 || MembershipEpoch < 1 || SessionEpoch < 0 ||
            string.IsNullOrWhiteSpace(PermissionCode) || PermissionCode.Length > 96 ||
            string.IsNullOrWhiteSpace(ResourceKey) || ResourceKey.Length > 160 ||
            PermissionCode.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')) ||
            ResourceKey.Any(char.IsControl) ||
            (ApprovalSourceId.HasValue && ApprovalSourceId.Value == Guid.Empty))
            throw new AuthorizationDeniedException("B5_ENVELOPE_DENIED");
    }
}

public sealed class AuthorizationDeniedException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>
/// Runs the authorization assertion and any protected effect in ONE SQL Server
/// transaction on ONE SqlConnection. Callers may use only a server-resolved,
/// quoted three-part tenant target on this same instance. A second connection,
/// deferred job or HTTP call is NOT fenced and must never be dispatched here.
/// The protected workload remains disabled in PREPROD until B.4 / B.3 / B.7
/// authoritative integration and physical operator acceptance.
/// </summary>
public sealed class SqlAuthorizationFence(string fenceConnectionString, string approvedTenantDatabase)
{
    private readonly string tenantDatabase = SqlControlPlaneGuards.ValidateApprovedDatabase(approvedTenantDatabase);
    // TEST ONLY: disposable LocalDB login to verify real SQL permission checks.
    internal Func<SqlConnection, CancellationToken, Task>? InitializeCiSession { get; init; }
    // Optional only for legacy B.5 disposable tests. The runtime is hard-disabled;
    // future admission MUST mandate the independently durable B53 guard.
    internal DurableRevocationGuard? DurableGuard { get; init; }

    // Never part of the public API: unrestricted SQL callbacks are confined to
    // explicit friend test assemblies and disposable LocalDB only.
    internal async Task<T> ExecuteAsync<T>(AuthorizationContext authority,
        SqlControlPlaneGuards controlAuthority,
        Func<SqlConnection, SqlTransaction, CancellationToken, Task<T>> effect,
        CancellationToken cancellationToken)
    {
        authority.Validate();
        ArgumentNullException.ThrowIfNull(controlAuthority);
        // B.5 has no approved runtime authorization path. Disposable CI is the
        // only allowed execution context until restore-safe, tenant-bound SQL
        // isolation and Owner/operator admission have independent evidence.
        if (InitializeCiSession is null ||
            !new SqlConnectionStringBuilder(fenceConnectionString).DataSource
                .StartsWith("(localdb)\\", StringComparison.OrdinalIgnoreCase))
            throw new AuthorizationDeniedException("B5_RUNTIME_NOT_AUTHORIZED");
        await using var connection = new SqlConnection(fenceConnectionString);
        await connection.OpenAsync(cancellationToken);
        if (InitializeCiSession is not null)
            await InitializeCiSession(connection, cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        try
        {
            // Lock B.4 registry + B.3 current session through EXEC-only, same-
            // instance stored procedures INSIDE the protected transaction.
            // An independent revoke of B.3 or B.4 blocks until this COMMIT.
            await controlAuthority.AssertCurrentAsync(
                connection, transaction, authority, tenantDatabase, cancellationToken);
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
                Parameter("@ApprovalSourceId", authority.ApprovalSourceId)
            ]);
            await command.ExecuteNonQueryAsync(cancellationToken);
            // B53 session watermark lives outside B.3/B.4 restore scope; it is
            // locked in the same transaction before any protected SQL effect.
            if (DurableGuard is not null)
                await DurableGuard.AssertFreshAsync(connection, transaction,
                    authority, cancellationToken);
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
            Guid => SqlDbType.UniqueIdentifier,
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
