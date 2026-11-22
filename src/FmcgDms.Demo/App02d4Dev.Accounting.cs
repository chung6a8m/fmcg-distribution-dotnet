using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;

internal sealed class App02d4DevAccounting(
    HttpClient client, SqlConnection oracle, Guid[] deliveryIds, IReadOnlyDictionary<string, string> tokens,
    Func<DateTimeOffset, Task> clock, Func<Task> reset, Func<Task> restart, CancellationToken ct)
{
    internal sealed record CaseResult(string Id, string Status, IReadOnlyList<object> Checks, string? Error);

    private static readonly DateTimeOffset From = new(2026, 11, 23, 3, 10, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Cutoff = From.AddMinutes(50);
    private readonly List<object> checks = [];
    private Guid B20 => deliveryIds[2];

    internal static async Task<IReadOnlyList<CaseResult>> Run(HttpClient client, SqlConnection oracle,
        Guid[] deliveryIds, IReadOnlyDictionary<string, string> tokens, Func<DateTimeOffset, Task> clock,
        Func<Task> reset, Func<Task> restart, CancellationToken ct)
    {
        var suite = new App02d4DevAccounting(client, oracle, deliveryIds, tokens, clock, reset, restart, ct);
        return
        [
            await suite.Case("D4-11", suite.ReceiptOnly),
            await suite.Case("D4-12", suite.PostDuplicates),
            await suite.Case("D4-13", suite.CutoffAndMissing),
            await suite.Case("D4-14", suite.PostDifference),
            await suite.Case("D4-15", suite.CorrectDifference),
            await suite.Case("D4-16", suite.Recover)
        ];
    }

    private async Task<CaseResult> Case(string id, Func<Task> run)
    {
        checks.Clear();
        try
        {
            await reset();
            await run();
            await Snapshot("final");
            return new CaseResult(id, "PASS", checks.ToArray(), null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            try { await Snapshot("failure"); } catch (Exception snapshotError) when (!ct.IsCancellationRequested)
            {
                checks.Add(new { snapshotError = snapshotError.Message });
            }
            return new CaseResult(id, "FAIL", checks.ToArray(), ex.Message);
        }
    }

    private async Task ReceiptOnly()
    {
        await ReceiveAll();
        Expect(await Count("SELECT COUNT(*) FROM d4accounting.Receipt") == 3, "3 durable receipts");
        Expect(await Count("SELECT COUNT(*) FROM d4accounting.Posting") == 0, "No posting");
        Expect(await Count("SELECT COUNT(*) FROM d4app.AccountingReport") == 0, "No reconciliation");
        Expect(await Count("""
            SELECT COUNT(*) FROM d4accounting.Receipt r
            JOIN d4app.CommittedSource s ON s.SourceLineId=r.SourceLineId AND s.DeliveryId=r.DeliveryId
            WHERE LEN(r.PayloadHash)=64 AND r.Period='2026-11'
                AND r.Gross=s.Gross AND r.Ar=s.Ar AND r.Cost=s.Cost AND r.Quantity=s.Quantity
            """) == 3, "Receipt linkage and immutable amounts");
        Expect(await Count("""
            SELECT COUNT(*) FROM d4accounting.Journal j
            JOIN d4accounting.Receipt r ON r.SourceLineId=j.SourceLineId AND r.TransportId=j.TransportId
            WHERE j.Operation='RECEIVE' AND j.Outcome='COMMITTED' AND j.RecordedAt=r.ReceivedAt
            """) == 3, "Durable commit audit before acknowledged receipt");
    }

    private async Task PostDuplicates()
    {
        await clock(From.AddMinutes(40));
        await Transfer(B20, "post", "premature", status: HttpStatusCode.Conflict);
        var allReceipts = await Task.WhenAll(deliveryIds.SelectMany((delivery, ordinal) => new[]
        {
            Transfer(delivery, "receive", $"recv-{ordinal}-a"),
            Transfer(delivery, "receive", $"recv-{ordinal}-b")
        }));
        VerifyDuplicates(allReceipts, "RECEIVED");
        await Transfer(B20, "receive", "changed-receipt", hash: new string('0', 64), status: HttpStatusCode.Conflict);
        await clock(From.AddMinutes(45));
        var allPostings = await Task.WhenAll(deliveryIds.SelectMany((delivery, ordinal) => new[]
        {
            Transfer(delivery, "post", $"post-{ordinal}-a"),
            Transfer(delivery, "post", $"post-{ordinal}-b")
        }));
        VerifyDuplicates(allPostings, "POSTED");
        await Transfer(B20, "post", "changed-post-hash", hash: new string('0', 64), status: HttpStatusCode.Conflict);
        await Transfer(B20, "post", "changed-post-profile", scenario: "wrong-mapping", status: HttpStatusCode.Conflict);
        Expect(await Count("SELECT COUNT(*) FROM d4accounting.Receipt") == 3, "Exactly 3 receipts after replays");
        Expect(await Count("SELECT COUNT(*) FROM d4accounting.Posting") == 3, "Exactly 3 postings after replays");
        Expect(await Count("""
            SELECT COUNT(*) FROM d4accounting.Posting p
            JOIN d4accounting.Receipt r ON r.Id=p.ReceiptId AND r.SourceLineId=p.SourceLineId
            WHERE p.Kind='BASE' AND p.Mapping='DELIVERY' AND p.Period=r.Period
                AND p.Quantity=r.Quantity AND p.Gross=r.Gross AND p.Ar=r.Ar AND p.Cost=r.Cost
            """) == 3, "Posting is one to one with source receipt");
        Expect(await Count("SELECT COUNT(*) FROM d4app.AccountingReport") == 0, "POSTED precedes reconciliation");
    }

    private async Task CutoffAndMissing()
    {
        foreach (var scenario in new[] { "missing-receipt", "missing-post", "missing-line", "wrong-mapping" })
        {
            if (scenario != "missing-receipt") await reset();
            await clock(From.AddMinutes(40));
            foreach (Guid id in deliveryIds.Where(id => id != B20 || scenario != "missing-receipt"))
                await Transfer(id, "receive", $"cut-receive-{id}");
            await clock(From.AddMinutes(45));
            foreach (Guid id in deliveryIds.Where(id => id != B20 || scenario is "missing-line" or "wrong-mapping"))
                await Transfer(id, "post", $"cut-post-{id}", scenario: id == B20 ? scenario : null);
            await clock(Cutoff);
            var shortReport = await Reconcile(From.AddMinutes(20), Cutoff);
            ReportCounts(shortReport, "RECONCILED_OK", 2, 2, 0, 0);
            Expect(shortReport.GetProperty("lines").GetArrayLength() == 2 &&
                shortReport.GetProperty("lines")[0].GetProperty("deliveryId").GetGuid() == deliveryIds[0] &&
                !shortReport.GetProperty("lines").EnumerateArray().Any(line => line.GetProperty("deliveryId").GetGuid() == B20),
                "Lower inclusive and upper exclusive");
            var full = await Reconcile(Cutoff, Cutoff);
            bool isDiff = scenario is "missing-line" or "wrong-mapping";
            ReportCounts(full, isDiff ? "RECONCILED_DIFF" : "PENDING", 3, 2, isDiff ? 1 : 0, isDiff ? 0 : 1);
            var b = Line(full, B20);
            string reason = scenario switch
            {
                "missing-receipt" => "RECEIPT_MISSING",
                "missing-post" => "POSTING_MISSING",
                "missing-line" => "POSTING_LINE_MISSING",
                _ => "MAPPING_MISMATCH"
            };
            Expect(b.GetProperty("reason").GetString() == reason, reason);
            Expect(await Count("SELECT COUNT(*) FROM d4accounting.Receipt") == (scenario == "missing-receipt" ? 2 : 3),
                "Reconciliation does not affect receipt count");
            Expect(await Count("SELECT COUNT(*) FROM d4accounting.Posting") == (isDiff ? 3 : 2),
                "Reconciliation does not affect posting count");
            if (scenario == "wrong-mapping")
            {
                var unknown = await Reconcile(Cutoff, Cutoff, true);
                ReportCounts(unknown, "UNKNOWN_SCOPE", null, 0, 0, 0);
                Expect(unknown.GetProperty("report").GetProperty("receivedCount").GetInt32() == 3 &&
                    unknown.GetProperty("report").GetProperty("postedCount").GetInt32() == 3,
                    "Unknown scope retains observed counts");
            }
            await Snapshot(scenario);
        }
    }

    private async Task PostDifference() => _ = await OpenDiffReport();

    private async Task CorrectDifference()
    {
        var original = await OpenDiffReport();
        var initialReport = ReportId(original);
        Guid caseId = Line(original, B20).GetProperty("caseId").GetGuid();
        var wrongScope = await Call("maker", $"/accounting/reports/{initialReport}/corrections",
            new { deliveryId = deliveryIds[0] }, HttpStatusCode.Conflict);
        Expect(wrongScope.GetProperty("status").GetString() == "CORRECTION_SCOPE", "Wrong scope is denied");
        var locked = await Call("maker", $"/accounting/reports/{initialReport}/corrections",
            new { deliveryId = B20, period = "2026-10" }, HttpStatusCode.Conflict);
        Expect(locked.GetProperty("status").GetString() == "PERIOD_LOCKED", "Locked period is denied");
        var proposal = await Call(tokens.ContainsKey("dual-maker") ? "dual-maker" : "dual", $"/accounting/reports/{initialReport}/corrections", new { deliveryId = B20 });
        Guid correctionId = proposal.GetProperty("id").GetGuid();
        var sameActor = await Call(tokens.ContainsKey("dual-checker") ? "dual-checker" : "dual", $"/accounting/corrections/{correctionId}/approve", new { },
            HttpStatusCode.Conflict);
        Expect(sameActor.GetProperty("status").GetString() == "MAKER_CHECKER_MUST_DIFFER",
            "Same actor is denied despite holding both roles");
        await Call("reconciler", $"/accounting/cases/{caseId}/close", new { reportId = initialReport },
            HttpStatusCode.Conflict);
        await clock(Cutoff.AddMinutes(5));
        var first = await Call("checker", $"/accounting/corrections/{correctionId}/approve", new { });
        var replay = await Call("checker", $"/accounting/corrections/{correctionId}/approve", new { });
        Expect(first.GetProperty("postingId").GetGuid() == replay.GetProperty("postingId").GetGuid(),
            "Correction replay is a singleton");
        Expect(await Count("""
            SELECT COUNT(*) FROM d4accounting.Posting p
            JOIN d4app.AccountingCorrection c ON p.CorrectionId=c.Id AND p.Id=c.PostingId AND p.ApprovalId=c.ApprovalId
            JOIN d4accounting.Posting b ON b.Id=p.OriginalId AND b.Id=c.OriginalId AND b.Gross=310 AND b.Ar=110
            WHERE p.Kind='CORRECTION' AND p.Quantity=0 AND p.Gross=-10 AND p.Ar=-10 AND p.Cost=0
              AND p.ReportId=c.ReportId AND p.Period='2026-11' AND c.Maker<>c.Checker
            """) == 1, "Correction, approval, report and original linked");
        var historical = await Call("reconciler", $"/accounting/reports/{initialReport}", null);
        ReportCounts(historical, "RECONCILED_DIFF", 3, 2, 1, 0);
        await Call("reconciler", $"/accounting/cases/{caseId}/close", new { reportId = ReportId(historical) },
            HttpStatusCode.Conflict);
        await clock(Cutoff.AddMinutes(10));
        var fresh = await Reconcile(Cutoff, Cutoff.AddMinutes(10));
        ReportCounts(fresh, "RECONCILED_OK", 3, 3, 0, 0);
        await Totals(ReportId(fresh), 675, 475);
        await Totals(initialReport, 685, 485);
        await Call("reconciler", $"/accounting/cases/{caseId}/close", new { reportId = ReportId(fresh) });
        Expect(await oracle.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT COUNT(*) FROM d4app.AccountingCase WHERE Id=@caseId AND ClosedReportId=@closedReport
                AND ClosedBy IS NOT NULL AND ClosedAt IS NOT NULL
            """, new { caseId, closedReport = ReportId(fresh) }, cancellationToken: ct)) == 1, "Case closed against new report");
    }

    private async Task<JsonElement> OpenDiffReport()
    {
        await ReceiveAll();
        await clock(From.AddMinutes(45));
        foreach (Guid id in deliveryIds)
            await Transfer(id, "post", $"diff-{id}", scenario: id == B20 ? "wrong-amount" : null);
        Expect(await Count("SELECT COUNT(*) FROM d4app.AccountingReport") == 0, "Posting does not self-reconcile");
        await clock(Cutoff);
        var report = await Reconcile(Cutoff, Cutoff);
        ReportCounts(report, "RECONCILED_DIFF", 3, 2, 1, 0);
        var b = Line(report, B20);
        Expect(b.GetProperty("diffQuantity").GetDecimal() == 0 && b.GetProperty("diffGross").GetDecimal() == 10 &&
            b.GetProperty("diffAr").GetDecimal() == 10 && b.GetProperty("diffCost").GetDecimal() == 0 &&
            b.GetProperty("caseId").GetGuid() != Guid.Empty, "B20 difference and owned case");
        Expect(await Count("""
            SELECT COUNT(*) FROM d4app.AccountingCase c
            JOIN d4app.AccountingReportLine l ON l.ReportId=c.ReportId AND l.SourceLineId=c.SourceLineId
            JOIN d4accounting.Receipt r ON r.Id=l.ReceiptId
            JOIN d4accounting.Posting p ON p.Id=l.PostingId
            WHERE c.OwnerRole='reconciler' AND c.NextAction='REVIEW_POSTING_DIFFERENCE'
                AND l.OccurredAt<r.ReceivedAt AND r.ReceivedAt<p.PostedAt
            """) == 1, "Source time, receipt and posting are independently recorded");
        await Totals(ReportId(report), 685, 485);
        return report;
    }

    private async Task Recover()
    {
        Guid source = deliveryIds[0];
        string[] before = await SourceFingerprint();
        await clock(From.AddMinutes(40));
        await Transfer(source, "receive", "recover-before-receive", fault: "before-commit",
            status: HttpStatusCode.ServiceUnavailable);
        Expect(await Count("SELECT COUNT(*) FROM d4accounting.Receipt") == 0, "Receive rollback");
        await Transfer(source, "receive", "recover-receive-ack", fault: "after-commit",
            status: HttpStatusCode.ServiceUnavailable);
        var receipt = await oracle.ExecuteScalarAsync<Guid>(new CommandDefinition(
            "SELECT Id FROM d4accounting.Receipt", cancellationToken: ct));
        Expect(receipt != Guid.Empty, "Receipt remains after ACK loss");
        await restart();
        var restoredReceipt = await Transfer(source, "receive", "recover-receive-ack");
        Expect(restoredReceipt.GetProperty("id").GetGuid() == receipt, "Original receipt after restart");
        await clock(From.AddMinutes(45));
        await Transfer(source, "post", "recover-before-post", fault: "before-commit",
            status: HttpStatusCode.ServiceUnavailable);
        Expect(await Count("SELECT COUNT(*) FROM d4accounting.Posting") == 0, "Post rollback");
        await Transfer(source, "post", "recover-post-ack", fault: "after-commit",
            status: HttpStatusCode.ServiceUnavailable);
        var posting = await oracle.ExecuteScalarAsync<Guid>(new CommandDefinition(
            "SELECT Id FROM d4accounting.Posting", cancellationToken: ct));
        Expect(posting != Guid.Empty, "Posting remains after ACK loss");
        await restart();
        var restoredPosting = await Transfer(source, "post", "recover-post-ack");
        Expect(restoredPosting.GetProperty("id").GetGuid() == posting, "Original posting after restart");
        Expect(await Count("SELECT COUNT(*) FROM d4accounting.Receipt") == 1 &&
            await Count("SELECT COUNT(*) FROM d4accounting.Posting") == 1, "One durable effect per stage");
        Expect(await Count("SELECT COUNT(*) FROM d4accounting.Journal WHERE Outcome='ROLLED_BACK'") == 2 &&
            await Count("SELECT COUNT(*) FROM d4accounting.Journal WHERE Outcome='COMMITTED_NO_ACK'") == 2 &&
            await Count("SELECT COUNT(*) FROM d4accounting.Journal WHERE Outcome='REPLAY'") == 2,
            "Separate audit entries for rollback, commit with ACK loss and replay");
        string[] after = await SourceFingerprint();
        Expect(before.SequenceEqual(after, StringComparer.Ordinal),
            "Committed source survives partner rollback and both restarts");
    }

    private async Task ReceiveAll()
    {
        await clock(From.AddMinutes(40));
        foreach (Guid id in deliveryIds) await Transfer(id, "receive", $"receive-{id}");
    }

    private Task<JsonElement> Reconcile(DateTimeOffset cutoff, DateTimeOffset asOf, bool unknownScope = false) =>
        Call("reconciler", "/accounting/reconcile", new { from = From, cutoff, asOf, unknownScope });

    private Task<JsonElement> Transfer(Guid id, string stage, string transport, string? fault = null, string? scenario = null,
        string? hash = null, HttpStatusCode status = HttpStatusCode.OK) =>
        Call("accountant", $"/accounting/{id}/{stage}", new { transportId = transport, fault, scenario, expectedHash = hash }, status);

    private async Task<JsonElement> Call(string role, string path, object? body, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post,
            "/api/synthetic/app02d4" + path);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens[role]);
        using var response = await client.SendAsync(request, ct);
        string json = await response.Content.ReadAsStringAsync(ct);
        if (response.StatusCode != expected)
            throw new InvalidOperationException($"{path}: HTTP {(int)response.StatusCode} != {(int)expected} ({json})");
        if (string.IsNullOrWhiteSpace(json)) return default;
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static void VerifyDuplicates(JsonElement[] results, string stage)
    {
        for (int offset = 0; offset < results.Length; offset += 2)
        {
            var left = results[offset];
            var right = results[offset + 1];
            Expect(left.GetProperty("status").GetString() == stage && right.GetProperty("status").GetString() == stage &&
                left.GetProperty("id").GetGuid() == right.GetProperty("id").GetGuid(), $"Concurrent {stage} reuses ID");
        }
    }

    private static JsonElement Line(JsonElement result, Guid delivery) => result.GetProperty("lines").EnumerateArray()
        .Single(line => line.GetProperty("deliveryId").GetGuid() == delivery);
    private static Guid ReportId(JsonElement result) => result.GetProperty("report").GetProperty("id").GetGuid();

    private static void ReportCounts(JsonElement result, string status, int? total, int ok, int diff, int pending)
    {
        var report = result.GetProperty("report");
        var count = report.GetProperty("expectedCount");
        Expect(report.GetProperty("status").GetString() == status &&
            (count.ValueKind == JsonValueKind.Null ? total is null : count.GetInt32() == total) &&
            report.GetProperty("okCount").GetInt32() == ok && report.GetProperty("diffCount").GetInt32() == diff &&
            report.GetProperty("pendingCount").GetInt32() == pending, $"Reconciliation {status} {ok}/{total}, {diff} DIFF, {pending} pending");
    }

    private async Task Totals(Guid id, int gross, int ar) => Expect(await oracle.ExecuteScalarAsync<int>(new CommandDefinition("""
        SELECT COUNT(*) FROM (
            SELECT SUM(ActualQuantity) Q,SUM(ActualGross) G,SUM(ActualAr) A,SUM(ActualCost) C
            FROM d4app.AccountingReportLine WHERE ReportId=@id
        ) result WHERE Q=45 AND G=@gross AND A=@ar AND C=450
        """, new { id, gross, ar }, cancellationToken: ct)) == 1, $"Independent SQL totals 45/{gross}/{ar}/450");

    private async Task<string[]> SourceFingerprint() => (await oracle.QueryAsync<string>(new CommandDefinition("""
        SELECT Hash FROM d4app.CommittedSource ORDER BY OccurredAt,DeliveryId
        """, cancellationToken: ct))).ToArray();

    private Task<int> Count(string sql) => oracle.ExecuteScalarAsync<int>(new CommandDefinition(sql, cancellationToken: ct));

    private async Task Snapshot(string step)
    {
        string principal = await oracle.ExecuteScalarAsync<string>(new CommandDefinition("SELECT USER_NAME()", cancellationToken: ct))
            ?? throw new InvalidOperationException("SQL oracle principal unavailable");
        using var db = await oracle.QueryMultipleAsync(new CommandDefinition("""
            SELECT * FROM d4accounting.Receipt ORDER BY ReceivedAt,SourceLineId;
            SELECT * FROM d4accounting.Posting ORDER BY PostedAt,SourceLineId;
            SELECT * FROM d4accounting.Journal ORDER BY Id;
            SELECT * FROM d4app.AccountingReport ORDER BY CreatedAt,Id;
            SELECT * FROM d4app.AccountingReportLine ORDER BY ReportId,OccurredAt;
            SELECT * FROM d4app.AccountingCase ORDER BY ReportId,SourceLineId;
            SELECT * FROM d4app.AccountingCorrection ORDER BY Id;
            """, cancellationToken: ct));
        checks.Add(new
        {
            Step = step, SqlEffectivePrincipal = principal,
            Receipts = Rows(await db.ReadAsync()), Postings = Rows(await db.ReadAsync()), Journal = Rows(await db.ReadAsync()),
            Reports = Rows(await db.ReadAsync()), Lines = Rows(await db.ReadAsync()), Cases = Rows(await db.ReadAsync()),
            Corrections = Rows(await db.ReadAsync())
        });
    }

    private static Dictionary<string, object>[] Rows(IEnumerable<dynamic> rows) => rows
        .Select(row => new Dictionary<string, object>((IDictionary<string, object>)row))
        .ToArray();

    private static void Expect(bool result, string reason)
    {
        if (!result) throw new InvalidOperationException(reason);
    }
}
