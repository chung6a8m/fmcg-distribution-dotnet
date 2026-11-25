using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using FmcgDms.Api.Authentication;
using FmcgDms.Api.Tenancy;
using FmcgDms.Demo;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The demo requires Windows LocalDB.");
if (args is ["App03Session", var sessionAction, var sessionRoot, var sessionSource, var sessionId])
{
    await App03DevSession.Run(sessionAction, sessionRoot, sessionSource, sessionId == "-" ? null : sessionId, settlement: true, finance: true);
    return;
}
if (args is ["App03Session", var closeAction, var closeRoot, var closeSource, var closeId, "CloseReadiness"]
    && closeAction is "Start" or "Resume")
{
    await App03DevSession.Run(closeAction, closeRoot, closeSource, closeId == "-" ? null : closeId,
        closeReadiness: true);
    return;
}
if (args is ["App03Session", "NextDay", var dayRoot, var daySource, var dayId, var day])
{
    var target = DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    await App03DevSession.Run("NextDay", dayRoot, daySource, dayId, businessDate: target, settlement: true, finance: true);
    return;
}
if (args is ["App03SessionProbe", var probeRoot, var probeSource, var probeId])
{
    await App03DevSession.Run("Resume", probeRoot, probeSource, probeId, host =>
    {
        Console.WriteLine($"APP-03 probe ready {host.ApiPid} {host.VitePid}");
        return Task.CompletedTask;
    });
    return;
}
if (args is [var increment, var c1Mode, var c1Root, var c1Source] && increment is "App02C1" or "App02C2" or "App02C3" or "App02C4" or "App02C5" or "App02C6" or "App02D1" or "App02D2" or "App02D3" or "App02D4" or "DevF01" or "DevF02" or "DevF03" or "DevF04" or "DevF05" or "DevF06" or "DevF07" or "DevF08" or "App03A1")
{
    await App02c1Dev.Run(c1Mode, c1Root, c1Source, increment);
    return;
}
if (args.Length != 3) throw new ArgumentException("Expected operation, RunId and source identity.");
string mode = args[0];
var run = new DemoRun(args[1]);
string home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FmcgDms", "Demo");
Directory.CreateDirectory(home);
string manifest = Path.Combine(home, run.Id + ".json");
string lockPath = Path.Combine(home, run.Id + ".lock");
await using var sessionLock = File.Open(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

if (mode == "Remove")
{
    await run.Remove();
    File.Delete(manifest);
    Console.WriteLine($"Removed {run.Id}");
    return;
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "IntegrationTest" });
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
builder.Configuration["SyntheticAuth:Enabled"] = "true";
foreach (string tenant in DemoRun.Tenants) builder.Configuration[$"TenantDatabases:{tenant}"] = run.Connection(tenant);
builder.Services.AddSingleton<TenantConnections>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "FmcgDemo." + run.Id;
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.ExpireTimeSpan = TimeSpan.FromHours(10);
    options.Events.OnRedirectToLogin = ctx =>
    {
        ctx.Response.StatusCode = 401;
        return ctx.Response.WriteAsync("Open the demo using Start-Demo.ps1.");
    };
});
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(options => options.Cookie.Name = "FmcgDemoCsrf." + run.Id);
await using var app = builder.Build();
var scenario = new DemoScenario(run, app.Services.GetRequiredService<TenantConnections>());
DemoManifest metadata;
if (mode == "Resume")
{
    metadata = JsonSerializer.Deserialize<DemoManifest>(await File.ReadAllTextAsync(manifest))
        ?? throw new InvalidOperationException("Demo manifest missing.");
    if (metadata.RunId != run.Id || metadata.SeedVersion != DemoScenario.SeedVersion ||
        metadata.OracleSha256 != DemoScenario.OracleHash)
        throw new InvalidOperationException("Demo manifest does not match this version.");
    foreach (string tenant in DemoRun.Tenants) await run.Verify(tenant);
}
else
{
    if (mode is not ("Fresh" or "AC03Start" or "AC03Complete") || File.Exists(manifest))
        throw new InvalidOperationException("Invalid mode or existing demo.");
    Console.WriteLine($"Creating RunId {run.Id}");
    foreach (string tenant in DemoRun.Tenants)
    {
        await run.Create(tenant);
        if (mode == "Fresh") continue;
        await scenario.Prepare(DemoScenario.Identity(tenant, "warehouse"), DemoInputs.Sample);
        if (mode != "AC03Complete") continue;
        for (int action = 1; action <= 7; action++)
            await scenario.Step(DemoScenario.Identity(tenant, DemoScenario.Role(action)), action);
        await using var db = await scenario.Open(DemoScenario.Identity(tenant, "auditor"));
        if ((await DemoScenario.Checkpoints(db)).Any(row => row.Cells.Any(cell => cell.Difference != 0)))
            throw new InvalidOperationException("AC-03 reconciliation mismatch.");
    }
    metadata = new(run.Id, mode, args[2], DemoScenario.SeedVersion, DemoScenario.OracleHash, DateTimeOffset.UtcNow);
    string staging = manifest + ".pending";
    await File.WriteAllTextAsync(staging, JsonSerializer.Serialize(metadata));
    File.Move(staging, manifest);
}

var writeLock = new SemaphoreSlim(1);
string openKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
bool opened = false;
app.Use(async (context, next) =>
{
    if (context.Request.Host.Host != "127.0.0.1" ||
        context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote))
    {
        context.Response.StatusCode = 400;
        return;
    }
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'self'; font-src 'self'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    await next();
});
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/open", async (HttpContext http) =>
{
    if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(http.Request.Query["key"].ToString()),
        Encoding.UTF8.GetBytes(openKey))) return Results.NotFound();
    lock (writeLock)
    {
        if (opened) return Results.NotFound();
        opened = true;
    }
    await http.SignInAsync(DemoScenario.Identity("tenant-a", "warehouse"));
    return Results.Redirect("/");
});
app.MapGet("/tokens.css", () => Results.File(Path.Combine(AppContext.BaseDirectory, "tokens.css"), "text/css"));
app.MapGet("/demo.css", () => Results.Text(DemoPage.Css, "text/css"));
app.MapGet("/font/{weight:int}", (int weight) => weight is 400 or 700
    ? Results.File(Path.Combine(AppContext.BaseDirectory, $"noto-sans-{weight}.woff2"), "font/woff2")
    : Results.NotFound());
app.MapGet("/", async (HttpContext http, IAntiforgery csrf) =>
{
    await writeLock.WaitAsync(http.RequestAborted);
    try
    {
        await using var db = await scenario.Open(http.User);
        var scene = await DemoScenario.Scene(db);
        var checkpoints = await DemoScenario.Checkpoints(db);
        string checkpoint = http.Request.Query["moc"].ToString();
        string metric = http.Request.Query["chiSo"].ToString();
        DemoBreakdown? breakdown = null;
        if (checkpoint.Length > 0 || metric.Length > 0)
        {
            if (scene is null) return Results.NotFound();
            breakdown = await DemoScenario.Breakdown(db, SyntheticAuth.GetTenantId(http.User),
                scene, checkpoints, checkpoint, metric);
            if (breakdown is null) return Results.NotFound();
        }
        var sources = scene is null ? [] : (await DemoScenario.Sources(db, scene)).ToArray();
        bool offered = scene is not null && await db.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.CashHandoverOffer WHERE TripId=@Trip", new { scene.Trip }) > 0;
        return Results.Content(DemoPage.Render(metadata, http.User, csrf.GetAndStoreTokens(http),
            scene, checkpoints, sources, offered, http.Request.Query["result"].ToString(), breakdown), "text/html; charset=utf-8");
    }
    finally { writeLock.Release(); }
}).RequireAuthorization();
app.MapGet("/report.json", async (HttpContext http) =>
{
    await using var db = await scenario.Open(http.User);
    return Results.Json(new
    {
        environment = "SYNTHETIC_NONPRODUCTION", metadata, executingSource = args[2],
        tenant = SyntheticAuth.GetTenantId(http.User),
        checkpoints = await DemoScenario.Checkpoints(db)
    });
}).RequireAuthorization();
app.MapGet("/report.xlsx", async (HttpContext http) =>
{
    await writeLock.WaitAsync(http.RequestAborted);
    try
    {
        await using var db = await scenario.Open(http.User);
        var scene = await DemoScenario.Scene(db);
        var points = await DemoScenario.Checkpoints(db);
        if (scene is null || points.Length == 0) return Results.NotFound();
        string tenant = SyntheticAuth.GetTenantId(http.User);
        byte[] workbook = await DemoExcel.Create(db, metadata, args[2], tenant, scene, points);
        return Results.File(workbook, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"AC03-{tenant}-{run.Id}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}Z.xlsx");
    }
    finally { writeLock.Release(); }
}).RequireAuthorization();
app.MapPost("/{operation}", async (string operation, HttpContext http, IAntiforgery csrf) =>
{
    if (!await csrf.IsRequestValidAsync(http)) return Results.BadRequest("Invalid form token.");
    var form = await http.Request.ReadFormAsync();
    string tenant = SyntheticAuth.GetTenantId(http.User);
    if (operation == "identity")
    {
        string persona = form["persona"].ToString();
        var parts = persona.Split(':', 2);
        if (parts.Length != 2 || !DemoRun.Tenants.Contains(parts[0]) || !DemoScenario.Roles.Contains(parts[1]))
            return Results.BadRequest("Invalid demo persona.");
        await http.SignInAsync(DemoScenario.Identity(parts[0], parts[1]));
        return Results.Redirect("/");
    }

    if (form["tenant"] != tenant) return Results.Conflict("Tenant changed. Reload the demo.");
    await writeLock.WaitAsync(http.RequestAborted);
    try
    {
        if (operation == "setup" && http.User.HasClaim("synthetic_role", "warehouse"))
        {
            var inputs = DemoPage.Inputs(form);
            await scenario.Prepare(http.User, inputs);
            return Results.Redirect("/");
        }
        if (operation == "step" && int.TryParse(form["action"], out int action) && action is >= 1 and <= 7)
        {
            var outcome = await scenario.Step(http.User, action);
            string result = outcome.OutcomeCode + (outcome.Replayed ? " · đã trả lại kết quả của lệnh gốc" : "");
            return Results.Redirect("/?result=" + Uri.EscapeDataString(result));
        }
        return Results.BadRequest("Invalid demo operation.");
    }
    catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
    catch (InvalidOperationException ex)
    {
        return Results.Redirect("/?result=" + Uri.EscapeDataString(ex.Message));
    }
    finally { writeLock.Release(); }
}).RequireAuthorization();

await app.StartAsync();
string url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
string launchUrl = $"{url}/open?key={openKey}";
Console.WriteLine($"RunId {run.Id}");
Console.WriteLine($"Demo {url}");
if (Environment.GetEnvironmentVariable("FMCG_DEMO_NO_BROWSER") == "1")
    Console.WriteLine($"Open once {launchUrl}");
else
    Process.Start(new ProcessStartInfo(launchUrl) { UseShellExecute = true });
await app.WaitForShutdownAsync();

internal sealed record DemoManifest(string RunId, string Mode, string Source, string SeedVersion,
    string OracleSha256, DateTimeOffset CreatedAt);
