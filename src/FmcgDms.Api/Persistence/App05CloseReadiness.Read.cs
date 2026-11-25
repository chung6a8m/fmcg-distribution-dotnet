using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Persistence;

internal static partial class App05CloseReadiness
{
    private sealed record FinanceProjection(Guid DeliveryId, Guid SourceId, DateTimeOffset? OccurredAt,
        Guid? ReceiptId, Guid? PostingId, string? ReconciliationStatus, bool ReconciliationStale, Guid? CaseId,
        Guid? ReportId, DateTimeOffset? ReportFromUtc, DateTimeOffset? ReportCutoff, DateTimeOffset? ReportAsOf)
    {
        internal FinanceRow ToRow(DateTimeOffset asOf) =>
            new(DeliveryId, SourceId, OccurredAt!.Value, ReceiptId, PostingId, ReconciliationStatus,
                ReconciliationStale || (ReportId is not null && !(ReportFromUtc <= OccurredAt &&
                    OccurredAt < ReportCutoff && ReportCutoff <= ReportAsOf && ReportAsOf <= asOf)), CaseId);
    }

    private static async Task<Preview> Read(SqlConnection db, SqlTransaction tx, string tenant, Guid tripId, bool ops,
        CancellationToken ct)
    {
        var scope = new { Tenant = tenant, Trip = tripId };
        var trip = await db.QuerySingleAsync<Trip>(App02a3Sql.Cmd(TripSql + " AND t.Id=@Trip;", scope, tx, ct));
        var asOf = await db.ExecuteScalarAsync<DateTimeOffset>(App02a3Sql.Cmd(
            $"SELECT TODATETIMEOFFSET({App02c6Clock.SqlUtcNow},'+00:00');", null, tx, ct));
        Guid[] drafts, pending, unprovenOrders, heldB, manifests;
        ARow[] aRows;
        FinanceProjection[] finances;
        Receivable[] ar;
        List<Source> sources;
        using (var grid = await db.QueryMultipleAsync(App02a3Sql.Cmd(SourcesSql, scope, tx, ct)))
        {
            drafts = (await grid.ReadAsync<Guid>()).ToArray();
            pending = (await grid.ReadAsync<Guid>()).ToArray();
            aRows = (await grid.ReadAsync<ARow>()).ToArray();
            unprovenOrders = (await grid.ReadAsync<Guid>()).ToArray();
            finances = (await grid.ReadAsync<FinanceProjection>()).ToArray();
            ar = (await grid.ReadAsync<Receivable>()).ToArray();
            sources = (await grid.ReadAsync<Source>()).AsList();
            heldB = (await grid.ReadAsync<Guid>()).ToArray();
            manifests = (await grid.ReadAsync<Guid>()).ToArray();
        }

        var rights = (await App01dCashierUnloadService.ReadD3Rights(db, tx, tenant, tripId, ct))
            .OrderBy(right => right.Id).ToArray();
        var loads = await App01dCashierUnloadService.ReadD3LoadedLines(db, tx, tenant, tripId, ct);
        var issued = loads.ToLookup(load => load.RightId);
        var rightIds = rights.Select(right => right.Id).ToHashSet();
        var unprovenRights = rights.Where(right => !right.HasAllocationProof ||
            issued[right.Id].Sum(load => load.Issued) != right.Consumed).Select(right => right.Id).ToArray();
        var unprovenLoads = loads.Where(load => !load.HasMovementProof || !load.HasValidIssues ||
            load.Remaining < 0 || !rightIds.Contains(load.RightId)).Select(load => load.Id).ToArray();
        var loadScopeProven = manifests.ToHashSet().SetEquals(loads.Select(load => load.TripManifestId));

        var stock = new List<OwnerSource>();
        App03OwnerReport.AddTripStock(stock, await db.QueryAsync<App03OwnerReport.TripStock>(
            App02a3Sql.Cmd(App03OwnerReport.TripStockSql, scope, tx, ct)));
        var tripStock = stock.Where(row => row.Id == tripId).ToArray();
        var custody = await App01dCashierUnloadService.Custody(db, tx, tenant, tripId, ct);
        var custodyProven = await App01dCashierUnloadService.ReconcileCustody(db, tx, tenant, tripId, custody, ct);
        var pendingOffer = await App01dCashierUnloadService.HasPendingD3CashOffer(db, tx, tenant, tripId, ct);

        var blockers = new List<Blocker>();
        blockers.AddRange(drafts.Select(id => new Blocker("UNCONFIRMED_ORDER", "A_ORDER", id, "Xử lý đơn nháp trước khi chốt")));
        blockers.AddRange(pending.Select(id => new Blocker("DELIVERY_OUTCOME_PENDING", "C4_EVIDENCE", id, "Tra kết quả bằng chứng giao hàng")));
        blockers.AddRange(unprovenOrders.Select(id => new Blocker("A_SOURCE_DRIFT", "A_ORDER", id, "Đối chiếu nguồn xác nhận đơn A")));

        blockers.AddRange(unprovenRights.Select(id => new Blocker("RIGHT_SOURCE_DRIFT", D3, id,
            "Đối chiếu nguồn cấp giữ và lượng đã sử dụng")));
        blockers.AddRange(unprovenLoads.Select(id => new Blocker("MANIFEST_SOURCE_DRIFT", D3, id,
            "Đối chiếu chuyển kho lên xe và xuất giao theo manifest")));
        if (!loadScopeProven)
            blockers.Add(new("MANIFEST_SCOPE_UNKNOWN", D3, tripId, "Xác minh phạm vi manifest thuộc chuyến"));

        var aLines = new List<ALine>();
        foreach (var row in aRows)
        {
            var line = row.ToView();
            if (!row.Proven || row.Granted > row.Confirmed || row.Consumed != row.Delivered ||
                line.Remaining < 0 || line.Held < 0 || line.OnVehicle < 0)
            {
                blockers.Add(new("A_SOURCE_DRIFT", A, row.OrderLineId, "Đối chiếu nguồn giữ, nạp và giao của dòng A", row.OrderId));
                continue;
            }
            aLines.Add(line);
            if (line.Remaining > 0)
                sources.Add(new(A, line.OrderLineId, line.OrderId, "A còn phải giao · " + line.SkuCode,
                    "Theo dõi phần chưa giao trên đơn gốc", null));
        }

        foreach (var f in finances)
        {
            if (f.OccurredAt is null || f.OccurredAt > asOf)
            {
                blockers.Add(new("FINANCE_SOURCE_UNKNOWN", D4, f.SourceId, "Kiểm tra nguồn giao đã cam kết trong D.4", f.DeliveryId));
                continue;
            }
            var row = f.ToRow(asOf);
            if (row.Pending(asOf) is { } pendingState)
                sources.Add(new(D4, row.SourceId, row.DeliveryId, FinanceLabel(pendingState),
                    FinanceAction(pendingState), row.CaseId));
        }
        blockers.AddRange(ar.Where(row => !row.Proven).Select(row =>
            new Blocker("AR_SOURCE_DRIFT", D4, row.SourceId, "Đối chiếu phải thu với nguồn giao và phân bổ thu", row.DeliveryId)));
        if (!custodyProven)
            blockers.Add(new("CUSTODY_SOURCE_DRIFT", D3, tripId, "Đối chiếu nguồn thu, nhận bàn giao và quỹ"));
        else if (custody != 0)
            blockers.Add(new("CUSTODY_OUTSTANDING", D3, tripId, "Bàn giao tiền còn giữ của chuyến"));
        if (pendingOffer)
            blockers.Add(new("CASH_OFFER_PENDING", D3, tripId, "Kiểm đếm và xử lý đề nghị bàn giao tiền"));
        if (tripStock.Any(row => !row.Value.HasValue) || tripStock.Any(row => row.Metric == "trip-qty" && row.Value < 0))
            blockers.Add(new("VEHICLE_SOURCE_DRIFT", D3, tripId, "Đối chiếu manifest, giao, trả và nguồn tồn xe"));
        if (tripStock.Any(row => row.Metric == "trip-qty" && row.Value > 0))
            blockers.Add(new("VEHICLE_STOCK_OUTSTANDING", D3, tripId, "Trả lượng còn trên xe thuộc chuyến này"));
        if (heldB.Length > 0)
            blockers.Add(new("B_QUOTA_OUTSTANDING", D3, tripId, "Giải phóng quota B qua bàn giao cuối chuyến"));
        if (trip.Used && trip.UnloadId is null)
            blockers.Add(new(manifests.Length == 0 && heldB.Length == 0 ? "ZERO_STOCK_HANDOFF_REQUIRED" : "HANDOFF_REQUIRED",
                D3, tripId, manifests.Length == 0 && heldB.Length == 0
                    ? "Cần biên nhận bàn giao chuyến chưa nạp" : "Hoàn thành bàn giao cuối chuyến"));
        if (trip.UnloadId is not null && trip.Status is not ("RECONCILING" or "CLOSED" or "CLOSED_WITH_OPEN_CASES"))
            blockers.Add(new("HANDOFF_SOURCE_DRIFT", D3, trip.UnloadId, "Đối chiếu trạng thái chuyến với biên nhận bàn giao"));

        var assignments = (await db.QueryAsync<Assignment>(App02a3Sql.Cmd(AssignmentsSql, scope, tx, ct)))
            .ToDictionary(row => (row.SourceKind, row.SourceId));
        var assignees = (await db.QueryAsync<string>(App02a3Sql.Cmd(AssigneesSql, scope, tx, ct))).ToArray();
        var work = sources.OrderBy(row => row.SourceKind, StringComparer.Ordinal).ThenBy(row => row.SourceId)
            .Select(row => new Work(row.SourceKind, row.SourceId, row.RelatedId, row.Label, row.Action, row.CaseId,
                assignments.GetValueOrDefault((row.SourceKind, row.SourceId))))
            .ToArray();
        var missing = work.Where(row => row.FollowUp is null || !assignees.Contains(row.FollowUp.Assignee, StringComparer.Ordinal)).ToArray();
        string state = ReadinessState(trip.Used, blockers.Count, missing.Length, work.Length);
        blockers.AddRange(missing.Select(row => new Blocker("FOLLOW_UP_REQUIRED", row.SourceKind, row.SourceId,
            "Giao người phụ trách, hạn và hành động tiếp theo", row.RelatedId)));
        int expected = aRows.Length + unprovenOrders.Length + finances.Length + ar.Length + pending.Length + tripStock.Length + rights.Length + loads.Length + 2;
        int known = aLines.Count + finances.Count(row => row.OccurredAt <= asOf) + ar.Count(row => row.Proven)
            + tripStock.Count(row => row.Value.HasValue) + (custodyProven ? 1 : 0)
            + rights.Length - unprovenRights.Length + loads.Length - unprovenLoads.Length + (loadScopeProven ? 1 : 0);
        var coverage = trip.Used ? new Coverage(known, expected) : new Coverage(0, 0);
        var revision = Convert.ToHexString(Digest(new
        {
            tenant, trip, asOf, aRows, finances, ar, work, assignees, blockers, custody, stock = tripStock,
            rights, loads, heldB, manifests
        }));
        return new(tripId, trip.Code, DateOnly.FromDateTime(trip.BusinessDate), asOf, trip.Status,
            state, revision, coverage, ops && trip.Used && trip.Status is not ("CLOSED" or "CLOSED_WITH_OPEN_CASES"),
            ops ? assignees : [], blockers.ToArray(), aLines.ToArray(), ar.Select(row => row.Proven ? row : row with { Amount = null }).ToArray(), work);
    }

    private static string FinanceLabel(string state) => state switch
    {
        "AWAITING_RECEIPT" => "Đối soát D.4 · Chờ tiếp nhận",
        "AWAITING_POSTING" => "Đối soát D.4 · Chờ hạch toán",
        "AWAITING_CUTOFF" => "Đối soát D.4 · Chờ cutoff",
        "AWAITING_RECONCILIATION" => "Đối soát D.4 · Chờ báo cáo",
        "REPORT_STALE" => "Đối soát D.4 · Báo cáo cần cập nhật",
        _ => "Đối soát D.4 · Hồ sơ cần xử lý"
    };

    private static string FinanceAction(string state) => state switch
    {
        "AWAITING_RECEIPT" => "Tiếp nhận nguồn giao trong D.4 và theo dõi đối soát",
        "AWAITING_POSTING" => "Hạch toán nguồn đã tiếp nhận và theo dõi đối soát",
        "AWAITING_CUTOFF" => "Đối soát khi cutoff bao phủ thời điểm giao",
        "AWAITING_RECONCILIATION" or "REPORT_STALE" => "Lập báo cáo bao phủ nguồn giao",
        _ => "Mở hồ sơ đối soát gốc và xử lý chênh lệch"
    };
}
