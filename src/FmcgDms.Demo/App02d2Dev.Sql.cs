using Dapper;

namespace FmcgDms.Demo;

internal sealed record D2Snapshot(decimal Main, decimal MainValue, decimal Truck, decimal TruckValue,
    decimal A, decimal B, decimal B10Remaining, decimal B20Remaining,
    decimal CreditGranted, decimal CreditConsumed, decimal CreditReleased,
    decimal ArGross, decimal ArApplied, decimal ArNet, decimal ArA, decimal Cogs,
    decimal Cash, decimal CashApplied, decimal Custody, decimal CustomerCredit, decimal Fund, decimal Advance,
    int Sales, int Issues, int OpenItems, int Receipts, int Allocations, int GrantUses, int InvoiceEligible, int Outbox)
{
    public decimal CreditRemaining => CreditGranted - CreditConsumed - CreditReleased;
}

internal sealed record D2Posting(D2PostingRow Row, D2Effect[] Ledger);
internal sealed record D2PostingRow(Guid Source, Guid SourceLine, Guid Delivery, Guid Customer, Guid ShipTo,
    Guid? Selection, Guid? Outlet, Guid ManifestLine, Guid LoadSourceLine, Guid Right, Guid Sku,
    decimal Quantity, decimal Payable, decimal Cost, decimal IssueQuantity, decimal IssueCost,
    decimal ArGross, decimal ArApplied, decimal ArNet, decimal Cash, decimal CashApplied, decimal Allocation,
    string? CashEvidence, decimal CreditApplied, decimal CreditIssued, decimal GrantConsumed, int GrantUses, Guid? Grant,
    Guid? ApprovedPolicy, string? PendingBank, decimal BankReported, Guid? Reservation, Guid? CreditOrigin,
    bool InvoiceEligible);

internal sealed record D2Effect(string Key, string Kind, decimal Quantity, decimal Amount,
    Guid? Customer, Guid? Sku, Guid? Location);

internal sealed record D2LoadOrigin(Guid ManifestLine, Guid SourceLine, Guid Movement, Guid Right, decimal Quantity,
    decimal Cost, decimal MainOutQuantity, decimal MainOutValue, decimal TruckInQuantity, decimal TruckInValue,
    bool D1Writer);

internal static class D2Sql
{
    internal static async Task<D2Snapshot> Read(DemoRun run, D2Scene s)
    {
        await using var reader = await App02c6Dev.ReadOnly(run, s.Tenant);
        return await reader.QuerySingleAsync<D2Snapshot>("""
            WITH sale AS (
                SELECT * FROM dbo.SaleDelivery WHERE TenantId=@Tenant AND TripId=@Trip
            ), effects AS (
                SELECT e.LedgerKind,e.AmountDelta FROM dbo.LedgerEffect e
                JOIN dbo.BusinessSourceLine l ON l.TenantId=e.TenantId AND l.Id=e.BusinessSourceLineId
                JOIN dbo.BusinessSource s ON s.TenantId=l.TenantId AND s.Id=l.BusinessSourceId
                WHERE s.TenantId=@Tenant AND s.TripId=@Trip
            ), rightBalances AS (
                SELECT r.RightKind,SUM(r.GrantedBaseQuantity-r.ReleasedBaseQuantity-r.ConsumedBaseQuantity) AS Amount
                FROM dbo.StockRight r WHERE r.TenantId=@Tenant AND r.TripId=@Trip AND r.SkuId=@Sku
                GROUP BY r.RightKind
            ), lineBalances AS (
                SELECT m.Id,m.LoadedBaseQuantity-COALESCE(SUM(i.BaseQuantity),0) AS Amount
                FROM dbo.TripManifestLine m LEFT JOIN dbo.DeliveryStockIssue i
                    ON i.TenantId=m.TenantId AND i.TripManifestLineId=m.Id
                WHERE m.TenantId=@Tenant AND m.TripId=@Trip AND m.Id IN (@B10,@B20)
                GROUP BY m.Id,m.LoadedBaseQuantity
            ), ar AS (
                SELECT a.* FROM dbo.ReceivableOpenItem a
                JOIN sale s ON s.TenantId=a.TenantId AND s.Id=a.SaleDeliveryId
            ), cash AS (
                SELECT * FROM dbo.CashReceipt WHERE TenantId=@Tenant AND TripId=@Trip
            )
            SELECT m.OnHandBaseQuantity AS Main,m.OnHandCostAmount AS MainValue,
                   t.OnHandBaseQuantity AS Truck,t.OnHandCostAmount AS TruckValue,
                   COALESCE((SELECT Amount FROM rightBalances WHERE RightKind='A'),0) AS A,
                   COALESCE((SELECT Amount FROM rightBalances WHERE RightKind='B'),0) AS B,
                   COALESCE((SELECT Amount FROM lineBalances WHERE Id=@B10),0) AS B10Remaining,
                   COALESCE((SELECT Amount FROM lineBalances WHERE Id=@B20),0) AS B20Remaining,
                   COALESCE((SELECT SUM(GrantedAmount) FROM dbo.BCreditGrant WHERE TenantId=@Tenant AND TripId=@Trip),0) AS CreditGranted,
                   COALESCE((SELECT SUM(ConsumedAmount) FROM dbo.BCreditGrant WHERE TenantId=@Tenant AND TripId=@Trip),0) AS CreditConsumed,
                   COALESCE((SELECT SUM(ReleasedAmount) FROM dbo.BCreditGrant WHERE TenantId=@Tenant AND TripId=@Trip),0) AS CreditReleased,
                   COALESCE((SELECT SUM(OriginalAmount) FROM ar WHERE RightKind='B'),0) AS ArGross,
                   COALESCE((SELECT SUM(AppliedAmount) FROM ar WHERE RightKind='B'),0) AS ArApplied,
                   COALESCE((SELECT SUM(OutstandingAmount) FROM ar WHERE RightKind='B'),0) AS ArNet,
                   COALESCE((SELECT SUM(OutstandingAmount) FROM ar WHERE RightKind='A'),0) AS ArA,
                   COALESCE((SELECT SUM(AmountDelta) FROM effects WHERE LedgerKind='COGS'),0) AS Cogs,
                   COALESCE((SELECT SUM(ReceivedAmount) FROM cash),0) AS Cash,
                   COALESCE((SELECT SUM(AppliedAmount) FROM cash),0) AS CashApplied,
                   COALESCE((SELECT SUM(AmountDelta) FROM effects WHERE LedgerKind='CUSTODY'),0) AS Custody,
                   COALESCE((SELECT SUM(AmountDelta) FROM effects WHERE LedgerKind='CUSTOMER_CREDIT'),0) AS CustomerCredit,
                   COALESCE((SELECT SUM(AmountDelta) FROM effects WHERE LedgerKind='FUND'),0) AS Fund,
                   COALESCE((SELECT SUM(AmountDelta) FROM effects WHERE LedgerKind='ADVANCE'),0) AS Advance,
                   (SELECT COUNT(*) FROM sale) AS Sales,
                   (SELECT COUNT(*) FROM dbo.DeliveryStockIssue i JOIN sale s ON s.TenantId=i.TenantId AND s.Id=i.SaleDeliveryId) AS Issues,
                   (SELECT COUNT(*) FROM ar) AS OpenItems,(SELECT COUNT(*) FROM cash) AS Receipts,
                   (SELECT COUNT(*) FROM dbo.ReceiptAllocation a JOIN cash c ON c.TenantId=a.TenantId AND c.Id=a.CashReceiptId) AS Allocations,
                   (SELECT COUNT(*) FROM dbo.BGrantConsumption WHERE TenantId=@Tenant AND TripId=@Trip) AS GrantUses,
                   (SELECT COUNT(*) FROM sale WHERE InvoiceEligible=1) AS InvoiceEligible,
                   (SELECT COUNT(*) FROM dbo.App02OutboxMessage WHERE TenantId=@Tenant) AS Outbox
            FROM dbo.PhysicalStockBalance m JOIN dbo.PhysicalStockBalance t
                ON t.TenantId=m.TenantId AND t.SkuId=m.SkuId
            WHERE m.TenantId=@Tenant AND m.SkuId=@Sku AND m.StockLocationId=@Main AND t.StockLocationId=@Truck;
            """, s);
    }

    internal static async Task<D2Posting> Source(DemoRun run, D2Scene s, Guid sourceId)
    {
        await using var reader = await App02c6Dev.ReadOnly(run, s.Tenant);
        var keys = new { s.Tenant, s.Trip, sourceId };
        var row = await reader.QuerySingleAsync<D2PostingRow>("""
            SELECT bs.Id AS Source,l.Id AS SourceLine,d.Id AS Delivery,d.CustomerId AS Customer,
                   d.ShipToSnapshotId AS ShipTo,d.D2SelectionId AS Selection,c1.OutletId AS Outlet,
                   d.TripManifestLineId AS ManifestLine,d.LoadSourceLineId AS LoadSourceLine,
                   d.StockRightId AS [Right],d.SkuId AS Sku,d.BaseQuantity AS Quantity,d.PayableAmount AS Payable,
                   d.CostAmount AS Cost,i.BaseQuantity AS IssueQuantity,i.CostAmount AS IssueCost,
                   a.OriginalAmount AS ArGross,a.AppliedAmount AS ArApplied,a.OutstandingAmount AS ArNet,
                   COALESCE(r.ReceivedAmount,0) AS Cash,COALESCE(r.AppliedAmount,0) AS CashApplied,
                   COALESCE(ra.AppliedAmount,0) AS Allocation,r.VerificationEvidenceRef AS CashEvidence,
                   COALESCE(p.CreditApplied,0) AS CreditApplied,COALESCE(p.CreditIssued,0) AS CreditIssued,
                   COALESCE(credit.Consumed,0) AS GrantConsumed,credit.Uses AS GrantUses,
                   credit.GrantId AS [Grant],credit.ApprovalId AS ApprovedPolicy,p.PendingBankEvidenceRef AS PendingBank,
                   COALESCE(bank.ReportedAmount,0) AS BankReported,p.CreditReservationId AS Reservation,
                   origin.BusinessSourceId AS CreditOrigin,d.InvoiceEligible
            FROM dbo.BusinessSource bs
            JOIN dbo.BusinessSourceLine l ON l.TenantId=bs.TenantId AND l.BusinessSourceId=bs.Id
            JOIN dbo.SaleDelivery d ON d.TenantId=bs.TenantId AND d.BusinessSourceId=bs.Id AND d.BusinessSourceLineId=l.Id
            JOIN dbo.DeliveryStockIssue i ON i.TenantId=d.TenantId AND i.SaleDeliveryId=d.Id
            JOIN dbo.ReceivableOpenItem a ON a.TenantId=d.TenantId AND a.SaleDeliveryId=d.Id
            LEFT JOIN dbo.C1ShipToSelection c1 ON c1.TenantId=d.TenantId AND c1.Id=d.D2SelectionId
            LEFT JOIN dbo.CashReceipt r ON r.TenantId=d.TenantId AND r.SaleDeliveryId=d.Id
            LEFT JOIN dbo.ReceiptAllocation ra ON ra.TenantId=r.TenantId AND ra.CashReceiptId=r.Id AND ra.ReceivableOpenItemId=a.Id
            LEFT JOIN dbo.BLocalSalePayment p ON p.TenantId=d.TenantId AND p.SaleDeliveryId=d.Id
            LEFT JOIN dbo.BLocalPendingBankEvidence bank ON bank.TenantId=p.TenantId AND bank.EvidenceRef=p.PendingBankEvidenceRef
            LEFT JOIN dbo.BLocalCreditReservation hold ON hold.TenantId=p.TenantId AND hold.Id=p.CreditReservationId
            LEFT JOIN dbo.SaleDelivery origin ON origin.TenantId=hold.TenantId AND origin.Id=hold.CreditSaleId
            OUTER APPLY (
                SELECT COUNT(*) AS Uses,SUM(u.ConsumedAmount) AS Consumed,
                    MAX(b.Id) AS GrantId,MAX(b.ApprovalId) AS ApprovalId
                FROM dbo.BGrantConsumption u JOIN dbo.BCreditGrant b
                    ON b.TenantId=u.TenantId AND b.Id=u.BCreditGrantId
                WHERE u.TenantId=d.TenantId AND u.SaleDeliveryId=d.Id
            ) credit
            WHERE bs.TenantId=@Tenant AND bs.TripId=@Trip AND bs.Id=@sourceId;
            """, keys);
        var ledger = (await reader.QueryAsync<D2Effect>("""
            SELECT EffectKey AS [Key],LedgerKind AS Kind,QuantityDelta AS Quantity,AmountDelta AS Amount,
                   CustomerId AS Customer,SkuId AS Sku,StockLocationId AS Location
            FROM dbo.LedgerEffect WHERE TenantId=@Tenant AND BusinessSourceLineId=@SourceLine ORDER BY EffectKey;
            """, new { s.Tenant, row.SourceLine })).ToArray();
        return new D2Posting(row, ledger);
    }


    internal static async Task<D2LoadOrigin> LoadOrigin(DemoRun run, D2Scene s, Guid manifestLine)
    {
        await using var reader = await App02c6Dev.ReadOnly(run, s.Tenant);
        return await reader.QuerySingleAsync<D2LoadOrigin>("""
            SELECT m.Id AS ManifestLine,ml.Id AS SourceLine,p.Id AS Movement,m.StockRightId AS [Right],
                   p.BaseQuantity AS Quantity,p.CostAmount AS Cost,po.QuantityDelta AS MainOutQuantity,
                   po.AmountDelta AS MainOutValue,pi.QuantityDelta AS TruckInQuantity,pi.AmountDelta AS TruckInValue,
                   c.IsD1Load AS D1Writer
            FROM dbo.TripManifestLine m
            JOIN dbo.PhysicalStockMovement p ON p.TenantId=m.TenantId AND p.TripManifestLineId=m.Id
            JOIN dbo.BusinessSourceLine ml ON ml.TenantId=p.TenantId AND ml.Id=p.LoadSourceLineId
            JOIN dbo.BusinessSource bs ON bs.TenantId=ml.TenantId AND bs.Id=ml.BusinessSourceId AND bs.TripId=m.TripId
            JOIN dbo.C2CommandScope c ON c.TenantId=bs.TenantId AND c.CommandRowId=bs.BusinessCommandId
                AND c.RoleCode='warehouse' AND c.LoadTripId=m.TripId
            JOIN dbo.LedgerEffect po ON po.TenantId=ml.TenantId AND po.BusinessSourceLineId=ml.Id
                AND po.EffectKey='PHYSICAL-OUT' AND po.StockLocationId=@Main
            JOIN dbo.LedgerEffect pi ON pi.TenantId=ml.TenantId AND pi.BusinessSourceLineId=ml.Id
                AND pi.EffectKey='PHYSICAL-IN' AND pi.StockLocationId=@Truck
            WHERE m.TenantId=@Tenant AND m.TripId=@Trip AND m.Id=@manifestLine;
            """, new { s.Tenant, s.Trip, s.Main, s.Truck, manifestLine });
    }

    internal static async Task<int> CommandRows(DemoRun run, D2Scene s, string producer, string command)
    {
        await using var reader = await App02c6Dev.ReadOnly(run, s.Tenant);
        return await reader.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM dbo.BusinessCommand WHERE TenantId=@Tenant AND ProducerId=@producer AND CommandId=@command;
            """, new { s.Tenant, producer, command });
    }

    internal static async Task<Guid?> CommandSource(DemoRun run, D2Scene s, string producer, string command)
    {
        await using var reader = await App02c6Dev.ReadOnly(run, s.Tenant);
        return await reader.QuerySingleOrDefaultAsync<Guid?>("""
            SELECT s.Id FROM dbo.BusinessCommand c JOIN dbo.BusinessSource s
                ON s.TenantId=c.TenantId AND s.BusinessCommandId=c.Id AND s.TripId=@Trip
            WHERE c.TenantId=@Tenant AND c.ProducerId=@producer AND c.CommandId=@command;
            """, new { s.Tenant, s.Trip, producer, command });
    }

    // The protected business state excludes terminal command and security audit rows.
    // Use a fixed table/ordering list so hashes cover sources and effects, not only balances.
    internal static async Task<string> Fingerprint(DemoRun run, string tenant)
    {
        await using var reader = await App02c6Dev.ReadOnly(run, tenant);
        return await reader.QuerySingleAsync<string>("""
            SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONCAT(
              (SELECT * FROM dbo.BusinessSource ORDER BY Id FOR JSON PATH,INCLUDE_NULL_VALUES),
              (SELECT * FROM dbo.BusinessSourceLine ORDER BY Id FOR JSON PATH,INCLUDE_NULL_VALUES),
              (SELECT * FROM dbo.StockPool ORDER BY StockLocationId,SkuId FOR JSON PATH,INCLUDE_NULL_VALUES),
              (SELECT * FROM dbo.StockRight ORDER BY Id FOR JSON PATH,INCLUDE_NULL_VALUES),
              (SELECT * FROM dbo.BQuota ORDER BY Id FOR JSON PATH,INCLUDE_NULL_VALUES),
              (SELECT * FROM dbo.BCreditPool ORDER BY TripId,CustomerId FOR JSON PATH,INCLUDE_NULL_VALUES),
              (SELECT * FROM dbo.BCreditGrant ORDER BY Id FOR JSON PATH,INCLUDE_NULL_VALUES),
              (SELECT * FROM dbo.CreditPolicyApproval ORDER BY Id FOR JSON PATH,INCLUDE_NULL_VALUES),
              (SELECT * FROM dbo.TripManifest ORDER BY Id FOR JSON PATH,INCLUDE_NULL_VALUES),
              (SELECT * FROM dbo.TripManifestLine ORDER BY Id FOR JSON PATH,INCLUDE_NULL_VALUES),
              (SELECT * FROM dbo.PhysicalStockBalance ORDER BY StockLocationId,SkuId FOR JSON PATH,INCLUDE_NULL_VALUES),
              (SELECT * FROM dbo.PhysicalStockMovement ORDER BY Id FOR JSON PATH,INCLUDE_NULL_VALUES),
              (SELECT Id,TenantId,TripId,CustomerId,ShipToSnapshotId,TripManifestLineId,LoadSourceLineId,
                StockRightId,RightKind,SkuId,AOrderId,AOrderLineId,BusinessSourceId,BusinessSourceKind,
                BusinessSourceLineId,BaseQuantity,PayableAmount,CostAmount,CostBasisVersion,BusinessDate,
                InvoiceEligible,PostedAtUtc,RowVersion FROM dbo.SaleDelivery ORDER BY Id FOR JSON PATH,INCLUDE_NULL_VALUES),
              (SELECT * FROM dbo.DeliveryStockIssue ORDER BY Id FOR JSON PATH,INCLUDE_NULL_VALUES),
              (SELECT * FROM dbo.ReceivableOpenItem ORDER BY Id FOR JSON PATH,INCLUDE_NULL_VALUES),
              (SELECT * FROM dbo.CashReceipt ORDER BY Id FOR JSON PATH,INCLUDE_NULL_VALUES),
              (SELECT * FROM dbo.ReceiptAllocation ORDER BY Id FOR JSON PATH,INCLUDE_NULL_VALUES),
              (SELECT * FROM dbo.BGrantConsumption ORDER BY Id FOR JSON PATH,INCLUDE_NULL_VALUES),
              (SELECT * FROM dbo.BLocalSalePayment ORDER BY SaleDeliveryId FOR JSON PATH,INCLUDE_NULL_VALUES),
              (SELECT * FROM dbo.BLocalCreditReservation ORDER BY Id FOR JSON PATH,INCLUDE_NULL_VALUES),
              (SELECT * FROM dbo.VerifiedCashEvidence ORDER BY EvidenceRef FOR JSON PATH,INCLUDE_NULL_VALUES),
              (SELECT * FROM dbo.BLocalPendingBankEvidence ORDER BY EvidenceRef FOR JSON PATH,INCLUDE_NULL_VALUES),
              (SELECT * FROM dbo.App02OutboxMessage ORDER BY MessageId FOR JSON PATH,INCLUDE_NULL_VALUES),
              (SELECT * FROM dbo.LedgerEffect ORDER BY Id FOR JSON PATH,INCLUDE_NULL_VALUES))),2);
            """);
    }
}
