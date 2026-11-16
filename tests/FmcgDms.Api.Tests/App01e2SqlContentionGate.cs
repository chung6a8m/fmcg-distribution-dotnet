using System.Data;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Tests;

/// <summary>
/// APP-01E.2 test-only database contention gate. A real SERIALIZABLE SQL
/// transaction holds the same app lock (T3-T6) or mutable row (T0/T1)
/// requested by the application. We require TWO observed SQL lock waiters,
/// not merely two HTTP requests reaching TestServer.
/// </summary>
internal sealed class App01e2SqlContentionGate : IAsyncDisposable
{
    private readonly SqlConnection _connection;
    private readonly SqlTransaction _transaction;
    private readonly string _connectionString;
    private readonly bool _application;
    private bool _released;

    private App01e2SqlContentionGate(
        SqlConnection connection, SqlTransaction transaction, string connectionString,
        bool application)
    {
        _connection = connection;
        _transaction = transaction;
        _connectionString = connectionString;
        _application = application;
    }

    public static async Task<App01e2SqlContentionGate> HoldAsync(
        string connectionString, string tenant, Guid trip, Guid warehouse, Guid sku,
        string resource)
    {
        if (tenant is not ("tenant-a" or "tenant-b") || trip == Guid.Empty
            || resource is not ("trip-app" or "trip-row" or "stock-pool"))
            throw new ArgumentException("Unsupported E.2 SQL contention scope.");

        // Every DDL/DML-sensitive connection is constrained to the fixture's
        // nonce-owned LocalDB and its exact server-validated tenant binding.
        var builder = new SqlConnectionStringBuilder(connectionString);
        var match = Regex.Match(builder.InitialCatalog,
            @"\AFmcg_App00_([a-f0-9]{32})_([AB])\z",
            RegexOptions.CultureInvariant);
        if (!builder.DataSource.StartsWith(@"(localdb)\", StringComparison.OrdinalIgnoreCase)
            || !builder.IntegratedSecurity || !match.Success
            || (tenant == "tenant-a" && match.Groups[2].Value != "A")
            || (tenant == "tenant-b" && match.Groups[2].Value != "B"))
            throw new InvalidOperationException("E.2 only permits matching LocalDB scratch tenants.");

        var db = new SqlConnection(builder.ConnectionString);
        SqlTransaction? tx = null;
        try
        {
            await db.OpenAsync();
            string? nonce = await db.QuerySingleOrDefaultAsync<string>(
                "SELECT Nonce FROM dbo.App00ScratchMarker");
            string? boundTenant = await db.QuerySingleOrDefaultAsync<string>(
                "SELECT TenantId FROM dbo.AppTenantBinding WHERE Slot=1");
            if (nonce != match.Groups[1].Value || boundTenant != tenant)
                throw new InvalidOperationException("E.2 nonce / tenant ownership mismatch.");

            tx = (SqlTransaction)await db.BeginTransactionAsync(IsolationLevel.Serializable);
            if (resource == "trip-app")
            {
                int result = await db.ExecuteScalarAsync<int>(
                    """
                    DECLARE @r int;
                    EXEC @r=sys.sp_getapplock @Resource=@Key,
                        @LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=0;
                    SELECT @r;
                    """,
                    new { Key = "APP01C:TRIP:" + tenant + ":" + trip.ToString("N") }, tx);
                if (result < 0)
                    throw new InvalidOperationException("Cannot acquire E.2 trip lock.");
            }
            else if (resource == "trip-row")
            {
                _ = await db.QuerySingleAsync<Guid>(
                    "SELECT Id FROM dbo.Trip WITH (UPDLOCK,HOLDLOCK) WHERE TenantId=@Tenant AND Id=@Trip",
                    new { Tenant = tenant, Trip = trip }, tx);
            }
            else
            {
                _ = await db.QuerySingleAsync<int>(
                    """
                    SELECT COUNT(*) FROM dbo.StockPool WITH (UPDLOCK,HOLDLOCK)
                    WHERE TenantId=@Tenant AND StockLocationId=@Warehouse AND SkuId=@Sku
                    """, new { Tenant = tenant, Warehouse = warehouse, Sku = sku }, tx);
            }
            return new App01e2SqlContentionGate(db, tx, builder.ConnectionString,
                resource == "trip-app");
        }
        catch
        {
            if (tx is not null)
            {
                try { await tx.RollbackAsync(); } catch (InvalidOperationException) { }
                await tx.DisposeAsync();
            }
            await db.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Observes lock manager state in the tenant's *physical* LocalDB. Never
    /// substitute a delay or an HTTP admission counter for SQL contention.
    /// </summary>
    public async Task<int> WaitForBothSqlWaitersAsync(CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        int maximum = 0;
        while (clock.Elapsed < TimeSpan.FromSeconds(7))
        {
            ct.ThrowIfCancellationRequested();
            await using var witness = new SqlConnection(_connectionString);
            await witness.OpenAsync(ct);
            int waiting = await witness.ExecuteScalarAsync<int>(
                new CommandDefinition(
                    """
                    SELECT COUNT(DISTINCT request_session_id)
                    FROM sys.dm_tran_locks
                    WHERE resource_database_id=DB_ID()
                      AND request_status=N'WAIT'
                      AND request_session_id<>@@SPID
                      AND resource_type IN ('APPLICATION','KEY','RID','PAGE','OBJECT')
                      AND (@App=0 OR resource_type=N'APPLICATION')
                    """, new { App = _application ? 1 : 0 }, cancellationToken: ct));
            maximum = Math.Max(waiting, maximum);
            if (waiting >= 2) return waiting;
            await Task.Delay(TimeSpan.FromMilliseconds(45), ct);
        }
        throw new InvalidOperationException(
            $"E.2 expected two genuine SQL waiters; observed maximum {maximum}. " +
            "HTTP admission alone is not a database interleaving proof.");
    }

    public async Task ReleaseAsync()
    {
        if (_released) return;
        _released = true;
        try { await _transaction.RollbackAsync(); }
        catch (InvalidOperationException) { /* server already terminated transaction */ }
    }

    public async ValueTask DisposeAsync()
    {
        await ReleaseAsync();
        await _transaction.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
