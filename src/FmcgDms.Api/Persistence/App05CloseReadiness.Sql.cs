namespace FmcgDms.Api.Persistence;

internal static partial class App05CloseReadiness
{
    private const string TripSql = """
        SELECT t.Id AS TripId,t.Code,t.BusinessDate,t.Status,u.Id AS UnloadId,
            CONVERT(bit,CASE WHEN t.Status<>'PREPARED'
                OR EXISTS(SELECT 1 FROM dbo.BusinessSource s WHERE s.TenantId=t.TenantId AND s.TripId=t.Id)
                OR EXISTS(SELECT 1 FROM dbo.TripManifest m WHERE m.TenantId=t.TenantId AND m.TripId=t.Id)
                OR EXISTS(SELECT 1 FROM dbo.C4DeliveryEvidence e WHERE e.TenantId=t.TenantId AND e.TripId=t.Id)
                OR EXISTS(SELECT 1 FROM dbo.StockRight r WHERE r.TenantId=t.TenantId AND r.TripId=t.Id)
                THEN 1 ELSE 0 END) AS Used
        FROM dbo.Trip t
        LEFT JOIN dbo.D3TripUnload u ON u.TenantId=t.TenantId AND u.TripId=t.Id
        WHERE t.TenantId=@Tenant
        """;

    private const string SourcesSql = """
        SELECT o.Id FROM dbo.C2OrderDraft o
        WHERE o.TenantId=@Tenant AND o.TripId=@Trip AND NOT EXISTS(
            SELECT 1 FROM dbo.C2Confirmation c WHERE c.TenantId=o.TenantId AND c.OrderId=o.Id)
        ORDER BY o.Id;

        SELECT e.Id FROM dbo.C4DeliveryEvidence e JOIN dbo.BusinessCommand c ON c.TenantId=e.TenantId AND c.Id=e.Id
        WHERE e.TenantId=@Tenant AND e.TripId=@Trip AND c.Status IN ('RECEIVED','PENDING')
        ORDER BY e.Id;

        SELECT s.AOrderId AS OrderId,s.AOrderLineId AS OrderLineId,s.ConfirmBusinessSourceId AS ConfirmSourceId,
            s.SkuId,k.Code AS SkuCode,k.BaseUom AS Unit,s.ConfirmedBaseQuantity AS Confirmed,
            COALESCE(r.Granted,0) AS Granted,COALESCE(r.Consumed,0) AS Consumed,COALESCE(r.Released,0) AS Released,
            COALESCE(r.Loaded,0) AS Loaded,COALESCE(d.Delivered,0) AS Delivered,COALESCE(r.Returned,0) AS Returned,
            CONVERT(bit,CASE WHEN p.OrderLineId IS NOT NULL AND c.ConfirmSourceId=s.ConfirmBusinessSourceId
                AND CONVERT(varbinary(48),k.BaseUom)=CONVERT(varbinary(48),p.BaseUom)
                AND COALESCE(r.Drift,0)=0 THEN 1 ELSE 0 END) AS Proven
        FROM dbo.AConfirmedLineSnapshot s
        JOIN dbo.Sku k ON k.TenantId=s.TenantId AND k.Id=s.SkuId
        LEFT JOIN dbo.C2Confirmation c ON c.TenantId=s.TenantId AND c.OrderId=s.AOrderId
        LEFT JOIN dbo.C2LinePolicy p ON p.TenantId=s.TenantId AND p.OrderLineId=s.AOrderLineId
        OUTER APPLY (
            SELECT SUM(v.GrantedBaseQuantity) AS Granted,SUM(v.ConsumedBaseQuantity) AS Consumed,
                SUM(v.ReleasedBaseQuantity) AS Released,SUM(x.Loaded) AS Loaded,SUM(x.Returned) AS Returned,
                MAX(flags.Drift) AS Drift
            FROM dbo.AReservation a JOIN dbo.StockRight v ON v.TenantId=a.TenantId AND v.Id=a.StockRightId
            CROSS APPLY (
                SELECT COALESCE(SUM(m.LoadedBaseQuantity),0) AS Loaded,COALESCE(SUM(u.BaseQuantity),0) AS Returned
                FROM dbo.TripManifestLine m LEFT JOIN dbo.D3TripUnloadLine u
                    ON u.TenantId=m.TenantId AND u.TripManifestLineId=m.Id
                WHERE m.TenantId=v.TenantId AND m.StockRightId=v.Id
            ) x
            CROSS APPLY (
                SELECT CASE WHEN v.TripId<>s.TripId OR v.SkuId<>s.SkuId OR x.Loaded>v.GrantedBaseQuantity
                    OR NOT EXISTS(SELECT 1 FROM dbo.StockLocation w
                        WHERE w.TenantId=v.TenantId AND w.Id=v.StockLocationId AND w.Kind='WAREHOUSE')
                    OR NOT EXISTS(
                        SELECT 1 FROM dbo.BusinessSourceLine l
                        JOIN dbo.BusinessSource b ON b.TenantId=l.TenantId AND b.Id=l.BusinessSourceId
                        JOIN dbo.BusinessCommand q ON q.TenantId=b.TenantId AND q.Id=b.BusinessCommandId
                        JOIN dbo.C2CommandScope cs ON cs.TenantId=q.TenantId AND cs.CommandRowId=q.Id
                        WHERE l.TenantId=v.TenantId AND l.Id=v.BusinessSourceLineId AND l.SkuId=v.SkuId
                            AND l.BaseQuantity=v.GrantedBaseQuantity AND b.TripId=v.TripId AND b.CustomerId=s.CustomerId
                            AND b.SourceKind='STOCK_ALLOC' AND q.Status='APPLIED'
                            AND cs.RoleCode='warehouse' AND cs.LoadTripId IS NULL AND cs.D1GrantTripId IS NULL)
                    OR v.Status<>CASE WHEN v.GrantedBaseQuantity>v.ConsumedBaseQuantity+v.ReleasedBaseQuantity THEN 'HELD'
                           WHEN v.ReleasedBaseQuantity=0 THEN 'CONSUMED' ELSE 'RELEASED' END
                    THEN 1 ELSE 0 END AS Drift
            ) flags
            WHERE a.TenantId=s.TenantId AND a.AOrderLineId=s.AOrderLineId
        ) r
        OUTER APPLY (
            SELECT SUM(d.BaseQuantity) AS Delivered FROM dbo.SaleDelivery d
            WHERE d.TenantId=s.TenantId AND d.AOrderLineId=s.AOrderLineId AND d.RightKind='A'
        ) d
        WHERE s.TenantId=@Tenant AND s.TripId=@Trip
        ORDER BY s.AOrderId,s.AOrderLineId;

        SELECT a.Id FROM dbo.AOrder a JOIN dbo.AOrderLine l ON l.TenantId=a.TenantId AND l.AOrderId=a.Id
        WHERE a.TenantId=@Tenant AND a.TripId=@Trip
            AND NOT EXISTS(SELECT 1 FROM dbo.AConfirmedLineSnapshot s WHERE s.TenantId=l.TenantId AND s.AOrderLineId=l.Id)
        GROUP BY a.Id ORDER BY a.Id;

        SELECT d.Id AS DeliveryId,d.BusinessSourceLineId AS SourceId,q.OccurredAt,q.ReceiptId,q.PostingId,
            q.ReconciliationStatus,ISNULL(q.ReconciliationStale,CONVERT(bit,0)) AS ReconciliationStale,g.Id AS CaseId,
            q.ReportId,q.ReportFromUtc,q.ReportCutoff,q.ReportAsOf
        FROM dbo.SaleDelivery d
        LEFT JOIN d4app.FinanceQueueProvenance q ON q.DeliveryId=d.Id AND q.SourceLineId=d.BusinessSourceLineId
        OUTER APPLY (
            SELECT TOP(1) c.Id FROM d4app.FinanceCases c
            WHERE c.DeliveryId=d.Id AND c.SourceLineId=d.BusinessSourceLineId AND c.ClosedReportId IS NULL
            ORDER BY c.OccurredAt,c.Id
        ) g
        WHERE d.TenantId=@Tenant AND d.TripId=@Trip ORDER BY d.Id;

        SELECT d.Id AS DeliveryId,d.BusinessSourceLineId AS SourceId,ar.OutstandingAmount AS Amount,ar.DueBusinessDate,
            COALESCE(a.Currency,b.Currency,'SYNTHETIC_UNITLESS') AS Unit,
            CONVERT(bit,CASE WHEN ar.Id IS NOT NULL
                AND ar.AppliedAmount=COALESCE(cash.Applied,0)+COALESCE(f.Amount,0)
                AND EXISTS(SELECT 1 FROM dbo.LedgerEffect e WHERE e.TenantId=d.TenantId
                    AND e.BusinessSourceLineId=d.BusinessSourceLineId AND e.LedgerKind='AR'
                    AND e.EffectKey='AR-GROSS' AND e.AmountDelta=ar.OriginalAmount)
                THEN 1 ELSE 0 END) AS Proven
        FROM dbo.SaleDelivery d
        LEFT JOIN dbo.F04DeliveryPricing a ON a.TenantId=d.TenantId AND a.DeliveryId=d.Id
        LEFT JOIN dbo.App03BSalePricing b ON b.TenantId=d.TenantId AND b.SaleDeliveryId=d.Id
        LEFT JOIN dbo.ReceivableOpenItem ar ON ar.TenantId=d.TenantId AND ar.SaleDeliveryId=d.Id
        LEFT JOIN dbo.F08Settlement f ON f.TenantId=d.TenantId AND f.DeliveryId=d.Id
        OUTER APPLY (SELECT SUM(i.AppliedAmount) AS Applied FROM dbo.ReceiptAllocation i
            WHERE i.TenantId=ar.TenantId AND i.ReceivableOpenItemId=ar.Id) cash
        WHERE d.TenantId=@Tenant AND d.TripId=@Trip ORDER BY d.Id;

        SELECT f.SourceKind,f.SourceId,d.Id AS RelatedId,f.Label,f.Action,CAST(NULL AS uniqueidentifier) AS CaseId
        FROM dbo.SaleDelivery d
        CROSS APPLY (
            SELECT 'F05_RETURN' AS SourceKind,r.Id AS SourceId,
                CASE WHEN approval.SourceId IS NOT NULL THEN N'Trả hàng chờ QA'
                    WHEN receipt.SourceId IS NOT NULL THEN N'Trả hàng chờ duyệt' ELSE N'Trả hàng chờ nhận' END AS Label,
                N'Mở hồ sơ trả hàng và xử lý bước đang chờ' AS Action
            FROM dbo.F05ReturnCase r
            LEFT JOIN dbo.F05ReturnStage receipt ON receipt.TenantId=r.TenantId AND receipt.CaseId=r.Id AND receipt.Stage='RECEIVE'
            LEFT JOIN dbo.F05ReturnStage approval ON approval.TenantId=r.TenantId AND approval.CaseId=r.Id AND approval.Stage='APPROVE'
            WHERE r.TenantId=d.TenantId AND r.DeliveryId=d.Id AND NOT EXISTS(
                SELECT 1 FROM dbo.F05ReturnStage x WHERE x.TenantId=r.TenantId AND x.CaseId=r.Id AND x.Stage='RELEASE')
            UNION ALL
            SELECT 'F06_COST',p.Id,N'Điều chỉnh giá vốn',N'Mở đề nghị và kiểm chứng nguồn giá vốn'
            FROM dbo.F06CostProposal p WHERE p.TenantId=d.TenantId AND p.DeliveryId=d.Id AND NOT EXISTS(
                SELECT 1 FROM dbo.F06CostAdjustment x WHERE x.TenantId=p.TenantId AND x.DeliveryId=p.DeliveryId)
            UNION ALL
            SELECT 'F08_REFUND',r.Id,N'Hoàn tiền đang xử lý',N'Mở đề nghị hoàn tiền và xử lý bước đang chờ'
            FROM dbo.F08ReturnCredit c JOIN dbo.F08Refund r ON r.TenantId=c.TenantId AND r.CreditId=c.Id
            LEFT JOIN dbo.F08RefundApproval a ON a.TenantId=r.TenantId AND a.RefundId=r.Id
            OUTER APPLY (
                SELECT SUM(p.Amount) AS Amount FROM dbo.F08Refund proposal
                JOIN dbo.F08Disbursement p ON p.TenantId=proposal.TenantId AND p.RefundId=proposal.Id
                WHERE proposal.TenantId=c.TenantId AND proposal.CreditId=c.Id
            ) paid
            WHERE c.TenantId=d.TenantId AND c.DeliveryId=d.Id
                AND (a.Id IS NOT NULL OR r.Amount<=c.GrossAmount-COALESCE(paid.Amount,0))
                AND NOT EXISTS(
                SELECT 1 FROM dbo.F08Disbursement p WHERE p.TenantId=r.TenantId AND p.RefundId=r.Id)
        ) f
        WHERE d.TenantId=@Tenant AND d.TripId=@Trip ORDER BY f.SourceKind,f.SourceId;

        SELECT r.Id FROM dbo.StockRight r
        WHERE r.TenantId=@Tenant AND r.TripId=@Trip AND r.RightKind='B'
            AND r.GrantedBaseQuantity>r.ConsumedBaseQuantity+r.ReleasedBaseQuantity
        ORDER BY r.Id;

        SELECT m.Id FROM dbo.TripManifest m WHERE m.TenantId=@Tenant AND m.TripId=@Trip ORDER BY m.Id;
        """;

    private const string AssigneesSql = """
        SELECT a.ActorId FROM dbo.App05TripOperator a
        JOIN dbo.App02ActorRoleGrant g ON g.TenantId=a.TenantId AND g.ActorId=a.ActorId
        WHERE a.TenantId=@Tenant AND a.TripId=@Trip AND a.IsActive=1 AND g.IsActive=1 AND g.RoleCode='trip_assignee'
        ORDER BY a.ActorId;
        """;
}
