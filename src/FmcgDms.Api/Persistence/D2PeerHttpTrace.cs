using System.Diagnostics;
using System.Text.Json;

namespace FmcgDms.Api.Persistence;

// Only the D.2 synthetic integration-test host opts into this middleware.
// Never log the lookup's path parameters, query, authorization, body or exception text.
internal static class D2PeerHttpTrace
{
    internal const string Header = "X-Fmcg-D2-Peer-Witness";
    private const string Sales = "/api/synthetic/app02d2/sales";

    internal static string? Route(string method, string? path)
    {
        if (HttpMethods.IsPost(method) && path == Sales) return "D2_POST";
        if (!HttpMethods.IsGet(method) || path is null ||
            !path.StartsWith(Sales + "/", StringComparison.Ordinal)) return null;
        string[] parts = path[(Sales.Length + 1)..].Split('/');
        return parts.Length == 2 && parts.All(p => p.Length != 0) ? "D2_LOOKUP" : null;
    }

    internal static async Task Observe(HttpContext context, Func<Task> next, Action<string> write)
    {
        string? route = Route(context.Request.Method, context.Request.Path.Value);
        if (route is null || !context.Request.Headers.TryGetValue(Header, out var values) ||
            values.Count != 1 || !Guid.TryParseExact(values.ToString(), "N", out var parsed) ||
            parsed.ToString("N") != values.ToString())
        {
            await next();
            return;
        }

        string id = parsed.ToString("N");
        long start = Stopwatch.GetTimestamp();
        Emit(new { schema = "D2_PEER_API_V1", id, route, phase = "start",
            apiPid = Environment.ProcessId, utc = DateTimeOffset.UtcNow });
        bool faulted = true;
        try { await next(); faulted = false; }
        finally
        {
            Emit(new { schema = "D2_PEER_API_V1", id, route, phase = "end", apiPid = Environment.ProcessId,
                utc = DateTimeOffset.UtcNow, elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                status = context.Response.StatusCode, responseStarted = context.Response.HasStarted,
                requestAborted = context.RequestAborted.IsCancellationRequested, faulted });
        }

        void Emit(object row)
        {
            try { write("D2_PEER_HTTP " + JsonSerializer.Serialize(row)); }
            catch { /* Never change the business outcome. */ }
        }
    }
}
