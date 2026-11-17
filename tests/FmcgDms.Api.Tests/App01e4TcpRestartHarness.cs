using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace FmcgDms.Api.Tests;

/// <summary>
/// Starts a fresh *operating-system process* serving real Kestrel HTTP/TCP,
/// not a second WebApplicationFactory/TestServer instance. Only points to the
/// same fixture-owned nonce LocalDB scratch databases. No production knobs.
/// </summary>
internal sealed class App01e4ApiProcess : IAsyncDisposable
{
    private readonly Process process;
    public int Pid => process.Id;
    public int Port { get; }
    public Uri BaseAddress => new($"http://127.0.0.1:{Port}/");

    private App01e4ApiProcess(Process process, int port)
    {
        this.process = process;
        Port = port;
    }

    public static async Task<App01e4ApiProcess> StartAsync(
        SqlScratchFixture fixture, CancellationToken ct)
    {
        string? root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "FmcgDms.slnx")))
            root = Directory.GetParent(root)?.FullName;
        if (root is null) throw new InvalidOperationException("E.4 cannot locate solution root.");

        string projectDir = Path.Combine(root, "src", "FmcgDms.Api");
        string assembly = Path.Combine(projectDir, "bin", "Release", "net10.0", "FmcgDms.Api.dll");
        if (!File.Exists(assembly))
            throw new FileNotFoundException("APP-01E.4 requires the pre-built Release HTTP host.", assembly);

        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();

        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = projectDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        // Direct DLL execution avoids dotnet-run's URL/launch-profile resolution.
        // Explicit --urls is authoritative even when runner-level URL variables exist.
        start.ArgumentList.Add(assembly);
        start.ArgumentList.Add("--urls");
        start.ArgumentList.Add($"http://127.0.0.1:{port}");
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "IntegrationTest";
        start.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        // The application's default AllowedHosts permits localhost, not
        // numeric loopback. Explicitly admit only loopback for this ephemeral
        // child process; production HostFiltering configuration is unchanged.
        start.Environment["AllowedHosts"] = "127.0.0.1;localhost";
        start.Environment["SyntheticAuth__Enabled"] = "true";
        start.Environment["SyntheticAuth__SigningKey"] = SqlScratchFixture.TestKey;
        start.Environment["SyntheticAuth__Issuer"] = "fmcg-synthetic-app00";
        start.Environment["SyntheticAuth__Audience"] = "fmcg-synthetic-app00";
        start.Environment["TenantDatabases__tenant-a"] = fixture.TenantAConnectionString;
        start.Environment["TenantDatabases__tenant-b"] = fixture.TenantBConnectionString;
        start.Environment["DOTNET_NOLOGO"] = "1";

        Process process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not launch APP-01E.4 Kestrel process.");
        // Drain pipes; a blocked stdout/stderr pipe must not hang the API.
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        var host = new App01e4ApiProcess(process, port);
        try
        {
            using var probe = new HttpClient(new SocketsHttpHandler { UseProxy = false })
            {
                BaseAddress = host.BaseAddress, Timeout = TimeSpan.FromSeconds(3)
            };
            using var ready = CancellationTokenSource.CreateLinkedTokenSource(ct);
            ready.CancelAfter(TimeSpan.FromSeconds(20));
            while (!ready.Token.IsCancellationRequested)
            {
                if (process.HasExited)
                    throw new InvalidOperationException(
                        $"APP-01E.4 child exited before readiness: {process.ExitCode}");
                try
                {
                    using var response = await probe.GetAsync("health/live", ready.Token);
                    if (response.IsSuccessStatusCode) return host;
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) when (!ready.Token.IsCancellationRequested) { }
                await Task.Delay(150, ready.Token);
            }
            throw new TimeoutException("APP-01E.4 Kestrel child readiness timed out.");
        }
        catch (Exception ex)
        {
            await host.DisposeAsync();
            // Preserve bounded Kestrel startup diagnostics when readiness fails:
            // a connection refusal is not enough to diagnose the actual host bind.
            string output = stdout.IsCompletedSuccessfully ? stdout.Result : "(stdout unavailable)";
            string error = stderr.IsCompletedSuccessfully ? stderr.Result : "(stderr unavailable)";
            static string Tail(string value) => value.Length > 1200 ? value[^1200..] : value;
            throw new InvalidOperationException(
                $"APP-01E.4 Kestrel readiness failed on port {port}; " +
                $"stdout={Tail(output)}; stderr={Tail(error)}", ex);
        }
    }

    public HttpClient Client(string tenant, string actor, string role) =>
        SqlScratchFixture.WithTenant(
            new HttpClient(new SocketsHttpHandler { UseProxy = false })
            {
                BaseAddress = BaseAddress, Timeout = TimeSpan.FromSeconds(30)
            }, tenant, actor, [role]);

    public async ValueTask DisposeAsync()
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await process.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException) { throw new TimeoutException("Child did not stop."); }
        }
        process.Dispose();
    }
}

/// <summary>
/// A real loopback TCP one-shot response blackhole. It forwards the entire
/// client's HTTP request to a separate Kestrel process, observes the first
/// upstream HTTP status line (after the durable SQL command has completed),
/// then sends an RST to the caller without forwarding any response bytes.
/// Test-only: no application middleware, endpoint or database trigger.
/// </summary>
internal sealed class App01e4TcpAckDrop : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    public Uri BaseAddress { get; }

    public App01e4TcpAckDrop()
    {
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        BaseAddress = new Uri($"http://127.0.0.1:{port}/");
    }

    public async Task<string> DropOneAsync(int upstreamPort, CancellationToken ct)
    {
        using var inbound = await listener.AcceptTcpClientAsync(ct);
        using var upstream = new TcpClient();
        await upstream.ConnectAsync(IPAddress.Loopback, upstreamPort, ct);
        using var forwardCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task forward = inbound.GetStream().CopyToAsync(
            upstream.GetStream(), forwardCancellation.Token);
        string status;
        try
        {
            using var statusBytes = new MemoryStream();
            byte[] buffer = new byte[256];
            while (statusBytes.Length < 1024)
            {
                int count = await upstream.GetStream().ReadAsync(buffer, ct);
                if (count == 0) throw new IOException("Upstream closed without a response.");
                statusBytes.Write(buffer, 0, count);
                string text = Encoding.ASCII.GetString(statusBytes.ToArray());
                int end = text.IndexOf("\r\n", StringComparison.Ordinal);
                if (end >= 0)
                {
                    status = text[..end];
                    goto ReceivedStatus;
                }
            }
            throw new IOException("Upstream HTTP status line exceeded 1024 bytes.");
        ReceivedStatus: ;
            // Before any byte is written back to the client, an ACK is lost.
            inbound.Client.LingerState = new LingerOption(true, 0);
            inbound.Close();
        }
        finally
        {
            forwardCancellation.Cancel();
            try { await forward; }
            catch (Exception e) when (e is OperationCanceledException
                or IOException or ObjectDisposedException or SocketException) { }
        }
        return status;
    }

    public ValueTask DisposeAsync()
    {
        listener.Stop();
        return ValueTask.CompletedTask;
    }
}
