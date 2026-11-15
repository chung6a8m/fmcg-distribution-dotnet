using System.Security.Claims;
using Dapper;
using FmcgDms.Api.Authentication;
using FmcgDms.Api.Tenancy;

var builder = WebApplication.CreateBuilder(args);
SyntheticAuth.Configure(builder);
builder.Services.AddSingleton<TenantConnections>();

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
        await using var conn = await db.OpenAsync(principal, ct);
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
        await using var conn = await db.OpenAsync(principal, ct);
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
        await using var conn = await db.OpenAsync(principal, ct);
        if (await conn.ExecuteScalarAsync<string>("SELECT TenantId FROM dbo.AppTenantBinding WHERE Slot=1")
            != SyntheticAuth.GetTenantId(principal)) return Results.Forbid();
        var rows = await conn.QueryAsync<DiagnosticItem>(
            "SELECT Id,Description FROM dbo.DiagnosticItem ORDER BY CreatedAtUtc,Id");
        return Results.Ok(rows);
    });
}
app.Run();

public sealed record DiagnosticItemInput(string Description);
public sealed record DiagnosticItem(Guid Id, string Description);
public sealed class TenantDbInfo
{
    public string DatabaseName { get; set; } = "";
    public string BoundTenantId { get; set; } = "";
}
public partial class Program { }
