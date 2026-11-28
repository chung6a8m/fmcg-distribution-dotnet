using System.Diagnostics;
using System.Text.Json;

namespace FmcgDms.Demo;

// Reuse the existing bounded, ownership-checked Windows CI SQL observer. Its samples
// are temporal witnesses, not proof of a unique HTTP request to SQL SPID mapping.
internal sealed class App03SettlementSqlObserver : IAsyncDisposable
{
    private readonly Process child;
    private readonly App03ChildJob job;

    private App03SettlementSqlObserver(Process child, App03ChildJob job)
    {
        this.child = child;
        this.job = job;
    }

    internal static bool Enabled => OperatingSystem.IsWindows()
        && Environment.GetEnvironmentVariable("FMCG_APP03_SETTLEMENT_WITNESS") == "1"
        && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true"
        && Environment.GetEnvironmentVariable("RUNNER_OS") == "Windows";

    internal static async Task<App03SettlementSqlObserver?> Start(string root, string runId,
        string output, string source, CancellationToken ct)
    {
        if (!Enabled) return null;
        // A rejected directory must never receive even a diagnostic startup receipt.
        try
        {
            string directory = Path.GetFullPath(root);
            foreach (string part in new[] { "TestResults", "app03-6", new DemoRun(runId).Id })
            {
                directory = Path.Combine(directory, part);
                if (!Directory.Exists(directory) || File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
                    return null;
            }
            if (!string.Equals(Path.GetFullPath(output), directory, StringComparison.OrdinalIgnoreCase))
                return null;
        }
        catch { return null; }
        App03SettlementSqlObserver? observer = null;
        string status = "unavailable";
        int? pid = null;
        long started = Stopwatch.GetTimestamp();
        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        try
        {
            if (File.Exists(Path.Combine(output, "sql-reads.ndjson"))
                || File.Exists(Path.Combine(output, "sql-catalog-observer.json")))
            {
                status = "existingOutput";
                return null;
            }
            string windows = Environment.GetEnvironmentVariable("SystemRoot")
                ?? throw new InvalidOperationException();
            var info = new ProcessStartInfo(Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (string arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-File",
                Path.Combine(root, "scripts", "Watch-App03SqlReads.ps1"), "-ProfileName", "app03-6",
                "-RunId", runId, "-ReporterPid", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) })
                info.ArgumentList.Add(arg);
            info.Environment.Clear();
            foreach (string name in new[] { "SystemRoot", "LOCALAPPDATA", "USERPROFILE", "TEMP", "TMP" })
                if (Environment.GetEnvironmentVariable(name) is string value) info.Environment[name] = value;
            info.Environment["FMCG_APP03_READ_DIAGNOSTICS"] = "1";
            info.Environment["GITHUB_ACTIONS"] = "true";
            info.Environment["RUNNER_OS"] = "Windows";
            info.Environment["APP03_EVIDENCE_DIR"] = output;
            var job = new App03ChildJob();
            Process? child = null;
            try
            {
                child = Process.Start(info) ?? throw new InvalidOperationException();
                job.Attach(child);
                observer = new App03SettlementSqlObserver(child, job);
                pid = child.Id;
                // Drain child streams without publishing untrusted messages.
                child.OutputDataReceived += (_, _) => { };
                child.ErrorDataReceived += (_, _) => { };
                child.BeginOutputReadLine();
                child.BeginErrorReadLine();
            }
            catch
            {
                if (observer is null)
                {
                    try { if (child is { HasExited: false }) child.Kill(entireProcessTree: true); } catch { }
                    child?.Dispose();
                    job.Dispose();
                }
                throw;
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(8));
            string sampleFile = Path.Combine(output, "sql-reads.ndjson");
            while (!observer.child.HasExited)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (File.Exists(sampleFile))
                {
                    if (File.GetAttributes(sampleFile).HasFlag(FileAttributes.ReparsePoint)) break;
                    using var file = File.Open(sampleFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(file);
                    var text = new char[65536];
                    int length = await reader.ReadBlockAsync(text.AsMemory(), deadline.Token);
                    var lines = new string(text, 0, length).Split('\n');
                    bool identified = false;
                    bool available = false;
                    bool unavailable = false;
                    for (int i = 0; i < lines.Length - 1; i++)
                    {
                        using var row = JsonDocument.Parse(lines[i]);
                        var e = row.RootElement;
                        string? kind = e.GetProperty("event").GetString();
                        if (i == 0)
                            identified = kind == "start" && e.GetProperty("profile").GetString() == "app03-6"
                                && e.GetProperty("runId").GetString() == runId && e.GetProperty("pid").GetInt32() == pid;
                        if (!identified || kind is "unavailable" or "stop") { unavailable = true; break; }
                        if (kind == "available") available = true;
                        if (available && kind is ("sample" or "sampleUnavailable"))
                        {
                            if (observer.child.HasExited) break;
                            status = kind == "sample" ? "ready" : "readySampleUnavailable";
                            return observer;
                        }
                    }
                    if (unavailable || length == text.Length) break;
                }
                await Task.Delay(100, deadline.Token);
            }
        }
        catch (OperationCanceledException) { status = ct.IsCancellationRequested ? "callerCancelled" : "startupTimeout"; }
        catch { status = "unavailable"; }
        finally
        {
            if (status is not ("ready" or "readySampleUnavailable") && observer is not null)
                await observer.DisposeAsync();
            try
            {
                using var receipt = File.Open(Path.Combine(output, "sql-catalog-observer.json"), FileMode.CreateNew);
                JsonSerializer.Serialize(receipt, new { schema = "APP03_SETTLEMENT_SQL_OBSERVER_V1",
                    source, runId, profile = "app03-6", pid, status, startedUtc,
                    endedUtc = DateTimeOffset.UtcNow, elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
            }
            catch { Console.Error.WriteLine("APP-03 catalog SQL observer receipt unavailable."); }
        }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            child.StandardInput.Close();
            try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4)); }
            catch (TimeoutException)
            {
                job.Dispose();
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
        catch { Console.Error.WriteLine("APP-03 catalog SQL observer cleanup unavailable."); }
        finally
        {
            job.Dispose();
            child.Dispose();
        }
    }
}
