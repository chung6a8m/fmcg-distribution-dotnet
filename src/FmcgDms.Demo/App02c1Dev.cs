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

    internal static async Task Run(string mode, string root, string source)
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

        using var lifetime = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; lifetime.Cancel(); };
        Console.CancelKeyPress += cancel;
        var run = new DemoRun(Guid.NewGuid().ToString("N"));
        Console.WriteLine($"APP-02C.1 DEV session {run.Id}, source {source}");
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        string tokenA = Token(key, "tenant-a");
        string tokenB = Token(key, "tenant-b");
        Process? api = null;
        Process? viteProcess = null;
        Process? browser = null;
        Exception? failure = null;
        bool clipboardIssued = false;
        try
        {
            foreach (string tenant in DemoRun.Tenants)
            {
                lifetime.Token.ThrowIfCancellationRequested();
                await run.Create(tenant, bindBeforeFeatureMigrations: true);
                await run.Verify(tenant);
                await using var db = new SqlConnection(run.Connection(tenant));
                await db.ExecuteAsync("""
                    INSERT dbo.App02ActorRoleGrant(TenantId,ActorId,RoleCode,IsActive,ChangedBy)
                    VALUES(@tenant,@Actor,N'sales',1,N'c1-dev-launcher')
                    """, new { tenant, Actor });
                if (await BusinessRows(db) != 0) throw new InvalidOperationException("C.1 DEV database was not empty.");
            }
            int apiPort = FreePort();
            string apiUrl = $"http://127.0.0.1:{apiPort}";
            var apiStart = StartInfo("dotnet", Path.Combine(root, "src", "FmcgDms.Api"), apiPath, "--urls", apiUrl);
            // Only the child API receives the signing key and SQL credentials.
            foreach (string name in apiStart.Environment.Keys.Where(n =>
                n.StartsWith("Kestrel__", StringComparison.OrdinalIgnoreCase)
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
            apiStart.Environment["App02C1__Nonce"] = run.Id;
            apiStart.Environment["Logging__LogLevel__Default"] = "Warning";
            foreach (string tenant in DemoRun.Tenants)
                apiStart.Environment[$"TenantDatabases__{tenant}"] = run.Connection(tenant);
            api = Start(apiStart);

            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
            await WaitFor(api, client, apiUrl + "/health/live", lifetime.Token);
            int webPort = FreePort();
            string webUrl = $"http://127.0.0.1:{webPort}";
            var viteStart = StartInfo("node", web, vite, "--host", "127.0.0.1", "--port",
                webPort.ToString(System.Globalization.CultureInfo.InvariantCulture), "--strictPort");
            viteStart.Environment["APP02C1_API_PORT"] = apiPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
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
                clipboardIssued = true;
                Console.WriteLine($"Open {webUrl}/sales/customers. Paste the short-lived tenant-a credential from the clipboard.");
                Console.WriteLine("Press Ctrl+C to stop the session and remove its owned databases.");
                await Task.Delay(Timeout.InfiniteTimeSpan, lifetime.Token);
                return;
            }

            var browserStart = StartInfo("node", web, playwright, "test", "--config", "playwright.app02c1.config.ts");
            browserStart.Environment["APP02C1_WEB_URL"] = webUrl;
            browserStart.Environment["APP02C1_BROWSER_TOKEN_A"] = tokenA;
            browserStart.Environment["APP02C1_BROWSER_TOKEN_B"] = tokenB;
            browser = Start(browserStart);
            await browser.WaitForExitAsync(lifetime.Token);
            if (browser.ExitCode != 0) throw new InvalidOperationException($"C.1 browser verification exited {browser.ExitCode}.");
            await VerifySql(run, root, source, apiVersion, apiSha256);
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
            if (clipboardIssued) await Cleanup(() => CopyCredential(tokenA, clear: true));
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
            browserReportSha256 = HashFile(Path.Combine(root, "web", "test-results-app02c1", "junit.xml")),
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

    private static string Token(string key, string tenant)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = "fmcg-synthetic-app00", aud = "fmcg-synthetic-app00", sub = Actor, actor_id = Actor,
            tenant_id = tenant, synthetic_role = "sales", iat = now, nbf = now - 1, exp = now + 900
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
