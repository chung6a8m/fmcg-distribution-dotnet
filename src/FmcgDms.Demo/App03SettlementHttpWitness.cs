using System.Diagnostics;
using System.Text.Json;

namespace FmcgDms.Demo;

// Observe the existing request exactly once. No retry, deadline change, or outcome conversion.
internal static class App03SettlementHttpWitness
{
    internal const string Route = "/api/synthetic/app02c2/catalog";
    internal const string Header = "X-Fmcg-App03-Catalog-Witness";
    internal const string SessionRoute = "/api/synthetic/app02c1/session";

    internal sealed record Context(string Output, string Source, string RunId);

    // No diagnostic context means no GUID, header, file, or changed request outcome.
    internal static Task<T> ObserveIfEnabled<T>(Context? witness, string checkpoint,
        int apiPid, int vitePid, TimeSpan timeout, CancellationToken caller, Func<string?, Task<T>> send) =>
        witness is null ? send(null) : Observe(witness.Output, witness.Source, witness.RunId,
            checkpoint, apiPid, vitePid, timeout, caller, id => send(id));

    internal static Task<T> Observe<T>(string output, string source, string runId, string checkpoint,
        int apiPid, int vitePid, TimeSpan timeout, CancellationToken caller, Func<string, Task<T>> send) =>
        ObserveCore(output, source, runId, checkpoint, apiPid, vitePid, timeout, caller, send,
            Route, "tenant-a", "catalog-http", "APP03_SETTLEMENT_HTTP_V1");

    internal static async Task ObserveSession(Context? witness, string checkpoint, string tenant,
        string expectedSession, bool allowForbidden, int apiPid, int vitePid, TimeSpan timeout,
        CancellationToken caller, Func<string?, Task<HttpResponseMessage>> send)
    {
        if (witness is null)
        {
            using var response = await send(null);
            await Verify(response);
            return;
        }
        await ObserveCore(witness.Output, witness.Source, witness.RunId, checkpoint, apiPid, vitePid,
            timeout, caller, id => send(id), SessionRoute, tenant, "resume-session-http", "APP03_RESUME_HTTP_V1",
            async response =>
            {
                using (response) return await Verify(response);
            });

        async Task<string> Verify(HttpResponseMessage response)
        {
            // Preserve the original status/identity checks even with the witness disabled.
            // A revoked business role is allowed to return Forbidden on a persistent session.
            if (allowForbidden && response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                return "EXPECTED_FORBIDDEN";
            response.EnsureSuccessStatusCode();
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(caller));
            if (payload.RootElement.GetProperty("tenantId").GetString() != tenant ||
                payload.RootElement.GetProperty("sessionId").GetString() != expectedSession)
                throw new InvalidOperationException("C.1 proxy did not reach the expected tenant runtime.");
            return "PASSED";
        }
    }

    private static async Task<T> ObserveCore<T>(string output, string source, string runId, string checkpoint,
        int apiPid, int vitePid, TimeSpan timeout, CancellationToken caller, Func<string, Task<T>> send,
        string route, string tenant, string prefix, string schema, Func<T, Task<string>>? verify = null)
    {
        string id = Guid.NewGuid().ToString("N");
        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        Write("start", new { schema, source, runId, checkpoint, tenant, id,
            route, method = "GET", apiPid, vitePid, startedUtc, timeoutMs = timeout.TotalMilliseconds });
        long started = Stopwatch.GetTimestamp();
        string outcome = "UNEXPECTED_FAILURE";
        string assertion = verify is null ? "NOT_APPLICABLE" : "NOT_RUN";
        DateTimeOffset? responseReceivedUtc = null;
        double? responseElapsedMs = null;
        int? status = null;
        try
        {
            T result = await send(id);
            if (result is HttpResponseMessage response)
            {
                status = (int)response.StatusCode;
                responseReceivedUtc = DateTimeOffset.UtcNow;
                responseElapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                outcome = "RESPONSE_RECEIVED";
            }
            else outcome = "SUCCESS"; // Catalog deserialization/validation returned normally.
            if (verify is not null) assertion = await verify(result);
            return result;
        }
        catch (Exception ex) when (verify is not null && outcome == "RESPONSE_RECEIVED")
        {
            assertion = ex is HttpRequestException { StatusCode: not null } ? "HTTP_STATUS_REJECTED" : "ASSERTION_FAILED";
            throw;
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
            Write("end", new { schema, source, runId, checkpoint, tenant, id,
                route, method = "GET", apiPid, vitePid, startedUtc, endedUtc = DateTimeOffset.UtcNow,
                elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds, timeoutMs = timeout.TotalMilliseconds,
                outcome, status, assertion, responseReceivedUtc, responseElapsedMs, callerCancelled = caller.IsCancellationRequested });
        }

        void Write(string phase, object record)
        {
            try
            {
                using var file = File.Open(Path.Combine(output, $"{prefix}-{id}-{phase}.json"), FileMode.CreateNew);
                JsonSerializer.Serialize(file, record);
            }
            catch
            {
                // Diagnostics must not replace the actual request outcome, even on a full disk.
                try { Console.Error.WriteLine("APP-03 HTTP witness unavailable."); } catch { }
            }
        }
    }
}
