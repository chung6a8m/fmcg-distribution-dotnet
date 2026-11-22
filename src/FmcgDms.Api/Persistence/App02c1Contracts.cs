using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FmcgDms.Api.Persistence;

public interface IC1Command { Guid CommandId { get; } }
public sealed record C1CustomerInput(Guid CommandId, Guid? Id, string? Version, string? Code, string? Name) : IC1Command;
public sealed record C1OutletInput(Guid CommandId, Guid CustomerId, Guid? Id, string? Version, string? Name) : IC1Command;
public sealed record C1RegisteredInput(Guid CommandId, Guid CustomerId, Guid? Id, int? Revision,
    Guid? OutletId, C1Address? Address) : IC1Command;
public sealed record C1SelectionInput(Guid CommandId, Guid CustomerId, Guid? OutletId, Guid? RegisteredId,
    int? Revision, C1Address? Address) : IC1Command;
public sealed record C1Address(string Country, string Region, string Locality, string Line, string Recipient);
public sealed record C1Outcome(int HttpStatus, string Code, Guid? Id = null, int? Revision = null, bool Replayed = false);
public sealed record C1Identity(string TenantId, string ActorId, string SessionId);
public sealed record C1Customer(Guid Id, string Code, string Name, bool IsActive, string Version);
public sealed record C1Outlet(Guid Id, string Name, string Version);
public sealed record C1Registered(Guid Id, int Revision, Guid? OutletId, C1Address Address);
public sealed record C1Selection(Guid Id, string Origin, Guid? OutletId, Guid? RegisteredId,
    int? RegisteredRevision, C1Address Address, DateTime CapturedAtUtc);
public sealed record C1Detail(C1Customer Customer, IReadOnlyList<C1Outlet> Outlets,
    IReadOnlyList<C1Registered> RegisteredShipTos, IReadOnlyList<C1Selection> Selections);

internal static partial class C1Validation
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\A[A-Z0-9][A-Z0-9_-]{0,31}\z", RegexOptions.CultureInvariant)]
    internal static partial Regex CustomerCode();

    [GeneratedRegex(@"\A[0-9A-Fa-f]{16}\z", RegexOptions.CultureInvariant)]
    internal static partial Regex RowVersion();

    internal static string? Text(string? raw, int limit)
    {
        if (raw is null || raw.Length > limit * 4 || raw.Any(char.IsControl)) return null;
        try
        {
            string normalized = Whitespace().Replace(raw.Normalize(NormalizationForm.FormC).Trim(), " ");
            return normalized.Length is > 0 && normalized.Length <= limit ? normalized : null;
        }
        catch (ArgumentException) { return null; }
    }

    internal static string? AddressPayload(C1Address? address)
    {
        if (address is not { Country: "VN", Region: "HCM" or "HN" }) return null;
        string? locality = Text(address.Locality, 120);
        string? line = Text(address.Line, 200);
        string? recipient = Text(address.Recipient, 120);
        return locality is null || line is null || recipient is null ? null
            : JsonSerializer.Serialize(address with { Locality = locality, Line = line, Recipient = recipient }, Json);
    }
}
