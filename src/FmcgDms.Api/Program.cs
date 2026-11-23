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
builder.Services.AddSingleton<App02d4Runtime>();
builder.Services.AddScoped<App01a2CommandService>();
builder.Services.AddScoped<App01b2CommandService>();
builder.Services.AddScoped<App01c2BPostingService>();
builder.Services.AddScoped<App01c3APostingService>();
builder.Services.AddScoped<App01dCashierUnloadService>();
builder.Services.AddScoped<App01a3AuditService>();
builder.Services.AddScoped<App01e4OutcomeLookupService>();
builder.Services.AddSingleton<App02a3WorkerSignal>();
builder.Services.AddScoped<App02a3EventIntakeService>();
builder.Services.AddScoped<App02a3ProcessingService>();
builder.Services.AddScoped<App02a3OutboxService>();
builder.Services.AddScoped<App02a3CaseService>();
builder.Services.AddScoped<App02a3CorrectionService>();
builder.Services.AddScoped<App02c1Service>();
builder.Services.AddHostedService<App02a3HostedWorker>();

var app = builder.Build();
bool app02d1 = App02d1Runtime.Enabled(app.Configuration, app.Environment);
bool app02d2 = App02d2Runtime.Enabled(app.Configuration, app.Environment);
if (app02d2)
    await App02d2Runtime.VerifyTenantCatalog(app.Services.GetRequiredService<TenantConnections>(), CancellationToken.None);
bool app02d3 = App02d3Runtime.Enabled(app.Configuration, app.Environment);
if (app02d3)
    await App02d3Runtime.VerifyTenantCatalog(app.Services.GetRequiredService<TenantConnections>(), CancellationToken.None);
bool app02d4 = App02d4Runtime.Enabled(app.Configuration, app.Environment);
if (app02d4)
    await app.Services.GetRequiredService<App02d4Runtime>().Verify(CancellationToken.None);
bool app02c6 = !app02d1 && App02c6Clock.GetConfiguredPath(app.Configuration, app.Environment) is not null;
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
    bool c1 = app.Configuration.GetValue<bool>("App02C1:Enabled");
    bool visits = c1 && app.Configuration.GetValue<bool>("DevF01:Enabled");
    if (c1)
        App02c1Routes.Map(app, visits);
    if (app.Configuration.GetValue<bool>("App02C2:Enabled"))
        App02c2Routes.Map(app, visits);
    if (app.Configuration.GetValue<bool>("App02C3:Enabled") && app.Configuration.GetValue<bool>("App02C2:Enabled"))
        App02c3Routes.Map(app);
    if (app.Configuration.GetValue<bool>("App02C4:Enabled") && app.Configuration.GetValue<bool>("App02C3:Enabled")
        && app.Configuration.GetValue<bool>("App02C2:Enabled"))
    {
        App02c4Routes.Map(app);
        if (app.Configuration.GetValue<bool>("App02C5:Enabled"))
            App02c5Routes.Map(app);
    }
}

if (app02d1)
{
    App02d1GrantRoutes.Map(app);
    App02d1LoadRoutes.Map(app);
}

if (app02d2)
    App02d2Routes.Map(app);
else if (app02d1)
    App02d2Routes.MapChannelBlocked(app);

if (app02d3)
    App02d3Routes.Map(app);
if (app02d4)
    app.Services.GetRequiredService<App02d4Runtime>().Map(app);

bool legacySynthetic = (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("IntegrationTest"))
    && app.Configuration.GetValue<bool>("SyntheticAuth:Enabled")
    && !app.Configuration.GetValue<bool>("App02C2:DedicatedRuntime")
    && !app.Configuration.GetValue<bool>("App02C3:Enabled");
if (legacySynthetic || app02c6)
{
    // The C.6 fixture exercises only the existing quota and allocation endpoints.
    app.MapPost("/api/synthetic/app01a/commands/grant-b-quota", async (
        GrantBQuotaCommand input, ClaimsPrincipal user, App01a2CommandService service, CancellationToken ct) =>
        ToCommandHttp(await service.GrantBQuotaAsync(user, input, ct)))
        .RequireAuthorization("SyntheticWarehouse");
    app.MapPost("/api/synthetic/app01d/commands/allocate-cash-receipt", async (
        AllocateCashReceiptCommand input, ClaimsPrincipal user, App01dCashierUnloadService service, CancellationToken ct) =>
        ToCommandHttp(await service.AllocateCashReceiptAsync(user, input, ct)))
        .RequireAuthorization("SyntheticSales");
}
if (legacySynthetic)
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

    // APP-01C.2: synthetic-only B20 atomic posting. The service requires
    // a preexisting trusted cash witness; this route is absent in production.
    var postingCommands = app.MapGroup("/api/synthetic/app01c/commands")
        .RequireAuthorization();
    postingCommands.MapPost("/post-b-sale-delivery-receipt", async (
        PostBSaleDeliveryReceiptCommand input, ClaimsPrincipal user,
        App01c2BPostingService service, CancellationToken ct) =>
        ToCommandHttp(await service.PostBSaleDeliveryReceiptAsync(user, input, ct)))
        .RequireAuthorization("SyntheticSales");
    // APP-01C.3: source/ledger atomic A25 with independently approved,
    // immutable Confirm price/terms; synthetic two-tenant LocalDB only.
    postingCommands.MapPost("/post-a-partial-delivery", async (
        PostAPartialDeliveryCommand input, ClaimsPrincipal user,
        App01c3APostingService service, CancellationToken ct) =>
        ToCommandHttp(await service.PostAPartialDeliveryAsync(user, input, ct)))
        .RequireAuthorization("SyntheticSales");

    // APP-01D: synthetic nonproduction cashier acceptance and physical
    // unload. Cash offer does not post fund/custody; accept requires a
    // separately seeded cashier count witness.
    var closeoutCommands = app.MapGroup("/api/synthetic/app01d/commands")
        .RequireAuthorization();
    closeoutCommands.MapPost("/offer-cash-handover", async (
        OfferCashHandoverCommand input, ClaimsPrincipal user,
        App01dCashierUnloadService service, CancellationToken ct) =>
        ToCommandHttp(await service.OfferCashHandoverAsync(user, input, ct)))
        .RequireAuthorization("SyntheticSales");
    closeoutCommands.MapPost("/accept-cash-handover", async (
        AcceptCashHandoverCommand input, ClaimsPrincipal user,
        App01dCashierUnloadService service, CancellationToken ct) =>
        ToCommandHttp(await service.AcceptCashHandoverAsync(user, input, ct)))
        .RequireAuthorization("SyntheticCashier");
    closeoutCommands.MapPost("/unload-trip-remainder", async (
        UnloadTripRemainderCommand input, ClaimsPrincipal user,
        App01dCashierUnloadService service, CancellationToken ct) =>
        ToCommandHttp(await service.UnloadTripRemainderAsync(user, input, ct)))
        .RequireAuthorization("SyntheticWarehouse");

    // APP-01E.4: read-only, owner-scoped durable outcome recovery.
    // UNKNOWN is not a rejected command; retry only with the original identity.
    app.MapGet("/api/synthetic/app01e/commands/outcome", async (
        HttpContext context, ClaimsPrincipal user,
        App01e4OutcomeLookupService recovery, CancellationToken ct) =>
    {
        var producers = context.Request.Query["producerId"];
        var commands = context.Request.Query["commandId"];
        if (producers.Count != 1 || commands.Count != 1
            || !App01e4OutcomeLookupService.ValidIdentity(producers[0])
            || !App01e4OutcomeLookupService.ValidIdentity(commands[0]))
            return Results.BadRequest(new { error = "COMMAND_IDENTITY_INVALID" });
        return Results.Ok(await recovery.LookupAsync(user, producers[0]!, commands[0]!, ct));
    }).RequireAuthorization("SyntheticCommandRecovery");

    // APP-02A.3: synthetic event intake/lookup and independent CASE actors.
    // These routes never appear outside Development/IntegrationTest with
    // synthetic authentication enabled; actual C posting is worker-owned.
    var app02events = app.MapGroup("/api/synthetic/app02/events")
        .RequireAuthorization();
    app02events.MapPost("/intake", async (
        App02EventInput input, ClaimsPrincipal principal,
        App02a3EventIntakeService intake, CancellationToken ct) =>
    {
        try
        {
            App02IntakeResult saved = await intake.IntakeAsync(principal, input, ct);
            if (saved.Status == "CONFLICT") return Results.Conflict(saved);
            string locator = "/api/synthetic/app02/events/outcome?producerId=" +
                Uri.EscapeDataString(input.ProducerId) + "&eventId=" +
                Uri.EscapeDataString(input.EventId);
            return Results.Accepted(locator, saved);
        }
        catch (App02InvalidEventException ex)
        {
            return Results.BadRequest(new { error = ex.Code });
        }
    });
    app02events.MapGet("/outcome", async (
        HttpContext context, ClaimsPrincipal principal,
        App02a3EventIntakeService intake, CancellationToken ct) =>
    {
        var producers = context.Request.Query["producerId"];
        var eventIds = context.Request.Query["eventId"];
        if (producers.Count != 1 || eventIds.Count != 1
            || producers[0] is null || eventIds[0] is null)
            return Results.BadRequest(new { error = "EVENT_IDENTITY_INVALID" });
        try
        {
            return Results.Ok(await intake.LookupAsync(
                principal, producers[0]!, eventIds[0]!, ct));
        }
        catch (App02InvalidEventException ex)
        {
            return Results.BadRequest(new { error = ex.Code });
        }
    }).RequireAuthorization("SyntheticCommandRecovery");
    app02events.MapPost("/cases/propose", async (
        App02CaseProposalInput input, ClaimsPrincipal principal,
        App02a3CaseService cases, CancellationToken ct) =>
    {
        try { return Results.Ok(await cases.ProposeAsync(principal, input, ct)); }
        catch (App02InvalidEventException ex)
        {
            return Results.Conflict(new { error = ex.Code });
        }
    });
    app02events.MapPost("/cases/approve/{proposalId:guid}", async (
        Guid proposalId, ClaimsPrincipal principal,
        App02a3CaseService cases, CancellationToken ct) =>
    {
        try { return Results.Ok(await cases.ApproveAsync(principal, proposalId, ct)); }
        catch (App02InvalidEventException ex)
        {
            return Results.Conflict(new { error = ex.Code });
        }
    });

    // APP-02A.3: correction is an immutable NONPOSTING annotation only.
    // Financial reversal is NOT authorized by this synthetic interface.
    app02events.MapPost("/corrections/propose", async (
        App02CorrectionProposalInput input, ClaimsPrincipal principal,
        App02a3CorrectionService corrections, CancellationToken ct) =>
    {
        try { return Results.Ok(await corrections.ProposeAsync(principal, input, ct)); }
        catch (App02InvalidEventException ex)
        {
            return Results.Conflict(new { error = ex.Code });
        }
    });
    app02events.MapPost("/corrections/approve/{proposalId:guid}", async (
        Guid proposalId, ClaimsPrincipal principal,
        App02a3CorrectionService corrections, CancellationToken ct) =>
    {
        try { return Results.Ok(await corrections.ApproveAsync(principal, proposalId, ct)); }
        catch (App02InvalidEventException ex)
        {
            return Results.Conflict(new { error = ex.Code });
        }
    });

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
