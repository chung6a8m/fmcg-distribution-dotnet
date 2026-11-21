using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FmcgDms.Api.Tests;

internal sealed record FailureContext(string TestRunId, string SourceHeadSha, string WorkflowSha,
    string TenantId, string Group, int? PreviousPid = null);

// Keep only known diagnostic fields. Arbitrary messages, arguments, header values
// and payloads can contain credentials even when no secret name is present.
internal static partial class FailureDiagnostics
{
    private const int HttpBodyLimit = 4096;
    private const int ObservationLimit = 128;
    internal const int ArtifactByteLimit = 96 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [GeneratedRegex(@"\A[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}\z", RegexOptions.NonBacktracking)]
    private static partial Regex LabelPattern();

    [GeneratedRegex(@"\A(?:[a-f0-9]{32}|[a-f0-9]{16}|[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}|[A-Z0-9]{13}:[A-F0-9]{8}|00-[a-f0-9]{32}-[a-f0-9]{16}-[a-f0-9]{2})\z", RegexOptions.NonBacktracking)]
    private static partial Regex CorrelationPattern();

    [GeneratedRegex(@"\A(?:(?:System|Microsoft|FmcgDms)\.[A-Za-z0-9.]{0,110}Exception|[A-Za-z]{0,48}Exception)(?=[: (\s]|$)")]
    private static partial Regex ExceptionPattern();

    [GeneratedRegex(@"\AError Number:\s*(-?\d{1,10})(?:,State:\s*(\d{1,3}),Class:\s*(\d{1,3}))?\s*\z", RegexOptions.NonBacktracking)]
    private static partial Regex SqlPattern();

    [GeneratedRegex(@"\Aat ((?:FmcgDms|Microsoft\.Data\.SqlClient)\.[A-Za-z0-9_.$<>+]{1,180})\(", RegexOptions.NonBacktracking)]
    private static partial Regex StackPattern();

    [GeneratedRegex(@"\AConnection id ""([A-Z0-9]{13})"", Request id ""([A-Z0-9]{13}:[A-F0-9]{8})"":", RegexOptions.NonBacktracking)]
    private static partial Regex KestrelPattern();

    [GeneratedRegex(@"\A(?:=>\s*)?(?:TraceId|CorrelationId|ClientConnectionId|RequestId):\s*([a-fA-F0-9:-]{16,55})(?:\s|\z)", RegexOptions.NonBacktracking)]
    private static partial Regex LogCorrelationPattern();

    [GeneratedRegex(@"\A=> SpanId:([a-f0-9]{16}), TraceId:([a-f0-9]{32}), ParentId:([a-f0-9]{16})(?:\s|\z)", RegexOptions.NonBacktracking)]
    private static partial Regex LogScopePattern();

    public static string Label(string value) => LabelPattern().IsMatch(value) ? value : "invalid-label";

    public static string? Sha(string? value) => value == "LOCAL" ||
        (value is { Length: 40 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
        ? value : null;

    private static string? Correlation(string? value) =>
        value is { Length: <= 55 } && CorrelationPattern().IsMatch(value) ? value : null;

    public static object RedactedStream((string Tail, long Discarded, bool Complete, string? Error) snapshot)
    {
        var observations = new List<object>();
        string[] lines = snapshot.Tail.Split('\n');
        // A ring can start in the middle of a sensitive line. Never interpret
        // that fragment as a field. A live snapshot can also end mid-line.
        for (int i = snapshot.Discarded == 0 ? 0 : 1;
            i < lines.Length - (snapshot.Complete ? 0 : 1); i++)
        {
            string line = lines[i].Trim();
            Match match = ExceptionPattern().Match(line.StartsWith("---> ", StringComparison.Ordinal)
                ? line[5..] : line);
            if (match.Success)
                observations.Add(new { exceptionType = match.Value });
            else if ((match = SqlPattern().Match(line)).Success &&
                int.TryParse(match.Groups[1].Value, out int number))
                observations.Add(new { sqlErrorNumber = number,
                    state = ParseNumber(match.Groups[2].Value), severity = ParseNumber(match.Groups[3].Value) });
            else if ((match = KestrelPattern().Match(line)).Success)
                observations.Add(new { connectionId = match.Groups[1].Value, requestId = match.Groups[2].Value });
            else if ((match = LogCorrelationPattern().Match(line)).Success &&
                Correlation(match.Groups[1].Value) is { } correlation)
                observations.Add(new { correlation });
            else if ((match = LogScopePattern().Match(line)).Success)
                observations.Add(new { spanId = match.Groups[1].Value,
                    traceId = match.Groups[2].Value, parentId = match.Groups[3].Value });
            else if ((match = StackPattern().Match(line)).Success)
                observations.Add(new { frame = match.Groups[1].Value });
            else if (line.Length >= 20 && DateTimeOffset.TryParseExact(
                line.Split(' ', 2)[0], "O", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTimeOffset timestamp))
                observations.Add(new { timestampUtc = timestamp.ToUniversalTime() });
        }

        return new
        {
            retainedChars = snapshot.Tail.Length,
            discardedChars = snapshot.Discarded,
            truncated = snapshot.Discarded != 0 || observations.Count > ObservationLimit,
            snapshot.Complete,
            captureError = snapshot.Error,
            observations = observations.TakeLast(ObservationLimit).ToArray()
        };
    }

    private static int? ParseNumber(string value) => int.TryParse(value, out int number) ? number : null;

    private static string? Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? Correlation(values.FirstOrDefault()) : null;

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? Correlation(values.FirstOrDefault()) : null;

    public static async Task<object> RedactedHttpAsync(HttpRequestMessage request,
        HttpResponseMessage response, CancellationToken ct)
    {
        string? traceId = null;
        bool bodyTruncated = response.Content.Headers.ContentLength > HttpBodyLimit;
        if (!bodyTruncated && response.Content.Headers.ContentType?.MediaType is
            "application/problem+json" or "application/json")
        {
            // The intake call buffers the response before invoking diagnostics.
            Stream body = await response.Content.ReadAsStreamAsync(ct);
            var bytes = new byte[HttpBodyLimit + 1];
            long? position = body.CanSeek ? body.Position : null;
            int read;
            try { read = await body.ReadAtLeastAsync(bytes, bytes.Length, false, ct); }
            finally { if (position.HasValue) body.Position = position.Value; }
            bodyTruncated = read > HttpBodyLimit;
            if (!bodyTruncated)
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(bytes.AsMemory(0, read));
                    if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                        doc.RootElement.TryGetProperty("traceId", out JsonElement trace) &&
                        trace.ValueKind == JsonValueKind.String)
                        traceId = Correlation(trace.GetString());
                }
                catch (JsonException) { }
            }
        }

        const string intakePath = "/api/synthetic/app02/events/intake";
        string path = request.RequestUri is { } uri
            ? (uri.IsAbsoluteUri ? uri.AbsolutePath : uri.OriginalString.Split('?', '#')[0])
            : "";
        return new
        {
            method = request.Method == HttpMethod.Post ? "POST" : "other",
            path = path == intakePath ? intakePath : "[redacted]",
            status = (int)response.StatusCode,
            traceparent = Header(request, "traceparent"),
            responseTraceparent = Header(response, "traceparent"),
            correlationId = Header(response, "X-Correlation-ID"),
            requestId = Header(response, "X-Request-ID"),
            traceId,
            bodyTruncated
        };
    }

    public static async Task<string> WriteAsync(string baseDirectory, string failureId, object artifact,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(artifact, JsonOptions);
        if (bytes.Length > ArtifactByteLimit) throw new IOException("Failure diagnostic byte limit exceeded.");
        string folder = Path.Combine(baseDirectory, "TestResults", "LAB-E02");
        // The base is the test binary directory (or a test-owned temporary folder).
        // Reject existing junctions/symlinks before creating artifact directories.
        for (DirectoryInfo? directory = new(folder); directory is not null; directory = directory.Parent)
        {
            if (Path.Exists(directory.FullName) &&
                (File.GetAttributes(directory.FullName) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Failure diagnostics path contains a reparse point.");
        }
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, $"{Label(failureId)}-{Guid.NewGuid():N}.json");
        string pending = path + ".tmp";
        bool created = false;
        try
        {
            await using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous))
            {
                created = true;
                await stream.WriteAsync(bytes, ct);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(pending, path);
        }
        finally
        {
            if (created) File.Delete(pending);
        }
        return path;
    }
}

internal sealed class BoundedProcessOutput
{
    private const int Capacity = 32 * 1024;

    private readonly char[] ring = new char[Capacity];
    private readonly object gate = new();
    private long count;
    private bool complete;
    private string? error;

    public async Task DrainAsync(StreamReader reader, CancellationToken ct)
    {
        var buffer = new char[4096];
        try
        {
            while (await reader.ReadAsync(buffer.AsMemory(), ct) is int read && read != 0)
            {
                lock (gate)
                {
                    int start = (int)(count % Capacity);
                    int first = Math.Min(read, Capacity - start);
                    buffer.AsSpan(0, first).CopyTo(ring.AsSpan(start));
                    buffer.AsSpan(first, read - first).CopyTo(ring);
                    count += read;
                }
            }
            lock (gate) complete = true;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            lock (gate) error = ex.GetType().Name;
        }
    }

    public (string Tail, long Discarded, bool Complete, string? Error) Snapshot()
    {
        lock (gate)
        {
            int length = (int)Math.Min(count, Capacity);
            long discarded = count - length;
            int start = (int)(discarded % Capacity);
            int first = Math.Min(length, Capacity - start);
            string tail = new string(ring, start, first) + new string(ring, 0, length - first);
            return (tail, discarded, complete, error);
        }
    }
}
