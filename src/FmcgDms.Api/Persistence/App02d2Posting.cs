using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Persistence;

public sealed partial class App01c2BPostingService
{
    private const decimal D2FixtureUnitPrice = 10m;
    private sealed record D2PostingContext(Guid SelectionId, Guid? OutletId, D2PreviewResult? Preview = null,
        bool D3Mode = false, D3Price? Price = null, bool D3Runtime = false, bool Field = false,
        App01a2CommandService.FieldBPrice? FieldPrice = null);
    private sealed class D2PreviewResult
    {
        internal D2SalePreview? Value { get; set; }
    }

    private sealed record D2BoundAddress(string Payload, byte[] PayloadHash, byte ValidatorVersion,
        string DisplayName, string Address);

    // The exact partially loaded line and its paired stock movement must both
    // come from the guarded D.1 warehouse command; the quota is source-bound too.
    private const string D2ManifestProof = """
        AND EXISTS(
            SELECT 1
            FROM dbo.BusinessCommand quotaCommand WITH (HOLDLOCK)
            JOIN dbo.C2CommandScope quotaScope WITH (HOLDLOCK)
                ON quotaScope.TenantId=quotaCommand.TenantId AND quotaScope.CommandRowId=quotaCommand.Id
            JOIN dbo.LedgerEffect quotaEffect WITH (HOLDLOCK)
                ON quotaEffect.TenantId=al.TenantId AND quotaEffect.BusinessSourceLineId=al.Id
                AND quotaEffect.EffectKey='RIGHT-ALLOCATED' AND quotaEffect.LedgerKind='RIGHT_B'
                AND quotaEffect.SkuId=l.SkuId AND quotaEffect.StockLocationId=l.WarehouseLocationId
                AND quotaEffect.QuantityDelta=r.GrantedBaseQuantity AND quotaEffect.AmountDelta=0
            WHERE quotaCommand.TenantId=a.TenantId AND quotaCommand.Id=a.BusinessCommandId
                AND quotaCommand.Status='APPLIED' AND quotaScope.RoleCode='warehouse'
                AND quotaScope.D1GrantTripId=l.TripId AND r.StockLocationId=l.WarehouseLocationId
        )
        AND EXISTS(
            SELECT 1
            FROM dbo.BusinessSourceLine loadLine WITH (HOLDLOCK)
            JOIN dbo.BusinessSource loadSource WITH (HOLDLOCK)
                ON loadSource.TenantId=loadLine.TenantId AND loadSource.Id=loadLine.BusinessSourceId
                AND loadSource.SourceKind='MANIFEST_LOAD' AND loadSource.TripId=l.TripId
            JOIN dbo.BusinessCommand loadCommand WITH (HOLDLOCK)
                ON loadCommand.TenantId=loadSource.TenantId AND loadCommand.Id=loadSource.BusinessCommandId
                AND loadCommand.Status='APPLIED'
            JOIN dbo.C2CommandScope loadScope WITH (HOLDLOCK)
                ON loadScope.TenantId=loadCommand.TenantId AND loadScope.CommandRowId=loadCommand.Id
                AND loadScope.RoleCode='warehouse' AND loadScope.IsD1Load=1 AND loadScope.LoadTripId=l.TripId
            JOIN dbo.LedgerEffect loadOut WITH (HOLDLOCK)
                ON loadOut.TenantId=loadLine.TenantId AND loadOut.BusinessSourceLineId=loadLine.Id
                AND loadOut.EffectKey='PHYSICAL-OUT' AND loadOut.LedgerKind='PHYSICAL_STOCK'
                AND loadOut.SkuId=l.SkuId AND loadOut.StockLocationId=l.WarehouseLocationId
                AND loadOut.QuantityDelta=-l.LoadedBaseQuantity AND loadOut.AmountDelta=-l.LoadedCostAmount
            JOIN dbo.LedgerEffect loadIn WITH (HOLDLOCK)
                ON loadIn.TenantId=loadLine.TenantId AND loadIn.BusinessSourceLineId=loadLine.Id
                AND loadIn.EffectKey='PHYSICAL-IN' AND loadIn.LedgerKind='PHYSICAL_STOCK'
                AND loadIn.SkuId=l.SkuId AND loadIn.StockLocationId=l.VehicleLocationId
                AND loadIn.QuantityDelta=l.LoadedBaseQuantity AND loadIn.AmountDelta=l.LoadedCostAmount
            WHERE loadLine.TenantId=l.TenantId AND loadLine.Id=l.LoadSourceLineId
                AND loadLine.SkuId=l.SkuId AND loadLine.BaseQuantity=l.LoadedBaseQuantity
                AND loadLine.Amount=l.LoadedCostAmount
        )
        """;

    private const string D2GrantProof = """
        CAST(CASE WHEN EXISTS (
            SELECT 1
            FROM dbo.BusinessSourceLine grantLine WITH (HOLDLOCK)
            JOIN dbo.BusinessSource grantSource WITH (HOLDLOCK)
                ON grantSource.TenantId=grantLine.TenantId AND grantSource.Id=grantLine.BusinessSourceId
                AND grantSource.SourceKind='B_CREDIT_GRANT' AND grantSource.TripId=g.TripId
                AND grantSource.CustomerId=g.CustomerId
            JOIN dbo.BusinessCommand grantCommand WITH (HOLDLOCK)
                ON grantCommand.TenantId=grantSource.TenantId AND grantCommand.Id=grantSource.BusinessCommandId
                AND grantCommand.Status='APPLIED'
            JOIN dbo.C2CommandScope grantScope WITH (HOLDLOCK)
                ON grantScope.TenantId=grantCommand.TenantId AND grantScope.CommandRowId=grantCommand.Id
                AND grantScope.D1GrantTripId=g.TripId AND grantScope.RoleCode='finance'
            JOIN dbo.LedgerEffect grantEffect WITH (HOLDLOCK)
                ON grantEffect.TenantId=grantLine.TenantId AND grantEffect.BusinessSourceLineId=grantLine.Id
                AND grantEffect.EffectKey='RIGHT-ALLOCATED' AND grantEffect.LedgerKind='RIGHT_CREDIT_B'
                AND grantEffect.CustomerId=g.CustomerId AND grantEffect.QuantityDelta=0
                AND grantEffect.AmountDelta=g.GrantedAmount
            WHERE grantLine.TenantId=g.TenantId AND grantLine.Id=g.BusinessSourceLineId
                AND grantLine.Amount=g.GrantedAmount
        ) THEN 1 ELSE 0 END AS bit)
        """;

    private Task<SqlConnection> OpenD2(ClaimsPrincipal user, CancellationToken ct)
    {
        if (!connections.UsesApp02d2 || !user.HasClaim("synthetic_role", "sales"))
            throw new UnauthorizedAccessException("Guarded D.2 Sales runtime required.");
        return connections.OpenApp02c4Async(user, ct);
    }

    private static async Task RequireD2Writer(SqlConnection db, SqlTransaction tx,
        string tenant, string actor, Guid trip, CancellationToken ct)
    {
        await App02a3Sql.RequireRole(db, tx, tenant, actor, "sales", ct);
        if (await db.ExecuteScalarAsync<int>(Sql("""
            SELECT COUNT(*) FROM dbo.C4DeliveryWriter WITH (HOLDLOCK)
            WHERE TenantId=@Tenant AND TripId=@Trip AND ActorId=@Actor
            """, new { Tenant = tenant, Trip = trip, Actor = actor }, tx, ct)) != 1)
            throw new UnauthorizedAccessException("Current delivery writer required.");
    }

    private static PostBSaleDeliveryReceiptCommand D2Command(D2SaleInput input) => new(
        input.ProducerId, input.CommandId, input.SourceNumber, input.TripId, input.CustomerId,
        input.ShipToSnapshotId, input.TripManifestLineId, input.SkuId, input.BaseQuantity, D2FixtureUnitPrice,
        input.ReceivedCashAmount, input.VerificationEvidenceRef ?? "", input.PendingBankEvidenceRef,
        input.CustomerCreditReservationId);

    internal Task<BusinessCommandOutcome> PostD2Sale(ClaimsPrincipal user, D2SaleInput input, CancellationToken ct) =>
        PostSale(user, D2Command(input), new D2PostingContext(input.SelectionId, input.OutletId,
            Field: connections.UsesApp03Field), ct);

    internal Task<(D2SalePreview?, BusinessCommandOutcome)> PreviewD2Sale(
        ClaimsPrincipal user, D2SaleInput input, CancellationToken ct) =>
        ReadD2(user, input.TripId, async (db, tx, tenant, actor) =>
        {
            var preview = new D2PreviewResult();
            if (!ValidKey(input.SourceNumber, 80))
                return (preview.Value, BusinessCommandOutcome.Rejected("INVALID_COMMAND_IDENTITY"));
            var outcome = await ValidateAndPost(db, tx, tenant, actor, Guid.Empty, D2Command(input),
                new D2PostingContext(input.SelectionId, input.OutletId, preview,
                    await IsD3Trip(db, tx, tenant, input.TripId, ct), D3Runtime: connections.UsesApp02d3,
                    Field: connections.UsesApp03Field), ct);
            return (preview.Value, outcome);
        }, ct);

    private static async Task<bool> MatchesD2Selection(SqlConnection db, SqlTransaction tx, string tenant,
        PostBSaleDeliveryReceiptCommand request, D2PostingContext d2, CancellationToken ct)
    {
        var row = await db.QuerySingleOrDefaultAsync<D2BoundAddress>(Sql("""
            SELECT sel.Payload,sel.PayloadHash,sel.ValidatorVersion,sh.DisplayName,sh.Address
            FROM dbo.D2ShipToBinding bind WITH (HOLDLOCK)
            JOIN dbo.C1ShipToSelection sel WITH (HOLDLOCK) ON sel.TenantId=bind.TenantId
                AND sel.CustomerId=bind.CustomerId AND sel.Id=bind.SelectionId
            JOIN dbo.ShipToSnapshot sh WITH (HOLDLOCK) ON sh.TenantId=bind.TenantId
                AND sh.CustomerId=bind.CustomerId AND sh.Id=bind.ShipToSnapshotId
            WHERE bind.TenantId=@Tenant AND bind.CustomerId=@Customer AND bind.ShipToSnapshotId=@ShipTo
                AND bind.SelectionId=@Selection AND (sel.OutletId=@Outlet OR (sel.OutletId IS NULL AND @Outlet IS NULL))
            """, new { Tenant = tenant, Customer = request.CustomerId, ShipTo = request.ShipToSnapshotId,
                Selection = d2.SelectionId, Outlet = d2.OutletId }, tx, ct));
        if (row is null || row.ValidatorVersion != 1
            || !CryptographicOperations.FixedTimeEquals(row.PayloadHash, SHA256.HashData(Encoding.UTF8.GetBytes(row.Payload))))
            return false;
        var address = JsonSerializer.Deserialize<C1Address>(row.Payload, C1Validation.Json);
        return address is not null && C1Validation.AddressPayload(address) == row.Payload
            && address.Recipient == row.DisplayName
            && string.Join(", ", address.Line, address.Locality, address.Region, address.Country) == row.Address;
    }

    private sealed record D2Approval(Guid Id, string PolicyVersion, string EvidenceRef, string ProposedBy, string CheckedBy);

    private static async Task<(Guid? Approval, string? Error)> EffectiveD2Approval(SqlConnection db,
        SqlTransaction tx, string tenant, PostBSaleDeliveryReceiptCommand request, CancellationToken ct)
    {
        var rows = (await db.QueryAsync<D2Approval>(Sql($"""
            DECLARE @Now datetime2(7)={App02c6Clock.SqlUtcNow};
            SELECT TOP (2) Id,PolicyVersion,EvidenceRef,ProposedBy,CheckedBy
            FROM dbo.CreditPolicyApproval WITH (UPDLOCK,HOLDLOCK)
            WHERE TenantId=@Tenant AND TripId=@Trip AND CustomerId=@Customer
                AND PoolCode='B' AND CurrencyUnit='SYNTHETIC_UNITLESS' AND Decision='APPROVED'
                AND EffectiveFromUtc<=@Now AND EffectiveUntilUtc>@Now AND ReviewedAtUtc<=@Now
            ORDER BY EffectiveFromUtc,Id
            """, new { Tenant = tenant, Trip = request.TripId, Customer = request.CustomerId }, tx, ct))).AsList();
        if (rows.Count > 1) return (null, "CREDIT_APPROVAL_AMBIGUOUS");
        if (rows.Count == 0) return (null, "CREDIT_APPROVAL_REQUIRED");

        var approval = rows[0];
        if (!ValidKey(approval.PolicyVersion, 48) || !ValidKey(approval.EvidenceRef, 128)
            || !ValidKey(approval.ProposedBy, 128) || !ValidKey(approval.CheckedBy, 128)
            || string.Equals(approval.ProposedBy, approval.CheckedBy, StringComparison.OrdinalIgnoreCase))
            return (null, "CREDIT_APPROVAL_REQUIRED");
        // BCreditGrant.ApprovalId references a scope-qualified approval whose
        // ID and policy version become immutable when the grant is issued.
        return (approval.Id, null);
    }
}
