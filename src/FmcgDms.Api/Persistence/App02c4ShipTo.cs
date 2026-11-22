using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Persistence;

public sealed partial class App01a2CommandService
{
    private sealed record C4Order(Guid CustomerId, Guid ShipToSnapshotId, string Status);
    private sealed record C4Selection(string Payload, byte[] PayloadHash, byte ValidatorVersion,
        Guid? RegisteredId, int? RegisteredRevision, string ActorId);
    private sealed record C4Undelivered(int LineNumber, Guid SkuId, decimal Qty);

    public Task<BusinessCommandOutcome> AmendShipToC4(ClaimsPrincipal principal, C4AmendInput input, CancellationToken ct)
    {
        var key = C2Key(principal, input.CommandId);
        string reason = input.Reason?.Trim() ?? "";
        return ExecuteAsync(principal, key, "C4_AMEND_SHIPTO",
            [("Trip", input.TripId.ToString("N")), ("Order", input.OrderId.ToString("N")), ("Selection", input.SelectionId.ToString("N")),
             ("ExpectedVersion", input.ExpectedVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("Reason", reason)],
            (db, tx, tenant, actor, command, token) =>
                ApplyAmendmentC4(db, tx, tenant, actor, command, input, key.SourceNumber, reason, token),
            ct, "sales", deliveryTrip: input.TripId, amendment: true);
    }

    private static async Task<BusinessCommandOutcome> ApplyAmendmentC4(SqlConnection db, SqlTransaction tx,
        string tenant, string actor, Guid command, C4AmendInput input, string sourceNumber, string reason, CancellationToken ct)
    {
        if (input.ExpectedVersion is < 1 or int.MaxValue || input.OrderId == Guid.Empty || input.SelectionId == Guid.Empty
            || reason.Length is < 1 or > 240 || reason.Any(char.IsControl))
            return BusinessCommandOutcome.Rejected("C4_AMENDMENT_INVALID");
        var trip = await FindTrip(db, tx, tenant, input.TripId, ct);
        if (trip is not { Status: "PREPARED" or "IN_PROGRESS", VehicleActive: true, VehicleKind: "VEHICLE" })
            return BusinessCommandOutcome.Rejected("TRIP_NOT_ELIGIBLE");
        var order = await db.QuerySingleOrDefaultAsync<C4Order>(Sql("""
            SELECT o.CustomerId,c.ShipToSnapshotId,o.Status
            FROM dbo.AOrder o WITH (UPDLOCK,HOLDLOCK)
            JOIN dbo.C2Confirmation c ON c.TenantId=o.TenantId AND c.OrderId=o.Id
                AND c.CustomerId=o.CustomerId AND c.ShipToSnapshotId=o.ShipToSnapshotId
            JOIN dbo.Customer k ON k.TenantId=o.TenantId AND k.Id=o.CustomerId AND k.IsActive=1
            WHERE o.TenantId=@Tenant AND o.Id=@OrderId AND o.TripId=@TripId
            """, new { Tenant = tenant, input.OrderId, input.TripId }, tx, ct));
        if (order?.Status is not ("CONFIRMED" or "PARTIALLY_DELIVERED"))
            return BusinessCommandOutcome.Rejected("C4_NO_UNDELIVERED_SCOPE");
        var current = await CurrentC4Address(db, tx, tenant, input.OrderId, order.ShipToSnapshotId, ct);
        if (current.Version != input.ExpectedVersion) return BusinessCommandOutcome.Rejected("C4_STALE_SHIPTO");
        if (await db.ExecuteScalarAsync<int>(Sql("""
            SELECT COUNT(*) FROM dbo.C4DeliveryEvidence e
            JOIN dbo.TripManifestLine l ON l.TenantId=e.TenantId AND l.Id=e.ManifestLineId
            JOIN dbo.AReservation a ON a.TenantId=l.TenantId AND a.StockRightId=l.StockRightId
            JOIN dbo.AOrderLine ol ON ol.TenantId=a.TenantId AND ol.Id=a.AOrderLineId
            WHERE e.TenantId=@Tenant AND ol.AOrderId=@OrderId
                AND NOT EXISTS(SELECT 1 FROM dbo.SaleDelivery d WHERE d.TenantId=e.TenantId AND d.C4EvidenceId=e.Id)
            """, new { Tenant = tenant, input.OrderId }, tx, ct)) != 0)
            return BusinessCommandOutcome.Rejected("C4_UNRESOLVED_DELIVERY_EVIDENCE");

        var undelivered = (await db.QueryAsync<C4Undelivered>(Sql("""
            SELECT l.LineNumber,s.SkuId,s.ConfirmedBaseQuantity-d.Delivered AS Qty
            FROM dbo.AConfirmedLineSnapshot s JOIN dbo.AOrderLine l ON l.TenantId=s.TenantId AND l.Id=s.AOrderLineId
            CROSS APPLY (SELECT COALESCE(SUM(BaseQuantity),0) AS Delivered FROM dbo.SaleDelivery
                WHERE TenantId=s.TenantId AND AOrderLineId=s.AOrderLineId) d
            WHERE s.TenantId=@Tenant AND s.AOrderId=@OrderId AND s.ConfirmedBaseQuantity>d.Delivered
            ORDER BY l.LineNumber
            """, new { Tenant = tenant, input.OrderId }, tx, ct))).AsList();
        if (undelivered.Count == 0) return BusinessCommandOutcome.Rejected("C4_NO_UNDELIVERED_SCOPE");

        var address = await db.QuerySingleOrDefaultAsync<C4Selection>(Sql("""
            SELECT Payload,PayloadHash,ValidatorVersion,RegisteredId,RegisteredRevision,ActorId
            FROM dbo.C1ShipToSelection WITH (UPDLOCK,HOLDLOCK)
            WHERE TenantId=@Tenant AND CustomerId=@CustomerId AND Id=@SelectionId
            """, new { Tenant = tenant, order.CustomerId, input.SelectionId }, tx, ct));
        if (address is null || address.ValidatorVersion != 1 || address.ActorId == actor
            || !CryptographicOperations.FixedTimeEquals(address.PayloadHash, SHA256.HashData(Encoding.UTF8.GetBytes(address.Payload))))
            return BusinessCommandOutcome.Rejected("C4_AMENDMENT_SELECTION_OR_APPROVER_INVALID");
        if (await db.ExecuteScalarAsync<int>(Sql("""
            SELECT (SELECT COUNT(*) FROM dbo.C2Confirmation WHERE TenantId=@Tenant AND SelectionId=@SelectionId)
                + (SELECT COUNT(*) FROM dbo.C4ShipToAmendment WHERE TenantId=@Tenant AND SelectionId=@SelectionId)
            """, new { Tenant = tenant, input.SelectionId }, tx, ct)) != 0)
            return BusinessCommandOutcome.Rejected("C4_AMENDMENT_SELECTION_USED");
        if (address.RegisteredId is Guid registeredId &&
            address.RegisteredRevision != await db.ExecuteScalarAsync<int?>(Sql("""
                SELECT MAX(Revision) FROM dbo.C1RegisteredShipToRevision
                WHERE TenantId=@Tenant AND CustomerId=@CustomerId AND RegisteredId=@RegisteredId
                """, new { Tenant = tenant, order.CustomerId, RegisteredId = registeredId }, tx, ct)))
            return BusinessCommandOutcome.Rejected("C4_AMENDMENT_SELECTION_STALE");
        var decoded = JsonSerializer.Deserialize<C1Address>(address.Payload, C1Validation.Json);
        if (decoded is null || C1Validation.AddressPayload(decoded) != address.Payload)
            return BusinessCommandOutcome.Rejected("C4_AMENDMENT_SELECTION_OR_APPROVER_INVALID");

        Guid source = await AddSource(db, tx, tenant, command, "C4_SHIPTO_AMENDMENT", sourceNumber, actor,
            order.CustomerId, input.TripId, ct);
        foreach (var line in undelivered)
            await AddLine(db, tx, tenant, source, line.LineNumber, line.SkuId, line.Qty, null, ct);
        await db.ExecuteAsync(Sql("""
            DECLARE @ship uniqueidentifier=NEWID();
            INSERT dbo.ShipToSnapshot(Id,TenantId,CustomerId,DisplayName,Address)
            VALUES(@ship,@Tenant,@CustomerId,@Recipient,@Address);
            INSERT dbo.C4ShipToAmendment(TenantId,Id,OrderId,CustomerId,TripId,Version,SelectionId,ShipToSnapshotId,Reason)
            VALUES(@Tenant,@Source,@OrderId,@CustomerId,@TripId,@Version,@SelectionId,@ship,@Reason)
            """, new { Tenant = tenant, Source = source, input.OrderId, order.CustomerId, input.TripId,
                Version = current.Version + 1, input.SelectionId, Reason = reason, decoded.Recipient,
                Address = string.Join(", ", decoded.Line, decoded.Locality, decoded.Region, decoded.Country) }, tx, ct));
        return BusinessCommandOutcome.Applied("C4_SHIPTO_AMENDED", source);
    }
}
