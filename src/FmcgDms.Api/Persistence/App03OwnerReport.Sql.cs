namespace FmcgDms.Api.Persistence;

internal static partial class App03OwnerReport
{
    private static readonly string ReportSql = $$"""
        WITH Advance AS (
            {{App02c1Service.AdvanceSummarySql}}
        ), Delivery AS (
            SELECT d.*, CAST(COALESCE(a.Currency,b.Currency,'SYNTHETIC_UNITLESS') AS varchar(32)) AS Unit
            FROM dbo.SaleDelivery d
            LEFT JOIN dbo.F04DeliveryPricing a ON a.TenantId=d.TenantId AND a.DeliveryId=d.Id
            LEFT JOIN dbo.App03BSalePricing b ON b.TenantId=d.TenantId AND b.SaleDeliveryId=d.Id
            WHERE d.TenantId=@Tenant
        ), Effects AS (
            SELECT e.*, l.BusinessSourceId, s.TripId, s.SourceKind
            FROM dbo.LedgerEffect e
            JOIN dbo.BusinessSourceLine l ON l.TenantId=e.TenantId AND l.Id=e.BusinessSourceLineId
            JOIN dbo.BusinessSource s ON s.TenantId=l.TenantId AND s.Id=l.BusinessSourceId
            WHERE e.TenantId=@Tenant
        ), Stock AS (
            SELECT b.*, CASE loc.Kind WHEN 'WAREHOUSE' THEN 'main' WHEN 'VEHICLE' THEN 'vehicle' ELSE 'qa' END AS Scope,
                loc.Code, sku.Code AS Sku, sku.BaseUom
            FROM dbo.PhysicalStockBalance b
            JOIN dbo.StockLocation loc ON loc.TenantId=b.TenantId AND loc.Id=b.StockLocationId
            JOIN dbo.Sku sku ON sku.TenantId=b.TenantId AND sku.Id=b.SkuId
            WHERE b.TenantId=@Tenant
        ), Credits AS (
            SELECT c.*, c.GrossAmount-COALESCE(paid.Amount,0) AS Available
            FROM dbo.F08ReturnCredit c
            OUTER APPLY (
                SELECT SUM(r.Amount) AS Amount FROM dbo.F08Refund r
                JOIN dbo.F08Disbursement d ON d.TenantId=r.TenantId AND d.RefundId=r.Id
                WHERE r.TenantId=c.TenantId AND r.CreditId=c.Id
            ) paid
            WHERE c.TenantId=@Tenant
        ), ReturnCases AS (
            SELECT r.Id,r.DeliveryId,r.Quantity,r.LotId,rec.DestinationId AS QaId,rec.ReceivedQuantity,
                rel.DestinationId AS ReleaseLocationId, rel.SourceId AS ReleaseSourceId,
                CASE WHEN rel.SourceId IS NOT NULL THEN 'RELEASED' WHEN a.SourceId IS NOT NULL THEN 'AWAITING_QA'
                    WHEN rec.SourceId IS NOT NULL THEN 'AWAITING_APPROVAL' ELSE 'AWAITING_RECEIPT' END AS State
            FROM dbo.F05ReturnCase r
            LEFT JOIN dbo.F05ReturnStage rec ON rec.TenantId=r.TenantId AND rec.CaseId=r.Id AND rec.Stage='RECEIVE'
            LEFT JOIN dbo.F05ReturnStage a ON a.TenantId=r.TenantId AND a.CaseId=r.Id AND a.Stage='APPROVE'
            LEFT JOIN dbo.F05ReturnStage rel ON rel.TenantId=r.TenantId AND rel.CaseId=r.Id AND rel.Stage='RELEASE'
            WHERE r.TenantId=@Tenant
        ), LotDelta AS (
            SELECT o.LocationId,o.LotId,o.Quantity FROM dbo.F03LotOpening o WHERE o.TenantId=@Tenant
            UNION ALL
            SELECT x.LocationId,l.LotId,x.Quantity FROM dbo.F03LotLoad l
            JOIN dbo.TripManifestLine m ON m.TenantId=l.TenantId AND m.Id=l.ManifestLineId
            JOIN dbo.Trip t ON t.TenantId=m.TenantId AND t.Id=m.TripId
            JOIN dbo.StockRight r ON r.TenantId=m.TenantId AND r.Id=m.StockRightId
            CROSS APPLY (VALUES(r.StockLocationId,-l.Quantity),(t.VehicleLocationId,l.Quantity)) x(LocationId,Quantity)
            WHERE l.TenantId=@Tenant
            UNION ALL
            SELECT t.VehicleLocationId,i.LotId,-i.Quantity FROM dbo.F03LotIssue i
            JOIN Delivery d ON d.Id=i.DeliveryId AND d.TenantId=i.TenantId
            JOIN dbo.Trip t ON t.TenantId=d.TenantId AND t.Id=d.TripId WHERE i.TenantId=@Tenant
            UNION ALL
            SELECT x.LocationId,u.LotId,x.Quantity FROM dbo.App03LotUnload u
            JOIN dbo.TripManifestLine m ON m.TenantId=u.TenantId AND m.Id=u.ManifestLineId
            JOIN dbo.Trip t ON t.TenantId=m.TenantId AND t.Id=m.TripId
            JOIN dbo.StockRight r ON r.TenantId=m.TenantId AND r.Id=m.StockRightId
            CROSS APPLY (VALUES(r.StockLocationId,u.Quantity),(t.VehicleLocationId,-u.Quantity)) x(LocationId,Quantity)
            WHERE u.TenantId=@Tenant
            UNION ALL
            SELECT QaId,LotId,ReceivedQuantity FROM ReturnCases WHERE QaId IS NOT NULL AND ReleaseSourceId IS NULL
            UNION ALL
            SELECT ReleaseLocationId,LotId,Quantity FROM ReturnCases WHERE ReleaseSourceId IS NOT NULL
        ), Lots AS (
            SELECT x.LocationId,x.LotId,SUM(x.Quantity) AS Quantity FROM LotDelta x GROUP BY x.LocationId,x.LotId
        ), ReportRows AS (
            SELECT 'orders' AS Metric,'orders' AS Unit,d.Id,d.TripId AS RelatedId,t.Code+N' · '+c.Name AS Label,
                COALESCE(o.Status,'DRAFT') AS State,1 AS Value
            FROM dbo.C2OrderDraft d
            JOIN dbo.Trip t ON t.TenantId=d.TenantId AND t.Id=d.TripId
            JOIN dbo.Customer c ON c.TenantId=d.TenantId AND c.Id=d.CustomerId
            LEFT JOIN dbo.AOrder o ON o.TenantId=d.TenantId AND o.Id=d.Id
            WHERE d.TenantId=@Tenant
            UNION ALL
            SELECT 'confirm-pending','orders',d.Id,d.TripId,t.Code,IIF(c.OrderId IS NULL,'DRAFT','CONFIRMED'),IIF(c.OrderId IS NULL,1,0)
            FROM dbo.C2OrderDraft d JOIN dbo.Trip t ON t.TenantId=d.TenantId AND t.Id=d.TripId
            LEFT JOIN dbo.C2Confirmation c ON c.TenantId=d.TenantId AND c.OrderId=d.Id WHERE d.TenantId=@Tenant
            UNION ALL
            SELECT 'trips','trips',t.Id,t.VehicleLocationId,t.Code+N' · '+CONVERT(nvarchar(10),t.BusinessDate,23),t.Status,1
            FROM dbo.Trip t WHERE t.TenantId=@Tenant
            UNION ALL
            SELECT s.Scope+'-qty',s.BaseUom,s.StockLocationId,s.SkuId,s.Code+N' · '+s.Sku,'PHYSICAL',s.OnHandBaseQuantity FROM Stock s
            UNION ALL
            SELECT s.Scope+'-value','VND',s.StockLocationId,s.SkuId,s.Code+N' · '+s.Sku,'PHYSICAL',s.OnHandCostAmount FROM Stock s
            UNION ALL
            SELECT CASE loc.Kind WHEN 'WAREHOUSE' THEN 'main-lots' WHEN 'VEHICLE' THEN 'vehicle-lots' ELSE 'qa-lots' END,
                k.BaseUom,l.Id,x.LocationId,loc.Code+N' · '+k.Code+N' · '+l.Code+N' · '+CONVERT(nvarchar(10),l.ExpiresOn,23),
                CASE WHEN loc.Kind='QUARANTINE' THEN 'QA' WHEN l.ExpiresOn<CONVERT(date,DATEADD(hour,7,
                    {{App02c6Clock.SqlUtcNow}})) THEN 'EXPIRED' ELSE loc.Kind END,x.Quantity
            FROM Lots x JOIN dbo.F03Lot l ON l.TenantId=@Tenant AND l.Id=x.LotId
            JOIN dbo.StockLocation loc ON loc.TenantId=l.TenantId AND loc.Id=x.LocationId
            JOIN dbo.Sku k ON k.TenantId=l.TenantId AND k.Id=l.SkuId
            UNION ALL
            SELECT CASE r.RightKind WHEN 'A' THEN 'a-hold' ELSE 'b-quota' END,k.BaseUom,r.Id,r.TripId,
                t.Code+N' · '+k.Code,'RIGHT_REMAINING',r.GrantedBaseQuantity-r.ConsumedBaseQuantity-r.ReleasedBaseQuantity
            FROM dbo.StockRight r JOIN dbo.Trip t ON t.TenantId=r.TenantId AND t.Id=r.TripId
            JOIN dbo.Sku k ON k.TenantId=r.TenantId AND k.Id=r.SkuId WHERE r.TenantId=@Tenant
            UNION ALL
            SELECT 'ar-'+LOWER(d.RightKind),d.Unit,i.Id,d.Id,c.Name,
                CASE WHEN i.Id IS NULL THEN 'MISSING_OPEN_ITEM' WHEN proof.Matched=0 THEN 'RECONCILIATION_REQUIRED'
                    WHEN i.OutstandingAmount>0 AND i.DueBusinessDate<CONVERT(date,DATEADD(hour,7,
                    {{App02c6Clock.SqlUtcNow}})) THEN 'OVERDUE' ELSE 'OPEN_ITEM' END,
                CASE WHEN proof.Matched=1 THEN i.OutstandingAmount END
            FROM Delivery d JOIN dbo.Customer c ON c.TenantId=d.TenantId AND c.Id=d.CustomerId
            LEFT JOIN dbo.ReceivableOpenItem i ON i.TenantId=d.TenantId AND i.SaleDeliveryId=d.Id
            LEFT JOIN dbo.F08Settlement f ON f.TenantId=d.TenantId AND f.DeliveryId=d.Id
            OUTER APPLY (SELECT SUM(a.AppliedAmount) AS Applied FROM dbo.ReceiptAllocation a
                WHERE a.TenantId=i.TenantId AND a.ReceivableOpenItemId=i.Id) cash
            CROSS APPLY (SELECT IIF(i.AppliedAmount=COALESCE(cash.Applied,0)+COALESCE(f.Amount,0)
                AND EXISTS (SELECT 1 FROM Effects e WHERE e.BusinessSourceLineId=d.BusinessSourceLineId
                    AND e.LedgerKind='AR' AND e.EffectKey='AR-GROSS' AND e.AmountDelta=i.OriginalAmount),1,0) AS Matched) proof
            UNION ALL
            SELECT 'ar-a','VND',e.Id,e.ClientCommandId,t.Code,'AWAITING_DELIVERY_OUTCOME',NULL
            FROM dbo.C4DeliveryEvidence e JOIN dbo.BusinessCommand cmd ON cmd.TenantId=e.TenantId AND cmd.Id=e.Id
            JOIN dbo.Trip t ON t.TenantId=e.TenantId AND t.Id=e.TripId
            WHERE e.TenantId=@Tenant AND cmd.Status IN ('RECEIVED','PENDING')
            UNION ALL
            SELECT 'cogs',COALESCE(d.Unit,original.Unit,'SYNTHETIC_UNITLESS'),e.BusinessSourceId,COALESCE(d.Id,original.Id),
                e.EffectKey,e.SourceKind,e.AmountDelta
            FROM Effects e
            LEFT JOIN Delivery d ON d.BusinessSourceLineId=e.BusinessSourceLineId
            LEFT JOIN ReturnCases r ON r.ReleaseSourceId=e.BusinessSourceId
            LEFT JOIN dbo.F06CostAdjustment a ON a.TenantId=e.TenantId AND a.Id=e.BusinessSourceId
            LEFT JOIN Delivery original ON original.Id=COALESCE(r.DeliveryId,a.DeliveryId)
            WHERE e.LedgerKind='COGS'
            UNION ALL
            SELECT CASE e.LedgerKind WHEN 'CUSTODY' THEN 'custody' WHEN 'FUND' THEN 'fund' ELSE 'customer-credit' END,
                'VND',e.BusinessSourceId,e.TripId,e.EffectKey,e.SourceKind,e.AmountDelta
            FROM Effects e WHERE e.LedgerKind IN ('CUSTODY','FUND','CUSTOMER_CREDIT')
                AND e.EffectKey NOT IN ('F08-CREDIT-ISSUED','F08-CREDIT-PAID')
            UNION ALL
            SELECT 'advance',Currency,Id,NULL,EmployeeId,Evidence,Outstanding FROM Advance
            UNION ALL
            SELECT 'advance-pending','advances',Id,NULL,EmployeeId,Evidence,IIF(PendingReturns>0,1,0) FROM Advance
            UNION ALL
            SELECT 'advance-expense-pending',a.Currency,r.Id,a.Id,a.EmployeeId+N' · '+r.Evidence,
                CASE WHEN approved.Amount=r.Amount THEN 'APPROVED'
                     WHEN approved.Amount=0 THEN 'AWAITING_APPROVAL' ELSE 'PARTIALLY_APPROVED' END,
                r.Amount-approved.Amount
            FROM dbo.F02AdvanceEntry r
            JOIN dbo.F02Advance a ON a.TenantId=r.TenantId AND a.Id=r.AdvanceId
            CROSS APPLY (
                SELECT COALESCE(SUM(e.Amount),0) AS Amount FROM dbo.F02AdvanceEntry e
                WHERE e.TenantId=r.TenantId AND e.AdvanceId=r.AdvanceId
                    AND e.ReferenceId=r.Id AND e.Kind='EXPENSE_APPROVE'
            ) approved
            WHERE r.TenantId=@Tenant AND r.Kind='EXPENSE_REQUEST'
            UNION ALL
            SELECT 'return-credit','VND',Id,DeliveryId,'F08ReturnCredit',CONVERT(nvarchar(36),CaseId),Available FROM Credits
            UNION ALL
            SELECT 'refund-treasury','VND',NULL,NULL,'F08Treasury','AVAILABLE',AvailableAmount
            FROM dbo.F08Treasury WHERE TenantId=@Tenant
            UNION ALL
            SELECT 'refund-paid','VND',d.Id,r.Id,'F08Disbursement','DISBURSED',r.Amount
            FROM dbo.F08Disbursement d JOIN dbo.F08Refund r ON r.TenantId=d.TenantId AND r.Id=d.RefundId WHERE d.TenantId=@Tenant
            UNION ALL
            SELECT 'refund-pending','refunds',r.Id,r.CreditId,'F08Refund',
                CASE WHEN d.Id IS NOT NULL THEN 'DISBURSED' WHEN a.Id IS NOT NULL THEN 'APPROVED' ELSE 'PROPOSED' END,IIF(d.Id IS NULL,1,0)
            FROM dbo.F08Refund r LEFT JOIN dbo.F08RefundApproval a ON a.TenantId=r.TenantId AND a.RefundId=r.Id
            LEFT JOIN dbo.F08Disbursement d ON d.TenantId=r.TenantId AND d.RefundId=r.Id WHERE r.TenantId=@Tenant
            UNION ALL
            SELECT 'cash-pending','offers',o.Id,o.TripId,t.Code,IIF(a.Id IS NULL,'OFFERED','ACCEPTED'),IIF(a.Id IS NULL,1,0)
            FROM dbo.CashHandoverOffer o LEFT JOIN dbo.CashHandoverAcceptance a ON a.TenantId=o.TenantId AND a.OfferId=o.Id
            JOIN dbo.Trip t ON t.TenantId=o.TenantId AND t.Id=o.TripId WHERE o.TenantId=@Tenant
            UNION ALL
            SELECT 'return-pending','cases',Id,DeliveryId,'F05ReturnCase',State,IIF(ReleaseSourceId IS NULL,1,0) FROM ReturnCases
            UNION ALL
            SELECT 'cost-pending','cases',p.Id,p.DeliveryId,'F06CostProposal',IIF(a.Id IS NULL,'PROPOSED','APPLIED'),IIF(a.Id IS NULL,1,0)
            FROM dbo.F06CostProposal p LEFT JOIN dbo.F06CostAdjustment a ON a.TenantId=p.TenantId AND a.ProposalId=p.Id
            WHERE p.TenantId=@Tenant
            UNION ALL
            SELECT 'finance-pending','sources',d.Id,q.SourceLineId,q.Trip,
                CASE WHEN q.ReceiptId IS NULL THEN 'AWAITING_RECEIPT' WHEN q.PostingId IS NULL THEN 'AWAITING_POSTING'
                    WHEN q.ReconciliationStale=1 THEN 'STALE' ELSE COALESCE(q.ReconciliationStatus,'AWAITING_RECONCILIATION') END,
                IIF(q.ReceiptId IS NOT NULL AND q.PostingId IS NOT NULL
                    AND q.ReconciliationStatus='RECONCILED_OK' AND q.ReconciliationStale=0,0,1)
            FROM d4app.FinanceQueueProvenance q JOIN Delivery d ON d.Id=q.DeliveryId
            UNION ALL
            SELECT 'finance-cases','cases',c.Id,c.DeliveryId,'D4',c.Reason,IIF(c.ClosedReportId IS NULL,1,0)
            FROM d4app.FinanceCases c JOIN Delivery d ON d.Id=c.DeliveryId
        )
        SELECT r.Metric,r.Unit COLLATE DATABASE_DEFAULT AS Unit,r.Id,r.RelatedId,
            r.Label COLLATE DATABASE_DEFAULT AS Label,display.State,
            CONVERT(decimal(28,4),r.Value) AS Value,
            CASE WHEN r.Metric='finance-cases' AND r.Value=1 THEN c.OwnerRole
                WHEN r.Metric='return-pending' AND display.State='AWAITING_RECEIPT' THEN 'warehouse'
                WHEN r.Metric='return-pending' AND display.State='AWAITING_APPROVAL' THEN 'finance'
                WHEN r.Metric='return-pending' AND display.State='AWAITING_QA' THEN 'case_checker'
                WHEN r.Metric IN ('cash-pending','advance-pending') AND r.Value=1 THEN 'cashier'
                WHEN r.Metric='advance-expense-pending' AND r.Value>0 THEN 'finance'
                WHEN r.Metric='cost-pending' AND r.Value=1 THEN 'case_checker'
                WHEN r.Metric='refund-pending' AND display.State='PROPOSED' THEN 'refund-checker'
                WHEN r.Metric='refund-pending' AND display.State='APPROVED' THEN 'cashier / case_checker' END AS OwnerRole,
            CASE WHEN r.Metric='finance-cases' AND r.Value=1 THEN c.NextAction
                WHEN r.Metric='return-pending' AND display.State='AWAITING_RECEIPT' THEN N'Nhận vào cách ly'
                WHEN r.Metric='return-pending' AND display.State='AWAITING_APPROVAL' THEN N'Duyệt trả hàng'
                WHEN r.Metric='return-pending' AND display.State='AWAITING_QA' THEN N'Kiểm tra và giải phóng hàng'
                WHEN r.Metric='cash-pending' AND r.Value=1 THEN N'Kiểm đếm và nhận bàn giao'
                WHEN r.Metric='advance-pending' AND r.Value=1 THEN N'Nhận tiền hoàn ứng'
                WHEN r.Metric='advance-expense-pending' AND r.Value>0 THEN N'Duyệt khoản chi'
                WHEN r.Metric='cost-pending' AND r.Value=1 THEN N'Kiểm chứng tồn và duyệt điều chỉnh'
                WHEN r.Metric='refund-pending' AND display.State='PROPOSED' THEN N'Duyệt đề nghị hoàn tiền'
                WHEN r.Metric='refund-pending' AND display.State='APPROVED' THEN N'Xác minh chứng từ và ghi thực chi' END AS NextAction
        FROM ReportRows r LEFT JOIN d4app.AccountingCase c ON r.Metric='finance-cases' AND c.Id=r.Id
        CROSS APPLY (VALUES(r.State COLLATE DATABASE_DEFAULT)) display(State)
        ORDER BY r.Metric,Unit,Label,r.Id,State;
        """;

    private const string TripStockSql = """
        WITH VehicleLedger AS (
            SELECT t.Id AS TripId,e.SkuId,SUM(e.QuantityDelta) AS Quantity
            FROM dbo.Trip t
            JOIN dbo.BusinessSource s ON s.TenantId=t.TenantId AND s.TripId=t.Id
            JOIN dbo.BusinessSourceLine l ON l.TenantId=s.TenantId AND l.BusinessSourceId=s.Id
            JOIN dbo.LedgerEffect e ON e.TenantId=l.TenantId AND e.BusinessSourceLineId=l.Id
                AND e.StockLocationId=t.VehicleLocationId AND e.LedgerKind='PHYSICAL_STOCK'
            WHERE t.TenantId=@Tenant AND e.SkuId IS NOT NULL
            GROUP BY t.Id,e.SkuId
        ), Manifest AS (
            SELECT m.TripId,m.SkuId,SUM(m.LoadedBaseQuantity) AS Loaded,
                SUM(m.LoadedBaseQuantity-delivery.Quantity-COALESCE(u.BaseQuantity,0)) AS Remaining,
                MAX(CASE WHEN m.LoadedBaseQuantity>0 AND (v.Id IS NULL
                    OR m.LoadedBaseQuantity<delivery.Quantity+COALESCE(u.BaseQuantity,0)) THEN 1 ELSE 0 END) AS MissingProof
            FROM dbo.TripManifestLine m
            LEFT JOIN dbo.PhysicalStockMovement v ON v.TenantId=m.TenantId AND v.TripManifestLineId=m.Id
            LEFT JOIN dbo.D3TripUnloadLine u ON u.TenantId=m.TenantId AND u.TripManifestLineId=m.Id
            CROSS APPLY (
                SELECT CONVERT(decimal(18,3),COALESCE(SUM(d.BaseQuantity),0)) AS Quantity
                FROM dbo.SaleDelivery d WHERE d.TenantId=m.TenantId AND d.TripManifestLineId=m.Id
            ) delivery
            WHERE m.TenantId=@Tenant
            GROUP BY m.TripId,m.SkuId
        ), Expected AS (
            SELECT TripId,SkuId FROM dbo.StockRight WHERE TenantId=@Tenant
            UNION
            SELECT TripId,SkuId FROM Manifest
            UNION
            SELECT TripId,SkuId FROM VehicleLedger
        )
        SELECT t.Id AS TripId,k.Id AS SkuId,t.VehicleLocationId,t.Code+N' · '+k.Code AS Label,k.BaseUom AS Unit,
            CAST(CASE WHEN v.TripId IS NULL AND COALESCE(m.Loaded,0)=0 THEN 1 ELSE 0 END AS bit) AS NeverLoaded,
            CASE WHEN v.TripId IS NULL AND COALESCE(m.Loaded,0)=0 THEN 0
                 WHEN m.MissingProof=0 AND v.Quantity=m.Remaining THEN v.Quantity END AS Quantity,
            b.OnHandBaseQuantity AS VehicleQuantity,b.OnHandCostAmount AS VehicleCost,
            o.BaseQuantity+COALESCE(physical.Quantity,0) AS SourceQuantity,
            o.CostAmount+COALESCE(physical.Amount,0) AS SourceCost
        FROM Expected e
        JOIN dbo.Trip t ON t.TenantId=@Tenant AND t.Id=e.TripId
        JOIN dbo.Sku k ON k.TenantId=t.TenantId AND k.Id=e.SkuId
        LEFT JOIN Manifest m ON m.TripId=t.Id AND m.SkuId=k.Id
        LEFT JOIN VehicleLedger v ON v.TripId=t.Id AND v.SkuId=k.Id
        LEFT JOIN dbo.PhysicalStockBalance b ON b.TenantId=t.TenantId AND b.StockLocationId=t.VehicleLocationId AND b.SkuId=k.Id
        LEFT JOIN dbo.PhysicalStockOpening o ON o.TenantId=t.TenantId AND o.StockLocationId=t.VehicleLocationId AND o.SkuId=k.Id
        OUTER APPLY (
            SELECT CONVERT(decimal(28,3),SUM(l.QuantityDelta)) AS Quantity,
                CONVERT(decimal(28,4),SUM(l.AmountDelta)) AS Amount
            FROM dbo.LedgerEffect l
            WHERE l.TenantId=o.TenantId AND l.StockLocationId=o.StockLocationId AND l.SkuId=o.SkuId
                AND l.LedgerKind='PHYSICAL_STOCK'
        ) physical
        ORDER BY t.BusinessDate,t.Code,t.Id,k.Code;
        """;
}
