using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Persistence;

public sealed record C2DraftLine(Guid SkuId, decimal Quantity);
public sealed record C2DraftInput(Guid CommandId, Guid CustomerId, Guid TripId, Guid SelectionId, C2DraftLine[]? Lines);
public sealed record C2ConfirmInput(Guid CommandId, Guid OrderId);
public sealed record C2ReserveInput(Guid CommandId, Guid OrderId, Guid LineId, Guid LocationId,
    decimal Quantity, Guid ConfirmSourceId);

public sealed partial class App01a2CommandService
{
    private const string SellerZone = "Asia/Ho_Chi_Minh";
    private sealed record C2CommandKey(string ProducerId, string CommandId, string SourceNumber) : IApp01aCommand;
    private sealed record C2Draft(Guid CustomerId, Guid TripId, Guid SelectionId);
    private sealed record C2AddressSource(string Payload, byte[] PayloadHash, byte ValidatorVersion,
        Guid? RegisteredId, int? RegisteredRevision);
    private sealed record C2SkuUnit(Guid Id, string BaseUom);
    private sealed record C2PreparedLine(int LineNumber, Guid SkuId, decimal BaseQuantity, string BaseUom,
        string CurrentBaseUom, bool IsActive);
    private sealed record C2CommercialPolicy(Guid Id, int Version, string Decision, DateTime EffectiveFromUtc,
        DateTime EffectiveUntilUtc, string Maker, string? Checker, DateTime? ApprovedAtUtc, string EvidenceRef,
        decimal? UnitPrice, decimal? DiscountPerUnit, decimal? TaxRate, int? TermsDays, decimal? CreditCap, string? SellerTimeZone);
    private sealed record C2FrozenLine(C2PreparedLine Line, C2CommercialPolicy Policy, string Payload, decimal Gross);

    private static string C2Producer(ClaimsPrincipal principal) =>
        "C2-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(App02a3Sql.Actor(principal))));

    private static C2CommandKey C2Key(ClaimsPrincipal principal, Guid commandId)
    {
        string producer = C2Producer(principal);
        string id = commandId == Guid.Empty ? "" : commandId.ToString("N");
        string source = "C2-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(producer + "/" + id)));
        return new(producer, id, source);
    }

    public Task<BusinessCommandOutcome> DraftC2(ClaimsPrincipal principal, C2DraftInput input, CancellationToken ct) =>
        ExecuteAsync(principal, C2Key(principal, input.CommandId), "C2_DRAFT",
            [
                ("Customer", input.CustomerId.ToString("N")),
                ("Trip", input.TripId.ToString("N")),
                ("Selection", input.SelectionId.ToString("N")),
                ("Lines", JsonSerializer.Serialize(input.Lines?.Select(l => l is null ? null : new
                {
                    l.SkuId, Quantity = CommandPayloadHash.Decimal(l.Quantity)
                }), C1Validation.Json))
            ],
            async (db, tx, tenant, actor, command, token) =>
            {
                if (input.Lines is not { Length: > 0 and <= 24 } lines || lines.Any(l =>
                    l is null || l.SkuId == Guid.Empty || !ValidQuantity(l.Quantity)) ||
                    lines.Select(l => l.SkuId).Distinct().Count() != lines.Length)
                    return BusinessCommandOutcome.Rejected("C2_DRAFT_LINES_INVALID");
                var scope = new { Tenant = tenant, input.CustomerId, input.SelectionId };
                if (await db.ExecuteScalarAsync<int>(Sql("""
                    SELECT COUNT(*) FROM dbo.C1ShipToSelection s JOIN dbo.Customer c
                        ON c.TenantId=s.TenantId AND c.Id=s.CustomerId AND c.IsActive=1
                    WHERE s.TenantId=@Tenant AND s.CustomerId=@CustomerId AND s.Id=@SelectionId
                    """, scope, tx, token)) != 1)
                    return BusinessCommandOutcome.Rejected("C2_SHIP_TO_INVALID");
                if (!IsPreparedTrip(await FindTrip(db, tx, tenant, input.TripId, token)))
                    return BusinessCommandOutcome.Rejected("TRIP_NOT_PREPARED");
                var units = (await db.QueryAsync<C2SkuUnit>(Sql("""
                    SELECT Id,BaseUom FROM dbo.Sku WHERE TenantId=@Tenant AND IsActive=1 AND Id IN @Skus
                    """, new { Tenant = tenant, Skus = lines.Select(l => l.SkuId).ToArray() }, tx, token)))
                    .ToDictionary(row => row.Id, row => row.BaseUom);
                if (units.Count != lines.Length || units.Values.Any(string.IsNullOrWhiteSpace))
                    return BusinessCommandOutcome.Rejected("C2_SKU_INVALID");

                Guid draft = await AddSource(db, tx, tenant, command, "C2_A_DRAFT",
                    C2Key(principal, input.CommandId).SourceNumber, actor, input.CustomerId, input.TripId, token);
                await db.ExecuteAsync(Sql("""
                    INSERT dbo.C2OrderDraft(TenantId,Id,CustomerId,TripId,SelectionId,CreatedBy)
                    VALUES(@Tenant,@Id,@CustomerId,@TripId,@SelectionId,@Actor)
                    """, new { Tenant = tenant, Id = draft, input.CustomerId, input.TripId, input.SelectionId, Actor = actor }, tx, token));
                for (int i = 0; i < lines.Length; i++)
                {
                    await AddLine(db, tx, tenant, draft, i + 1, lines[i].SkuId, lines[i].Quantity, null, token);
                    await db.ExecuteAsync(Sql("""
                        INSERT dbo.C2OrderDraftLine(TenantId,DraftId,LineNumber,SkuId,BaseQuantity,BaseUom)
                        VALUES(@Tenant,@Draft,@Line,@SkuId,@Quantity,@BaseUom)
                        """, new { Tenant = tenant, Draft = draft, Line = i + 1,
                            lines[i].SkuId, lines[i].Quantity, BaseUom = units[lines[i].SkuId] }, tx, token));
                }
                return BusinessCommandOutcome.Applied("C2_DRAFT_CREATED", draft);
            }, ct, "sales");

    public Task<BusinessCommandOutcome> ConfirmC2(ClaimsPrincipal principal, C2ConfirmInput input, CancellationToken ct) =>
        ExecuteAsync(principal, C2Key(principal, input.CommandId), "C2_CONFIRM",
            [("OrderId", input.OrderId.ToString("N"))],
            (db, tx, tenant, actor, command, token) => ConfirmC2Core(
                db, tx, tenant, actor, command, input.OrderId, C2Key(principal, input.CommandId).SourceNumber, token), ct, "sales");

    public Task<BusinessCommandOutcome> ReserveC2(ClaimsPrincipal principal, C2ReserveInput input, CancellationToken ct)
    {
        var key = C2Key(principal, input.CommandId);
        var request = new ReserveAStockCommand(key.ProducerId, key.CommandId, key.SourceNumber,
            input.OrderId, input.LineId, input.LocationId, input.Quantity);
        return ReserveAStockCore(principal, request, input.ConfirmSourceId, ct);
    }

    private static async Task<BusinessCommandOutcome> ConfirmC2Core(SqlConnection db, SqlTransaction tx,
        string tenant, string actor, Guid command, Guid orderId, string sourceNumber, CancellationToken ct)
    {
        var draft = await db.QuerySingleOrDefaultAsync<C2Draft>(Sql("""
            SELECT d.CustomerId,d.TripId,d.SelectionId FROM dbo.C2OrderDraft d WITH (UPDLOCK,HOLDLOCK)
            JOIN dbo.Customer c ON c.TenantId=d.TenantId AND c.Id=d.CustomerId AND c.IsActive=1
            WHERE d.TenantId=@Tenant AND d.Id=@OrderId
            """, new { Tenant = tenant, OrderId = orderId }, tx, ct));
        if (draft is null) return BusinessCommandOutcome.Rejected("C2_DRAFT_UNAVAILABLE");
        if (!IsPreparedTrip(await FindTrip(db, tx, tenant, draft.TripId, ct)))
            return BusinessCommandOutcome.Rejected("TRIP_NOT_PREPARED");
        var scope = new { Tenant = tenant, OrderId = orderId, draft.CustomerId, draft.SelectionId, draft.TripId };
        var selected = await db.QuerySingleOrDefaultAsync<C2AddressSource>(Sql("""
            SELECT Payload,PayloadHash,ValidatorVersion,RegisteredId,RegisteredRevision
            FROM dbo.C1ShipToSelection WITH (UPDLOCK,HOLDLOCK)
            WHERE TenantId=@Tenant AND Id=@SelectionId AND CustomerId=@CustomerId
            """, scope, tx, ct));
        if (selected is null || selected.ValidatorVersion != 1 ||
            !CryptographicOperations.FixedTimeEquals(selected.PayloadHash, SHA256.HashData(Encoding.UTF8.GetBytes(selected.Payload))))
            return BusinessCommandOutcome.Rejected("C2_SHIP_TO_INVALID");
        if (await db.ExecuteScalarAsync<int>(Sql("""
            SELECT COUNT(*) FROM dbo.C2Confirmation WHERE TenantId=@Tenant AND SelectionId=@SelectionId
            """, scope, tx, ct)) != 0)
            return BusinessCommandOutcome.Rejected("C2_SHIP_TO_ALREADY_BOUND");
        if (selected.RegisteredId is Guid registeredId &&
            selected.RegisteredRevision != await db.ExecuteScalarAsync<int?>(Sql("""
                SELECT MAX(Revision) FROM dbo.C1RegisteredShipToRevision WITH (HOLDLOCK)
                WHERE TenantId=@Tenant AND CustomerId=@CustomerId AND RegisteredId=@RegisteredId
                """, new { Tenant = tenant, draft.CustomerId, RegisteredId = registeredId }, tx, ct)))
            return BusinessCommandOutcome.Rejected("C2_SHIP_TO_STALE");
        var address = JsonSerializer.Deserialize<C1Address>(selected.Payload, C1Validation.Json);
        if (address is null || C1Validation.AddressPayload(address) != selected.Payload)
            return BusinessCommandOutcome.Rejected("C2_SHIP_TO_INVALID");

        var lines = (await db.QueryAsync<C2PreparedLine>(Sql("""
            SELECT l.LineNumber,l.SkuId,l.BaseQuantity,l.BaseUom,s.BaseUom AS CurrentBaseUom,s.IsActive
            FROM dbo.C2OrderDraftLine l JOIN dbo.Sku s ON s.TenantId=l.TenantId AND s.Id=l.SkuId
            WHERE l.TenantId=@Tenant AND l.DraftId=@OrderId ORDER BY l.LineNumber
            """, scope, tx, ct))).AsList();
        if (lines.Count == 0 || lines.Any(l => !l.IsActive))
            return BusinessCommandOutcome.Rejected("C2_SKU_INVALID");
        if (lines.Any(l => !string.Equals(l.BaseUom, l.CurrentBaseUom, StringComparison.Ordinal)))
            return BusinessCommandOutcome.Rejected("C2_BASE_UOM_CHANGED");

        var now = DateTime.SpecifyKind(await db.ExecuteScalarAsync<DateTime>(Sql(
            "SELECT SYSUTCDATETIME()", null, tx, ct)), DateTimeKind.Utc);
        var frozen = new List<C2FrozenLine>();
        foreach (var line in lines)
        {
            var policy = await db.QuerySingleOrDefaultAsync<C2CommercialPolicy>(Sql("""
                SELECT TOP(1) Id,Version,Decision,EffectiveFromUtc,EffectiveUntilUtc,Maker,Checker,
                    ApprovedAtUtc,EvidenceRef,UnitPrice,DiscountPerUnit,TaxRate,TermsDays,CreditCap,SellerTimeZone
                FROM dbo.C2CommercialPolicy WITH (HOLDLOCK)
                WHERE TenantId=@Tenant AND CustomerId=@CustomerId AND SkuId=@SkuId
                    AND EffectiveFromUtc<=@Now
                ORDER BY Version DESC
                """, C2ClockParameters(new { Tenant = tenant, draft.CustomerId, line.SkuId }, now), tx, ct));
            if (!ValidC2Policy(policy, actor, now))
                return BusinessCommandOutcome.Rejected("C2_POLICY_REQUIRED");
            var approved = policy! with
            {
                EffectiveFromUtc = DateTime.SpecifyKind(policy.EffectiveFromUtc, DateTimeKind.Utc),
                EffectiveUntilUtc = DateTime.SpecifyKind(policy.EffectiveUntilUtc, DateTimeKind.Utc),
                ApprovedAtUtc = DateTime.SpecifyKind(policy.ApprovedAtUtc!.Value, DateTimeKind.Utc)
            };
            decimal taxableUnit = (approved.UnitPrice!.Value - approved.DiscountPerUnit!.Value)
                * (1 + approved.TaxRate!.Value);
            if (taxableUnit > 0 && line.BaseQuantity > 999999999999999.9999m / taxableUnit)
                return BusinessCommandOutcome.Rejected("C2_CREDIT_CAP_EXCEEDED");
            decimal gross = decimal.Round(taxableUnit * line.BaseQuantity, 4, MidpointRounding.AwayFromZero);
            string payload = JsonSerializer.Serialize(new
            {
                Seller = tenant, draft.CustomerId, line.SkuId, line.BaseUom, Factor = 1, Currency = "SYNTHETIC_UNITLESS",
                InvoiceEligible = false, Policy = approved
            }, C1Validation.Json);
            frozen.Add(new(line, approved, payload, gross));
        }
        var first = frozen[0].Policy;
        if (frozen.Any(l => l.Policy.TermsDays != first.TermsDays || l.Policy.CreditCap != first.CreditCap))
            return BusinessCommandOutcome.Rejected("C2_POLICY_SCOPE_CONFLICT");
        if (frozen.Sum(l => l.Gross) > first.CreditCap!.Value)
            return BusinessCommandOutcome.Rejected("C2_CREDIT_CAP_EXCEEDED");

        Guid shipTo = Guid.NewGuid();
        string street = string.Join(", ", address.Line, address.Locality, address.Region, address.Country);
        await db.ExecuteAsync(Sql("""
            INSERT dbo.ShipToSnapshot(Id,TenantId,CustomerId,DisplayName,Address,CapturedAtUtc)
            VALUES(@ShipTo,@Tenant,@CustomerId,@DisplayName,@Address,@Now);
            INSERT dbo.AOrder(Id,TenantId,OrderNumber,CustomerId,ShipToSnapshotId,TripId,BusinessDate,Status,ConfirmedAtUtc)
            VALUES(@OrderId,@Tenant,@OrderNumber,@CustomerId,@ShipTo,@TripId,
                CAST((@Now AT TIME ZONE 'UTC') AT TIME ZONE 'SE Asia Standard Time' AS date),N'CONFIRMED',@Now);
            """, C2ClockParameters(new
        {
            ShipTo = shipTo, Tenant = tenant, draft.CustomerId, DisplayName = address.Recipient, Address = street,
            OrderId = orderId, OrderNumber = $"A-{orderId:N}", draft.TripId
        }, now), tx, ct));
        Guid confirm = await AddSource(db, tx, tenant, command, "A_CONFIRM", sourceNumber, actor, draft.CustomerId, draft.TripId, ct);
        await db.ExecuteAsync(Sql("""
            INSERT dbo.C2Confirmation(TenantId,OrderId,CustomerId,SelectionId,ShipToSnapshotId,ConfirmSourceId)
            VALUES(@Tenant,@OrderId,@CustomerId,@SelectionId,@ShipTo,@Confirm)
            """, new { Tenant = tenant, OrderId = orderId, draft.CustomerId, draft.SelectionId, ShipTo = shipTo, Confirm = confirm }, tx, ct));
        foreach (var entry in frozen)
        {
            var line = entry.Line;
            var approved = entry.Policy;
            Guid lineId = Guid.NewGuid();
            Guid sourceLine = await AddLine(db, tx, tenant, confirm, line.LineNumber, line.SkuId, line.BaseQuantity, null, ct);
            await db.ExecuteAsync(Sql("""
                INSERT dbo.AOrderLine(Id,TenantId,AOrderId,LineNumber,SkuId,BaseQuantity,UomCode,UomToBaseFactor,UnitPrice)
                VALUES(@LineId,@Tenant,@OrderId,@LineNumber,@SkuId,@BaseQuantity,@BaseUom,1,@UnitPrice);
                INSERT dbo.AConfirmedLineSnapshot(TenantId,AOrderLineId,AOrderId,TripId,CustomerId,
                    SkuId,ConfirmBusinessSourceId,ConfirmSourceLineId,ConfirmedBaseQuantity,UnitPriceSnapshot,TermsDaysSnapshot,ConfirmedAtUtc)
                VALUES(@Tenant,@LineId,@OrderId,@TripId,@CustomerId,@SkuId,@Confirm,@SourceLine,@BaseQuantity,@UnitPrice,@TermsDays,@Now);
                INSERT dbo.C2LinePolicy(TenantId,OrderLineId,PolicyId,PolicyVersion,SourceHash,PolicyPayload,BaseUom,GrossAmount)
                VALUES(@Tenant,@LineId,@PolicyId,@Version,@Hash,@Payload,@BaseUom,@Gross);
                """, C2ClockParameters(new
            {
                LineId = lineId, Tenant = tenant, OrderId = orderId, line.LineNumber, line.SkuId, line.BaseQuantity, line.BaseUom,
                approved.UnitPrice, draft.TripId, draft.CustomerId, Confirm = confirm, SourceLine = sourceLine,
                approved.TermsDays, PolicyId = approved.Id, approved.Version,
                Hash = SHA256.HashData(Encoding.UTF8.GetBytes(entry.Payload)), entry.Payload, entry.Gross
            }, now), tx, ct));
        }
        return BusinessCommandOutcome.Applied("C2_ORDER_CONFIRMED", confirm);
    }

    private static DynamicParameters C2ClockParameters(object fields, DateTime now)
    {
        var parameters = new DynamicParameters(fields);
        parameters.Add("Now", now, DbType.DateTime2);
        return parameters;
    }

    private static bool ValidC2Policy(C2CommercialPolicy? policy, string actor, DateTime now) =>
        policy is { Decision: "APPROVED", UnitPrice: >= 0, DiscountPerUnit: >= 0,
            TaxRate: >= 0 and <= 1, TermsDays: >= 0 and <= 365, CreditCap: >= 0, SellerTimeZone: SellerZone }
        && policy.DiscountPerUnit <= policy.UnitPrice && policy.EffectiveUntilUtc > now
        && policy.ApprovedAtUtc is DateTime approval && approval <= now
        && !string.IsNullOrWhiteSpace(policy.Maker) && !string.IsNullOrWhiteSpace(policy.Checker)
        && !string.Equals(policy.Maker.Trim(), policy.Checker.Trim(), StringComparison.OrdinalIgnoreCase)
        && !string.Equals(actor, policy.Checker.Trim(), StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(policy.EvidenceRef);
}
