using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

internal static class App05DailyCloseOracle
{
    private const string Primary = "tenant-a";
    private const string Peer = "tenant-b";
    private const string Origin = "DEV-20261123-A";
    private const string Clean = "DEV-20261123-B";
    private const string Zero = "DEV-20261123-Z";
    private const string Unused = "DEV-20261123-U";
    private const string Successor = "DEV-20261124-A";
    private const string ZeroSuccessor = "DEV-20261124-B";
    private static readonly DateTimeOffset DayOne = new(2026, 11, 23, 9, 0, 0, TimeSpan.FromHours(7));
    private static readonly DateTimeOffset DayTwo = DayOne.AddDays(1);

    internal sealed record Snapshot(string Phase, Stock Stock, Trip[] Trips, Confirmation[] Confirms,
        Delivery[] Deliveries, Right[] Rights, Binding[] Bindings, Assignment[] Assignments,
        ClosingWork[] ClosingWork, ReportLine[] Reports, GuardFacts Guards, string PeerDigest);

    internal sealed record Stock(decimal MainQuantity, decimal MainValue, decimal VehicleQuantity,
        decimal VehicleValue, decimal HeldQuantity, decimal OtherWarehouseQuantity,
        decimal OtherWarehouseValue, decimal OtherWarehouseHeld, string OpeningDigest);
    internal sealed record Trip(Guid TripId, string Code, string Status, Guid? ClosureId, DateTimeOffset? ClosedAt,
        int? OpenWorkCount, string? SnapshotDigest, Guid? HandoffId, string? HandoffWarehouse,
        int ManifestLines, int DeliveryLines,
        int SourceRows, int MovementRows, int LedgerRows, int ClosureRejections, int ZeroProofs,
        decimal ReturnedQuantity, decimal ReturnedValue, decimal Custody, decimal Fund);
    internal sealed record Confirmation(Guid OrderId, Guid LineId, Guid ConfirmSourceId, string TripCode, string Sku,
        string Customer, string Status, decimal Quantity, decimal UnitPrice, int Terms, string SourceDigest);
    internal sealed record Delivery(Guid Id, Guid OrderId, Guid LineId, Guid TripId, string TripCode, string Sku,
        Guid SourceId, Guid SourceLineId, string SourceHash, Guid ShipToId, Guid RightId, Guid ManifestLineId,
        Guid? BindingId, Guid? OriginTripId, Guid? HeldExecutorId, DateTime BusinessDate, DateTimeOffset OccurredAt,
        decimal Quantity, decimal Gross, decimal Cost, decimal? Ar, decimal? ArApplied, DateTime? Due,
        decimal? LoadedQuantity, decimal? LoadedValue, decimal LotIssued, decimal MatchedLotLoaded,
        string? LotCode, int LotIssueRows, Guid? ReceiptId, Guid? PostingId, decimal? PostedQuantity,
        decimal? PostedGross, decimal? PostedAr, decimal? PostedCost, int ExactLedgerLegs, int LedgerRows,
        int StockIssueRows, int AppliedSourceRows, string SourceDigest);
    internal sealed record Right(Guid RightId, Guid LineId, Guid SourceLineId, string TripCode, decimal Granted,
        decimal Consumed, decimal Released, decimal Held, string Status, decimal LotHeld);
    internal sealed record Binding(Guid BindingId, Guid FollowUpId, Guid LineId, Guid ConfirmSourceId,
        string OriginTripCode, string DestinationTripCode, decimal Quantity, Guid? OriginalRightId, Guid? HeldRightId,
        Guid? ReserveRightId, decimal? NeverLoaded, decimal? Returned, decimal TransferLotQuantity,
        string? TransferLotCode);
    internal sealed record Assignment(long EventId, string TripCode, Guid FollowUpId, string SourceKind, Guid SourceId,
        string Assignee, DateTimeOffset DueAt, string NextAction, string Reason, Guid CommandId, string ActorId);
    internal sealed record ClosingWork(string TripCode, Guid FollowUpId, string SourceKind, Guid SourceId,
        string Assignee, DateTimeOffset DueAt, Guid CommandId, string Reason);
    internal sealed record ReportLine(Guid ReportId, string TripCode, Guid DeliveryId, Guid SourceLineId,
        string SourceHash, Guid? ReceiptId, Guid? PostingId, string Status, DateTimeOffset OccurredAt,
        DateTimeOffset FromUtc, DateTimeOffset Cutoff, DateTimeOffset AsOf, int? ExpectedCount,
        decimal? Quantity, decimal? Gross, decimal? Ar, decimal? Cost);
    internal sealed record GuardFacts(int ExactZeroConfirmProofs, int ReserveRowsOnZeroOrigin, int RightRowsOnUnused,
        int AssignmentsOnUnused, int CashReceipts, int DayTwoLinesInDayOneReports,
        int ActiveWarehouses, int WarehousesWithZeroConfirmSku);
    internal sealed record Comparison(string Phase, string Metric, string Unit, object? Expected, object? Actual,
        decimal? Diff, bool Match);

    internal static async Task<Snapshot> Capture(DemoRun run, string phase, CancellationToken ct)
    {
        await run.Verify(Primary, app03Session: true);
        await using var db = new SqlConnection(run.Connection(Primary));
        bool successor = await HasSuccessorSchema(db, ct);
        using var rows = await db.QueryMultipleAsync(new CommandDefinition(SourceSql(successor), new
        {
            Tenant = Primary, Origin, Clean, Zero, Unused, Successor, ZeroSuccessor, Sku = App05CloseBootstrap.SkuCode
        }, cancellationToken: ct));
        var stock = await rows.ReadSingleAsync<Stock>();
        var trips = (await rows.ReadAsync<Trip>()).ToArray();
        var confirms = (await rows.ReadAsync<Confirmation>()).ToArray();
        var deliveries = (await rows.ReadAsync<Delivery>()).ToArray();
        var rights = (await rows.ReadAsync<Right>()).ToArray();
        var bindings = successor ? (await rows.ReadAsync<Binding>()).ToArray() : [];
        var assignments = (await rows.ReadAsync<Assignment>()).ToArray();
        var closingWork = (await rows.ReadAsync<ClosingWork>()).ToArray();
        var reports = (await rows.ReadAsync<ReportLine>()).ToArray();
        var guards = await rows.ReadSingleAsync<GuardFacts>();

        await run.Verify(Peer, app03Session: true);
        await using var peer = new SqlConnection(run.Connection(Peer));
        string digest = await peer.QuerySingleAsync<string>(new CommandDefinition(PeerSql(await HasSuccessorSchema(peer, ct)), new { Tenant = Peer },
            cancellationToken: ct));
        return new(phase, stock, trips, confirms, deliveries, rights, bindings, assignments, closingWork, reports, guards, digest);
    }

    private static Task<bool> HasSuccessorSchema(SqlConnection db, CancellationToken ct) =>
        db.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT CONVERT(bit,CASE WHEN OBJECT_ID(N'dbo.App05AHeldExecution',N'U') IS NOT NULL THEN 1 ELSE 0 END);
            """, cancellationToken: ct));

    internal static Comparison[] Compare(Snapshot before, Snapshot afterDayOne, Snapshot afterDayTwo)
    {
        var checks = new Checks();
        CheckCommon(before, 60, 180000, 0);
        CheckCommon(afterDayOne, 35, 105000, 15);
        CheckCommon(afterDayTwo, 20, 60000, 0);
        checks.Equal(before, "no main-fixture sale before journey", "rows", 0, before.Deliveries.Length);
        checks.Equal(afterDayOne, "delivery row count across fixtures", "rows", 2, afterDayOne.Deliveries.Length);
        checks.Equal(afterDayTwo, "delivery row count across fixtures", "rows", 4, afterDayTwo.Deliveries.Length);
        checks.Equal(afterDayTwo, "main A gross AR across both trips", "VND", 400000m,
            afterDayTwo.Deliveries.Where(row => row.Sku == App05CloseBootstrap.SkuCode).Sum(row => row.Ar ?? 0));
        checks.Equal(afterDayTwo, "main A COGS across both trips", "VND", 120000m,
            afterDayTwo.Deliveries.Where(row => row.Sku == App05CloseBootstrap.SkuCode).Sum(row => row.Cost));
        CheckTrip(afterDayOne, Origin, "CLOSED_WITH_OPEN_CASES", 2, DayOne);
        CheckTrip(afterDayTwo, Origin, "CLOSED_WITH_OPEN_CASES", 2, DayOne);
        CheckTrip(afterDayOne, Clean, "RECONCILING", null, null);
        CheckTrip(afterDayTwo, Clean, "CLOSED", 0, DayTwo);
        CheckTrip(afterDayOne, Zero, "CLOSED_WITH_OPEN_CASES", 1, DayOne);
        CheckTrip(afterDayTwo, Zero, "CLOSED_WITH_OPEN_CASES", 1, DayOne);
        CheckTrip(afterDayOne, Unused, "PREPARED", null, null);
        CheckTrip(afterDayTwo, Unused, "PREPARED", null, null);
        var beforeClean = Single(afterDayOne.Trips, row => row.Code == Clean);
        var afterClean = Single(afterDayTwo.Trips, row => row.Code == Clean);
        checks.Equal(afterDayTwo, "clean closure adds no commercial source", "rows", beforeClean?.SourceRows, afterClean?.SourceRows);
        checks.Equal(afterDayTwo, "clean closure adds no ledger effect", "rows", beforeClean?.LedgerRows, afterClean?.LedgerRows);
        checks.Equal(afterDayTwo, "clean closure adds no stock movement", "rows", beforeClean?.MovementRows, afterClean?.MovementRows);
        checks.Equal(afterDayTwo, "original clean handoff reused", "ID", beforeClean?.HandoffId, afterClean?.HandoffId);

        checks.Equal(afterDayOne, "no successor binding before NextDay", "rows", 0, afterDayOne.Bindings.Length);
        checks.Equal(afterDayTwo, "one source-bound binding each for main and zero manifest orders", "rows", 2, afterDayTwo.Bindings.Length);
        checks.Equal(afterDayOne, "three original Confirm rows", "rows", 3, afterDayOne.Confirms.Length);
        checks.Equal(afterDayTwo, "successor delivery does not duplicate Confirm", "rows", 3, afterDayTwo.Confirms.Length);
        checks.Equal(afterDayTwo, "main A line is fulfilled", "state", "FULFILLED",
            Single(afterDayTwo.Confirms, row => row.TripCode == Origin)?.Status);
        checks.Equal(afterDayTwo, "zero-manifest A line is fulfilled", "state", "FULFILLED",
            Single(afterDayTwo.Confirms, row => row.TripCode == Zero)?.Status);
        checks.Equal(afterDayOne, "no covering day-one report before NextDay", "rows", 0, afterDayOne.Reports.Length);
        checks.Equal(afterDayTwo, "day-two lines in a report containing day-one deliveries", "rows", 0,
            afterDayTwo.Guards.DayTwoLinesInDayOneReports);
        checks.Equal(afterDayTwo, "day-one report coverage across main and clean fixtures", "rows", 2, afterDayTwo.Reports.Length);
        checks.Equal(afterDayTwo, "same report ID covers main and clean day-one lines", "rows", 1,
            afterDayTwo.Reports.Select(row => row.ReportId).Distinct().Count());
        CheckConfirmation(afterDayOne, Origin, "DAY-A-EA", "DAY-A-DEV", 40, 10000, 7);
        CheckConfirmation(afterDayOne, Zero, "Z-FIELD-EA", "C2-FIXTURE", 5, 10000, 7);
        CheckConfirmation(afterDayOne, Clean, "Z-REFUND-EA", "C2-FIXTURE", 1, 10000, 7);

        var first = CheckDelivery(afterDayOne, Origin, 25, 250000, 75000, 40, 120000,
            new DateTime(2026, 11, 30), posted: true);
        var next = CheckDelivery(afterDayTwo, Successor, 15, 150000, 45000, 15, 45000,
            new DateTime(2026, 12, 1), posted: true);
        CheckDelivery(afterDayOne, Clean, 1, 10000, 3000, 1, 3000, new DateTime(2026, 11, 30), posted: true);
        var zero = CheckDelivery(afterDayTwo, ZeroSuccessor, 5, 50000, 20000, 5, 20000,
            new DateTime(2026, 12, 1), posted: true, lotTracked: false);

        if (first is not null && next is not null)
        {
            checks.Equal(afterDayOne, "main origin lot", "lot code", "DAY-A-ORIGIN", first.LotCode);
            checks.Equal(afterDayTwo, "one original order", "ID", first.OrderId, next.OrderId);
            checks.Equal(afterDayTwo, "one original order line", "ID", first.LineId, next.LineId);
            checks.Equal(afterDayTwo, "successor retains origin trip identity", "ID", first.TripId, next.OriginTripId);
            checks.Equal(afterDayTwo, "successor consumes successor HELD executor", "ID", next.RightId, next.HeldExecutorId);
            checks.Equal(afterDayTwo, "successor issues the original lot", "lot code", first.LotCode, next.LotCode);
            var binding = Single(afterDayTwo.Bindings, row => row.OriginTripCode == Origin);
            checks.Equal(afterDayTwo, "one main source-bound successor", "rows", 1, binding is null ? 0 : 1);
            if (binding is not null)
            {
                checks.Equal(afterDayTwo, "main binding origin Confirm", "ID",
                    Single(afterDayOne.Confirms, row => row.TripCode == Origin)?.ConfirmSourceId, binding.ConfirmSourceId);
                checks.Equal(afterDayTwo, "main binding destination", "trip", Successor, binding.DestinationTripCode);
                checks.Equal(afterDayTwo, "main binding matches successor delivery", "ID", binding.BindingId, next.BindingId);
                checks.Equal(afterDayTwo, "main binding preserves closing assignment identity", "ID",
                    Single(afterDayOne.ClosingWork, row => row.TripCode == Origin && row.SourceKind == "A_REMAINDER")?.FollowUpId,
                    binding.FollowUpId);
                checks.Equal(afterDayTwo, "main binding quantity", "EA", 15m, binding.Quantity);
                checks.Equal(afterDayTwo, "HELD parent right", "ID", first.RightId, binding.OriginalRightId);
                checks.Equal(afterDayTwo, "HELD child right", "ID", next.RightId, binding.HeldRightId);
                checks.Equal(afterDayTwo, "returned HELD transfer", "EA", 15m, binding.Returned);
                checks.Equal(afterDayTwo, "never-loaded contribution to main transfer", "EA", 0m, binding.NeverLoaded);
                checks.Equal(afterDayTwo, "HELD transfer lot quantity", "EA", 15m, binding.TransferLotQuantity);
                checks.Equal(afterDayTwo, "HELD transfer preserves original lot", "lot code", first.LotCode, binding.TransferLotCode);
                checks.Equal<Guid?>(afterDayTwo, "no substitute Reserve for transferred HELD", "ID", null, binding.ReserveRightId);
            }
            CheckRight(afterDayOne, first.RightId, 40, 25, 0, 15, "HELD");
            CheckRight(afterDayTwo, first.RightId, 40, 25, 15, 0, "RELEASED");
            CheckRight(afterDayTwo, next.RightId, 15, 15, 0, 0, "CONSUMED");
        }

        if (zero is not null)
        {
            var confirmation = Single(afterDayOne.Confirms, row => row.TripCode == Zero);
            var binding = Single(afterDayTwo.Bindings, row => row.OriginTripCode == Zero);
            checks.Equal(afterDayTwo, "one zero-manifest source-bound successor", "rows", 1, binding is null ? 0 : 1);
            if (binding is not null)
            {
                checks.Equal(afterDayTwo, "zero successor uses original order", "ID", confirmation?.OrderId, zero.OrderId);
                checks.Equal(afterDayTwo, "zero successor uses original Confirm", "ID", confirmation?.ConfirmSourceId, binding.ConfirmSourceId);
                checks.Equal(afterDayTwo, "zero successor binding matches delivery", "ID", binding.BindingId, zero.BindingId);
                checks.Equal(afterDayTwo, "zero successor preserves closing assignment identity", "ID",
                    Single(afterDayOne.ClosingWork, row => row.TripCode == Zero && row.SourceKind == "A_REMAINDER")?.FollowUpId,
                    binding.FollowUpId);
                checks.Equal(afterDayTwo, "zero successor binding destination", "trip", ZeroSuccessor, binding.DestinationTripCode);
                checks.Equal(afterDayTwo, "zero successor binding quantity", "EA", 5m, binding.Quantity);
                checks.Equal(afterDayTwo, "zero successor consumes separate Reserve", "ID", zero.RightId, binding.ReserveRightId);
                checks.Equal<Guid?>(afterDayTwo, "zero successor has no manufactured HELD transfer", "ID", null, binding.HeldRightId);
            }
            CheckRight(afterDayTwo, zero.RightId, 5, 5, 0, 0, "CONSUMED", lotTracked: false);
        }

        foreach (var old in afterDayOne.Deliveries)
            CheckReport(afterDayTwo, old);
        CheckClosureWork(afterDayOne, first);
        checks.CompareOriginals(afterDayOne, afterDayTwo);
        return checks.Values();

        void CheckCommon(Snapshot snapshot, decimal quantity, decimal value, decimal held)
        {
            checks.Equal(snapshot, "unchanged MAIN opening", "SHA-256", before.Stock.OpeningDigest, snapshot.Stock.OpeningDigest);
            checks.Equal(snapshot, "contrast tenant business image", "SHA-256", before.PeerDigest, snapshot.PeerDigest);
            checks.Equal(snapshot, "MAIN day-close stock", "EA", quantity, snapshot.Stock.MainQuantity);
            checks.Equal(snapshot, "MAIN day-close cost", "VND", value, snapshot.Stock.MainValue);
            checks.Equal(snapshot, "day-close vehicle stock", "EA", 0m, snapshot.Stock.VehicleQuantity);
            checks.Equal(snapshot, "day-close vehicle cost", "VND", 0m, snapshot.Stock.VehicleValue);
            checks.Equal(snapshot, "MAIN HELD day-close stock", "EA", held, snapshot.Stock.HeldQuantity);
            checks.Equal(snapshot, "day-close stock in other warehouses", "EA", 0m, snapshot.Stock.OtherWarehouseQuantity);
            checks.Equal(snapshot, "day-close cost in other warehouses", "VND", 0m, snapshot.Stock.OtherWarehouseValue);
            checks.Equal(snapshot, "day-close HELD in other warehouses", "EA", 0m, snapshot.Stock.OtherWarehouseHeld);
            checks.Equal(snapshot, "no cash receipt in selected shift trips", "rows", 0, snapshot.Guards.CashReceipts);
            checks.Equal(snapshot, "unused trip does not acquire rights", "rows", 0, snapshot.Guards.RightRowsOnUnused);
            checks.Equal(snapshot, "unused trip does not acquire work", "rows", 0, snapshot.Guards.AssignmentsOnUnused);
            foreach (var trip in snapshot.Trips)
            {
                checks.Equal(snapshot, trip.Code + " custody balance", "VND", 0m, trip.Custody);
                checks.Equal(snapshot, trip.Code + " fund balance", "VND", 0m, trip.Fund);
            }
        }

        void CheckTrip(Snapshot snapshot, string code, string status, int? open, DateTimeOffset? closedAt)
        {
            var trip = Single(snapshot.Trips, row => row.Code == code);
            checks.Equal(snapshot, code + " status", "state", status, trip?.Status);
            if (trip is null) return;
            checks.Equal(snapshot, code + " closure recorded", "bool", open.HasValue, trip.ClosureId.HasValue);
            checks.Equal(snapshot, code + " work at close", "rows", open, trip.OpenWorkCount);
            checks.Equal(snapshot, code + " time recorded", "ICT", closedAt, trip.ClosedAt);
            if (code == Origin)
            {
                checks.Equal(snapshot, code + " returned", "EA", 15m, trip.ReturnedQuantity);
                checks.Equal(snapshot, code + " return value", "VND", 45000m, trip.ReturnedValue);
                checks.Equal(snapshot, code + " recorded blocked Close while stock remained on the vehicle", "rows",
                    1, trip.ClosureRejections);
            }
            if (code is Zero or Unused)
            {
                checks.Equal(snapshot, code + " manifest", "rows", 0, trip.ManifestLines);
                checks.Equal(snapshot, code + " stock movement", "rows", 0, trip.MovementRows);
                checks.Equal(snapshot, code + " delivery", "rows", 0, trip.DeliveryLines);
                checks.Equal(snapshot, code + " ledger effects", "rows", 0, trip.LedgerRows);
            }
            if (code == Zero)
            {
                checks.Equal(snapshot, "zero-stock handoff ID exists", "bool", true, trip.HandoffId.HasValue);
                checks.Equal(snapshot, "zero-stock handoff warehouse", "warehouse", "C2-MAIN", trip.HandoffWarehouse);
                checks.Equal(snapshot, "fixture contains multiple active warehouses", "bool", true,
                    snapshot.Guards.ActiveWarehouses > 1);
                checks.Equal(snapshot, "one active warehouse carries the confirmed zero-stock SKU", "rows", 1,
                    snapshot.Guards.WarehousesWithZeroConfirmSku);
                checks.Equal(snapshot, "zero-stock handoff line count", "rows", 1, trip.ZeroProofs);
                checks.Equal(snapshot, "zero-stock handoff references the original unreserved Confirm", "rows", 1,
                    snapshot.Guards.ExactZeroConfirmProofs);
                checks.Equal(snapshot, "zero-stock origin never acquired a stock right", "rows", 0, snapshot.Guards.ReserveRowsOnZeroOrigin);
            }
            if (code == Unused)
            {
                checks.Equal(snapshot, "unused trip has no handoff", "bool", false, trip.HandoffId.HasValue);
                checks.Equal(snapshot, "unused trip has no source", "rows", 0, trip.SourceRows);
            }
        }

        void CheckConfirmation(Snapshot snapshot, string code, string sku, string customer, decimal quantity,
            decimal? unitPrice, int? terms)
        {
            var confirmation = Single(snapshot.Confirms, row => row.TripCode == code);
            checks.Equal(snapshot, code + " single original Confirm", "rows", 1, confirmation is null ? 0 : 1);
            if (confirmation is null) return;
            checks.Equal(snapshot, code + " original Confirm quantity", "EA", quantity, confirmation.Quantity);
            checks.Equal(snapshot, code + " original Confirm SKU", "SKU", sku, confirmation.Sku);
            checks.Equal(snapshot, code + " original Confirm customer", "customer", customer, confirmation.Customer);
            if (unitPrice.HasValue) checks.Equal(snapshot, code + " original unit price", "VND/EA", unitPrice, confirmation.UnitPrice);
            if (terms.HasValue) checks.Equal(snapshot, code + " original terms", "days", terms, confirmation.Terms);
        }

        Delivery? CheckDelivery(Snapshot snapshot, string code, decimal quantity, decimal gross, decimal cost,
            decimal loaded, decimal loadedValue, DateTime? due, bool posted, bool lotTracked = true)
        {
            var delivery = Single(snapshot.Deliveries, row => row.TripCode == code);
            checks.Equal(snapshot, code + " exactly one delivery", "rows", 1, delivery is null ? 0 : 1);
            if (delivery is null) return null;
            var businessDay = code is Successor or ZeroSuccessor ? DayTwo : DayOne;
            checks.Equal(snapshot, code + " delivery business date", "date", businessDay.Date, delivery.BusinessDate);
            checks.Equal(snapshot, code + " committed occurrence", "ICT", businessDay, delivery.OccurredAt);
            checks.Equal(snapshot, code + " delivered quantity", "EA", quantity, delivery.Quantity);
            checks.Equal(snapshot, code + " load quantity", "EA", loaded, delivery.LoadedQuantity);
            checks.Equal(snapshot, code + " load value", "VND", loadedValue, delivery.LoadedValue);
            checks.Equal(snapshot, code + " delivery gross", "VND", gross, delivery.Gross);
            checks.Equal(snapshot, code + " delivered cost", "VND", cost, delivery.Cost);
            checks.Equal(snapshot, code + " gross AR origination", "VND", gross, delivery.Ar);
            checks.Equal(snapshot, code + " AR collected", "VND", 0m, delivery.ArApplied);
            if (due.HasValue) checks.Equal(snapshot, code + " due date", "date", due, delivery.Due);
            checks.Equal(snapshot, code + " exact commercial and ledger legs", "rows", 4, delivery.ExactLedgerLegs);
            checks.Equal(snapshot, code + " all ledger effects on source", "rows", 4, delivery.LedgerRows);
            checks.Equal(snapshot, code + " source-bound stock issue", "rows", 1, delivery.StockIssueRows);
            checks.Equal(snapshot, code + " C4 source and command", "rows", 1, delivery.AppliedSourceRows);
            checks.Equal(snapshot, code + " loaded lot quantity for exact issue lot", "EA", lotTracked ? loaded : 0, delivery.MatchedLotLoaded);
            checks.Equal(snapshot, code + " issued lot quantity", "EA", lotTracked ? quantity : 0, delivery.LotIssued);
            checks.Equal(snapshot, code + " lot issue rows", "rows", lotTracked ? 1 : 0, delivery.LotIssueRows);
            checks.Equal(snapshot, code + " D.4 source hash available", "bool", true, delivery.SourceHash?.Length == 64);
            if (posted)
            {
                checks.Equal(snapshot, code + " exact D.4 receipt and posting", "bool", true, delivery.ReceiptId.HasValue && delivery.PostingId.HasValue);
                checks.Equal(snapshot, code + " D.4 posted quantity", "EA", quantity, delivery.PostedQuantity);
                checks.Equal(snapshot, code + " D.4 gross posting", "VND", gross, delivery.PostedGross);
                checks.Equal(snapshot, code + " D.4 receivable posting", "VND", gross, delivery.PostedAr);
                checks.Equal(snapshot, code + " D.4 cost posting", "VND", cost, delivery.PostedCost);
            }
            return delivery;
        }

        void CheckRight(Snapshot snapshot, Guid id, decimal granted, decimal consumed, decimal released, decimal held,
            string status, bool lotTracked = true)
        {
            var right = Single(snapshot.Rights, row => row.RightId == id);
            checks.Equal(snapshot, id + " stock right exists", "bool", true, right is not null);
            if (right is null) return;
            checks.Equal(snapshot, id + " granted quantity", "EA", granted, right.Granted);
            checks.Equal(snapshot, id + " consumed quantity", "EA", consumed, right.Consumed);
            checks.Equal(snapshot, id + " released quantity", "EA", released, right.Released);
            checks.Equal(snapshot, id + " held quantity", "EA", held, right.Held);
            checks.Equal(snapshot, id + " lot hold quantity", "EA", lotTracked ? granted : 0, right.LotHeld);
            checks.Equal(snapshot, id + " status", "state", status, right.Status);
        }

        void CheckReport(Snapshot snapshot, Delivery delivery)
        {
            var report = Single(snapshot.Reports, row => row.SourceLineId == delivery.SourceLineId);
            checks.Equal(snapshot, delivery.TripCode + " exact report line found", "bool", true, report is not null);
            if (report is null) return;
            checks.Equal(snapshot, delivery.TripCode + " report source hash", "SHA-256", delivery.SourceHash, report.SourceHash);
            checks.Equal(snapshot, delivery.TripCode + " report delivery", "ID", delivery.Id, report.DeliveryId);
            checks.Equal(snapshot, delivery.TripCode + " report receipt", "ID", delivery.ReceiptId, report.ReceiptId);
            checks.Equal(snapshot, delivery.TripCode + " report posting", "ID", delivery.PostingId, report.PostingId);
            checks.Equal(snapshot, delivery.TripCode + " report status", "state", "RECONCILED_OK", report.Status);
            checks.Equal(snapshot, delivery.TripCode + " report exact population", "rows", 2, report.ExpectedCount);
            checks.Equal(snapshot, delivery.TripCode + " report starts at day-one midnight", "ICT",
                DayOne.AddHours(-9), report.FromUtc);
            checks.Equal(snapshot, delivery.TripCode + " report cutoff is day-two midnight", "ICT",
                DayTwo.AddHours(-9), report.Cutoff);
            checks.Equal(snapshot, delivery.TripCode + " report uses day-two DEV clock", "ICT", DayTwo, report.AsOf);
            checks.Equal(snapshot, delivery.TripCode + " report window", "bool", true,
                report.FromUtc <= delivery.OccurredAt && delivery.OccurredAt == report.OccurredAt &&
                report.OccurredAt < report.Cutoff && report.Cutoff <= report.AsOf && report.AsOf <= DayTwo);
            checks.Equal(snapshot, delivery.TripCode + " reconciled quantity", "EA", delivery.Quantity, report.Quantity);
            checks.Equal(snapshot, delivery.TripCode + " reconciled gross", "VND", delivery.Gross, report.Gross);
            checks.Equal(snapshot, delivery.TripCode + " reconciled AR", "VND", delivery.Ar, report.Ar);
            checks.Equal(snapshot, delivery.TripCode + " reconciled COGS", "VND", delivery.Cost, report.Cost);
        }

        void CheckClosureWork(Snapshot snapshot, Delivery? delivery)
        {
            var main = snapshot.ClosingWork.Where(row => row.TripCode == Origin).ToArray();
            checks.Equal(snapshot, "main immutable closure work", "rows", 2, main.Length);
            checks.Equal(snapshot, "main assignment history before successor", "rows", 2,
                snapshot.Assignments.Count(row => row.TripCode == Origin));
            foreach (var entry in main)
            {
                var assignment = Single(snapshot.Assignments, row => row.CommandId == entry.CommandId && row.FollowUpId == entry.FollowUpId);
                checks.Equal(snapshot, entry.SourceKind + " snapshot links exact assignment audit", "bool", true,
                    assignment is not null && assignment.SourceId == entry.SourceId && assignment.SourceKind == entry.SourceKind &&
                    assignment.Assignee == entry.Assignee && assignment.DueAt == entry.DueAt && assignment.Reason == entry.Reason &&
                    assignment.ActorId == App05CloseBootstrap.Ops && assignment.Reason.Length > 0);
                checks.Equal(snapshot, entry.SourceKind + " close assignee", "actor", App05CloseBootstrap.Assignee, entry.Assignee);
                checks.Equal(snapshot, entry.SourceKind + " close due", "ICT", DayTwo.AddHours(3), entry.DueAt);
            }
            checks.Equal(snapshot, "close follows exact remaining A line", "ID", delivery?.LineId,
                Single(main, row => row.SourceKind == "A_REMAINDER")?.SourceId);
            checks.Equal(snapshot, "close follows exact pending D.4 source", "ID", delivery?.SourceLineId,
                Single(main, row => row.SourceKind == "D4_RECONCILIATION")?.SourceId);
        }
    }

    internal static Comparison[] CompareUpgrade(Snapshot before, Snapshot after)
    {
        var checks = new Checks();
        checks.CompareOriginals(before, after);
        checks.Equal(after, "original stock preserved across host restart and NextDay", "row", before.Stock, after.Stock);
        checks.Equal(after, "original rights preserved across host restart and NextDay", "JSON",
            JsonSerializer.Serialize(before.Rights), JsonSerializer.Serialize(after.Rights));
        checks.Equal(after, "contrast tenant unchanged across host restart and NextDay", "SHA-256", before.PeerDigest, after.PeerDigest);
        checks.Equal(after, "NextDay does not create a successor binding", "rows", before.Bindings.Length, after.Bindings.Length);
        checks.Equal(after, "NextDay does not create another delivery", "rows", before.Deliveries.Length, after.Deliveries.Length);
        checks.Equal(after, "NextDay does not create a reconciliation report", "rows", before.Reports.Length, after.Reports.Length);
        return checks.Values();
    }

    private static T? Single<T>(IEnumerable<T> rows, Func<T, bool> predicate) where T : class
    {
        var found = rows.Where(predicate).Take(2).ToArray();
        return found.Length == 1 ? found[0] : null;
    }

    private sealed class Checks
    {
        private readonly List<Comparison> comparisons = [];

        internal void Equal<T>(Snapshot snapshot, string metric, string unit, T expected, T actual)
        {
            decimal? diff = expected is decimal e && actual is decimal a ? a - e : null;
            comparisons.Add(new(snapshot.Phase, metric, unit, expected, actual, diff, EqualityComparer<T>.Default.Equals(expected, actual)));
        }

        internal void CompareOriginals(Snapshot before, Snapshot after)
        {
            foreach (var confirm in before.Confirms)
                Equal(after, confirm.TripCode + " original Confirm, price and policy", "SHA-256", confirm.SourceDigest,
                    Single(after.Confirms, row => row.LineId == confirm.LineId)?.SourceDigest);
            foreach (var delivery in before.Deliveries)
                Equal(after, delivery.TripCode + " original delivery, ShipTo, price and ledger", "SHA-256", delivery.SourceDigest,
                    Single(after.Deliveries, row => row.Id == delivery.Id)?.SourceDigest);
            foreach (var trip in before.Trips.Where(row => row.ClosureId.HasValue))
            {
                var current = Single(after.Trips, row => row.TripId == trip.TripId);
                Equal(after, trip.Code + " original closure identity", "ID", trip.ClosureId, current?.ClosureId);
                Equal(after, trip.Code + " original closure snapshot", "SHA-256", trip.SnapshotDigest, current?.SnapshotDigest);
            }
            foreach (var row in before.Assignments)
                Equal(after, row.TripCode + " original assignment " + row.CommandId, "row", row,
                    Single(after.Assignments, current => current.EventId == row.EventId));
            Equal(after, "historical close work unchanged", "JSON", JsonSerializer.Serialize(before.ClosingWork),
                JsonSerializer.Serialize(after.ClosingWork.Where(row => before.Trips.Any(trip =>
                    trip.Code == row.TripCode && trip.ClosureId.HasValue))));
        }

        internal Comparison[] Values() => comparisons.ToArray();
    }

    private static string SourceSql(bool successor) => $$"""
        SET NOCOUNT ON;
        DECLARE @SkuId uniqueidentifier=(SELECT Id FROM dbo.Sku WHERE TenantId=@Tenant AND Code=@Sku);
        DECLARE @Trips TABLE (Id uniqueidentifier PRIMARY KEY,Code nvarchar(64) NOT NULL);
        INSERT @Trips SELECT Id,Code FROM dbo.Trip
        WHERE TenantId=@Tenant AND Code IN (@Origin,@Clean,@Zero,@Unused,@Successor,@ZeroSuccessor);

        SELECT SUM(CASE WHEN l.Kind='WAREHOUSE' AND l.Code='C2-MAIN' THEN b.OnHandBaseQuantity ELSE 0 END) AS MainQuantity,
            SUM(CASE WHEN l.Kind='WAREHOUSE' AND l.Code='C2-MAIN' THEN b.OnHandCostAmount ELSE 0 END) AS MainValue,
            SUM(CASE WHEN l.Kind='VEHICLE' THEN b.OnHandBaseQuantity ELSE 0 END) AS VehicleQuantity,
            SUM(CASE WHEN l.Kind='VEHICLE' THEN b.OnHandCostAmount ELSE 0 END) AS VehicleValue,
            (SELECT COALESCE(SUM(r.GrantedBaseQuantity-r.ConsumedBaseQuantity-r.ReleasedBaseQuantity),0)
                FROM dbo.StockRight r JOIN dbo.StockLocation w ON w.TenantId=r.TenantId AND w.Id=r.StockLocationId
                WHERE r.TenantId=@Tenant AND r.SkuId=@SkuId AND r.RightKind='A' AND w.Kind='WAREHOUSE'
                    AND w.Code='C2-MAIN') AS HeldQuantity,
            SUM(CASE WHEN l.Kind='WAREHOUSE' AND l.Code<>'C2-MAIN' THEN b.OnHandBaseQuantity ELSE 0 END) AS OtherWarehouseQuantity,
            SUM(CASE WHEN l.Kind='WAREHOUSE' AND l.Code<>'C2-MAIN' THEN b.OnHandCostAmount ELSE 0 END) AS OtherWarehouseValue,
            (SELECT COALESCE(SUM(r.GrantedBaseQuantity-r.ConsumedBaseQuantity-r.ReleasedBaseQuantity),0)
                FROM dbo.StockRight r JOIN dbo.StockLocation w ON w.TenantId=r.TenantId AND w.Id=r.StockLocationId
                WHERE r.TenantId=@Tenant AND r.SkuId=@SkuId AND r.RightKind='A' AND w.Kind='WAREHOUSE'
                    AND w.Code<>'C2-MAIN') AS OtherWarehouseHeld,
            CONVERT(varchar(64),HASHBYTES('SHA2_256',(
                SELECT JSON_QUERY((SELECT o.* FROM dbo.PhysicalStockOpening o
                        WHERE o.TenantId=@Tenant AND o.SkuId=@SkuId ORDER BY o.StockLocationId,o.SnapshotCode FOR JSON PATH)) AS Physical,
                    JSON_QUERY((SELECT o.* FROM dbo.F03LotOpening o JOIN dbo.F03Lot l ON l.TenantId=o.TenantId AND l.Id=o.LotId
                        WHERE o.TenantId=@Tenant AND l.SkuId=@SkuId ORDER BY o.LotId,o.LocationId FOR JSON PATH)) AS Lots
                FOR JSON PATH,WITHOUT_ARRAY_WRAPPER)),2) AS OpeningDigest
        FROM dbo.PhysicalStockBalance b JOIN dbo.StockLocation l ON l.TenantId=b.TenantId AND l.Id=b.StockLocationId
        WHERE b.TenantId=@Tenant AND b.SkuId=@SkuId;

        SELECT t.Id AS TripId,t.Code,t.Status,c.ClosureId,c.ClosedAt,c.OpenWorkCount,
            CONVERT(varchar(64),HASHBYTES('SHA2_256',c.Snapshot),2) AS SnapshotDigest,h.Id AS HandoffId,
            w.Code AS HandoffWarehouse,
            (SELECT COUNT(*) FROM dbo.TripManifestLine m WHERE m.TenantId=@Tenant AND m.TripId=t.Id) AS ManifestLines,
            (SELECT COUNT(*) FROM dbo.SaleDelivery d WHERE d.TenantId=@Tenant AND d.TripId=t.Id) AS DeliveryLines,
            (SELECT COUNT(*) FROM dbo.BusinessSource b WHERE b.TenantId=@Tenant AND b.TripId=t.Id) AS SourceRows,
            (SELECT COUNT(*) FROM dbo.PhysicalStockMovement p JOIN dbo.TripManifestLine m
                ON m.TenantId=p.TenantId AND m.Id=p.TripManifestLineId
                WHERE m.TenantId=@Tenant AND m.TripId=t.Id) AS MovementRows,
            ledger.LedgerRows,
            (SELECT COUNT(*) FROM dbo.App05TripCloseRejection r WHERE r.TenantId=@Tenant AND r.TripId=t.Id
                AND r.Code='TRIP_CLOSE_NOT_READY') AS ClosureRejections,
            (SELECT COUNT(*) FROM dbo.App05ZeroStockHandoffLine z WHERE z.TenantId=@Tenant AND z.HandoffId=h.Id) AS ZeroProofs,
            COALESCE((SELECT SUM(u.BaseQuantity) FROM dbo.D3TripUnloadLine u WHERE u.TenantId=@Tenant AND u.TripUnloadId=h.Id),0) AS ReturnedQuantity,
            COALESCE((SELECT SUM(u.CostAmount) FROM dbo.D3TripUnloadLine u WHERE u.TenantId=@Tenant AND u.TripUnloadId=h.Id),0) AS ReturnedValue,
            ledger.Custody,ledger.Fund
        FROM dbo.Trip t JOIN @Trips scope ON scope.Id=t.Id
        LEFT JOIN dbo.App05TripClosure c ON c.TenantId=t.TenantId AND c.TripId=t.Id
        LEFT JOIN dbo.D3TripUnload h ON h.TenantId=t.TenantId AND h.TripId=t.Id
        LEFT JOIN dbo.StockLocation w ON w.TenantId=h.TenantId AND w.Id=h.WarehouseLocationId AND w.Kind='WAREHOUSE'
        CROSS APPLY(
            SELECT COUNT(*) AS LedgerRows,
                COALESCE(SUM(CASE WHEN e.LedgerKind='CUSTODY' THEN e.AmountDelta ELSE 0 END),0) AS Custody,
                COALESCE(SUM(CASE WHEN e.LedgerKind='FUND' THEN e.AmountDelta ELSE 0 END),0) AS Fund
            FROM dbo.BusinessSource b JOIN dbo.BusinessSourceLine l ON l.TenantId=b.TenantId AND l.BusinessSourceId=b.Id
            JOIN dbo.LedgerEffect e ON e.TenantId=l.TenantId AND e.BusinessSourceLineId=l.Id
            WHERE b.TenantId=@Tenant AND b.TripId=t.Id
        ) ledger
        WHERE t.TenantId=@Tenant ORDER BY t.Code;

        SELECT o.Id AS OrderId,s.AOrderLineId AS LineId,s.ConfirmBusinessSourceId AS ConfirmSourceId,t.Code AS TripCode,
            sku.Code AS Sku,cu.Code AS Customer,o.Status,s.ConfirmedBaseQuantity AS Quantity,s.UnitPriceSnapshot AS UnitPrice,
            s.TermsDaysSnapshot AS Terms,
            CONVERT(varchar(64),HASHBYTES('SHA2_256',(
                SELECT JSON_QUERY((SELECT s.* FOR JSON PATH,WITHOUT_ARRAY_WRAPPER)) AS ConfirmedSnapshot,
                    JSON_QUERY((SELECT c.* FOR JSON PATH,WITHOUT_ARRAY_WRAPPER)) AS Confirmation,
                    JSON_QUERY((SELECT p.* FROM dbo.C2LinePolicy p WHERE p.TenantId=s.TenantId AND p.OrderLineId=s.AOrderLineId FOR JSON PATH)) AS Policy,
                    JSON_QUERY((SELECT p.* FROM dbo.F04PriceLine p WHERE p.TenantId=s.TenantId AND p.OrderLineId=s.AOrderLineId FOR JSON PATH)) AS Pricing,
                    JSON_QUERY((SELECT h.* FROM dbo.ShipToSnapshot h WHERE h.TenantId=c.TenantId AND h.Id=c.ShipToSnapshotId FOR JSON PATH)) AS ShipTo,
                    JSON_QUERY((SELECT b.* FROM dbo.BusinessSource b WHERE b.TenantId=s.TenantId AND b.Id=s.ConfirmBusinessSourceId FOR JSON PATH)) AS ConfirmSource
                FOR JSON PATH,WITHOUT_ARRAY_WRAPPER)),2) AS SourceDigest
        FROM dbo.AConfirmedLineSnapshot s JOIN @Trips t ON t.Id=s.TripId
        JOIN dbo.C2Confirmation c ON c.TenantId=s.TenantId AND c.OrderId=s.AOrderId AND c.ConfirmSourceId=s.ConfirmBusinessSourceId
        JOIN dbo.AOrder o ON o.TenantId=s.TenantId AND o.Id=s.AOrderId AND o.TripId=s.TripId
        JOIN dbo.Sku sku ON sku.TenantId=s.TenantId AND sku.Id=s.SkuId
        JOIN dbo.Customer cu ON cu.TenantId=s.TenantId AND cu.Id=s.CustomerId
        WHERE s.TenantId=@Tenant ORDER BY t.Code,s.AOrderLineId;

        SELECT d.Id,d.AOrderId AS OrderId,d.AOrderLineId AS LineId,d.TripId,t.Code AS TripCode,sku.Code AS Sku,
            d.BusinessSourceId AS SourceId,d.BusinessSourceLineId AS SourceLineId,account.Hash AS SourceHash,
            d.ShipToSnapshotId AS ShipToId,d.StockRightId AS RightId,d.TripManifestLineId AS ManifestLineId,
            {{(successor ? "d.App05BindingId AS BindingId,d.App05OriginTripId AS OriginTripId,d.App05HeldExecutorId AS HeldExecutorId,"
                : "CAST(NULL AS uniqueidentifier) AS BindingId,CAST(NULL AS uniqueidentifier) AS OriginTripId,CAST(NULL AS uniqueidentifier) AS HeldExecutorId,")}}
            d.BusinessDate,TODATETIMEOFFSET(b.OccurredAtUtc,'+00:00') AS OccurredAt,
            d.BaseQuantity AS Quantity,d.PayableAmount AS Gross,d.CostAmount AS Cost,ar.OriginalAmount AS Ar,
            ar.AppliedAmount AS ArApplied,ar.DueBusinessDate AS Due,m.LoadedBaseQuantity AS LoadedQuantity,m.LoadedCostAmount AS LoadedValue,
            lots.LotIssued,lots.MatchedLotLoaded,lots.LotCode,lots.LotIssueRows,
            receipt.Id AS ReceiptId,posting.Id AS PostingId,posting.Quantity AS PostedQuantity,
            posting.Gross AS PostedGross,posting.Ar AS PostedAr,posting.Cost AS PostedCost,
            (SELECT COUNT(*) FROM dbo.LedgerEffect e WHERE e.TenantId=d.TenantId AND e.BusinessSourceLineId=d.BusinessSourceLineId
                AND ((e.EffectKey='PHYSICAL-OUT' AND e.LedgerKind='PHYSICAL_STOCK'
                    AND e.QuantityDelta=-d.BaseQuantity AND e.AmountDelta=-d.CostAmount
                    AND e.SkuId=d.SkuId AND e.StockLocationId=m.VehicleLocationId)
                OR (e.EffectKey='COGS' AND e.LedgerKind='COGS' AND e.AmountDelta=d.CostAmount AND e.SkuId=d.SkuId)
                OR (e.EffectKey='AR-GROSS' AND e.LedgerKind='AR' AND e.AmountDelta=d.PayableAmount AND e.CustomerId=d.CustomerId)
                OR (e.EffectKey='RIGHT-A-USED' AND e.LedgerKind='RIGHT_A_CONSUMED'
                    AND e.QuantityDelta=-d.BaseQuantity AND e.SkuId=d.SkuId))) AS ExactLedgerLegs,
            (SELECT COUNT(*) FROM dbo.LedgerEffect e WHERE e.TenantId=d.TenantId
                AND e.BusinessSourceLineId=d.BusinessSourceLineId) AS LedgerRows,
            (SELECT COUNT(*) FROM dbo.DeliveryStockIssue i WHERE i.TenantId=d.TenantId AND i.SaleDeliveryId=d.Id
                AND i.BusinessSourceLineId=d.BusinessSourceLineId AND i.TripManifestLineId=d.TripManifestLineId
                AND i.BaseQuantity=d.BaseQuantity AND i.CostAmount=d.CostAmount) AS StockIssueRows,
            (SELECT COUNT(*) FROM dbo.BusinessCommand q JOIN dbo.C4DeliveryEvidence e ON e.TenantId=q.TenantId AND e.Id=q.Id
                WHERE q.TenantId=d.TenantId AND q.Id=b.BusinessCommandId AND q.Status='APPLIED' AND b.SourceKind='A_DELIVERY'
                    AND e.Id=d.C4EvidenceId AND e.TripId=d.TripId AND e.ManifestLineId=d.TripManifestLineId) AS AppliedSourceRows,
            CONVERT(varchar(64),HASHBYTES('SHA2_256',(
                SELECT JSON_QUERY((SELECT d.Id,d.TenantId,d.TripId,d.CustomerId,d.ShipToSnapshotId,d.TripManifestLineId,
                        d.LoadSourceLineId,d.StockRightId,d.RightKind,d.SkuId,d.AOrderId,d.AOrderLineId,d.BusinessSourceId,
                        d.BusinessSourceKind,d.BusinessSourceLineId,d.BaseQuantity,d.PayableAmount,d.CostAmount,d.CostBasisVersion,
                        d.BusinessDate,d.InvoiceEligible,d.PostedAtUtc,d.RowVersion,d.C4EvidenceId FOR JSON PATH,WITHOUT_ARRAY_WRAPPER)) AS Delivery,
                    JSON_QUERY((SELECT b.* FOR JSON PATH,WITHOUT_ARRAY_WRAPPER)) AS Source,
                    JSON_QUERY((SELECT l.* FROM dbo.BusinessSourceLine l WHERE l.TenantId=d.TenantId
                        AND l.Id=d.BusinessSourceLineId FOR JSON PATH)) AS SourceLine,
                    JSON_QUERY((SELECT h.* FROM dbo.ShipToSnapshot h WHERE h.TenantId=d.TenantId AND h.Id=d.ShipToSnapshotId FOR JSON PATH)) AS ShipTo,
                    JSON_QUERY((SELECT p.* FROM dbo.F04DeliveryPricing p WHERE p.TenantId=d.TenantId AND p.DeliveryId=d.Id FOR JSON PATH)) AS Pricing,
                    JSON_QUERY((SELECT a.* FROM dbo.ReceivableOpenItem a WHERE a.TenantId=d.TenantId AND a.SaleDeliveryId=d.Id FOR JSON PATH)) AS Receivable,
                    JSON_QUERY((SELECT f.* FROM dbo.F03LotIssue f WHERE f.TenantId=d.TenantId AND f.DeliveryId=d.Id ORDER BY f.LotId FOR JSON PATH)) AS Lots,
                    JSON_QUERY((SELECT e.* FROM dbo.LedgerEffect e WHERE e.TenantId=d.TenantId AND e.BusinessSourceLineId=d.BusinessSourceLineId
                        ORDER BY e.EffectKey FOR JSON PATH)) AS Effects
                FOR JSON PATH,WITHOUT_ARRAY_WRAPPER)),2) AS SourceDigest
        FROM dbo.SaleDelivery d JOIN @Trips t ON t.Id=d.TripId
        JOIN dbo.BusinessSource b ON b.TenantId=d.TenantId AND b.Id=d.BusinessSourceId AND b.TripId=d.TripId
        JOIN dbo.Sku sku ON sku.TenantId=d.TenantId AND sku.Id=d.SkuId
        LEFT JOIN dbo.TripManifestLine m ON m.TenantId=d.TenantId AND m.Id=d.TripManifestLineId
            AND m.StockRightId=d.StockRightId AND m.LoadSourceLineId=d.LoadSourceLineId
        LEFT JOIN dbo.ReceivableOpenItem ar ON ar.TenantId=d.TenantId AND ar.SaleDeliveryId=d.Id
            AND ar.BusinessSourceLineId=d.BusinessSourceLineId
        LEFT JOIN d4app.CommittedSource account ON account.DeliveryId=d.Id AND account.SourceLineId=d.BusinessSourceLineId
        LEFT JOIN d4accounting.Receipt receipt ON receipt.DeliveryId=d.Id AND receipt.SourceLineId=d.BusinessSourceLineId
            AND receipt.Quantity=d.BaseQuantity AND receipt.Gross=d.PayableAmount AND receipt.Cost=d.CostAmount
        LEFT JOIN d4accounting.Posting posting ON posting.ReceiptId=receipt.Id AND posting.SourceLineId=d.BusinessSourceLineId
            AND posting.Kind='BASE' AND posting.Mapping='DELIVERY'
        CROSS APPLY(
            SELECT COUNT(*) AS LotIssueRows,COALESCE(SUM(i.Quantity),0) AS LotIssued,MAX(k.Code) AS LotCode,
                COALESCE(SUM(f.Quantity),0) AS MatchedLotLoaded
            FROM dbo.F03LotIssue i JOIN dbo.F03Lot k ON k.TenantId=i.TenantId AND k.Id=i.LotId
            LEFT JOIN dbo.F03LotLoad f ON f.TenantId=i.TenantId AND f.ManifestLineId=d.TripManifestLineId AND f.LotId=i.LotId
            WHERE i.TenantId=d.TenantId AND i.DeliveryId=d.Id
        ) lots
        WHERE d.TenantId=@Tenant AND d.RightKind='A' ORDER BY t.Code,d.Id;

        SELECT r.Id AS RightId,a.AOrderLineId AS LineId,r.BusinessSourceLineId AS SourceLineId,t.Code AS TripCode,
            r.GrantedBaseQuantity AS Granted,r.ConsumedBaseQuantity AS Consumed,r.ReleasedBaseQuantity AS Released,
            r.GrantedBaseQuantity-r.ConsumedBaseQuantity-r.ReleasedBaseQuantity AS Held,r.Status,
            COALESCE((SELECT SUM(h.Quantity) FROM dbo.F03LotHold h WHERE h.TenantId=r.TenantId AND h.StockRightId=r.Id),
                {{(successor ? """
                    (SELECT SUM(l.NeverLoaded+l.Returned) FROM dbo.App05AHeldTransferLot l
                        WHERE l.TenantId=r.TenantId AND l.SuccessorRightId=r.Id),
                    """ : "")}}0) AS LotHeld
        FROM dbo.StockRight r JOIN @Trips t ON t.Id=r.TripId
        JOIN dbo.AReservation a ON a.TenantId=r.TenantId AND a.StockRightId=r.Id
        WHERE r.TenantId=@Tenant ORDER BY t.Code,r.Id;

        {{(successor ? BindingSql : "")}}

        SELECT a.EventId,t.Code AS TripCode,a.FollowUpId,a.SourceKind,a.SourceId,a.Assignee,a.DueAt,a.NextAction,a.Reason,a.CommandId,a.ActorId
        FROM dbo.App05FollowUpAssignment a JOIN @Trips t ON t.Id=a.TripId
        WHERE a.TenantId=@Tenant ORDER BY t.Code,a.EventId;

        SELECT t.Code AS TripCode,w.FollowUpId,w.SourceKind,w.SourceId,w.Assignee,w.DueAt,w.CommandId,w.Reason
        FROM dbo.App05TripClosure c JOIN @Trips t ON t.Id=c.TripId
        CROSS APPLY OPENJSON(c.Snapshot,'$.work') WITH (
            FollowUpId uniqueidentifier '$.followUp.followUpId',SourceKind varchar(32) '$.sourceKind',
            SourceId uniqueidentifier '$.sourceId',Assignee nvarchar(128) '$.followUp.assignee',
            DueAt datetimeoffset(7) '$.followUp.dueAt',CommandId uniqueidentifier '$.followUp.commandId',
            Reason nvarchar(240) '$.followUp.reason'
        ) w WHERE c.TenantId=@Tenant ORDER BY t.Code,w.SourceKind,w.SourceId;

        SELECT r.ReportId,t.Code AS TripCode,r.DeliveryId,r.SourceLineId,r.SourceHash,r.ReceiptId,r.PostingId,r.Status,r.OccurredAt,
            h.FromUtc,h.Cutoff,h.AsOf,h.ExpectedCount,r.ActualQuantity AS Quantity,r.ActualGross AS Gross,r.ActualAr AS Ar,r.ActualCost AS Cost
        FROM d4app.AccountingReportLine r JOIN d4app.AccountingReport h ON h.Id=r.ReportId
        JOIN dbo.SaleDelivery d ON d.TenantId=@Tenant AND d.Id=r.DeliveryId AND d.BusinessSourceLineId=r.SourceLineId
        JOIN @Trips t ON t.Id=d.TripId ORDER BY t.Code,r.ReportId,r.DeliveryId;

        SELECT
            (SELECT COUNT(*) FROM dbo.App05ZeroStockHandoffLine z JOIN @Trips t ON t.Id=z.TripId AND t.Code=@Zero
                JOIN dbo.AConfirmedLineSnapshot s ON s.TenantId=z.TenantId AND s.TripId=z.TripId
                    AND s.AOrderLineId=z.OrderLineId AND s.ConfirmBusinessSourceId=z.ConfirmSourceId
                JOIN dbo.App05TripClosure c ON c.TenantId=z.TenantId AND c.TripId=z.TripId AND c.HandoffId=z.HandoffId
                WHERE z.TenantId=@Tenant AND z.HeldBaseQuantity=0 AND s.ConfirmedBaseQuantity=5) AS ExactZeroConfirmProofs,
            (SELECT COUNT(*) FROM dbo.StockRight r JOIN @Trips t ON t.Id=r.TripId AND t.Code=@Zero WHERE r.TenantId=@Tenant) AS ReserveRowsOnZeroOrigin,
            (SELECT COUNT(*) FROM dbo.StockRight r JOIN @Trips t ON t.Id=r.TripId AND t.Code=@Unused WHERE r.TenantId=@Tenant) AS RightRowsOnUnused,
            (SELECT COUNT(*) FROM dbo.App05FollowUpAssignment a JOIN @Trips t ON t.Id=a.TripId AND t.Code=@Unused
                WHERE a.TenantId=@Tenant) AS AssignmentsOnUnused,
            (SELECT COUNT(*) FROM dbo.CashReceipt r JOIN @Trips t ON t.Id=r.TripId WHERE r.TenantId=@Tenant) AS CashReceipts,
            (SELECT COUNT(*) FROM d4app.AccountingReportLine r
                JOIN dbo.SaleDelivery d ON d.TenantId=@Tenant AND d.Id=r.DeliveryId AND d.BusinessSourceLineId=r.SourceLineId
                JOIN @Trips t ON t.Id=d.TripId AND t.Code IN (@Successor,@ZeroSuccessor)
                WHERE EXISTS(SELECT 1 FROM d4app.AccountingReportLine old
                    JOIN dbo.SaleDelivery original ON original.TenantId=@Tenant AND original.Id=old.DeliveryId
                        AND original.BusinessSourceLineId=old.SourceLineId
                    JOIN @Trips origin ON origin.Id=original.TripId AND origin.Code IN (@Origin,@Clean)
                    WHERE old.ReportId=r.ReportId)) AS DayTwoLinesInDayOneReports,
            (SELECT COUNT(*) FROM dbo.StockLocation w WHERE w.TenantId=@Tenant AND w.Kind='WAREHOUSE' AND w.IsActive=1) AS ActiveWarehouses,
            (SELECT COUNT(DISTINCT w.Id) FROM dbo.AConfirmedLineSnapshot z JOIN @Trips t ON t.Id=z.TripId AND t.Code=@Zero
                JOIN dbo.StockPool p ON p.TenantId=z.TenantId AND p.SkuId=z.SkuId
                JOIN dbo.StockLocation w ON w.TenantId=p.TenantId AND w.Id=p.StockLocationId
                WHERE z.TenantId=@Tenant AND w.Kind='WAREHOUSE' AND w.IsActive=1) AS WarehousesWithZeroConfirmSku;
        """;

    private const string BindingSql = """
        SELECT b.BindingId,b.FollowUpId,b.OrderLineId AS LineId,b.ConfirmSourceId,o.Code AS OriginTripCode,x.Code AS DestinationTripCode,
            b.Quantity,h.OriginalRightId,h.SuccessorRightId AS HeldRightId,r.RightId AS ReserveRightId,h.NeverLoaded,h.Returned,
            COALESCE(l.Quantity,0) AS TransferLotQuantity,l.Code AS TransferLotCode
        FROM dbo.App05ASuccessor b JOIN @Trips o ON o.Id=b.OriginTripId JOIN @Trips x ON x.Id=b.DestinationTripId
        LEFT JOIN dbo.App05AHeldTransfer h ON h.TenantId=b.TenantId AND h.BindingId=b.BindingId
        LEFT JOIN dbo.App05ASuccessorReserve r ON r.TenantId=b.TenantId AND r.BindingId=b.BindingId
        OUTER APPLY(
            SELECT SUM(a.NeverLoaded+a.Returned) AS Quantity,MAX(k.Code) AS Code
            FROM dbo.App05AHeldTransferLot a JOIN dbo.F03Lot k ON k.TenantId=a.TenantId AND k.Id=a.LotId
            WHERE a.TenantId=b.TenantId AND a.SuccessorRightId=h.SuccessorRightId AND a.OriginalRightId=h.OriginalRightId
        ) l WHERE b.TenantId=@Tenant ORDER BY o.Code,x.Code,b.BindingId;
        """;

    private static string PeerSql(bool successor) => $$"""
        SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',(
            SELECT JSON_QUERY((SELECT * FROM dbo.BusinessCommand WHERE TenantId=@Tenant ORDER BY Id FOR JSON PATH)) AS Commands,
                JSON_QUERY((SELECT * FROM dbo.BusinessSource WHERE TenantId=@Tenant ORDER BY Id FOR JSON PATH)) AS Sources,
                JSON_QUERY((SELECT * FROM dbo.BusinessSourceLine WHERE TenantId=@Tenant ORDER BY Id FOR JSON PATH)) AS SourceLines,
                JSON_QUERY((SELECT * FROM dbo.LedgerEffect WHERE TenantId=@Tenant ORDER BY Id FOR JSON PATH)) AS Ledger,
                JSON_QUERY((SELECT * FROM dbo.PhysicalStockBalance WHERE TenantId=@Tenant ORDER BY StockLocationId,SkuId FOR JSON PATH)) AS Stock,
                JSON_QUERY((SELECT * FROM dbo.PhysicalStockOpening WHERE TenantId=@Tenant
                    ORDER BY StockLocationId,SkuId,SnapshotCode FOR JSON PATH)) AS OpeningStock,
                JSON_QUERY((SELECT * FROM dbo.F03LotOpening WHERE TenantId=@Tenant ORDER BY LotId,LocationId FOR JSON PATH)) AS OpeningLots,
                JSON_QUERY((SELECT * FROM dbo.StockRight WHERE TenantId=@Tenant ORDER BY Id FOR JSON PATH)) AS Rights,
                JSON_QUERY((SELECT Id,TripId,CustomerId,ShipToSnapshotId,StockRightId,BusinessSourceId,BusinessSourceLineId,
                    BaseQuantity,PayableAmount,CostAmount,RowVersion FROM dbo.SaleDelivery WHERE TenantId=@Tenant ORDER BY Id FOR JSON PATH)) AS Deliveries,
                JSON_QUERY((SELECT * FROM dbo.ReceivableOpenItem WHERE TenantId=@Tenant ORDER BY Id FOR JSON PATH)) AS Receivables,
                JSON_QUERY((SELECT * FROM dbo.App05TripClosure WHERE TenantId=@Tenant ORDER BY TripId FOR JSON PATH)) AS Closures,
                JSON_QUERY((SELECT * FROM dbo.App05FollowUpAssignment WHERE TenantId=@Tenant ORDER BY EventId FOR JSON PATH)) AS Work,
                {{(successor ? "(SELECT COUNT(*) FROM dbo.App05ASuccessor WHERE TenantId=@Tenant)" : "0")}} AS Bindings,
                JSON_QUERY((SELECT * FROM d4app.AccountingReportLine ORDER BY ReportId,SourceLineId FOR JSON PATH)) AS Reports
            FOR JSON PATH,WITHOUT_ARRAY_WRAPPER)),2);
        """;
}
