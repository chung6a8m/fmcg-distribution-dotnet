using System.Diagnostics;
using System.Text.Json;

namespace FmcgDms.Demo;

// Observe the existing request exactly once. No retry, deadline change, or outcome conversion.
internal static class App03SettlementHttpWitness
{
    internal const string Route = "/api/synthetic/app02c2/catalog";
    internal const string Header = "X-Fmcg-App03-Catalog-Witness";

    internal sealed record Context(string Output, string Source, string RunId);

    // No diagnostic context means no GUID, header, file, or changed request outcome.
    internal static Task<T> ObserveIfEnabled<T>(Context? witness, string checkpoint,
        int apiPid, int vitePid, TimeSpan timeout, CancellationToken caller, Func<string?, Task<T>> send) =>
        witness is null ? send(null) : Observe(witness.Output, witness.Source, witness.RunId,
            checkpoint, apiPid, vitePid, timeout, caller, id => send(id));

    internal static async Task<T> Observe<T>(string output, string source, string runId, string checkpoint,
        int apiPid, int vitePid, TimeSpan timeout, CancellationToken caller, Func<string, Task<T>> send)
    {
        string id = Guid.NewGuid().ToString("N");
        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        Write("start", new { schema = "APP03_SETTLEMENT_HTTP_V1", source, runId, checkpoint, id,
            route = Route, method = "GET", apiPid, vitePid, startedUtc, timeoutMs = timeout.TotalMilliseconds });
        long started = Stopwatch.GetTimestamp();
        string outcome = "UNEXPECTED_FAILURE";
        int? status = null;
        try
        {
            T result = await send(id);
            outcome = "SUCCESS";
            return result;
        }
        catch (OperationCanceledException) when (caller.IsCancellationRequested)
        {
            outcome = "CALLER_CANCELLED";
            throw;
        }
        catch (OperationCanceledException ex)
        {
            outcome = ex.InnerException is TimeoutException ? "HTTP_TIMEOUT" : "CANCELLED_UNKNOWN";
            throw;
        }
        catch (HttpRequestException ex)
        {
            outcome = ex.StatusCode.HasValue ? "HTTP_STATUS_FAILURE" : "HTTP_TRANSPORT_FAILURE";
            status = (int?)ex.StatusCode;
            throw;
        }
        finally
        {
            Write("end", new { schema = "APP03_SETTLEMENT_HTTP_V1", source, runId, checkpoint, id,
                route = Route, method = "GET", apiPid, vitePid, startedUtc, endedUtc = DateTimeOffset.UtcNow,
                elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds, timeoutMs = timeout.TotalMilliseconds,
                outcome, status, callerCancelled = caller.IsCancellationRequested });
        }

        void Write(string phase, object record)
        {
            try
            {
                using var file = File.Open(Path.Combine(output, $"catalog-http-{id}-{phase}.json"), FileMode.CreateNew);
                JsonSerializer.Serialize(file, record);
            }
            catch
            {
                // Diagnostics must not replace the actual request outcome, even on a full disk.
                try { Console.Error.WriteLine("APP-03 catalog HTTP witness unavailable."); } catch { }
            }
        }
    }
}
