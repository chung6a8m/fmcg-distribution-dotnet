using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;
using Xunit.Sdk;

namespace FmcgDms.Api.Tests;

public sealed class FailureDiagnosticsTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), $"lab-e02-{Guid.NewGuid():N}");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Trace = "1ab234567890abcd1234567890abcdef";

    [Fact]
    public async Task Failure_diagnostics_redacts_bearer_cookie_connection_secret_and_preserves_correlation()
    {
        await using var host = Launch("""
            [Console]::Out.WriteLine('Authorization: Bearer stream-bearer; Cookie: stream-cookie')
            [Console]::Error.WriteLine('Server=stream-server;User ID=stream-user;Password="stream;password"')
            [Console]::Error.WriteLine('Microsoft.Data.SqlClient.SqlException (0x80131904): stream-exception-message')
            [Console]::Error.WriteLine('Error Number:1205,State:45,Class:13')
            [Console]::Out.WriteLine('TraceId: 1ab234567890abcd1234567890abcdef')
            """);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "http://localhost/api/synthetic/app02/events/intake?key=query-secret");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer request-bearer");
        request.Headers.TryAddWithoutValidation("Cookie", "request-cookie");
        request.Headers.TryAddWithoutValidation("traceparent", $"00-{Trace}-1234567890abcdef-00");
        request.Content = new StringContent("request-body-secret");
        using var response = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent($$"""{"traceId":"{{Trace}}","password":"response-body-secret"}""",
                Encoding.UTF8, "application/problem+json")
        };
        response.Headers.TryAddWithoutValidation("Set-Cookie", "response-cookie");
        response.Headers.TryAddWithoutValidation("X-Api-Key", "response-header-secret");
        using JsonDocument artifact = await WaitForArtifact(host, "post-restart-P", "1205",
            request, response);
        JsonElement root = artifact.RootElement;
        Assert.Equal(host.Pid, root.GetProperty("process").GetProperty("pid").GetInt32());
        Assert.Equal(TimeSpan.Zero, root.GetProperty("process").GetProperty("startedUtc").GetDateTimeOffset().Offset);
        Assert.Equal(TimeSpan.Zero, root.GetProperty("capturedUtc").GetDateTimeOffset().Offset);
        Assert.Equal("post-restart-P", root.GetProperty("stage").GetString());
        Assert.Contains("Microsoft.Data.SqlClient.SqlException", root.GetRawText());
        Assert.Contains(Trace, root.GetRawText());
        Assert.Contains("/api/synthetic/app02/events/intake", root.GetRawText());
        Assert.Equal(500, root.GetProperty("http").GetProperty("status").GetInt32());
        Assert.Equal(Trace, root.GetProperty("http").GetProperty("traceId").GetString());
        Assert.Equal($"00-{Trace}-1234567890abcdef-00",
            root.GetProperty("http").GetProperty("traceparent").GetString());
        Assert.Equal("A11-09", root.GetProperty("context").GetProperty("group").GetString());
        foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            Assert.InRange(new FileInfo(file).Length, 1, FailureDiagnostics.ArtifactByteLimit);
            string text = await File.ReadAllTextAsync(file, Ct);
            foreach (string marker in new[] { "stream-bearer", "stream-cookie", "stream-server",
                "stream-user", "stream;password", "stream-exception-message", "query-secret",
                "request-bearer", "request-cookie", "request-body-secret", "response-cookie",
                "response-header-secret", "response-body-secret" })
                Assert.DoesNotContain(marker, text);
        }
    }

    [Fact]
    public async Task Live_process_drains_both_pipes_and_reports_truncation_during_shutdown()
    {
        await using var host = Launch("""
            for ($i=0; $i -lt 2048; $i++) {
                [Console]::Out.WriteLine('x' * 256)
                [Console]::Error.WriteLine('y' * 256)
            }
            [Console]::Out.WriteLine('Error Number:12345,State:1,Class:16')
            [Console]::Error.WriteLine('Error Number:23456,State:1,Class:16')
            """);
        using JsonDocument live = await WaitForArtifact(host, "live", "23456", additionalMarker: "12345");
        Assert.False(live.RootElement.GetProperty("process").GetProperty("exited").GetBoolean());
        foreach (string stream in new[] { "stdout", "stderr" })
        {
            JsonElement snapshot = live.RootElement.GetProperty(stream);
            Assert.True(snapshot.GetProperty("truncated").GetBoolean());
            Assert.True(snapshot.GetProperty("discardedChars").GetInt64() > 0);
        }
        Task<string> capture = host.CaptureFailureAsync("race", "shutdown", Ct);
        await Task.WhenAll(capture, host.DisposeAsync().AsTask());
        using JsonDocument stopped = await Read(await host.CaptureFailureAsync("after", "shutdown", Ct));
        Assert.True(stopped.RootElement.GetProperty("process").GetProperty("exited").GetBoolean());
        Assert.Equal(TimeSpan.Zero, stopped.RootElement.GetProperty("process")
            .GetProperty("stoppedUtc").GetDateTimeOffset().Offset);
    }

    [Fact]
    public async Task Capture_cancellation_and_untrusted_path_inputs_are_safe()
    {
        await using var host = Launch("");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => host.CaptureFailureAsync("cancel", "cancel", canceled.Token));
        Assert.False(Directory.Exists(folder));
        string artifact = await host.CaptureFailureAsync("../../outside", @"..\..\evil", Ct);
        Assert.Equal(Path.Combine(folder, "TestResults", "LAB-E02"), Path.GetDirectoryName(artifact));
        Assert.DoesNotContain("outside", Path.GetFileName(artifact));
        using JsonDocument result = await Read(artifact);
        Assert.DoesNotContain("evil", result.RootElement.GetProperty("stage").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Nonaccepted_intake_keeps_the_original_assertion_even_if_writer_fails(bool writerFails)
    {
        if (writerFails)
        {
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "TestResults"), "not a directory", Ct);
        }

        await using var host = Launch("");
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "http://localhost/api/synthetic/app02/events/intake");
        using var response = new HttpResponseMessage(HttpStatusCode.InternalServerError);
        string? reference = null;
        string? captureError = null;
        EqualException original = await Assert.ThrowsAsync<EqualException>(() =>
            App01e2CrossCommandRaceTests.RequireAcceptedA3Async(request, response, async (req, res) =>
            {
                reference = Path.GetFileName(await host.CaptureHttpFailureAsync(
                    "http500", "post-restart-P", req, res, Context, Ct));
            }, error => captureError = error));
        Assert.Contains("Accepted", original.Message);
        Assert.Contains("InternalServerError", original.Message);
        if (writerFails)
        {
            Assert.Null(reference);
            Assert.Equal("IOException", captureError);
        }
        else
        {
            Assert.Null(captureError);
            Assert.Single(Directory.EnumerateFiles(folder, reference!, SearchOption.AllDirectories));
        }
    }

    [Fact]
    public async Task Http_capture_times_out_when_response_content_stalls()
    {
        await using var host = Launch("");
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "http://localhost/api/synthetic/app02/events/intake");
        using var response = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StreamContent(new StalledBody())
        };
        response.Content.Headers.ContentType = new("application/problem+json");
        using var outerDeadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        outerDeadline.CancelAfter(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.CaptureHttpFailureAsync(
            "timeout", "post-restart-P", request, response, Context, outerDeadline.Token));
        Assert.False(outerDeadline.IsCancellationRequested);
        Assert.False(Directory.Exists(folder));
    }

    private sealed class StalledBody : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private static readonly FailureContext Context = new("app02a4-local-local", "LOCAL", "LOCAL", "tenant-b", "A11-09");

    [Fact]
    public async Task LocalDb_collector_reports_missing_command_timeout_and_permission_denial()
    {
        Directory.CreateDirectory(folder);
        var source = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(source.FullName, "FmcgDms.slnx")))
            source = source.Parent ?? throw new InvalidOperationException("Solution root not found.");
        string scriptPath = Path.Combine(folder, "scripts", "Collect-LocalDbDiagnostics.ps1");
        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
        File.Copy(Path.Combine(source.FullName, "scripts", "Collect-LocalDbDiagnostics.ps1"), scriptPath);
        var start = Shell($$"""
            . '{{scriptPath.Replace("'", "''")}}'
            $shell = Join-Path $PSHOME $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })
            $missing = Invoke-LocalDbProbe 'lab-e02-absent-executable.exe' @()
            $timeout = Invoke-LocalDbProbe $shell @('-NoProfile', '-Command', 'Start-Sleep 30') 300
            function Get-Command {
                param($Name, $CommandType, $ErrorAction)
                if ($Name -eq 'SqlLocalDB.exe') {
                    throw [UnauthorizedAccessException]::new('collector-private-resource')
                }
                Microsoft.PowerShell.Core\Get-Command @PSBoundParameters
            }
            $denied = Invoke-LocalDbProbe 'SqlLocalDB.exe' @('info')
            [Console]::Out.WriteLine("$($missing.status)/$($timeout.status)/$($denied.status)")
            $env:LAB_E02_BOOTSTRAP = 'start,1,AutomaticInstanceCreationFailed,0x80131500|bad-stage,1,collector-private-resource,None'
            Write-LocalDbDiagnostics
            """);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start fixture.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(deadline.Token);
        try { await process.WaitForExitAsync(deadline.Token); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        string console = await stdout + await stderr;
        Assert.Equal(0, process.ExitCode);
        Assert.Contains("COMMAND_NOT_FOUND/TIMEOUT/ACCESS_DENIED", console);
        string report = Assert.Single(Directory.EnumerateFiles(folder, "localdb-*.json", SearchOption.AllDirectories));
        using JsonDocument artifact = await Read(report);
        Assert.Equal("PARTIAL", artifact.RootElement.GetProperty("status").GetString());
        JsonElement bootstrap = Assert.Single(artifact.RootElement.GetProperty("bootstrap").EnumerateArray());
        Assert.Equal(1, bootstrap.GetProperty("exitCode").GetInt32());
        Assert.Equal("AutomaticInstanceCreationFailed", bootstrap.GetProperty("error").GetString());
        Assert.DoesNotContain("collector-private-resource", console);
        Assert.DoesNotContain("collector-private-resource", artifact.RootElement.GetRawText());
    }

    private App01e4ApiProcess Launch(string script)
        => App01e4ApiProcess.Launch(Shell(script + "\nStart-Sleep -Seconds 60"), 0, true, folder);

    private static ProcessStartInfo Shell(string script)
    {
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            start.ArgumentList.Add(arg);
        return start;
    }

    private static async Task<JsonDocument> WaitForArtifact(App01e4ApiProcess host, string stage,
        string marker, HttpRequestMessage? request = null, HttpResponseMessage? response = null,
        string? additionalMarker = null)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        wait.CancelAfter(TimeSpan.FromSeconds(12));
        while (true)
        {
            string artifact = request is null
                ? await host.CaptureFailureAsync("live", stage, wait.Token)
                : await host.CaptureHttpFailureAsync("http500", stage, request, response!, Context, wait.Token);
            JsonDocument snapshot = await Read(artifact);
            string raw = snapshot.RootElement.GetRawText();
            if (raw.Contains(marker) && (additionalMarker is null || raw.Contains(additionalMarker)))
                return snapshot;
            snapshot.Dispose();
            await Task.Delay(50, wait.Token);
        }
    }

    private static async Task<JsonDocument> Read(string path) =>
        JsonDocument.Parse(await File.ReadAllBytesAsync(path, Ct));

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }
}
