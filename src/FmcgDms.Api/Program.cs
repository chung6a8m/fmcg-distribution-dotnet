using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Dapper;
using FmcgDms.Api.Authentication;
using FmcgDms.Api.Persistence;
using FmcgDms.Api.Tenancy;

var builder = WebApplication.CreateBuilder(args);
SyntheticAuth.Configure(builder);
builder.Services.AddSingleton<TenantConnections>();
builder.Services.AddScoped<App01a2CommandService>();
builder.Services.AddScoped<App01b2CommandService>();
builder.Services.AddScoped<App01c2BPostingService>();
builder.Services.AddScoped<App01a3AuditService>();

var app = builder.Build();
// Do not reveal misrouted tenant connection information or allow a fallback DB.
app.Use(async (context, next) =>
{
    try { await next(); }
    catch (UnauthorizedAccessException)
    {
        if (!context.Response.HasStarted)
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
    }
});
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/health/live", () => Results.Ok(new { status = "live", scope = "APP-00A", businessWrites = false }));

if ((app.Environment.IsDevelopment() || app.Environment.IsEnvironment("IntegrationTest"))
    && app.Configuration.GetValue<bool>("SyntheticAuth:Enabled"))
{
    // Synthetic diagnostics ONLY. Not a production business API.
    var diag = app.MapGroup("/api/diagnostics").RequireAuthorization();
    diag.MapGet("/tenant", async (ClaimsPrincipal principal, TenantConnections db, CancellationToken ct) =>
    {
        await using var conn = await db.OpenSyntheticScratchAsync(principal, ct);
        var result = await conn.QuerySingleAsync<TenantDbInfo>(
            "SELECT DB_NAME() AS DatabaseName, TenantId AS BoundTenantId FROM dbo.AppTenantBinding WHERE Slot=1");
        string tenantId = SyntheticAuth.GetTenantId(principal);
        if (result.BoundTenantId != tenantId) return Results.Forbid();
        return Results.Ok(new { tenantId, result.DatabaseName });
    });
    diag.MapPost("/items", async (DiagnosticItemInput input, ClaimsPrincipal principal, TenantConnections db, CancellationToken ct) =>
    {
        if (string.IsNullOrWhiteSpace(input.Description) || input.Description.Length > 120)
            return Results.BadRequest(new { error = "DESCRIPTION_INVALID" });
        await using var conn = await db.OpenSyntheticScratchAsync(principal, ct);
        if (await conn.ExecuteScalarAsync<string>("SELECT TenantId FROM dbo.AppTenantBinding WHERE Slot=1")
            != SyntheticAuth.GetTenantId(principal)) return Results.Forbid();
        var id = Guid.NewGuid();
        await conn.ExecuteAsync(
            "INSERT INTO dbo.DiagnosticItem(Id,Description,CreatedAtUtc) VALUES (@Id,@Description,SYSUTCDATETIME())",
            new { Id = id, input.Description });
        return Results.Created($"/api/diagnostics/items/{id}", new { id });
    });
    diag.MapGet("/items", async (ClaimsPrincipal principal, TenantConnections db, CancellationToken ct) =>
    {
        await using var conn = await db.OpenSyntheticScratchAsync(principal, ct);
        if (await conn.ExecuteScalarAsync<string>("SELECT TenantId FROM dbo.AppTenantBinding WHERE Slot=1")
            != SyntheticAuth.GetTenantId(principal)) return Results.Forbid();
        var rows = await conn.QueryAsync<DiagnosticItem>(
            "SELECT Id,Description FROM dbo.DiagnosticItem ORDER BY CreatedAtUtc,Id");
        return Results.Ok(rows);
    });
    // APP-01A.2 is deliberately not a production route. Signed synthetic JWT,
    // exact tenant binding and nonce-owned LocalDB are required by the service.
    var commands = app.MapGroup("/api/synthetic/app01a/commands").RequireAuthorization();
    commands.MapPost("/reserve-a-stock", async (
        ReserveAStockCommand input, ClaimsPrincipal user, App01a2CommandService service, CancellationToken ct) =>
        ToCommandHttp(await service.ReserveAStockAsync(user, input, ct)))
        .RequireAuthorization("SyntheticWarehouse");
    commands.MapPost("/confirm-a-order", async (
        ConfirmAOrderCommand input, ClaimsPrincipal user, App01a2CommandService service, CancellationToken ct) =>
        ToCommandHttp(await service.ConfirmAOrderAsync(user, input, ct)))
        .RequireAuthorization("SyntheticSales");
    commands.MapPost("/grant-b-quota", async (
        GrantBQuotaCommand input, ClaimsPrincipal user, App01a2CommandService service, CancellationToken ct) =>
        ToCommandHttp(await service.GrantBQuotaAsync(user, input, ct)))
        .RequireAuthorization("SyntheticWarehouse");
    commands.MapPost("/grant-b-credit", async (
        GrantBCreditCommand input, ClaimsPrincipal user, App01a2CommandService service, CancellationToken ct) =>
        ToCommandHttp(await service.GrantBCreditAsync(user, input, ct)))
        .RequireAuthorization("SyntheticWarehouse");

    // APP-01B.2: signed LocalDB-only synthetic warehouse transfer and B sale
    // quota preflight. Neither route exists in production.
    var manifestCommands = app.MapGroup("/api/synthetic/app01b/commands")
        .RequireAuthorization();
    manifestCommands.MapPost("/load-manifest", async (
        LoadManifestCommand input, ClaimsPrincipal user,
        App01b2CommandService service, CancellationToken ct) =>
        ToCommandHttp(await service.LoadManifestAsync(user, input, ct)))
        .RequireAuthorization("SyntheticWarehouse");
    manifestCommands.MapPost("/preflight-b-sale", async (
        PreflightBSaleCommand input, ClaimsPrincipal user,
        App01b2CommandService service, CancellationToken ct) =>
        ToCommandHttp(await service.PreflightBSaleAsync(user, input, ct)))
        .RequireAuthorization("SyntheticSales");

    // Explicit UTC cutoff; server-resolved tenant only. Reads never select a
    // target tenant/database by query string, header or body.
    app.MapGet("/api/synthetic/app01a/audit/as-of", async (
        HttpContext context, ClaimsPrincipal user, App01a3AuditService audit, CancellationToken ct) =>
    {
        var values = context.Request.Query["asOf"];
        if (values.Count != 1 || !TryParseAuditAsOf(values[0], out var asOf))
            return Results.BadRequest(new { error = "AS_OF_INVALID" });
        return Results.Ok(await audit.QueryAsync(user, asOf, ct));
    }).RequireAuthorization("SyntheticAudit");

}
app.Run();

// Reject timestamps without an explicit timezone: DateTimeOffset's default parser
// otherwise interprets an offset-less local wall clock using the server timezone.
static bool TryParseAuditAsOf(string? text, out DateTimeOffset utc)
{
    utc = default;
    if (string.IsNullOrWhiteSpace(text)
        || !Regex.IsMatch(text,
            @"\A\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})\z",
            RegexOptions.CultureInvariant)
        || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None,
            out var parsed)
        || parsed == default || parsed > DateTimeOffset.UtcNow.AddMinutes(1))
        return false;
    utc = parsed.ToUniversalTime();
    return true;
}

static IResult ToCommandHttp(BusinessCommandOutcome outcome) => outcome.Status switch
{
    "CONFLICT" => Results.Conflict(outcome),
    _ when outcome.ReasonCode == "INVALID_COMMAND_IDENTITY" => Results.BadRequest(outcome),
    _ => Results.Ok(outcome)
};

public sealed record DiagnosticItemInput(string Description);
public sealed record DiagnosticItem(Guid Id, string Description);
public sealed class TenantDbInfo
{
    public string DatabaseName { get; set; } = "";
    public string BoundTenantId { get; set; } = "";
}
public partial class Program { }
