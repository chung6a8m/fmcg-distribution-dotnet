using System.Text.Json;
using FmcgDms.Api.Persistence;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace FmcgDms.Api.Tests;

public sealed class D2PeerHttpTraceTests
{
    [Theory]
    [InlineData("POST", "/api/synthetic/app02d2/sales", "D2_POST")]
    [InlineData("POST", "/api/synthetic/app02c1/ship-to-selections", "C1_SHIP_TO")]
    [InlineData("POST", "/api/synthetic/app02c2/drafts", "C2_DRAFT")]
    [InlineData("POST", "/api/synthetic/app02c2/confirm", "C2_CONFIRM")]
    [InlineData("POST", "/api/synthetic/app02c2/reserve", "C2_RESERVE")]
    [InlineData("POST", "/api/synthetic/app02d1/quota", "D1_QUOTA")]
    [InlineData("POST", "/api/synthetic/app02d1/credit", "D1_CREDIT")]
    [InlineData("POST", "/api/synthetic/app02d1/load", "D1_LOAD")]
    [InlineData("POST", "/api/synthetic/app02c3/load", "C3_LOAD")]
    [InlineData("GET", "/api/synthetic/app02d2/sales/private-producer/private-command", "D2_LOOKUP")]
    [InlineData("GET", "/api/synthetic/app02d2/sales", null)]
    [InlineData("GET", "/api/synthetic/app02d2/sales/one/two/three", null)]
    [InlineData("POST", "/api/synthetic/app02d2/sales/one/two", null)]
    [InlineData("GET", "/health/live", null)]
    public void Route_is_a_fixed_label(string method, string path, string? expected)
        => Assert.Equal(expected, D2PeerHttpTrace.Route(method, path));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Observe_retains_response_or_original_exception_and_never_logs_identifiers(bool fault)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/api/synthetic/app02d2/sales/private-producer/private-command";
        ctx.Request.QueryString = new QueryString("?tripId=private-trip");
        ctx.Request.Headers.Authorization = "Bearer private-token";
        string id = Guid.NewGuid().ToString("N");
        ctx.Request.Headers[D2PeerHttpTrace.Header] = id;
        var rows = new List<string>();
        int called = 0;
        var original = new InvalidOperationException("private-message");
        Task Run() => D2PeerHttpTrace.Observe(ctx, () =>
        {
            called++;
            if (fault) throw original;
            ctx.Response.StatusCode = 404;
            return Task.CompletedTask;
        }, rows.Add);
        if (fault) Assert.Same(original, await Assert.ThrowsAsync<InvalidOperationException>(Run));
        else await Run();
        Assert.Equal(1, called);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.DoesNotContain("private-", row);
            using var json = JsonDocument.Parse(row["D2_PEER_HTTP ".Length..]);
            Assert.Equal(id, json.RootElement.GetProperty("id").GetString());
            Assert.Equal("D2_LOOKUP", json.RootElement.GetProperty("route").GetString());
        });
        using var end = JsonDocument.Parse(rows[1]["D2_PEER_HTTP ".Length..]);
        Assert.Equal(fault, end.RootElement.GetProperty("faulted").GetBoolean());
        if (!fault) Assert.Equal(404, end.RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Invalid_id_does_not_log_and_sink_failure_cannot_change_outcome()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "POST";
        ctx.Request.Path = "/api/synthetic/app02d2/sales";
        ctx.Request.Headers[D2PeerHttpTrace.Header] = "private-id";
        int called = 0, logged = 0;
        await D2PeerHttpTrace.Observe(ctx, () => { called++; return Task.CompletedTask; }, _ => logged++);
        Assert.Equal(0, logged);
        ctx.Request.Headers[D2PeerHttpTrace.Header] = Guid.NewGuid().ToString("N");
        await D2PeerHttpTrace.Observe(ctx, () => { called++; return Task.CompletedTask; },
            _ => throw new IOException());
        Assert.Equal(2, called);
    }
}
