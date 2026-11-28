using System.Diagnostics;
using System.Text.Json;

namespace FmcgDms.Api;

internal static class App03CatalogTrace
{
    private const string Route = "/api/synthetic/app02c2/catalog";
    private const string Header = "X-Fmcg-App03-Catalog-Witness";

    internal static async Task Observe(HttpContext context, Func<Task> next, Action<string> write)
    {
        // No arbitrary request fields, SQL values, tokens, URLs or exception messages are recorded.
        if (!HttpMethods.IsGet(context.Request.Method)
            || !string.Equals(context.Request.Path.Value, Route, StringComparison.Ordinal)
            || !context.Request.Headers.TryGetValue(Header, out var value) || value.Count != 1
            || !Guid.TryParseExact(value.ToString(), "N", out Guid parsed)
            || parsed.ToString("N") != value.ToString())
        {
            await next();
            return;
        }
        string id = parsed.ToString("N");
        long started = Stopwatch.GetTimestamp();
        Emit(new { schema = "APP03_SETTLEMENT_API_V1", phase = "start", id, route = Route,
            apiPid = Environment.ProcessId, utc = DateTimeOffset.UtcNow });
        bool faulted = true;
        try
        {
            await next();
            faulted = false;
        }
        finally
        {
            Emit(new { schema = "APP03_SETTLEMENT_API_V1", phase = "end", id, route = Route,
                apiPid = Environment.ProcessId, utc = DateTimeOffset.UtcNow,
                elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                status = context.Response.StatusCode, responseStarted = context.Response.HasStarted,
                requestAborted = context.RequestAborted.IsCancellationRequested, faulted });
        }

        void Emit(object row)
        {
            try { write("APP03_CATALOG_WITNESS " + JsonSerializer.Serialize(row)); }
            catch { /* The diagnostic sink must not change the HTTP outcome. */ }
        }
    }
}
