using System.Data;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Preprod.Authorization;

/// <summary>
/// B.3 and B.4 are verified THROUGH narrow EXEC-only SQL procedures on the
/// SAME SQL Server instance/connection/transaction as the B.5 fence and effect.
/// An untrusted tenant/identity database name is never accepted from a request.
/// Only operator-pinned Control Plane database identifiers may be supplied.
/// </summary>
public sealed class SqlControlPlaneGuards
{
    private readonly string identityProcedure;
    private readonly string registryProcedure;

    public SqlControlPlaneGuards(string approvedIdentityDatabase,
        string approvedRegistryDatabase)
    {
        identityProcedure = QuoteApprovedDb(approvedIdentityDatabase) +
            ".dbo.b5_AssertCurrentSession";
        registryProcedure = QuoteApprovedDb(approvedRegistryDatabase) +
            ".dbo.b5_AssertCurrentTenant";
    }

    public async Task AssertCurrentAsync(SqlConnection connection, SqlTransaction transaction,
        AuthorizationContext authority, CancellationToken cancellationToken)
    {
        if (transaction.Connection != connection)
            throw new AuthorizationDeniedException("B5_CONNECTION_MISMATCH");
        // Existing B.4 transitions update the same registry row with UPDLOCK;
        // the guard retains that row lock until the protected effect COMMIT.
        await using (var registry = new SqlCommand(registryProcedure, connection, transaction)
        { CommandType = CommandType.StoredProcedure, CommandTimeout = 15 })
        {
            registry.Parameters.AddRange([
                SqlAuthorizationFence.Parameter("@TenantId", authority.TenantId),
                SqlAuthorizationFence.Parameter("@BindingId", authority.BindingId),
                SqlAuthorizationFence.Parameter("@RegistryEpoch", authority.RegistryEpoch)
            ]);
            await registry.ExecuteNonQueryAsync(cancellationToken);
        }

        // B.3 logout/refresh reuse updates the same LoginSession row. Its
        // write waits until the guarded transaction commits; otherwise this
        // assertion observes the revoked row and denies the effect.
        await using var identity = new SqlCommand(identityProcedure, connection, transaction)
        { CommandType = CommandType.StoredProcedure, CommandTimeout = 15 };
        identity.Parameters.AddRange([
            SqlAuthorizationFence.Parameter("@ActorId", authority.ActorId),
            SqlAuthorizationFence.Parameter("@SessionId", authority.SessionId),
            SqlAuthorizationFence.Parameter("@SessionEpoch", authority.SessionEpoch)
        ]);
        await identity.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string QuoteApprovedDb(string? db)
    {
        if (string.IsNullOrWhiteSpace(db) || db.Length > 128 ||
            !char.IsAsciiLetter(db[0]) ||
            db.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')) ||
            new[] { "master", "model", "msdb", "tempdb" }
                .Contains(db, StringComparer.OrdinalIgnoreCase))
            throw new AuthorizationDeniedException("B5_CONTROL_DB_DENIED");
        return "[" + db + "]";
    }
}
