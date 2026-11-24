using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using FmcgDms.Api.Authentication;
using FmcgDms.Api.Tenancy;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Persistence;

internal sealed class App02d4Denied : UnauthorizedAccessException;

internal sealed record D4Source(Guid DeliveryId, Guid SourceLineId, string Seller, string Channel,
    DateTimeOffset OccurredAt, decimal Quantity, decimal Gross, decimal Ar, decimal Cost, string Hash);

internal sealed class App02d4Runtime(TenantConnections connections, IConfiguration configuration, IHostEnvironment environment)
{
    private static readonly SocketsHttpHandler Handler = new() { AllowAutoRedirect = false, UseProxy = false };
    private Uri Origin => new(configuration["App02D4:Origin"]!);
    internal bool UsesFinance => connections.UsesApp03Finance;
    internal DateTimeOffset UtcNow => new(App02c6Clock.Read(App02c6Clock.GetConfiguredPath(configuration, environment)!));

    internal static bool Enabled(IConfiguration config, IHostEnvironment environment)
    {
        if (!config.GetValue<bool>("App02D4:Enabled")) return false;
        if (!environment.IsEnvironment("IntegrationTest") || !App02d3Runtime.Enabled(config, environment)
            || App02c6Clock.GetConfiguredPath(config, environment) is null
            || config["App02D4:Profile"] != "D4-v0.1"
            || config["App02D4:Nonce"] != config["App02C1:Nonce"]
            || config["App02D4:PartnerKey"] is not { Length: 64 } key || !key.All(Uri.IsHexDigit)
            || !Uri.TryCreate(config["App02D4:Origin"], UriKind.Absolute, out var origin)
            || !origin.IsWellFormedOriginalString() || origin.Scheme != "http" || origin.Host != "127.0.0.1"
            || origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0
            || origin.UserInfo.Length != 0 || origin.IsDefaultPort
            || config["urls"] != origin.GetLeftPart(UriPartial.Authority))
            throw new InvalidOperationException("D.4 requires the guarded two-tenant LocalDB rehearsal profile and loopback origin.");
        return true;
    }

    internal async Task Verify(CancellationToken ct)
    {
        foreach (string tenant in new[] { "tenant-a", "tenant-b" })
        {
            await using var db = await connections.OpenApp02c4Async(App02a3Sql.WorkerPrincipal(tenant), ct);
            if (await db.ExecuteScalarAsync<int>(new CommandDefinition(
                    "SELECT COUNT(*) FROM dbo.VersionInfo WHERE Version=202611230001", cancellationToken: ct)) != 1)
                throw new InvalidOperationException("D.4 requires its migration on each nonce-owned tenant database.");
        }
    }

    internal void Map(WebApplication app)
    {
        // Keep the D.4 public API and both technical endpoints on the configured loopback listener.
        app.Use(async (context, next) =>
        {
            if ((context.Request.Path.StartsWithSegments("/api/synthetic/app02d4")
                 || context.Request.Path.StartsWithSegments("/stub"))
                && (context.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address)
                    || context.Connection.LocalPort != Origin.Port))
            {
                context.Response.StatusCode = 403;
                return;
            }
            await next();
        });
        var group = app.MapGroup("/api/synthetic/app02d4").RequireAuthorization();
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            if (context.HttpContext.Request.Headers.ContainsKey("X-Tenant-Id"))
                return Results.StatusCode(403);
            return await next(context);
        });
        group.MapGet("/session", async (ClaimsPrincipal user, CancellationToken ct) =>
        {
            await using var db = await Open(user, "", ct);
            return Results.Ok(new { tenantId = SyntheticAuth.GetTenantId(user), roles = await Roles(db, user, ct) });
        });
        group.MapGet("/sources", async (ClaimsPrincipal user, CancellationToken ct) =>
        {
            await using var db = await Open(user, "", ct);
            return Results.Ok(await ReadSources(db, ct));
        });
        App02d4Invoice.Map(group);
        App02d4Accounting.Map(group);
        if (connections.UsesApp03Finance)
        {
            group.MapGet("/accounting/queue", FinanceQueue);
            group.MapGet("/accounting/{deliveryId:guid}/transfers/{operation}/{transportId:guid}", FinanceTransfer);
        }
        App02d4Invoice.MapStub(app.MapGroup("/stub/invoice"));
        App02d4Accounting.MapStub(app.MapGroup("/stub/accounting"));
    }

    internal async Task<string[]> FinanceRoles(ClaimsPrincipal user, CancellationToken ct)
    {
        await using var db = await Open(user, "", ct);
        return await Roles(db, user, ct);
    }

    private async Task<IResult> FinanceTransfer(Guid deliveryId, string operation, Guid transportId,
        ClaimsPrincipal user, CancellationToken ct)
    {
        await using var db = await Open(user, "accountant", ct);
        if (operation is not ("receive" or "post")) return Results.NotFound();
        var outcome = await db.QuerySingleOrDefaultAsync<FinanceTransferView>(new CommandDefinition("""
            SELECT TOP(1) Id,SourceLineId
            FROM d4app.FinanceTransferOutcome
            WHERE DeliveryId=@deliveryId AND Operation=@operation AND TransportId=@transport
            ORDER BY RecordedAt DESC
            """, new { deliveryId, operation = operation.ToUpperInvariant(), transport = transportId.ToString("D") },
            cancellationToken: ct));
        return outcome is null ? Results.NotFound() : Results.Ok(outcome);
    }

    private sealed record FinanceTransferView(Guid Id, Guid SourceLineId);

    private async Task<IResult> FinanceQueue(ClaimsPrincipal user, CancellationToken ct)
    {
        await using var db = await Open(user, "", ct);
        using var rows = await db.QueryMultipleAsync(new CommandDefinition("""
            SELECT DeliveryId,SourceLineId,Seller,Channel,OccurredAt,Quantity,Gross,Ar,Cost,ReceiptId,PostingId,
                ReportId,ReconciliationStatus,Customer,Sku,Trip,ReportFromUtc,ReportCutoff,ReportAsOf,ReconciliationStale
            FROM d4app.FinanceQueueProvenance ORDER BY OccurredAt DESC,DeliveryId;
            SELECT Id,FromUtc,Cutoff,AsOf,Status FROM d4app.AccountingReport ORDER BY FinanceSequence DESC;
            SELECT Id,ReportId,DeliveryId,SourceLineId,ClosedReportId,Reason,ClosedBy,CanonicalCaseId
            FROM d4app.FinanceCaseLinks ORDER BY OccurredAt DESC,Id;
            SELECT c.Id,c.CaseId,q.DeliveryId,c.Period,c.PostingId,c.ApprovalId,c.Maker
            FROM d4app.AccountingCorrection c
            JOIN d4app.FinanceQueue q ON q.SourceLineId=c.SourceLineId
            ORDER BY q.OccurredAt DESC,c.Id;
            """, cancellationToken: ct));
        return Results.Ok(new
        {
            asOf = UtcNow,
            sources = await rows.ReadAsync<FinanceSource>(),
            reports = await rows.ReadAsync<FinanceReport>(),
            cases = await rows.ReadAsync<FinanceCase>(),
            corrections = await rows.ReadAsync<FinanceCorrection>()
        });
    }

    private sealed record FinanceSource(Guid DeliveryId, Guid SourceLineId, string Seller, string Channel,
        DateTimeOffset OccurredAt, decimal Quantity, decimal Gross, decimal Ar, decimal Cost,
        Guid? ReceiptId, Guid? PostingId, Guid? ReportId, string? ReconciliationStatus, string Customer, string Sku, string Trip,
        DateTimeOffset? ReportFromUtc, DateTimeOffset? ReportCutoff, DateTimeOffset? ReportAsOf, bool ReconciliationStale);
    private sealed record FinanceReport(Guid Id, DateTimeOffset FromUtc, DateTimeOffset Cutoff, DateTimeOffset AsOf, string Status);
    private sealed record FinanceCase(Guid Id, Guid ReportId, Guid DeliveryId, Guid SourceLineId, Guid? ClosedReportId, string? Reason, string? ClosedBy, Guid CanonicalCaseId);
    private sealed record FinanceCorrection(Guid Id, Guid CaseId, Guid DeliveryId, string Period, Guid? PostingId, Guid? ApprovalId, string Maker);

    internal async Task<SqlConnection> Open(ClaimsPrincipal user, string role, CancellationToken ct)
    {
        if (user.Identity?.IsAuthenticated != true) throw new App02d4Denied();
        var db = await Scope(user, "d4app", ct);
        try
        {
            string[] roles = await Roles(db, user, ct);
            if (role.Length == 0 ? roles.Length == 0 : !roles.Contains(role, StringComparer.Ordinal))
                throw new App02d4Denied();
            return db;
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }

    internal Task<SqlConnection> OpenStub(HttpContext http, string partner, CancellationToken ct)
    {
        string tenant = http.Request.Headers["X-D4-Technical-Tenant"].ToString();
        string proof = http.Request.Headers["X-D4-Technical-Proof"].ToString();
        if (tenant is not ("tenant-a" or "tenant-b") || proof.Length != 64
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(proof),
                Encoding.ASCII.GetBytes(Proof(tenant, partner))))
            throw new App02d4Denied();
        return Scope(App02a3Sql.WorkerPrincipal(tenant), Principal(partner), ct);
    }

    internal HttpClient Client(HttpContext http, string partner)
    {
        _ = Principal(partner);
        string tenant = SyntheticAuth.GetTenantId(http.User);
        var client = new HttpClient(Handler, disposeHandler: false)
        {
            BaseAddress = new Uri(Origin, $"stub/{partner}/"),
            Timeout = TimeSpan.FromSeconds(3)
        };
        client.DefaultRequestHeaders.Add("X-D4-Technical-Tenant", tenant);
        client.DefaultRequestHeaders.Add("X-D4-Technical-Proof", Proof(tenant, partner));
        return client;
    }

    private static string Principal(string partner) => partner switch
    {
        "invoice" => "d4invoice",
        "accounting" => "d4accounting",
        _ => throw new ArgumentOutOfRangeException(nameof(partner))
    };

    private string Proof(string tenant, string partner) => Convert.ToHexString(HMACSHA256.HashData(
        Convert.FromHexString(configuration["App02D4:PartnerKey"]!),
        Encoding.ASCII.GetBytes($"D4-v0.1|{configuration["App02D4:Nonce"]}|{tenant}|{partner}")));

    private async Task<SqlConnection> Scope(ClaimsPrincipal user, string principal, CancellationToken ct)
    {
        var db = await connections.OpenApp02c4Async(user, ct);
        try
        {
            string scope = principal switch
            {
                "d4app" => "EXECUTE AS USER='d4app' WITH NO REVERT;",
                "d4invoice" => "EXECUTE AS USER='d4invoice' WITH NO REVERT;",
                "d4accounting" => "EXECUTE AS USER='d4accounting' WITH NO REVERT;",
                _ => throw new ArgumentOutOfRangeException(nameof(principal))
            };
            await db.ExecuteAsync(new CommandDefinition(scope, cancellationToken: ct));
            return db;
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }

    private static async Task<string[]> Roles(SqlConnection db, ClaimsPrincipal user, CancellationToken ct) =>
        (await db.QueryAsync<string>(new CommandDefinition(
            "SELECT RoleCode FROM d4app.ActorRole WHERE ActorId=@actor AND IsActive=1 ORDER BY RoleCode",
            new { actor = user.FindFirst("actor_id")?.Value }, cancellationToken: ct)))
        .Where(role => user.HasClaim("synthetic_role", role)).ToArray();

    internal async Task<D4Source?> ReadSource(SqlConnection db, Guid deliveryId, CancellationToken ct) =>
        (await QuerySources(db, deliveryId, null, ct)).SingleOrDefault();

    internal Task<IReadOnlyList<D4Source>> ReadSources(SqlConnection db, CancellationToken ct, DateTimeOffset? asOf = null) =>
        QuerySources(db, null, asOf, ct);

    private static async Task<IReadOnlyList<D4Source>> QuerySources(SqlConnection db, Guid? deliveryId, DateTimeOffset? asOf, CancellationToken ct)
    {
        var rows = await db.QueryAsync<D4Source>(new CommandDefinition("""
            SELECT DeliveryId, SourceLineId, Seller, Channel, OccurredAt, Quantity, Gross, Ar, Cost, Hash
            FROM d4app.CommittedSource
            WHERE (@deliveryId IS NULL OR DeliveryId=@deliveryId) AND (@asOf IS NULL OR RecordedAt<=@asOf)
            ORDER BY OccurredAt, DeliveryId
            """, new { deliveryId, asOf }, cancellationToken: ct));
        return rows.ToArray();
    }
}
