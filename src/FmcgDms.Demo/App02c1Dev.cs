using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

internal static class App02c1Dev
{
    private const string Actor = "c1-dev-sales";
    private const string BrowserTest = "web/e2e-app02c1/customers.spec.ts";
    private const int ApiPort = 5116; // Existing Vite API/health proxy target.

    internal static async Task Run(string mode, string root, string source, string increment)
    {
        if (mode is not ("Verify" or "Interactive")) throw new ArgumentException("Invalid C.1 DEV mode.");
        string web = Path.Combine(root, "web");
        string vite = Path.Combine(web, "node_modules", "vite", "bin", "vite.js");
        string playwright = Path.Combine(web, "node_modules", "@playwright", "test", "cli.js");
        if (!File.Exists(vite) || (mode == "Verify" && !File.Exists(playwright)))
            throw new InvalidOperationException("Run npm ci in web before starting C.1.");
        string apiPath = Path.Combine(root, "src", "FmcgDms.Api", "bin", "Release", "net10.0", "FmcgDms.Api.dll");
        if (!File.Exists(apiPath)) throw new FileNotFoundException("C.1 API build is missing.", apiPath);
        string apiVersion = FileVersionInfo.GetVersionInfo(apiPath).ProductVersion ?? "";
        string sourceHead = source.Split('+')[0];
        if (!apiVersion.EndsWith("+" + sourceHead, StringComparison.Ordinal))
            throw new InvalidOperationException("C.1 API assembly source identity does not match HEAD.");
        string apiSha256 = HashFile(apiPath);
        CheckApiPort();

        using var lifetime = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; lifetime.Cancel(); };
        Console.CancelKeyPress += cancel;
        var run = new DemoRun(Guid.NewGuid().ToString("N"));
        bool visit = increment == "DevF01";
        bool fefo = increment == "DevF03";
        bool pricing = increment == "DevF04";
        bool returns = increment == "DevF05";
        bool adjustments = increment == "DevF06";
        bool gifts = increment == "DevF07";
        bool refunds = increment == "DevF08";
        bool returnLane = returns || adjustments || gifts || refunds;
        bool c6 = increment == "App02C6";
        if (c6 && mode != "Verify") throw new ArgumentException("C.6 requires Verify mode.");
        bool d4 = increment == "App02D4";
        bool d3 = d4 || increment == "App02D3";
        bool d2 = increment == "App02D2";
        bool d1 = d3 || d2 || increment == "App02D1";
        if (d1 && mode != "Verify") throw new ArgumentException("D.1 requires Verify mode.");
        bool c5 = returnLane || pricing || fefo || d1 || c6 || increment == "App02C5";
        bool c4 = c5 || fefo || increment == "App02C4";
        bool c3 = c4 || increment == "App02C3";
        bool c2 = visit || c3 || increment == "App02C2";
        string feature = refunds ? "DEV-F08" : gifts ? "DEV-F07" : adjustments ? "DEV-F06" : returns ? "DEV-F05" : pricing ? "DEV-F04" : fefo ? "DEV-F03" : visit ? "DEV-F01" : d4 ? "APP-02D.4" : d3 ? "APP-02D.3" : d2 ? "APP-02D.2" : d1 ? "APP-02D.1" : c6 ? "APP-02C.6" : c5 ? "APP-02C.5" : c4 ? "APP-02C.4" : c3 ? "APP-02C.3" : c2 ? "APP-02C.2" : "APP-02C.1";
        Console.WriteLine($"{feature} DEV session {run.Id}, source {source}");
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        string[] roles = c2 ? ["sales", "warehouse"] : ["sales"];
        string Sign(string tenant, string role, string actor) => Token(key, tenant, [role], actor, uniqueSession: true);
        var d1Run = d1 ? new App02d1Dev(root, run, source, apiSha256, Sign) : null;
        d1Run?.VerifyDocuments();
        string tokenA = d1Run?.Credential("tenant-a", "sales") ?? Token(key, "tenant-a", roles);
        string tokenB = d1Run?.Credential("tenant-b", "sales") ?? Token(key, "tenant-b", roles);
        Process? api = null;
        Process? viteProcess = null;
        Process? browser = null;
        Exception? failure = null;
        string? clipboardCredential = null;
        var returnCredentials = returnLane ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["sales"] = Sign("tenant-a", "sales", Actor),
            ["receiver"] = Sign("tenant-a", "warehouse", DevF05Evidence.Receiver),
            ["finance"] = Sign("tenant-a", "finance", DevF05Evidence.Finance),
            ["qa"] = Sign("tenant-a", "case_checker", DevF05Evidence.Qa)
        } : null;
        Guid? returnQuarantineId = null;
        if (refunds && returnCredentials is not null)
        {
            returnCredentials["refund-maker"] = Sign("tenant-a", "finance", DevF08Evidence.Maker);
            returnCredentials["refund-checker"] = Sign("tenant-a", "finance", DevF08Evidence.Checker);
            returnCredentials["cashier"] = Sign("tenant-a", "cashier", DevF08Evidence.Cashier);
            returnCredentials["refund-witness"] = Sign("tenant-a", "case_checker", DevF08Evidence.Witness);
        }
        Guid? fefoQuarantineId = null;
        var d2Run = d2 ? new App02d2Dev(root, run, source, apiSha256, d1Run!) : null;
        d2Run?.VerifyDocuments();
        var d3Run = d3 && !d4 ? new App02d3Dev(root, run, source, apiSha256, d1Run!, Sign) : null;
        d3Run?.VerifyDocuments();
        var acceptance = c6 ? new App02c6Dev(root, run, source, apiSha256) : null;
        try
        {
            foreach (string tenant in DemoRun.Tenants)
            {
                lifetime.Token.ThrowIfCancellationRequested();
                await run.Create(tenant, bindBeforeFeatureMigrations: true, beforeD1: d1);
                await run.Verify(tenant);
                if (d1Run is not null)
                {
                    await d1Run.PrepareUpgrade(tenant, leaveAtD1: d2, leaveAtD2: d3 && !d4);
                    await d1Run.Prepare(tenant);
                    continue;
                }
                await using var db = new SqlConnection(run.Connection(tenant));
                await db.ExecuteAsync("""
                    INSERT dbo.App02ActorRoleGrant(TenantId,ActorId,RoleCode,IsActive,ChangedBy)
                    VALUES(@tenant,@Actor,N'sales',1,N'c1-dev-launcher')
                    """, new { tenant, Actor });
                if (await BusinessRows(db) != 0) throw new InvalidOperationException("C.1 DEV database was not empty.");
                if (c2) await App02c2DevFixture.Seed(db, tenant, Actor,
                    returnLane ? DevF05Evidence.FixtureUtc : pricing ? DevF04Evidence.FixtureUtc :
                        fefo ? DevF03Evidence.FixtureUtc : acceptance?.InitialTime.UtcDateTime);
                if (visit)
                {
                    FmcgDms.Api.Persistence.ScratchMigrator.MigrateDevF01(run.Connection(tenant), tenant, run.Id,
                        Microsoft.Extensions.Logging.LogLevel.Warning);
                    await db.ExecuteAsync("""
                        INSERT dbo.F01CustomerScope(TenantId,CustomerId,ActorId)
                        SELECT TenantId,Id,@Actor FROM dbo.Customer WHERE TenantId=@tenant AND Code=N'C2-FIXTURE'
                        """, new { tenant, Actor });
                }
                if (fefo)
                {
                    FmcgDms.Api.Persistence.ScratchMigrator.MigrateDevF03(run.Connection(tenant), tenant, run.Id,
                        Microsoft.Extensions.Logging.LogLevel.Warning);
                    Guid quarantineId = await DevF03Evidence.Seed(db, tenant, lifetime.Token);
                    if (tenant == "tenant-a") fefoQuarantineId = quarantineId;
                }
                if (pricing)
                {
                    FmcgDms.Api.Persistence.ScratchMigrator.MigrateDevF04(run.Connection(tenant), tenant, run.Id,
                        Microsoft.Extensions.Logging.LogLevel.Warning);
                    await DevF04Evidence.Seed(db, tenant, lifetime.Token);
                }
                if (returnLane)
                {
                    string connection = run.Connection(tenant);
                    FmcgDms.Api.Persistence.ScratchMigrator.MigrateDevF03(connection, tenant, run.Id,
                        Microsoft.Extensions.Logging.LogLevel.Warning);
                    FmcgDms.Api.Persistence.ScratchMigrator.MigrateDevF04(connection, tenant, run.Id,
                        Microsoft.Extensions.Logging.LogLevel.Warning);
                    FmcgDms.Api.Persistence.ScratchMigrator.MigrateDevF05(connection, tenant, run.Id,
                        Microsoft.Extensions.Logging.LogLevel.Warning);
                    Guid quarantineId = await DevF05Evidence.Seed(db, tenant, lifetime.Token);
                    if (adjustments)
                    {
                        FmcgDms.Api.Persistence.ScratchMigrator.MigrateDevF06(connection, tenant, run.Id,
                            Microsoft.Extensions.Logging.LogLevel.Warning);
                        await DevF06Evidence.Seed(connection, tenant, lifetime.Token);
                    }
                    if (gifts)
                    {
                        FmcgDms.Api.Persistence.ScratchMigrator.MigrateDevF07(connection, tenant, run.Id);
                        await DevF07Evidence.Seed(connection, tenant, Actor, lifetime.Token);
                    }
                    if (refunds)
                    {
                        FmcgDms.Api.Persistence.ScratchMigrator.MigrateDevF08(connection, tenant, run.Id);
                        await DevF08Evidence.Seed(connection, tenant, quarantineId, lifetime.Token);
                    }
                    if (tenant == "tenant-a") returnQuarantineId = quarantineId;
                }
                if (c3 && (c6 || tenant == "tenant-a"))
                    await App02c3DevFixture.Seed(db, tenant, Actor, c6 || (!c4 && mode == "Verify"));
                if (c4 && (c6 || tenant == "tenant-a"))
                    await App02c4DevFixture.Seed(db, tenant, Actor, !returnLane && !pricing && !fefo && mode == "Verify");
                if (acceptance is not null) await acceptance.Prepare(db, tenant);
            }
            string apiUrl = $"http://127.0.0.1:{ApiPort}";
            var apiStart = StartInfo("dotnet", Path.Combine(root, "src", "FmcgDms.Api"), apiPath,
                "--urls", d4 ? apiUrl : $"http://localhost:{ApiPort}");
            // Only the child API receives the signing key and SQL credentials.
            foreach (string name in apiStart.Environment.Keys.Where(n =>
                n.StartsWith("App02C6__", StringComparison.OrdinalIgnoreCase)
                || n.StartsWith("App02D1__", StringComparison.OrdinalIgnoreCase)
                || n.StartsWith("App02D2__", StringComparison.OrdinalIgnoreCase)
                || n.StartsWith("App02D3__", StringComparison.OrdinalIgnoreCase)
                || n.StartsWith("App02D4__", StringComparison.OrdinalIgnoreCase)
                || n.StartsWith("Kestrel__", StringComparison.OrdinalIgnoreCase)
                || n.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase)
                || n.StartsWith("SyntheticAuth__", StringComparison.OrdinalIgnoreCase)
                || n.StartsWith("TenantDatabases__", StringComparison.OrdinalIgnoreCase)).ToArray())
                apiStart.Environment.Remove(name);
            apiStart.Environment["DOTNET_ENVIRONMENT"] = "IntegrationTest";
            apiStart.Environment["ASPNETCORE_ENVIRONMENT"] = "IntegrationTest";
            apiStart.Environment["AllowedHosts"] = "127.0.0.1;localhost";
            apiStart.Environment["SyntheticAuth__Enabled"] = "true";
            apiStart.Environment["SyntheticAuth__SigningKey"] = key;
            apiStart.Environment["SyntheticAuth__Issuer"] = "fmcg-synthetic-app00";
            apiStart.Environment["SyntheticAuth__Audience"] = "fmcg-synthetic-app00";
            apiStart.Environment["App02Worker__Enabled"] = "false";
            apiStart.Environment["App02C1__Enabled"] = "true";
            apiStart.Environment["DevF01__Enabled"] = visit ? "true" : "false";
            apiStart.Environment["DevF03__Enabled"] = fefo || returnLane ? "true" : "false";
            apiStart.Environment["DevF04__Enabled"] = pricing || returnLane ? "true" : "false";
            apiStart.Environment["DevF05__Enabled"] = returnLane ? "true" : "false";
            apiStart.Environment["DevF06__Enabled"] = adjustments ? "true" : "false";
            apiStart.Environment["DevF07__Enabled"] = gifts ? "true" : "false";
            apiStart.Environment["DevF08__Enabled"] = refunds ? "true" : "false";
            apiStart.Environment["App02C1__Nonce"] = run.Id;
            apiStart.Environment["App02C2__Enabled"] = c2 ? "true" : "false";
            apiStart.Environment["App02C2__DedicatedRuntime"] = c2 ? "true" : "false";
            apiStart.Environment["App02C3__Enabled"] = c3 ? "true" : "false";
            apiStart.Environment["App02C4__Enabled"] = c4 ? "true" : "false";
            apiStart.Environment["App02C5__Enabled"] = c5 ? "true" : "false";
            apiStart.Environment["Logging__LogLevel__Default"] = "Warning";
            foreach (string tenant in DemoRun.Tenants)
                apiStart.Environment[$"TenantDatabases__{tenant}"] = run.Connection(tenant);
            if (fefo || pricing || returnLane)
            {
                string profile = refunds ? "dev-f08" : gifts ? "dev-f07" : adjustments ? "dev-f06" : returns ? "dev-f05" : pricing ? "dev-f04" : "dev-f03";
                DateTime clockTime = returnLane ? DevF05Evidence.FixtureUtc : pricing ? DevF04Evidence.FixtureUtc : DevF03Evidence.FixtureUtc;
                string clock = Path.Combine(root, "TestResults", profile, run.Id, "clock.utc");
                Directory.CreateDirectory(Path.GetDirectoryName(clock)!);
                await File.WriteAllTextAsync(clock, clockTime.ToString("O"), lifetime.Token);
                apiStart.Environment["App02C6__ClockFile"] = clock;
            }
            if (acceptance is not null)
            {
                await acceptance.SetClock(acceptance.CanonicalTime);
                apiStart.Environment["App02C6__ClockFile"] = acceptance.ClockPath;
            }
            if (d1Run is not null)
            {
                await d1Run.SetClock(App02d1Dev.StartTime);
                apiStart.Environment["App02D1__Enabled"] = "true";
                apiStart.Environment["App02C6__ClockFile"] = d1Run.ClockPath;
            }
            if (d3) apiStart.Environment["App02D2__Enabled"] = "true";
            if (d4)
            {
                apiStart.Environment["App02D3__Enabled"] = "true";
                apiStart.Environment["App02D4__Origin"] = apiUrl;
                apiStart.Environment["App02D4__Profile"] = "D4-v0.1";
                apiStart.Environment["App02D4__Nonce"] = run.Id;
                apiStart.Environment["App02D4__PartnerKey"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            }
            api = Start(apiStart);

            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
            await WaitFor(api, client, apiUrl + "/health/live", lifetime.Token);
            int webPort = FreePort();
            string webUrl = $"http://127.0.0.1:{webPort}";
            var viteStart = StartInfo("node", web, vite, "--host", "127.0.0.1", "--port",
                webPort.ToString(System.Globalization.CultureInfo.InvariantCulture), "--strictPort");
            if (d4) viteStart.Environment["NODE_OPTIONS"] = "--dns-result-order=ipv4first";
            viteProcess = Start(viteStart);
            await WaitFor(viteProcess, client, webUrl + "/sales/customers", lifetime.Token);

            foreach (var (tenant, token) in new[] { ("tenant-a", tokenA), ("tenant-b", tokenB) })
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, webUrl + "/api/synthetic/app02c1/session");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var response = await client.SendAsync(request, lifetime.Token);
                response.EnsureSuccessStatusCode();
                using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(lifetime.Token));
                if (payload.RootElement.GetProperty("tenantId").GetString() != tenant)
                    throw new InvalidOperationException("C.1 proxy did not reach the expected tenant runtime.");
            }

            if (mode == "Interactive")
            {
                await CopyCredential(tokenA);
                clipboardCredential = tokenA;
                Console.WriteLine($"Open {webUrl}/sales{(fefo || visit || c5 ? "" : c4 ? "/delivery" : c3 ? "/load" : c2 ? "/orders" : "/customers")}. Paste the short-lived tenant-a credential from the clipboard.");
                Console.WriteLine("Press Ctrl+C to stop the session and remove its owned databases.");
                if (returnCredentials is not null)
                {
                    Console.WriteLine($"After opening a delivery in Sales A, open {webUrl}/sales/returns.");
                    if (gifts)
                        Console.WriteLine($"Use {webUrl}/sales/gifts for gift issue, return and waiver.");
                    if (refunds)
                        Console.WriteLine($"Use {webUrl}/sales/refunds with refund-maker, refund-checker, refund-witness or cashier credentials.");
                    if (adjustments)
                        Console.WriteLine($"After releasing one return in November, enter close, then open {webUrl}/sales/cost-adjustments.");
                    Console.WriteLine("Enter sales, receiver, finance, or qa to copy that DEV credential. Enter source to restore the Sales A credential.");
                    while (!Console.IsInputRedirected)
                    {
                        string? role = await Task.Run(Console.ReadLine).WaitAsync(lifetime.Token);
                        if (role is null) break;
                        role = role.Trim();
                        if (adjustments && role.Equals("close", StringComparison.OrdinalIgnoreCase))
                        {
                            bool closed = await DevF06Evidence.CloseInteractive(run, root, lifetime.Token);
                            Console.WriteLine(closed
                                ? "Closed November and advanced the DEV clock to open December."
                                : "Release exactly one November return before closing the fixture period.");
                            continue;
                        }
                        if (role.Equals("source", StringComparison.OrdinalIgnoreCase))
                            clipboardCredential = tokenA;
                        else if (returnCredentials.TryGetValue(role, out string? credential))
                            clipboardCredential = credential;
                        else
                        {
                            Console.WriteLine("Choose " + string.Join(", ", returnCredentials.Keys) + ", or source.");
                            continue;
                        }
                        await CopyCredential(clipboardCredential);
                        Console.WriteLine($"Copied {role} DEV credential.");
                    }
                }
                await Task.Delay(Timeout.InfiniteTimeSpan, lifetime.Token);
                return;
            }

            if (d1Run is not null)
                await d1Run.Initialize(client, webUrl, lifetime.Token);
            var browserStart = StartInfo("node", web, playwright, "test", "--config",
                refunds ? "playwright.dev-f08.config.ts" : gifts ? "playwright.dev-f07.config.ts" : adjustments ? "playwright.dev-f06.config.ts" : returns ? "playwright.dev-f05.config.ts" : pricing ? "playwright.dev-f04.config.ts" : fefo ? "playwright.dev-f03.config.ts" : visit ? "playwright.dev-f01.config.ts" : d4 ? "playwright.app02d4.config.ts" : d3 ? "playwright.app02d3.config.ts" : d2 ? "playwright.app02d2.config.ts" : d1 ? "playwright.app02d1.config.ts" : c6 ? "playwright.app02c6.config.ts" : c5 ? "playwright.app02c5.config.ts" : c4 ? "playwright.app02c4.config.ts" : c3 ? "playwright.app02c3.config.ts" :
                c2 ? "playwright.app02c2.config.ts" : "playwright.app02c1.config.ts");
            browserStart.Environment["APP02C1_WEB_URL"] = webUrl;
            browserStart.Environment["APP02C1_RUN_ID"] = run.Id;
            browserStart.Environment["APP02C1_BROWSER_TOKEN_A"] = tokenA;
            browserStart.Environment["APP02C1_BROWSER_TOKEN_B"] = tokenB;
            if (fefoQuarantineId is Guid quarantine) browserStart.Environment["DEV_F03_QA"] = quarantine.ToString("D");
            if (pricing) browserStart.Environment["DEV_F04_CHECKPOINTS"] = DevF04Evidence.CheckpointDirectory(root, run);
            if (returnCredentials is not null && returnQuarantineId is Guid returnQuarantine)
            {
                browserStart.Environment["DEV_F05_QA_LOCATION"] = returnQuarantine.ToString("D");
                browserStart.Environment["DEV_F05_CHECKPOINTS"] = DevF05Evidence.CheckpointDirectory(root, run);
                browserStart.Environment["DEV_F05_QA_UNASSIGNED"] = Sign("tenant-a", "case_checker", DevF05Evidence.UnassignedQa);
                foreach (var (role, credential) in returnCredentials)
                    browserStart.Environment["DEV_F05_" + role.ToUpperInvariant()] = credential;
            }
            if (adjustments)
            {
                browserStart.Environment["DEV_F06_CHECKPOINTS"] = DevF06Evidence.CheckpointDirectory(root, run);
                browserStart.Environment["DEV_F06_PEER_FINANCE"] = Sign("tenant-b", "finance", DevF05Evidence.Finance);
                browserStart.Environment["DEV_F06_SELF_CHECK"] =
                    Token(key, "tenant-a", ["finance", "case_checker"], DevF05Evidence.Finance, uniqueSession: true);
            }
            if (gifts)
            {
                browserStart.Environment["DEV_F07_CHECKPOINTS"] = DevF07Evidence.CheckpointDirectory(root, run);
                browserStart.Environment["DEV_F07_PEER_FINANCE"] = Sign("tenant-b", "finance", DevF05Evidence.Finance);
                browserStart.Environment["DEV_F07_SELF_CHECK"] =
                    Token(key, "tenant-a", ["sales", "finance"], Actor, uniqueSession: true);
            }
            if (refunds)
            {
                browserStart.Environment["DEV_F08_CHECKPOINTS"] = DevF08Evidence.CheckpointDirectory(root, run);
                browserStart.Environment["DEV_F08_PEER_FINANCE"] = Sign("tenant-b", "finance", DevF08Evidence.Maker);
                browserStart.Environment["DEV_F08_MAKER"] = returnCredentials!["refund-maker"];
                browserStart.Environment["DEV_F08_CHECKER"] = returnCredentials["refund-checker"];
                browserStart.Environment["DEV_F08_CASHIER"] = returnCredentials["cashier"];
                browserStart.Environment["DEV_F08_WITNESS"] = returnCredentials["refund-witness"];
            }
            if (c3)
                foreach (var (slot, tenant) in new[] { ("A", "tenant-a"), ("B", "tenant-b") })
                    browserStart.Environment["APP02C3_WAREHOUSE_TOKEN_" + slot] = Token(key, tenant, ["warehouse"]);
            if (c4)
                foreach (var (slot, tenant) in new[] { ("A", "tenant-a"), ("B", "tenant-b") })
                    browserStart.Environment["APP02C4_SALES_TOKEN_" + slot] = Token(key, tenant, ["sales"]);
            if (acceptance is not null)
            {
                browserStart.Environment["APP02C6_CHECKPOINTS"] = acceptance.CheckpointPath;
                browserStart.Environment["APP02C6_CANONICAL_UTC"] = acceptance.CanonicalTime.ToString("O");
            }
            if (d4)
            {
                var acceptanceD4 = new App02d4Dev(root, run, source, apiSha256, d1Run!, Sign);
                async Task Restart(bool enabled)
                {
                    await Stop(api);
                    apiStart.Environment["App02D4__Enabled"] = enabled ? "true" : "false";
                    api = Start(apiStart);
                    await WaitFor(api, client, apiUrl + "/health/live", lifetime.Token);
                }
                await acceptanceD4.Execute(client, apiUrl, browserStart, Start, Restart, lifetime.Token);
                return;
            }
            if (d3Run is not null)
            {
                async Task Restart(string profile)
                {
                    await Stop(api);
                    apiStart.Environment["App02D3__Enabled"] = profile == "on" ? "true" : "false";
                    apiStart.Environment["App02D2__Enabled"] = profile == "legacy" ? "false" : "true";
                    apiStart.Environment["App02D1__Enabled"] = profile == "legacy" ? "false" : "true";
                    apiStart.Environment["App02C2__DedicatedRuntime"] = profile == "legacy" ? "false" : "true";
                    apiStart.Environment["App02C3__Enabled"] = profile == "legacy" ? "false" : "true";
                    if (profile == "legacy") apiStart.Environment.Remove("App02C6__ClockFile");
                    else apiStart.Environment["App02C6__ClockFile"] = d1Run!.ClockPath;
                    api = Start(apiStart);
                    await WaitFor(api, client, apiUrl + "/health/live", lifetime.Token);
                }
                await d3Run.Initialize(client, webUrl, lifetime.Token, Restart);
                d3Run.ConfigureBrowser(browserStart);
                browser = Start(browserStart);
                await d3Run.Serve(browser);
                await d3Run.Complete(browser.ExitCode);
                return;
            }
            if (d2Run is not null)
            {
                async Task Restart(string profile)
                {
                    await Stop(api);
                    apiStart.Environment["App02D2__Enabled"] = profile == "on" ? "true" : "false";
                    apiStart.Environment["App02D1__Enabled"] = profile == "legacy" ? "false" : "true";
                    apiStart.Environment["App02C2__DedicatedRuntime"] = profile == "legacy" ? "false" : "true";
                    apiStart.Environment["App02C3__Enabled"] = profile == "legacy" ? "false" : "true";
                    if (profile == "legacy") apiStart.Environment.Remove("App02C6__ClockFile");
                    else apiStart.Environment["App02C6__ClockFile"] = d1Run!.ClockPath;
                    api = Start(apiStart);
                    await WaitFor(api, client, apiUrl + "/health/live", lifetime.Token);
                }
                await d2Run.Initialize(client, webUrl, lifetime.Token, Restart);
                d2Run.ConfigureBrowser(browserStart);
                browser = Start(browserStart);
                await d2Run.Serve(browser);
                await d2Run.Complete(browser.ExitCode);
                return;
            }
            var visitBefore = visit ? await DevF01Evidence.Capture(run) : null;
            var fefoBefore = fefo ? await DevF03Evidence.CaptureStock(run, lifetime.Token) : null;
            d1Run?.ConfigureBrowser(browserStart);
            browser = Start(browserStart);
            if (d1Run is not null)
            {
                await d1Run.Serve(browser);
                await d1Run.Complete(browser.ExitCode, async legacy =>
                {
                    await Stop(api);
                    apiStart.Environment["App02D1__Enabled"] = legacy ? "false" : "true";
                    apiStart.Environment["App02C2__DedicatedRuntime"] = legacy ? "false" : "true";
                    apiStart.Environment["App02C3__Enabled"] = legacy ? "false" : "true";
                    if (legacy) apiStart.Environment.Remove("App02C6__ClockFile");
                    else apiStart.Environment["App02C6__ClockFile"] = d1Run.ClockPath;
                    api = Start(apiStart);
                    await WaitFor(api, client, apiUrl + "/health/live", lifetime.Token);
                });
                return;
            }
            if (acceptance is not null)
            {
                await acceptance.ServeCheckpoints(browser, lifetime.Token);
                await acceptance.Complete(browser.ExitCode, client, webUrl, tokenA, tokenB, Actor,
                    Token(key, "tenant-a", ["sales", "case_checker"], App02c6Dev.Checker),
                    Token(key, "tenant-a", ["system_admin"]),
                    Token(key, "tenant-a", ["warehouse"]),
                    () => Token(key, "tenant-a", roles, uniqueSession: true),
                    async () =>
                    {
                        await Stop(api);
                        api = Start(apiStart);
                        await WaitFor(api, client, apiUrl + "/health/live", lifetime.Token);
                    }, lifetime.Token);
                return;
            }
            var masterChange = pricing
                ? await DevF04Evidence.ServeMasterChange(run, root, browser, client, webUrl, tokenA, tokenB, lifetime.Token)
                : null;
            var returnSnapshots = returns
                ? await DevF05Evidence.ServeCheckpoints(run, root, browser, lifetime.Token)
                : null;
            var adjustmentSnapshots = adjustments
                ? await DevF06Evidence.ServeCheckpoints(run, root, browser, lifetime.Token)
                : null;
            var giftSnapshots = gifts
                ? await DevF07Evidence.ServeCheckpoints(run, root, browser, lifetime.Token)
                : null;
            var refundSnapshots = refunds
                ? await DevF08Evidence.ServeCheckpoints(run, root, browser, lifetime.Token)
                : null;
            await browser.WaitForExitAsync(lifetime.Token);
            if (browser.ExitCode != 0) throw new InvalidOperationException($"{feature} browser verification exited {browser.ExitCode}.");
            if (gifts)
            {
                await DevF07Evidence.Verify(run, root, source, apiSha256, giftSnapshots, lifetime.Token);
                return;
            }
            if (refunds)
            {
                await DevF08Evidence.Verify(run, root, source, apiSha256, refundSnapshots, lifetime.Token);
                return;
            }
            if (adjustments)
            {
                await DevF06Evidence.Verify(run, root, source, apiSha256, adjustmentSnapshots, lifetime.Token);
                return;
            }
            if (returns)
            {
                await DevF05Evidence.Verify(run, root, source, apiSha256, returnSnapshots, lifetime.Token);
                return;
            }
            if (pricing)
            {
                await DevF04Evidence.Verify(run, root, source, apiSha256, masterChange, lifetime.Token);
                return;
            }
            if (fefoBefore is not null)
            {
                await DevF03Evidence.Verify(run, root, source, apiSha256, fefoBefore, lifetime.Token);
                return;
            }
            if (visitBefore is not null)
            {
                await DevF01Evidence.Verify(run, root, source, apiSha256, visitBefore);
                return;
            }
            if (c5)
                await App02c5DevFixture.VerifyCurrentAuthority(run, client, webUrl, tokenA, Actor, lifetime.Token);
            if (c4) await App02c4DevFixture.Verify(run, root, source, apiVersion, apiSha256, c5);
            else if (c3) await App02c3DevFixture.Verify(run, root, source, apiVersion, apiSha256);
            else if (c2) await App02c2DevFixture.Verify(run, root, source, apiVersion, apiSha256);
            else await VerifySql(run, root, source, apiVersion, apiSha256);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested && mode == "Interactive") { }
        catch (Exception ex) { failure = ex; throw; }
        finally
        {
            Console.CancelKeyPress -= cancel;
            var cleanupErrors = new List<Exception>();
            await Cleanup(() => Stop(browser));
            await Cleanup(() => Stop(viteProcess));
            await Cleanup(() => Stop(api));
            if (clipboardCredential is not null) await Cleanup(() => CopyCredential(clipboardCredential, clear: true));
            foreach (string tenant in DemoRun.Tenants)
                await Cleanup(() => run.RemoveDevSlot(tenant));
            if (cleanupErrors.Count > 0 && failure is null)
                throw new AggregateException($"C.1 cleanup failed for session {run.Id}.", cleanupErrors);

            async Task Cleanup(Func<Task> action)
            {
                try { await action(); }
                catch (Exception ex)
                {
                    cleanupErrors.Add(ex);
                    Console.Error.WriteLine($"C.1 cleanup failed for {run.Id}: {ex.Message}");
                }
            }
        }
    }

    private static async Task VerifySql(DemoRun run, string root, string source, string apiVersion, string apiSha256)
    {
        await using var a = new SqlConnection(run.Connection("tenant-a"));
        await using var b = new SqlConnection(run.Connection("tenant-b"));
        var actual = (await a.QueryAsync<string>("""
            SELECT CONCAT('CUSTOMER|',Code,'|',Name) FROM dbo.Customer
            UNION ALL
            SELECT CONCAT('OUTLET|',c.Code,'|',o.Name) FROM dbo.C1Outlet o
                JOIN dbo.Customer c ON c.TenantId=o.TenantId AND c.Id=o.CustomerId
            UNION ALL
            SELECT CONCAT('REGISTERED|',COUNT(*)) FROM dbo.C1RegisteredShipTo
            UNION ALL
            SELECT CONCAT('REVISION|',r.Revision,'|',r.Payload,'|',o.Name,'|',k.Kind,'|',k.Code)
                FROM dbo.C1RegisteredShipToRevision r JOIN dbo.C1Outlet o
                ON o.TenantId=r.TenantId AND o.CustomerId=r.CustomerId AND o.Id=r.OutletId
                JOIN dbo.C1Command k ON k.TenantId=r.TenantId AND k.ActorId=r.ActorId AND k.CommandId=r.CommandId
                    AND k.ResultId=r.RegisteredId AND k.Revision=r.Revision
            UNION ALL
            SELECT CONCAT('SELECTION|',s.Origin,'|',ISNULL(s.RegisteredRevision,0),'|',
                s.Payload,'|',CONVERT(varchar(64),s.PayloadHash,2),'|',s.ValidatorVersion,'|',
                ISNULL(o.Name,N'∅'),'|',s.ActorId COLLATE DATABASE_DEFAULT,'|',k.Kind,'|',k.Code)
                FROM dbo.C1ShipToSelection s LEFT JOIN dbo.C1Outlet o
                ON o.TenantId=s.TenantId AND o.CustomerId=s.CustomerId AND o.Id=s.OutletId
                JOIN dbo.C1Command k ON k.TenantId=s.TenantId AND k.ActorId=s.ActorId AND k.CommandId=s.CommandId
                    AND k.ResultId=s.Id
            UNION ALL
            SELECT CONCAT('COMMAND|',Kind,'|',Code,'|',COUNT(*)) FROM dbo.C1Command GROUP BY Kind,Code
            """)).Order(StringComparer.Ordinal).ToArray();
        const string direct = """{"country":"VN","region":"HCM","locality":"Khu DEV","line":"01 Duong DEV","recipient":"Nguoi Nhan"}""";
        const string registered = """{"country":"VN","region":"HCM","locality":"Khu DEV","line":"02 Duong DEV","recipient":"Nguoi Nhan"}""";
        const string revised = """{"country":"VN","region":"HCM","locality":"Khu DEV","line":"03 Duong DEV","recipient":"Nguoi Nhan"}""";
        static string Hash(string payload) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        string[] expected = [
            "COMMAND|CUSTOMER_SAVE|APPLIED|2",
            "COMMAND|CUSTOMER_SAVE|STALE_VERSION|1",
            "COMMAND|OUTLET_SAVE|APPLIED|2",
            "COMMAND|REGISTERED_SAVE|APPLIED|2",
            "COMMAND|SHIP_TO_SELECT|APPLIED|2",
            "CUSTOMER|C1-DEV|Khách hàng DEV đã sửa",
            "OUTLET|C1-DEV|Điểm bán DEV đã sửa",
            "REGISTERED|1",
            $"REVISION|1|{registered}|Điểm bán DEV đã sửa|REGISTERED_SAVE|APPLIED",
            $"REVISION|2|{revised}|Điểm bán DEV đã sửa|REGISTERED_SAVE|APPLIED",
            $"SELECTION|DIRECT|0|{direct}|{Hash(direct)}|1|∅|c1-dev-sales|SHIP_TO_SELECT|APPLIED",
            $"SELECTION|REGISTERED|1|{registered}|{Hash(registered)}|1|Điểm bán DEV đã sửa|c1-dev-sales|SHIP_TO_SELECT|APPLIED"
        ];
        int otherCount = await BusinessRows(b);
        bool matches = otherCount == 0 && actual.SequenceEqual(expected, StringComparer.Ordinal);
        string dir = Path.Combine(root, "TestResults", "app02c1");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, run.Id + "-sql.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new
        {
            source, apiVersion, apiSha256, runId = run.Id, environment = "synthetic DEV Windows LocalDB", instance = DemoRun.Instance,
            observedAtUtc = DateTimeOffset.UtcNow, fixtureVersion = 1,
            browserFixtureSha256 = HashFile(Path.Combine(root, BrowserTest)),
            browserReportSha256 = HashFile(Path.Combine(root, "web", "test-results-app02c1", run.Id, "junit.xml")),
            expected, actual, beforeTenantARows = 0, beforeTenantBRows = 0,
            expectedTenantBRows = 0, actualTenantBRows = otherCount, matches
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"APP-02C.1 SQL comparison: {file}");
        if (!matches) throw new InvalidOperationException("C.1 SQL rows differed from the fixture expectations.");
    }

    private static Task<int> BusinessRows(SqlConnection db) => db.ExecuteScalarAsync<int>("""
        SELECT (SELECT COUNT(*) FROM dbo.Customer)
            + (SELECT COUNT(*) FROM dbo.C1Outlet)
            + (SELECT COUNT(*) FROM dbo.C1RegisteredShipTo)
            + (SELECT COUNT(*) FROM dbo.C1RegisteredShipToRevision)
            + (SELECT COUNT(*) FROM dbo.C1ShipToSelection)
            + (SELECT COUNT(*) FROM dbo.C1Command)
        """);

    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static void CheckApiPort()
    {
        try
        {
            using var ipv4 = new TcpListener(IPAddress.Loopback, ApiPort);
            ipv4.Start();
            if (Socket.OSSupportsIPv6)
            {
                using var ipv6 = new TcpListener(IPAddress.IPv6Loopback, ApiPort);
                ipv6.Start();
            }
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException($"C.1 requires the existing Vite API proxy port {ApiPort} to be free.", ex);
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static ProcessStartInfo StartInfo(string exe, string directory, params string[] args)
    {
        var start = new ProcessStartInfo(exe)
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        return start;
    }

    private static Process Start(ProcessStartInfo start)
    {
        var process = Process.Start(start) ?? throw new InvalidOperationException("C.1 DEV process did not start.");
        process.OutputDataReceived += (_, line) => { if (line.Data is not null) Console.WriteLine(line.Data); };
        process.ErrorDataReceived += (_, line) => { if (line.Data is not null) Console.Error.WriteLine(line.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task Stop(Process? process)
    {
        using (process)
        {
            if (process is null) return;
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
            }
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static async Task WaitFor(Process process, HttpClient client, string url, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (process.HasExited) throw new InvalidOperationException($"C.1 DEV host exited {process.ExitCode}.");
            try
            {
                using var reply = await client.GetAsync(url, deadline.Token);
                if (reply.IsSuccessStatusCode && !process.HasExited) return;
            }
            catch (HttpRequestException) { }
            await Task.Delay(200, deadline.Token);
        }
    }

    private static string Token(string key, string tenant, string[] roles, string actor = Actor, bool uniqueSession = false)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = "fmcg-synthetic-app00", aud = "fmcg-synthetic-app00", sub = actor, actor_id = actor,
            tenant_id = tenant, synthetic_role = roles,
            iat = now, nbf = now - 1, exp = now + 900,
            jti = uniqueSession ? Guid.NewGuid().ToString("N") : null
        });
        static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string value = B64("""{"alg":"HS256","typ":"JWT"}"""u8.ToArray()) + "." + B64(payload);
        return value + "." + B64(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.ASCII.GetBytes(value)));
    }

    private static async Task CopyCredential(string token, bool clear = false)
    {
        string command = clear
            ? "$value = [Console]::In.ReadToEnd(); if ((Get-Clipboard -Raw) -ceq $value) { Set-Clipboard -Value '' }"
            : "Set-Clipboard -Value ([Console]::In.ReadToEnd())";
        var info = StartInfo("powershell.exe", Environment.CurrentDirectory, "-NoProfile", "-NonInteractive", "-Command", command);
        info.RedirectStandardInput = true;
        using var clipboard = Start(info);
        await clipboard.StandardInput.WriteAsync(token);
        clipboard.StandardInput.Close();
        await clipboard.WaitForExitAsync();
        if (clipboard.ExitCode != 0) throw new InvalidOperationException("Could not copy the DEV credential.");
    }
}
