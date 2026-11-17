using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FmcgDms.Api.Persistence;

/// <summary>
/// APP-02A.2 synthetic-only canonical event-v1 fixture contract.
/// This is deliberately NOT an intake endpoint or a command processor.
/// </summary>
public sealed record App02EventEnvelope(
    string TenantId, string ProducerId, string EventId, string EventKind,
    string ActorId, Guid TripId, Guid? OfferId, decimal? OfferedAmount,
    string? PrerequisiteProducerId, string? PrerequisiteEventId,
    string? CountEvidenceRef, DateTimeOffset OccurredAtUtc);

public static class App02EventCanonicalV1
{
    public const short Version = 1;
    public const string OfferKind = "CashHandoverOffered";
    public const string AcceptanceKind = "CashierAcceptanceRequested";

    // Synthetic event-v1 identity grammar. No normalization, implicit trims,
    // SQL tenant selection, culture defaults or floating-point intermediates.
    private static readonly Regex Identifier = new(
        @"\A[A-Za-z0-9][A-Za-z0-9._:-]{0,127}\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static bool IsId(string? value) =>
        value is not null && Identifier.IsMatch(value);

    public static byte[] Encode(App02EventEnvelope value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.TenantId is not ("tenant-a" or "tenant-b")
            || !IsId(value.ProducerId) || !IsId(value.EventId)
            || !IsId(value.ActorId) || value.TripId == Guid.Empty
            || value.OccurredAtUtc == default)
            throw new ArgumentException("Invalid synthetic event-v1 identity.", nameof(value));

        bool offer = value.EventKind == OfferKind;
        bool acceptance = value.EventKind == AcceptanceKind;
        const decimal maxSqlAmount = 999999999999999.9999m;
        if ((!offer && !acceptance)
            || (offer && (value.OfferId is null || value.OfferId == Guid.Empty
                || value.OfferedAmount is null or <= 0
                || value.OfferedAmount > maxSqlAmount
                || decimal.Round(value.OfferedAmount.Value, 4) != value.OfferedAmount.Value
                || value.PrerequisiteProducerId is not null
                || value.PrerequisiteEventId is not null
                || value.CountEvidenceRef is not null))
            || (acceptance && (value.OfferId is not null
                || value.OfferedAmount is not null
                || !IsId(value.PrerequisiteProducerId)
                || !IsId(value.PrerequisiteEventId)
                || !IsId(value.CountEvidenceRef))))
            throw new ArgumentException("Invalid P/C event-v1 shape.", nameof(value));

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", Version);
            writer.WriteString("tenantId", value.TenantId);
            writer.WriteString("producerId", value.ProducerId);
            writer.WriteString("eventId", value.EventId);
            writer.WriteString("kind", value.EventKind);
            writer.WriteString("actorId", value.ActorId);
            writer.WriteString("tripId", value.TripId.ToString("N"));
            NullableString(writer, "offerId", value.OfferId?.ToString("N"));
            NullableString(writer, "offeredAmount", value.OfferedAmount?.ToString(
                "0.############################", CultureInfo.InvariantCulture));
            NullableString(writer, "prerequisiteProducerId", value.PrerequisiteProducerId);
            NullableString(writer, "prerequisiteEventId", value.PrerequisiteEventId);
            NullableString(writer, "countEvidenceRef", value.CountEvidenceRef);
            writer.WriteString("occurredAtUtc",
                value.OccurredAtUtc.ToUniversalTime().ToString(
                    "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture));
            writer.WriteEndObject();
            writer.Flush();
        }

        if (buffer.WrittenCount > 4096)
            throw new ArgumentException("Event-v1 canonical bytes exceed 4096.", nameof(value));
        return buffer.WrittenSpan.ToArray();
    }

    public static byte[] Sha256(App02EventEnvelope value) =>
        SHA256.HashData(Encode(value));

    private static void NullableString(Utf8JsonWriter writer, string field, string? value)
    {
        if (value is null)
            writer.WriteNull(field);
        else
            writer.WriteString(field, value);
    }
}
