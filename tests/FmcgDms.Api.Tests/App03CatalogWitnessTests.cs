using System.Net;
using System.Text.Json;
using FmcgDms.Api;
using FmcgDms.Demo;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace FmcgDms.Api.Tests;

public sealed class App03CatalogWitnessTests : IDisposable
{
    private readonly string output = Directory.CreateTempSubdirectory("app03-catalog-").FullName;

    [Fact]
    public async Task Witness_preserves_success_and_correlates_both_records()
    {
        int calls = 0;
        string? observedId = null;
        int result = await Observe(CancellationToken.None, id =>
        {
            calls++;
            observedId = id;
            return Task.FromResult(42);
        });
        Assert.Equal(42, result);
        Assert.Equal(1, calls);
        Assert.Equal(observedId, Record("start").GetProperty("id").GetString());
        var end = Record("end");
        Assert.Equal(observedId, end.GetProperty("id").GetString());
        Assert.Equal("SUCCESS", end.GetProperty("outcome").GetString());
        Assert.Equal(3000, end.GetProperty("timeoutMs").GetDouble());
    }

    [Fact]
    public async Task Disabled_witness_preserves_legacy_send_without_id_or_files()
    {
        int calls = 0;
        int value = await App03SettlementHttpWitness.ObserveIfEnabled<int>(null, "day", 1, 2,
            TimeSpan.FromSeconds(3), CancellationToken.None, id =>
            {
                calls++;
                Assert.Null(id);
                return Task.FromResult(42);
            });
        Assert.Equal(42, value);
        Assert.Equal(1, calls);
        Assert.Empty(Directory.GetFiles(output));

        var original = new InvalidOperationException("original");
        var caught = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            App03SettlementHttpWitness.ObserveIfEnabled<int>(null, "day", 1, 2,
                TimeSpan.FromSeconds(3), CancellationToken.None, id =>
                {
                    calls++;
                    Assert.Null(id);
                    return Task.FromException<int>(original);
                }));
        Assert.Same(original, caught);
        Assert.Equal(2, calls);
        Assert.Empty(Directory.GetFiles(output));
    }

    [Fact]
    public async Task Real_http_client_timeout_is_recorded_and_not_retried()
    {
        using var handler = new NeverRespond();
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(50) };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            App03SettlementHttpWitness.Observe(output, "source", "run", "day", 1, 2, client.Timeout,
                CancellationToken.None, id => client.GetAsync("http://localhost" + App03SettlementHttpWitness.Route)));
        Assert.Equal(1, handler.Calls);
        Assert.Equal("HTTP_TIMEOUT", Record("end").GetProperty("outcome").GetString());
        Assert.Equal(50, Record("end").GetProperty("timeoutMs").GetDouble());
    }

    [Fact]
    public async Task Caller_cancellation_and_writer_failure_do_not_replace_original_exception()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var original = new OperationCanceledException(cancelled.Token);
        var caught = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Observe(cancelled.Token, _ => Task.FromException<int>(original)));
        Assert.Same(original, caught);
        Assert.Equal("CALLER_CANCELLED", Record("end").GetProperty("outcome").GetString());

        var http = new HttpRequestException("private-message", null, HttpStatusCode.ServiceUnavailable);
        var preserved = await Assert.ThrowsAsync<HttpRequestException>(() =>
            App03SettlementHttpWitness.Observe(Path.Combine(output, "missing"), "source", "run", "day", 1, 2,
                TimeSpan.FromSeconds(3), CancellationToken.None, _ => Task.FromException<int>(http)));
        Assert.Same(http, preserved);
    }

    [Fact]
    public async Task Api_trace_is_allowlisted_and_does_not_swallow_failure()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = App03SettlementHttpWitness.Route;
        context.Request.QueryString = new QueryString("?key=private-query");
        context.Request.Headers["Authorization"] = "Bearer private-token";
        string id = Guid.NewGuid().ToString("N");
        context.Request.Headers[App03SettlementHttpWitness.Header] = id;
        var lines = new List<string>();
        var original = new InvalidOperationException("private-error");
        var caught = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            App03CatalogTrace.Observe(context, () => Task.FromException(original), lines.Add));
        Assert.Same(original, caught);
        Assert.Equal(2, lines.Count);
        Assert.All(lines, line =>
        {
            Assert.Contains(id, line);
            Assert.DoesNotContain("private", line);
        });
        Assert.Contains("\"faulted\":true", lines[1]);

        lines.Clear();
        context.Request.Method = HttpMethods.Post;
        await App03CatalogTrace.Observe(context, () => Task.CompletedTask, lines.Add);
        Assert.Empty(lines);
        context.Request.Method = HttpMethods.Get;
        context.Request.Headers[App03SettlementHttpWitness.Header] = "private-invalid";
        await App03CatalogTrace.Observe(context, () => Task.CompletedTask, lines.Add);
        Assert.Empty(lines);
    }

    [Fact]
    public async Task Status_failure_and_api_sink_failure_preserve_original_behavior()
    {
        var original = new HttpRequestException("private-http-message", null, HttpStatusCode.ServiceUnavailable);
        var caught = await Assert.ThrowsAsync<HttpRequestException>(() =>
            Observe(CancellationToken.None, _ => Task.FromException<int>(original)));
        Assert.Same(original, caught);
        var end = Record("end");
        Assert.Equal("HTTP_STATUS_FAILURE", end.GetProperty("outcome").GetString());
        Assert.Equal(503, end.GetProperty("status").GetInt32());
        Assert.DoesNotContain("private", end.GetRawText());

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = App03SettlementHttpWitness.Route;
        context.Request.Headers[App03SettlementHttpWitness.Header] = Guid.NewGuid().ToString("N");
        int calls = 0;
        await App03CatalogTrace.Observe(context, () =>
        {
            calls++;
            context.Response.StatusCode = 204;
            return Task.CompletedTask;
        }, _ => throw new IOException("private-sink-message"));
        Assert.Equal(1, calls);
        Assert.Equal(204, context.Response.StatusCode);
    }

    [Fact]
    public async Task Resume_session_witness_distinguishes_tenants_and_preserves_status()
    {
        var witness = new App03SettlementHttpWitness.Context(output, "source", "run");
        int calls = 0;
        foreach (string tenant in new[] { "tenant-a", "tenant-b" })
        {
            using var response = await App03SettlementHttpWitness.ObserveSession(witness, "nextday-1", tenant,
                1, 2, TimeSpan.FromSeconds(3), CancellationToken.None, id =>
                {
                    calls++;
                    Assert.NotNull(id);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
                });
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        Assert.Equal(2, calls);
        var rows = Directory.GetFiles(output, "resume-session-http-*-end.json").Select(file =>
        {
            using var json = JsonDocument.Parse(File.ReadAllText(file));
            return json.RootElement.Clone();
        }).ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal(new[] { "tenant-a", "tenant-b" }, rows.Select(r => r.GetProperty("tenant").GetString()).Order().ToArray());
        Assert.All(rows, r =>
        {
            Assert.Equal(403, r.GetProperty("status").GetInt32());
            Assert.Equal(App03SettlementHttpWitness.SessionRoute, r.GetProperty("route").GetString());
        });
        Assert.Empty(Directory.GetFiles(output, "catalog-http-*.json"));
    }

    [Fact]
    public async Task Api_trace_accepts_session_but_not_unrelated_paths()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = App03SettlementHttpWitness.SessionRoute;
        context.Request.Headers[App03SettlementHttpWitness.Header] = Guid.NewGuid().ToString("N");
        var lines = new List<string>();
        await App03CatalogTrace.Observe(context, () => Task.CompletedTask, lines.Add);
        Assert.Equal(2, lines.Count);
        Assert.All(lines, line => Assert.Contains(App03SettlementHttpWitness.SessionRoute, line));
        lines.Clear();
        context.Request.Path = "/api/synthetic/app02c1/customers";
        await App03CatalogTrace.Observe(context, () => Task.CompletedTask, lines.Add);
        Assert.Empty(lines);
    }

    private Task<int> Observe(CancellationToken ct, Func<string, Task<int>> send) =>
        App03SettlementHttpWitness.ObserveIfEnabled(new(output, "source", "run"), "day", 1, 2,
            TimeSpan.FromSeconds(3), ct, id => send(id!));

    private JsonElement Record(string phase)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Directory.GetFiles(output, $"catalog-http-*-{phase}.json").Single()));
        return document.RootElement.Clone();
    }

    public void Dispose() => Directory.Delete(output, recursive: true);

    private sealed class NeverRespond : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unexpected completion");
        }
    }
}
