using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FmcgDms.Api.Persistence;

public interface IApp01aCommand
{
    string ProducerId { get; }
    string CommandId { get; }
    string SourceNumber { get; }
}

public sealed record ConfirmAOrderCommand(
    string ProducerId, string CommandId, string SourceNumber, Guid OrderId) : IApp01aCommand;

public sealed record ReserveAStockCommand(
    string ProducerId, string CommandId, string SourceNumber,
    Guid OrderId, Guid OrderLineId, Guid StockLocationId, decimal BaseQuantity) : IApp01aCommand;

public sealed record GrantBQuotaCommand(
    string ProducerId, string CommandId, string SourceNumber,
    Guid TripId, Guid StockLocationId, Guid SkuId, decimal BaseQuantity) : IApp01aCommand;

/// <summary>Load the entire pre-staged, rights-scoped DRAFT manifest once.</summary>
public sealed record LoadManifestCommand(
    string ProducerId, string CommandId, string SourceNumber,
    Guid ManifestId) : IApp01aCommand;

/// <summary>
/// Non-posting B_ONLINE sale offer check for AC-03 T2 (synthetic only).
/// A valid within-quota offer is still rejected until APP-01C.
/// </summary>
public sealed record PreflightBSaleCommand(
    string ProducerId, string CommandId, string SourceNumber,
    Guid TripId, Guid SkuId, decimal BaseQuantity,
    decimal UnitPrice, decimal OfferedCashAmount) : IApp01aCommand;

/// <summary>
/// Synthetic-only B sale with source-bound loaded right and independently
/// verified physical cash evidence; no caller-supplied VERIFIED flag.
/// </summary>
public sealed record PostBSaleDeliveryReceiptCommand(
    string ProducerId, string CommandId, string SourceNumber,
    Guid TripId, Guid CustomerId, Guid ShipToSnapshotId,
    Guid TripManifestLineId, Guid SkuId, decimal BaseQuantity,
    decimal UnitPrice, decimal ReceivedCashAmount,
    string VerificationEvidenceRef,
    string? PendingBankEvidenceRef = null,
    Guid? CustomerCreditReservationId = null) : IApp01aCommand;

/// <summary>
/// One A delivery against a loaded, exact reserved source/order line.
/// ConfirmAOrder independently froze price and approved N-day terms.
/// No unit price or terms are accepted from this delivery command.
/// </summary>
public sealed record PostAPartialDeliveryCommand(
    string ProducerId, string CommandId, string SourceNumber,
    Guid TripId, Guid CustomerId, Guid ShipToSnapshotId,
    Guid AOrderId, Guid AOrderLineId, Guid TripManifestLineId, Guid SkuId,
    decimal BaseQuantity) : IApp01aCommand;

/// <summary>Apply part of a confirmed receipt to an open receivable on the same trip.</summary>
public sealed record AllocateCashReceiptCommand(
    string ProducerId, string CommandId, string SourceNumber,
    Guid TripId, Guid CashReceiptId, Guid ReceivableOpenItemId,
    decimal Amount) : IApp01aCommand;

/// <summary>APP-01D: a cash OFFER has no ledger impact.</summary>
public sealed record OfferCashHandoverCommand(
    string ProducerId, string CommandId, string SourceNumber,
    Guid TripId, decimal OfferedAmount) : IApp01aCommand;

/// <summary>Only independently counted cashier evidence can accept an offer.</summary>
public sealed record AcceptCashHandoverCommand(
    string ProducerId, string CommandId, string SourceNumber,
    Guid TripId, Guid OfferId, string CountEvidenceRef) : IApp01aCommand;

/// <summary>Atomically unload all remaining source-backed manifest lines.</summary>
public sealed record UnloadTripRemainderCommand(
    string ProducerId, string CommandId, string SourceNumber,
    Guid TripId, Guid ManifestId) : IApp01aCommand;

public sealed record GrantBCreditCommand(
    string ProducerId, string CommandId, string SourceNumber,
    Guid TripId, Guid CustomerId, Guid ShipToSnapshotId,
    string PoolCode, string CurrencyUnit, decimal Amount) : IApp01aCommand;

/// <summary>Durable APPLIED/REJECTED outcome, or a no-effect idempotency CONFLICT.</summary>
public sealed record BusinessCommandOutcome(
    string Status, string OutcomeCode, string? ReasonCode, bool Replayed, Guid? SourceId)
{
    public static BusinessCommandOutcome Applied(string code, Guid source) =>
        new("APPLIED", code, null, false, source);
    public static BusinessCommandOutcome Rejected(string reason) =>
        new("REJECTED", "REJECTED", reason, false, null);
    public static BusinessCommandOutcome Conflict() =>
        new("CONFLICT", "CONFLICT", "IDEMPOTENCY_PAYLOAD_MISMATCH", false, null);
}

/// <summary>
/// Version 1 canonical UTF-8 JSON. Field order is specified by each command
/// handler, GUIDs are N-format and decimal values are invariant without scale
/// padding. Never hash arbitrary request JSON, header-selected tenant or floats.
/// Changing this representation requires a new persisted hash version.
/// </summary>
public static class CommandPayloadHash
{
    public const short Version = 1;

    public static string Decimal(decimal value) =>
        value.ToString("0.############################", CultureInfo.InvariantCulture);

    public static byte[] Compute(string kind, string tenant, string actor, string source,
        params (string Key, string Value)[] fields)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            Version,
            Kind = kind,
            TenantId = tenant,
            ActorId = actor,
            SourceNumber = source,
            Fields = fields.Select(f => new { f.Key, f.Value }).ToArray()
        });
        return SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
    }
}
