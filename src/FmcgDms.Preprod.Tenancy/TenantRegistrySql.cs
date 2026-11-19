using System.Data;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Preprod.Tenancy;

// This class is NOT registered in the public PREPROD API or Worker.
// Every privileged mutation is a SQL procedure using authenticated SUSER_SNAME().
public sealed record TenantRegistryEntry(
    Guid TenantId, string DatabaseName, Guid BindingId, long BindingVersion,
    long SecurityEpoch, string State, byte[] RowVersion, Guid? OperationId,
    Guid? LeaseToken, DateTime? LeaseUntilUtc, string? VerifiedBy, DateTime? VerifiedUtc,
    string? ApprovedOwnerLogin=null,string? ApprovedProvisionerLogin=null,
    string? ApprovedFinalizerLogin=null,string? ApprovedRetirerLogin=null,
    Guid? FinalizationOperationId=null,
    Guid? FinalizationLeaseToken=null,DateTime? FinalizationLeaseUntilUtc=null,
    string? FinalizedBy=null,DateTime? FinalizedUtc=null,
    long? FinalizedEpoch=null,Guid? FinalizedBindingId=null,
    string? FinalizerRetiredBy=null,DateTime? FinalizerRetiredUtc=null)
{
    public bool IsRoutable => State == "ACTIVE";
    public static string ExpectedDatabase(Guid id) => "FmcgPpT_" + id.ToString("N");
}

public static class TenantSchemaVersions
{
    public const string Registry = "B4_20261118_001";
    public const string Tenant = "B4_TENANT_20261118_001";
}

public sealed class TenantLifecycleDeniedException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed class TenantRegistrySql(string connectionString)
{
    public string ConnectionString { get; } = connectionString;

    // Internal SQL-engine impersonation seam for disposable LocalDB integration tests only.
    // The public operator constructor always opens a real independently authenticated login.
    internal Func<SqlConnection,CancellationToken,Task>? InitializeCiSession { get; init; }
    internal async Task OpenCiSessionAsync(SqlConnection db,CancellationToken ct)
    {
        if(InitializeCiSession is not null) await InitializeCiSession(db,ct);
    }

    public async Task<TenantRegistryEntry?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var db = new SqlConnection(ConnectionString);
        await db.OpenAsync(ct);
        await OpenCiSessionAsync(db,ct);
        await using var c = new SqlCommand("dbo.cp_GetTenant", db)
            { CommandType = CommandType.StoredProcedure };
        c.Parameters.Add(new SqlParameter("@TenantId", SqlDbType.UniqueIdentifier) { Value=id });
        await using var rd = await c.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct) ? Read(rd) : null;
    }

    public async Task<TenantRegistryEntry> RequestAsync(Guid id, string key,
        string requestRef, CancellationToken ct)
    {
        await ExecuteAsync("dbo.cp_RequestTenant", ct,
            Param("@TenantId", id), Param("@TenantKey", key), Param("@RequestRef", requestRef));
        return await RequiredAsync(id, ct);
    }

    public async Task<TenantRegistryEntry> ApproveAsync(Guid id, string changeRef,
        string approvedOwnerLogin,string provisionerLogin,string finalizerLogin,
        string retirerLogin,CancellationToken ct)
    {
        await ExecuteAsync("dbo.cp_ApproveTenant", ct,
            Param("@TenantId", id), Param("@ApprovalRef", changeRef),
            Param("@ApprovedOwnerLogin", approvedOwnerLogin),
            Param("@ProvisionerLogin", provisionerLogin),Param("@FinalizerLogin",finalizerLogin),
            Param("@RetirerLogin",retirerLogin));
        return await RequiredAsync(id, ct);
    }

    public async Task<TenantRegistryEntry> ClaimAsync(Guid id, Guid lease,
        CancellationToken ct)
    {
        await ExecuteAsync("dbo.cp_ClaimProvision", ct,
            Param("@TenantId", id), Param("@LeaseToken", lease));
        return await RequiredAsync(id, ct);
    }

    public Task CompleteAsync(Guid id, Guid lease, CancellationToken ct) =>
        ExecuteAsync("dbo.cp_CompleteProvision", ct,
            Param("@TenantId", id), Param("@LeaseToken", lease));

    public Task FailAsync(Guid id, Guid lease, string code, CancellationToken ct) =>
        ExecuteAsync("dbo.cp_FailProvision", ct,
            Param("@TenantId", id), Param("@LeaseToken", lease), Param("@ErrorCode", code));

    public async Task<TenantRegistryEntry> ClaimFinalizationAsync(Guid id,Guid token,CancellationToken ct)
    {
        await ExecuteAsync("dbo.cp_ClaimFinalization",ct,
            Param("@TenantId",id),Param("@LeaseToken",token));
        return await RequiredAsync(id,ct);
    }

    public Task CompleteFinalizationAsync(Guid id,Guid token,Guid operation,
        Guid binding,long epoch,CancellationToken ct) =>
        ExecuteAsync("dbo.cp_CompleteFinalization",ct,
            Param("@TenantId",id),Param("@LeaseToken",token),
            Param("@ExpectedOperationId",operation),Param("@ExpectedBindingId",binding),
            Param("@ExpectedEpoch",epoch));

    public Task FailFinalizationAsync(Guid id,Guid token,string code,CancellationToken ct) =>
        ExecuteAsync("dbo.cp_FailFinalization",ct,
            Param("@TenantId",id),Param("@LeaseToken",token),Param("@ErrorCode",code));

    public async Task<TenantRegistryEntry> VerifyAsync(Guid id, byte[] expectedRowVersion,
        Guid observedBinding, byte[] witnessHash, CancellationToken ct)
    {
        if (expectedRowVersion.Length!=8 || witnessHash.Length!=32)
            throw new TenantLifecycleDeniedException("TENANT_WITNESS_INVALID");
        await ExecuteAsync("dbo.cp_RecordVerification", ct,
            Param("@TenantId", id), Param("@ExpectedRowVer", expectedRowVersion),
            Param("@ObservedBindingId", observedBinding), Param("@WitnessHash", witnessHash));
        return await RequiredAsync(id, ct);
    }

    public async Task<TenantRegistryEntry> TransitionAsync(Guid id, string action,
        byte[]? expectedVersion, string? evidenceRef, CancellationToken ct)
    {
        if (action is not ("ACTIVATE" or "SUSPEND" or "REVOKE" or "START_REACTIVATION"
            or "ABORT_REACTIVATION" or "DECOMMISSION" or "COMPLETE_DECOMMISSION"))
            throw new TenantLifecycleDeniedException("TENANT_ACTION_INVALID");
        if (expectedVersion is not null && expectedVersion.Length!=8)
            throw new TenantLifecycleDeniedException("TENANT_VERSION_INVALID");
        await ExecuteAsync("dbo.cp_TenantTransition", ct,
            Param("@TenantId", id), Param("@Action", action),
            Param("@ExpectedRowVer", expectedVersion), Param("@EvidenceRef", evidenceRef));
        return await RequiredAsync(id, ct);
    }

    private async Task<TenantRegistryEntry> RequiredAsync(Guid id, CancellationToken ct) =>
        await GetAsync(id, ct) ?? throw new TenantLifecycleDeniedException("TENANT_NOT_FOUND");

    private async Task ExecuteAsync(string procedure, CancellationToken ct,
        params SqlParameter[] parameters)
    {
        await using var db = new SqlConnection(ConnectionString);
        await db.OpenAsync(ct);
        await OpenCiSessionAsync(db,ct);
        await using var c = new SqlCommand(procedure, db)
        { CommandType=CommandType.StoredProcedure, CommandTimeout=30 };
        c.Parameters.AddRange(parameters);
        await c.ExecuteNonQueryAsync(ct);
    }

    private static SqlParameter Param(string name, object? value)
    {
        var type = value switch
        {
            Guid => SqlDbType.UniqueIdentifier,
            long => SqlDbType.BigInt,
            byte[] => SqlDbType.Binary,
            _ when name is "@ExpectedRowVer" or "@WitnessHash" => SqlDbType.Binary,
            _ => SqlDbType.NVarChar
        };
        return new SqlParameter(name,type)
        {
            Value=value ?? DBNull.Value,
            Size=value is byte[] data ? data.Length
                : name=="@ExpectedRowVer" ? 8 : name=="@WitnessHash" ? 32
                : name=="@TenantKey" ? 64 : name=="@EvidenceRef" ? 200
                : name=="@ErrorCode" ? 32 : 160
        };
    }

    private static TenantRegistryEntry Read(SqlDataReader rd) =>
        new(rd.GetGuid(0),rd.GetString(1),rd.GetGuid(2),rd.GetInt64(3),
            rd.GetInt64(4),rd.GetString(5),(byte[])rd.GetValue(6),
            rd.IsDBNull(7)?null:rd.GetGuid(7),rd.IsDBNull(8)?null:rd.GetGuid(8),
            rd.IsDBNull(9)?null:rd.GetDateTime(9),rd.IsDBNull(10)?null:rd.GetString(10),
            rd.IsDBNull(11)?null:rd.GetDateTime(11),
            rd.IsDBNull(12)?null:rd.GetString(12),
            rd.IsDBNull(13)?null:rd.GetString(13),
            rd.IsDBNull(14)?null:rd.GetString(14),
            rd.IsDBNull(15)?null:rd.GetString(15),
            rd.IsDBNull(16)?null:rd.GetGuid(16),
            rd.IsDBNull(17)?null:rd.GetGuid(17),
            rd.IsDBNull(18)?null:rd.GetDateTime(18),
            rd.IsDBNull(19)?null:rd.GetString(19),
            rd.IsDBNull(20)?null:rd.GetDateTime(20),
            rd.IsDBNull(21)?null:rd.GetInt64(21),
            rd.IsDBNull(22)?null:rd.GetGuid(22),
            rd.IsDBNull(23)?null:rd.GetString(23),
            rd.IsDBNull(24)?null:rd.GetDateTime(24));
}
