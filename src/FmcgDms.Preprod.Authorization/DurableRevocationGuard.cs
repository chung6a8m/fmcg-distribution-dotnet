using System.Data;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Preprod.Authorization;

/// <summary>
/// Independent B53 high-watermark guard. Mandatory in any future, separately
/// authorized runtime, after both current B3/B4 guards and before protected
/// SQL effects. A restored B3/B4 image lacking the installed trigger guard
/// must fail closed, even if its old rows appear ACTIVE. No production route
/// or Worker service registers this component in this source-only increment.
/// </summary>
public sealed class DurableRevocationGuard(
    string approvedFenceDatabase,
    string approvedIdentityDatabase,
    string approvedRegistryDatabase)
{
    private readonly string sessionProcedure = "[" +
        SqlControlPlaneGuards.ValidateApprovedDatabase(approvedFenceDatabase) +
        "].dbo.b53_AssertFreshSession";
    private readonly string identityBridgeProcedure = "[" +
        SqlControlPlaneGuards.ValidateApprovedDatabase(approvedIdentityDatabase) +
        "].dbo.b53_AssertIdentityBridgeInstalled";
    private readonly string registryBridgeProcedure = "[" +
        SqlControlPlaneGuards.ValidateApprovedDatabase(approvedRegistryDatabase) +
        "].dbo.b53_AssertRegistryBridgeInstalled";

    internal async Task AssertFreshAsync(SqlConnection connection,
        SqlTransaction transaction, AuthorizationContext authority,
        CancellationToken cancellationToken)
    {
        if (transaction.Connection != connection)
            throw new AuthorizationDeniedException("B53_CONNECTION_MISMATCH");

        // All three assertions hold independently scoped SQL locks until the
        // SAME transaction COMMIT. Do not split into a remote API, a second
        // connection, or a post-COMMIT best-effort audit check.
        try
        {
            foreach (var bridge in new[] { identityBridgeProcedure, registryBridgeProcedure })
            {
                await using var current = new SqlCommand(bridge, connection, transaction)
                { CommandType = CommandType.StoredProcedure, CommandTimeout = 15 };
                await current.ExecuteNonQueryAsync(cancellationToken);
            }
            await using var watermark = new SqlCommand(
                sessionProcedure, connection, transaction)
            { CommandType = CommandType.StoredProcedure, CommandTimeout = 15 };
            watermark.Parameters.AddRange([
                SqlAuthorizationFence.Parameter("@SessionId", authority.SessionId),
                SqlAuthorizationFence.Parameter("@ActorId", authority.ActorId),
                SqlAuthorizationFence.Parameter("@SessionEpoch", authority.SessionEpoch)
            ]);
            await watermark.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqlException)
        {
            throw new AuthorizationDeniedException("B53_DURABLE_AUTHORITY_DENIED");
        }
    }
}
