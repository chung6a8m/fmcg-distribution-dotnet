using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;

namespace FmcgDms.Demo;

internal static class App03AcceptanceEvidence
{
    [SupportedOSPlatform("windows")]
    internal static async Task Run(string root, string source)
    {
        var run = new DemoRun(Guid.NewGuid().ToString("N"));
        string folder = Directory.CreateDirectory(Path.Combine(root, "TestResults", "app03-9", run.Id)).FullName;
        Console.WriteLine($"APP-03.9 evidence: {folder}");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var ct = deadline.Token;
        Task? session = null;
        var hosts = new List<HostReceipt>();
        var oracle = new App03AcceptanceOracle(run, folder, ct);
        bool direct = Environment.GetEnvironmentVariable("APP03_ACCEPTANCE_DIRECT") == "1";
        try
        {
            var first = await Open("Start");
            var initial = await oracle.Capture("bootstrap", "tenant-a");
            var peer = await oracle.Capture("bootstrap", "tenant-b");
            oracle.Expected(initial, "refund-sku-main", "EA", 6);
            oracle.Expected(peer, "refund-sku-main", "EA", 6);
            oracle.Expected(peer, "refund-treasury", "VND", 100000);
            await Browser(first, "day-one");
            await Stop();
            var before = await oracle.Capture("stopped", "tenant-a");
            oracle.Expected(before, "refund-sku-main", "EA", 4);
            oracle.Expected(before, "refund-sku-truck-b", "EA", 0);
            oracle.Expected(before, "refund-sku-b-reservation", "EA", 2);
            oracle.Expected(before, "orders", "rows", 2);
            oracle.Expected(before, "deliveries", "rows", 2);
            oracle.Expected(before, "visits", "rows", 1);
            oracle.Expected(before, "ar-a", "VND", 20000);
            oracle.Expected(before, "ar-b", "VND", 0);
            oracle.Expected(before, "custody", "VND", 0);
            oracle.Expected(before, "fund", "VND", 20000);
            oracle.Expected(before, "advance", "VND", 500000);
            oracle.Expected(before, "acceptances", "rows", 1);
            oracle.Expected(before, "cash-counts", "rows", 1);
            var resumed = await Open("Resume");
            oracle.SameMeasures(before, await oracle.Capture("resumed", "tenant-a"));
            await Browser(resumed, "resume", first.CashierToken);
            await Stop();

            var close = await oracle.Capture("day-one-close", "tenant-a");
            var peerClose = await oracle.Capture("day-one-close", "tenant-b");
            oracle.SameMeasures(peer, peerClose);
            oracle.SameHistory(before, close);
            oracle.Expected(close, "refund-sku-main", "EA", 3);
            oracle.Expected(close, "refund-sku-main-value", "VND", 9000);
            oracle.Expected(close, "refund-sku-truck-b", "EA", 2);
            oracle.Expected(close, "refund-sku-truck-a", "EA", 0);
            oracle.Expected(close, "refund-sku-qa", "EA", 0);
            oracle.Expected(close, "refund-sku-total", "EA", 5);
            oracle.Expected(close, "trip-qty", "EA", 2);
            oracle.Expected(close, "trip-value", "VND", 6000);
            oracle.Expected(close, "main-qty", "EA", initial.Value("main-qty", "EA") - 5);
            oracle.Expected(close, "main-value", "VND", initial.Value("main-value", "VND") - 17000);
            oracle.Expected(close, "ar-a", "VND", 0);
            oracle.Expected(close, "advance", "VND", 500000);
            oracle.Expected(close, "acceptances", "rows", 1);
            oracle.Expected(close, "cash-counts", "rows", 1);
            oracle.Expected(close, "custody", "VND", 0);
            oracle.Expected(close, "fund", "VND", 20000);
            oracle.Expected(close, "return-credit", "VND", 6000);
            oracle.Expected(close, "refund-paid", "VND", 4000);
            oracle.Expected(close, "refund-treasury", "VND", 96000);
            await oracle.LinkedSources(close);
            await oracle.Owner(close, peerClose);

            DateOnly dayTwo = App03DayBootstrap.FirstDay.AddDays(1);
            await App03DevSession.Run("NextDay", root, source, run.Id, businessDate: dayTwo, settlement: true, finance: true);
            await App03DevSession.Run("NextDay", root, source, run.Id, businessDate: dayTwo, settlement: true, finance: true);
            var next = await Open("Resume");
            var opening = await oracle.Capture("day-two-opening", "tenant-a");
            oracle.SameMeasures(close, opening, "trips");
            oracle.Expected(opening, "trips", "rows", 3);
            var peerOpening = await oracle.Capture("day-two-opening", "tenant-b");
            oracle.SameMeasures(peer, peerOpening, "trips");
            oracle.Expected(peerOpening, "trips", "rows", 3);
            await Browser(next, "day-two");
            var nextDay = await oracle.Capture("day-two-confirmed", "tenant-a");
            oracle.SameMeasures(opening, nextDay, "orders", "day-two-price");
            oracle.Expected(nextDay, "orders", "rows", 3);
            oracle.Expected(nextDay, "day-two-price", "VND", 12000);
            await oracle.DayTwoSource(nextDay);
            oracle.SameMeasures(peerOpening, await oracle.Capture("day-two-confirmed", "tenant-b"));
            if (direct)
            {
                string reason = "APP03-9 browser " + run.Id;
                await App02c1Dev.CopyCredential(next.SourceToken);
                try
                {
                    Console.WriteLine($"Open {next.WebUrl}/work/visits, paste the DEV credential, select the DEV customer and submit NO_ORDER with reason: {reason}");
                    await oracle.DirectVisit(reason, nextDay);
                }
                finally { await App02c1Dev.CopyCredential(next.SourceToken, clear: true); }
            }
            await oracle.Write(source);
            await Stop();
        }
        finally
        {
            try { await Stop(); }
            finally
            {
                if (App03DevSession.HasManifest(run.Id))
                    await App03DevSession.Run("Remove", root, source, run.Id);
            }
        }

        var artifacts = new List<Artifact>();
        foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            await using var content = File.OpenRead(path);
            artifacts.Add(new(Path.GetRelativePath(folder, path), Convert.ToHexString(await SHA256.HashDataAsync(content, ct))));
        }
        await File.WriteAllTextAsync(Path.Combine(folder, "run.json"), JsonSerializer.Serialize(new
        {
            source, tree = Environment.GetEnvironmentVariable("APP03_ACCEPTANCE_TREE"), runId = run.Id,
            command = "pwsh -NoProfile -File ./scripts/Run-App03Dev.ps1 -Action VerifyAcceptance",
            os = Environment.OSVersion.VersionString, dotnetRuntime = Environment.Version.ToString(),
            instance = DemoRun.Instance, automated = "PASS", directBrowser = direct ? "PASS" : "NOT_RUN", hosts, artifacts
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), ct);
        Console.WriteLine($"APP-03.9 synthetic DEV journey verified: {folder}");

        async Task<App03DevHost> Open(string action)
        {
            var ready = new TaskCompletionSource<App03DevHost>(TaskCreationOptions.RunContinuationsAsynchronously);
            session = App03DevSession.Run(action, root, source, run.Id, host =>
            {
                ready.SetResult(host);
                return Task.CompletedTask;
            }, settlement: true, finance: true);
            if (await Task.WhenAny(session, ready.Task).WaitAsync(ct) == session) await session;
            var active = await ready.Task.WaitAsync(ct);
            if (hosts.Count > 0 && hosts[0].WebUrl != active.WebUrl)
                throw new InvalidOperationException("Resume changed the browser origin.");
            hosts.Add(new(active.WebUrl, active.ApiPid, active.VitePid, action));
            return active;
        }

        async Task Stop()
        {
            if (session is null) return;
            Task ending = session;
            if (!ending.IsCompleted)
            {
                using var signal = EventWaitHandle.OpenExisting($@"Local\FmcgDms.APP03.{run.Id}");
                signal.Set();
            }
            session = null;
            await ending;
            if (hosts.Count == 0) return;
            using var probe = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
            try { using var response = await probe.GetAsync(hosts[^1].WebUrl, ct); }
            catch (HttpRequestException) { return; }
            throw new InvalidOperationException("The DEV browser origin still responds after Stop.");
        }

        async Task CheckAdvance(Task exited)
        {
            var unpaid = await Checkpoint("before-advance");
            if (unpaid is null) return;
            var issued = await Checkpoint("after-advance");
            if (issued is null) return;
            oracle.SameMeasures(unpaid, issued, "advance");
            oracle.Expected(unpaid, "ar-a", "VND", 20000);
            oracle.Expected(unpaid, "custody", "VND", 20000);
            oracle.Expected(unpaid, "fund", "VND", 0);
            oracle.Expected(unpaid, "advance", "VND", 0);
            oracle.Expected(issued, "advance", "VND", 500000);

            async Task<App03AcceptanceOracle.SqlSnapshot?> Checkpoint(string name)
            {
                while (!File.Exists(Path.Combine(folder, name + ".ready")))
                {
                    if (exited.IsCompleted) return null;
                    await Task.Delay(100, ct);
                }
                var snapshot = await oracle.Capture(name, "tenant-a");
                await File.WriteAllTextAsync(Path.Combine(folder, name + ".checked"), "", ct);
                return snapshot;
            }
        }

        async Task Browser(App03DevHost active, string stage, string? oldToken = null)
        {
            var start = new ProcessStartInfo("node")
            {
                WorkingDirectory = Path.Combine(root, "web"), UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var arg in new[] { "node_modules/@playwright/test/cli.js", "test", "--config",
                "playwright.app03-9.config.ts", "--grep", stage + " " })
                start.ArgumentList.Add(arg);
            start.Environment["APP02C1_WEB_URL"] = active.WebUrl;
            start.Environment["APP02C1_RUN_ID"] = run.Id;
            start.Environment["APP03_ACCEPTANCE_STAGE"] = stage;
            start.Environment["APP03_EVIDENCE_DIR"] = folder;
            start.Environment["APP03_SOURCE_TOKEN"] = active.SourceToken;
            start.Environment["APP03_RECEIVER_TOKEN"] = active.ReceiverToken;
            start.Environment["APP03_FINANCE_TOKEN"] = active.FinanceToken;
            start.Environment["APP03_QA_TOKEN"] = active.QaToken;
            start.Environment["APP03_CASHIER_TOKEN"] = active.CashierToken;
            if (oldToken is not null) start.Environment["APP03_OLD_CASHIER_TOKEN"] = oldToken;
            foreach (var (role, token) in active.FinanceCredentials!)
                start.Environment["APP03_" + role + "_TOKEN"] = token;
            using var browser = Process.Start(start) ?? throw new InvalidOperationException("Cannot start APP-03.9 Chromium.");
            try
            {
                var stdout = browser.StandardOutput.ReadToEndAsync(ct);
                var stderr = browser.StandardError.ReadToEndAsync(ct);
                var exited = browser.WaitForExitAsync(ct);
                if (stage == "day-one") await CheckAdvance(exited);
                await exited;
                await File.WriteAllTextAsync(Path.Combine(folder, stage + ".log"),
                    await stdout + Environment.NewLine + await stderr, ct);
                if (browser.ExitCode != 0)
                    throw new InvalidOperationException($"APP-03.9 Chromium {stage} exited {browser.ExitCode}.");
            }
            finally
            {
                if (!browser.HasExited)
                {
                    browser.Kill(entireProcessTree: true);
                    await browser.WaitForExitAsync();
                }
            }
        }
    }

    private sealed record Artifact(string File, string Sha256);
    private sealed record HostReceipt(string WebUrl, int ApiPid, int VitePid, string Operation);
}
