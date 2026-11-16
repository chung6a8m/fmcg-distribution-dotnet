using System.Security.Cryptography;
using Dapper;
using FmcgDms.Api.Persistence;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FmcgDms.Api.Tests;

/// <summary>
/// APP-01C.1 physical LocalDB schema contract only: direct SQL rows exercise
/// FK/uniqueness/immutability. This does NOT prove transactional T3/T4 posting.
/// </summary>
[Collection("SqlScratch")]
public sealed class App01c1PostingSchemaTests(SqlScratchFixture fixture)
{
    private const string A = "tenant-a";
    private const string B = "tenant-b";

    private sealed record Scene(
        Guid Customer, Guid ShipTo, Guid Sku, Guid Warehouse, Guid Vehicle,
        Guid Trip, Guid Order, Guid OrderLine, Guid ARight, Guid BRight,
        Guid AManifestLine, Guid BManifestLine, Guid ALoadLine, Guid BLoadLine,
        Guid ConfirmSource, Guid ConfirmLine);

    private sealed record SaleInput(
        Guid Id, string Tenant, Guid Trip, Guid Customer, Guid ShipTo,
        Guid ManifestLine, Guid LoadLine, Guid Right, string Kind, Guid Sku,
        Guid? Order, Guid? OrderLine, Guid Source, string SourceKind, Guid SourceLine,
        decimal Qty, decimal Payable, decimal Cost);

    private sealed record Sale(Guid Id, Guid SourceLine, SaleInput Input);

    private SqlConnection Open(string tenant) =>
        new(tenant == A ? fixture.TenantAConnectionString : fixture.TenantBConnectionString);

    private static string Code() => Guid.NewGuid().ToString("N");

    private static void Reject(SqlConnection db, string sql, object args, params int[] codes)
    {
        var ex = Assert.Throws<SqlException>(() => db.Execute(sql, args));
        Assert.Contains(ex.Number, codes);
    }

    [Fact]
    public void Two_nonce_tenants_have_repeatable_v5_schema_and_no_automatic_posting()
    {
        foreach (var (tenant, connection) in new[]
        {
            (A, fixture.TenantAConnectionString),
            (B, fixture.TenantBConnectionString)
        })
        {
            using var db = new SqlConnection(connection);
            foreach (var name in new[] {
                "AConfirmedLineSnapshot", "SaleDelivery", "DeliveryStockIssue",
                "ReceivableOpenItem", "CashReceipt", "ReceiptAllocation",
                "BGrantConsumption"
            })
                Assert.Equal(1, db.ExecuteScalar<int>("""
                    SELECT COUNT(*) FROM sys.tables
                    WHERE schema_id=SCHEMA_ID(N'dbo') AND name=@name
                    """, new { name }));
            Assert.Equal(8, db.ExecuteScalar<int>("""
                SELECT COUNT(*) FROM sys.triggers WHERE name IN
                    (N'TR_AConfirmedLineSnapshot_Immutable',N'TR_SaleDelivery_Immutable',
                     N'TR_DeliveryStockIssue_Immutable',N'TR_ReceiptAllocation_Immutable',
                     N'TR_BGrantConsumption_Immutable',N'TR_CashReceipt_VerifiedImmutable',
                     N'TR_ReceivableOpenItem_SourceImmutable',
                     N'TR_LedgerEffect_PostedDeliveryImmutable')
                """));
            Assert.Equal(1, db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM dbo.VersionInfo WHERE Version=202611160005"));
            Assert.Equal(1, db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM sys.foreign_keys WHERE name=N'FK_SaleDelivery_LoadedManifestLine'"));
            int postedBefore = db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SaleDelivery");
            int receiptsBefore = db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.CashReceipt");
            ScratchMigrator.Migrate(connection, tenant, fixture.Nonce);
            ScratchMigrator.Migrate(connection, tenant, fixture.Nonce);
            Assert.Equal(1, db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM dbo.VersionInfo WHERE Version=202611160005"));
            Assert.Equal(postedBefore, db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM dbo.SaleDelivery"));
            Assert.Equal(receiptsBefore, db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM dbo.CashReceipt"));
            Assert.Equal(tenant, db.ExecuteScalar<string>(
                "SELECT TenantId FROM dbo.AppTenantBinding WHERE Slot=1"));
        }
    }

    [Fact]
    public void A_confirmation_price_terms_and_lineage_snapshot_cannot_be_rewritten()
    {
        using var db = Open(A);
        var s = SeedLoadedTrip(db, A);
        InsertConfirmSnapshot(db, A, s);
        Assert.Equal((15m, 7), db.QuerySingle<(decimal, int)>("""
            SELECT UnitPriceSnapshot,TermsDaysSnapshot
            FROM dbo.AConfirmedLineSnapshot
            WHERE TenantId=@tenant AND AOrderLineId=@line
            """, new { tenant = A, line = s.OrderLine }));
        Reject(db, """
            UPDATE dbo.AConfirmedLineSnapshot SET UnitPriceSnapshot=19
            WHERE TenantId=@tenant AND AOrderLineId=@line
            """, new { tenant = A, line = s.OrderLine }, 51061);
        Reject(db, """
            DELETE dbo.AConfirmedLineSnapshot
            WHERE TenantId=@tenant AND AOrderLineId=@line
            """, new { tenant = A, line = s.OrderLine }, 51061);
        Reject(db, InsertSnapshotSql, new {
            tenant = A, line = Guid.NewGuid(), s.Order, s.Trip, s.Customer, s.Sku,
            source = s.ConfirmSource, sourceLine = s.ConfirmLine,
            qty = 40m, price = 15m, terms = 7
        }, 547); // Cannot attach a snapshot to a nonexistent A order line.
        // Changes to a mutable pre-existing order-line row never rewrite the
        // source-bound confirmed price (C.3 must consume the frozen snapshot).
        db.Execute("UPDATE dbo.AOrderLine SET UnitPrice=19 WHERE Id=@line",
            new { line = s.OrderLine });
        Assert.Equal(15m, db.ExecuteScalar<decimal>(
            "SELECT UnitPriceSnapshot FROM dbo.AConfirmedLineSnapshot WHERE AOrderLineId=@line",
            new { line = s.OrderLine }));
    }

    [Fact]
    public void B_delivery_and_stock_issue_require_exact_loaded_source_qty_cost_and_are_immutable()
    {
        using var db = Open(A);
        var s = SeedLoadedTrip(db, A);
        var b = InsertSale(db, A, s, "B", 20m, 300m, 200m);
        Guid stockIssue = InsertStockIssue(db, A, b);
        Assert.Equal((20m, 200m), db.QuerySingle<(decimal, decimal)>("""
            SELECT BaseQuantity,CostAmount FROM dbo.DeliveryStockIssue
            WHERE TenantId=@tenant AND Id=@id
            """, new { tenant = A, id = stockIssue }));

        Reject(db, InsertIssueSql, new {
            id = Guid.NewGuid(), tenant = A, delivery = b.Id,
            sourceLine = b.SourceLine, manifestLine = b.Input.ManifestLine, s.Sku,
            qty = 20m, cost = 200m
        }, 2627, 2601);
        var second = InsertSale(db, A, s, "B", 5m, 75m, 50m);
        Reject(db, InsertIssueSql, new {
            id = Guid.NewGuid(), tenant = A, delivery = second.Id,
            sourceLine = second.SourceLine, manifestLine = second.Input.ManifestLine, s.Sku,
            qty = 5m, cost = 51m
        }, 547);
        Reject(db, """
            UPDATE dbo.DeliveryStockIssue SET CostAmount=199 WHERE Id=@id
            """, new { id = stockIssue }, 51063);
        Reject(db, """
            UPDATE dbo.SaleDelivery SET PayableAmount=301 WHERE Id=@id
            """, new { id = b.Id }, 51062, 547);

        Guid effectId = Guid.NewGuid();
        db.Execute("""
            INSERT dbo.LedgerEffect(Id,TenantId,BusinessSourceLineId,
                EffectKey,LedgerKind,AmountDelta)
            VALUES(@id,@tenant,@line,N'AR-B-GROSS',N'AR_B',300)
            """, new { id = effectId, tenant = A, line = b.SourceLine });
        Reject(db, "UPDATE dbo.LedgerEffect SET AmountDelta=1 WHERE Id=@id",
            new { id = effectId }, 51068);
        Reject(db, "DELETE dbo.LedgerEffect WHERE Id=@id",
            new { id = effectId }, 51068);
    }

    [Fact]
    public void B_sale_rejects_A_manifest_line_wrong_trip_source_and_tenant()
    {
        using var db = Open(A);
        var s = SeedLoadedTrip(db, A);
        Guid source = NewSource(db, A, s.Trip, s.Customer, "B_SALE_DELIVERY");
        Guid line = NewLine(db, A, source, s.Sku, 20m, 300m);
        var input = MakeSale(A, s, "B", Guid.NewGuid(), source, line, 20m, 300m, 200m);
        Reject(db, InsertSaleSql, input with {
            ManifestLine = s.AManifestLine, LoadLine = s.ALoadLine
        }, 547);
        Reject(db, InsertSaleSql, input with { Right = s.ARight }, 547);
        Reject(db, InsertSaleSql, input with { Trip = Guid.NewGuid() }, 547);
        Reject(db, InsertSaleSql, input with { SourceKind = "A_DELIVERY" }, 547);
        Reject(db, InsertSaleSql, input with { Tenant = B }, 547);
        Reject(db, InsertSaleSql, input with { Qty = 21m }, 547);
        Reject(db, InsertSaleSql, input with { Payable = 301m }, 547);

        Assert.Equal(0, db.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM dbo.SaleDelivery WHERE TenantId=@tenant AND TripId=@trip
            """, new { tenant = A, trip = s.Trip }));
        db.Execute(InsertSaleSql, input);
        Reject(db, InsertSaleSql, input with { Id = Guid.NewGuid() }, 2627, 2601);
    }

    [Fact]
    public void A_delivery_must_bind_confirmed_order_line_and_due_date_not_B_pool()
    {
        using var db = Open(A);
        var s = SeedLoadedTrip(db, A);
        InsertConfirmSnapshot(db, A, s);
        var a = InsertSale(db, A, s, "A", 25m, 375m, 250m);
        InsertStockIssue(db, A, a);
        var ar = Guid.NewGuid();
        DateTime due = DateTime.UtcNow.Date.AddDays(7);
        db.Execute(InsertOpenItemSql, new {
            id = ar, tenant = A, delivery = a.Id, s.Customer,
            sourceLine = a.SourceLine, kind = "A", amount = 375m, due
        });
        Assert.Equal(375m, db.ExecuteScalar<decimal>(
            "SELECT OutstandingAmount FROM dbo.ReceivableOpenItem WHERE Id=@id",
            new { id = ar }));
        var nextA = InsertSale(db, A, s, "A", 5m, 75m, 50m);
        Reject(db, InsertOpenItemSql, new {
            id = Guid.NewGuid(), tenant = A, delivery = nextA.Id, s.Customer,
            sourceLine = nextA.SourceLine, kind = "A", amount = 75m, due = (DateTime?)null
        }, 547);
        Reject(db, InsertOpenItemSql, new {
            id = Guid.NewGuid(), tenant = A, delivery = nextA.Id, s.Customer,
            sourceLine = nextA.SourceLine, kind = "B", amount = 75m, due
        }, 547);
        var source = NewSource(db, A, s.Trip, s.Customer, "A_DELIVERY");
        var sourceLine = NewLine(db, A, source, s.Sku, 25m, 375m);
        var valid = MakeSale(A, s, "A", Guid.NewGuid(), source, sourceLine,
            25m, 375m, 250m);
        Reject(db, InsertSaleSql, valid with { OrderLine = Guid.NewGuid() }, 547);
        Reject(db, InsertSaleSql, valid with {
            ManifestLine = s.BManifestLine, LoadLine = s.BLoadLine, Right = s.BRight
        }, 547);
    }

    [Fact]
    public void B_verified_cash_receipt_allocation_and_AR_counters_preserve_source()
    {
        using var db = Open(A);
        var s = SeedLoadedTrip(db, A);
        var b = InsertSale(db, A, s, "B", 20m, 300m, 200m);
        Guid ar = Guid.NewGuid(), receipt = Guid.NewGuid(), allocation = Guid.NewGuid();
        db.Execute(InsertOpenItemSql, new {
            id = ar, tenant = A, delivery = b.Id, s.Customer,
            sourceLine = b.SourceLine, kind = "B", amount = 300m,
            due = (DateTime?)null
        });
        db.Execute(InsertReceiptSql, new {
            id = receipt, tenant = A, delivery = b.Id, s.Trip, s.Customer,
            sourceLine = b.SourceLine, amount = 200m, evidence = "synthetic-verified-cash"
        });
        db.Execute(InsertAllocationSql, new {
            id = allocation, tenant = A, receipt, ar, s.Customer, amount = 200m
        });
        db.Execute("""
            UPDATE dbo.CashReceipt SET AppliedAmount=AppliedAmount+200
            WHERE Id=@receipt;
            UPDATE dbo.ReceivableOpenItem SET AppliedAmount=AppliedAmount+200
            WHERE Id=@ar;
            """, new { receipt, ar });
        Assert.Equal(100m, db.ExecuteScalar<decimal>(
            "SELECT OutstandingAmount FROM dbo.ReceivableOpenItem WHERE Id=@ar",
            new { ar }));
        Assert.Equal(200m, db.ExecuteScalar<decimal>(
            "SELECT AppliedAmount FROM dbo.CashReceipt WHERE Id=@receipt",
            new { receipt }));
        Reject(db, InsertAllocationSql, new {
            id = Guid.NewGuid(), tenant = A, receipt, ar, s.Customer, amount = 1m
        }, 2627, 2601);
        Reject(db, "UPDATE dbo.ReceivableOpenItem SET AppliedAmount=301 WHERE Id=@ar",
            new { ar }, 547);
        Reject(db, "UPDATE dbo.CashReceipt SET AppliedAmount=201 WHERE Id=@receipt",
            new { receipt }, 547);
        Reject(db, "UPDATE dbo.CashReceipt SET ReceivedAmount=201 WHERE Id=@receipt",
            new { receipt }, 51066);
        Reject(db, "DELETE dbo.ReceiptAllocation WHERE Id=@allocation",
            new { allocation }, 51064);
        var foreign = SeedLoadedTrip(db, A);
        var otherSale = InsertSale(db, A, foreign, "B", 1m, 15m, 10m);
        Guid otherAr = Guid.NewGuid();
        db.Execute(InsertOpenItemSql, new {
            id = otherAr, tenant = A, delivery = otherSale.Id, foreign.Customer,
            sourceLine = otherSale.SourceLine, kind = "B", amount = 15m,
            due = (DateTime?)null
        });
        Reject(db, InsertAllocationSql, new {
            id = Guid.NewGuid(), tenant = A, receipt, ar = otherAr,
            foreign.Customer, amount = 1m
        }, 547);
        Reject(db, InsertReceiptSql, new {
            id = Guid.NewGuid(), tenant = A, delivery = otherSale.Id,
            foreign.Trip, foreign.Customer, sourceLine = otherSale.SourceLine,
            amount = -1m, evidence = "synthetic-verified-cash"
        }, 547);
    }

    [Fact]
    public void B_grant_consumption_requires_matching_customer_trip_and_B_sale()
    {
        using var db = Open(A);
        var s = SeedLoadedTrip(db, A);
        var b = InsertSale(db, A, s, "B", 20m, 300m, 200m);
        Guid grant = NewGrant(db, A, s, s.Customer, s.ShipTo, 100m);
        Guid consumption = Guid.NewGuid();
        db.Execute(InsertGrantUseSql, new {
            id = consumption, tenant = A, delivery = b.Id, grant,
            s.Trip, s.Customer, amount = 100m
        });
        Reject(db, InsertGrantUseSql, new {
            id = Guid.NewGuid(), tenant = A, delivery = b.Id, grant,
            s.Trip, s.Customer, amount = 100m
        }, 2627, 2601);
        Reject(db, "UPDATE dbo.BGrantConsumption SET ConsumedAmount=99 WHERE Id=@id",
            new { id = consumption }, 51065);
        Guid otherCustomer = Guid.NewGuid(), otherShipTo = Guid.NewGuid();
        db.Execute("""
            INSERT dbo.Customer(Id,TenantId,Code,Name) VALUES
                (@customer,@tenant,@code,N'other customer');
            INSERT dbo.ShipToSnapshot(Id,TenantId,CustomerId,DisplayName,Address)
                VALUES(@shipTo,@tenant,@customer,N'other',N'other');
            """, new { customer = otherCustomer, shipTo = otherShipTo,
                tenant = A, code = Code() });
        Guid otherGrant = NewGrant(db, A, s, otherCustomer, otherShipTo, 100m);
        var otherSale = InsertSale(db, A, s, "B", 5m, 75m, 50m);
        Reject(db, InsertGrantUseSql, new {
            id = Guid.NewGuid(), tenant = A, delivery = otherSale.Id,
            grant = otherGrant, s.Trip, s.Customer, amount = 1m
        }, 547);
        Reject(db, InsertGrantUseSql, new {
            id = Guid.NewGuid(), tenant = A, delivery = otherSale.Id,
            grant, s.Trip, s.Customer, amount = -1m
        }, 547);
    }

    [Fact]
    public void Cross_tenant_posting_is_rejected_and_second_database_is_unchanged()
    {
        using var dbA = Open(A);
        using var dbB = Open(B);
        var sceneA = SeedLoadedTrip(dbA, A);
        var sceneB = SeedLoadedTrip(dbB, B);
        int beforeB = dbB.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.BusinessSource WHERE TenantId=@tenant AND TripId=@trip",
            new { tenant = B, trip = sceneB.Trip });
        var saleA = InsertSale(dbA, A, sceneA, "B", 20m, 300m, 200m);
        Assert.Equal(1, dbA.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.SaleDelivery WHERE Id=@id", new { id = saleA.Id }));
        Assert.Equal(0, dbB.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.SaleDelivery WHERE TenantId=@tenant AND TripId=@trip",
            new { tenant = B, trip = sceneB.Trip }));
        Assert.Equal(beforeB, dbB.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.BusinessSource WHERE TenantId=@tenant AND TripId=@trip",
            new { tenant = B, trip = sceneB.Trip }));
        var input = saleA.Input with { Id = Guid.NewGuid(), Tenant = B };
        Reject(dbA, InsertSaleSql, input, 547);
        Assert.Equal(0, dbA.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.SaleDelivery WHERE TenantId=@tenant",
            new { tenant = B }));
        Assert.Equal(0, dbB.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.ReceivableOpenItem WHERE TenantId=@tenant",
            new { tenant = B }));
    }

    private static Scene SeedLoadedTrip(SqlConnection db, string tenant)
    {
        Guid customer = Guid.NewGuid(), shipTo = Guid.NewGuid(), sku = Guid.NewGuid();
        Guid warehouse = Guid.NewGuid(), vehicle = Guid.NewGuid(), trip = Guid.NewGuid();
        Guid order = Guid.NewGuid(), orderLine = Guid.NewGuid(), manifest = Guid.NewGuid();
        db.Execute("""
            INSERT dbo.Customer(Id,TenantId,Code,Name)
                VALUES(@customer,@tenant,@customerCode,N'APP01C1 customer');
            INSERT dbo.ShipToSnapshot(Id,TenantId,CustomerId,DisplayName,Address)
                VALUES(@shipTo,@tenant,@customer,N'APP01C1 shipto',N'APP01C1 synthetic');
            INSERT dbo.Sku(Id,TenantId,Code,Name,BaseUom)
                VALUES(@sku,@tenant,@skuCode,N'APP01C1 sku',N'EA');
            INSERT dbo.StockLocation(Id,TenantId,Code,Kind)
                VALUES(@warehouse,@tenant,@warehouseCode,N'WAREHOUSE');
            INSERT dbo.StockLocation(Id,TenantId,Code,Kind)
                VALUES(@vehicle,@tenant,@vehicleCode,N'VEHICLE');
            INSERT dbo.Trip(Id,TenantId,Code,BusinessDate,VehicleLocationId)
                VALUES(@trip,@tenant,@tripCode,CONVERT(date,SYSUTCDATETIME()),@vehicle);
            INSERT dbo.AOrder(Id,TenantId,OrderNumber,CustomerId,ShipToSnapshotId,
                TripId,BusinessDate,Status,ConfirmedAtUtc)
                VALUES(@order,@tenant,@orderCode,@customer,@shipTo,@trip,
                       CONVERT(date,SYSUTCDATETIME()),N'CONFIRMED',SYSUTCDATETIME());
            INSERT dbo.AOrderLine(Id,TenantId,AOrderId,LineNumber,SkuId,
                BaseQuantity,UomCode,UomToBaseFactor,UnitPrice)
                VALUES(@orderLine,@tenant,@order,1,@sku,40,N'EA',1,15);
            """, new {
                tenant, customer, shipTo, sku, warehouse, vehicle, trip, order, orderLine,
                customerCode = Code(), skuCode = Code(), warehouseCode = Code(),
                vehicleCode = Code(), tripCode = Code(), orderCode = Code()
            });

        Guid aSource = NewSource(db, tenant, trip, customer: null, "STOCK_ALLOC");
        Guid bSource = NewSource(db, tenant, trip, customer: null, "STOCK_ALLOC");
        Guid aLine = NewLine(db, tenant, aSource, sku, 40m, null);
        Guid bLine = NewLine(db, tenant, bSource, sku, 30m, null);
        Guid aRight = Guid.NewGuid(), bRight = Guid.NewGuid();
        db.Execute("""
            INSERT dbo.StockRight(Id,TenantId,TripId,StockLocationId,SkuId,
                BusinessSourceLineId,RightKind,GrantedBaseQuantity)
            VALUES(@aRight,@tenant,@trip,@warehouse,@sku,@aLine,'A',40);
            INSERT dbo.StockRight(Id,TenantId,TripId,StockLocationId,SkuId,
                BusinessSourceLineId,RightKind,GrantedBaseQuantity)
            VALUES(@bRight,@tenant,@trip,@warehouse,@sku,@bLine,'B',30);
            INSERT dbo.AReservation(Id,TenantId,StockRightId,AOrderLineId)
            VALUES(NEWID(),@tenant,@aRight,@orderLine);
            INSERT dbo.BQuota(Id,TenantId,StockRightId)
            VALUES(NEWID(),@tenant,@bRight);
            """, new {
                tenant, trip, warehouse, sku, aLine, bLine, aRight, bRight, orderLine
            });

        Guid confirmSource = NewSource(db, tenant, trip, customer, "A_CONFIRM");
        Guid confirmLine = NewLine(db, tenant, confirmSource, sku, 40m, null);

        Guid loadedSource = NewSource(db, tenant, trip, customer: null, "MANIFEST_LOAD");
        Guid aLoad = NewLine(db, tenant, loadedSource, sku, 40m, 400m, 1);
        Guid bLoad = NewLine(db, tenant, loadedSource, sku, 30m, 300m, 2);
        Guid aManifestLine = Guid.NewGuid(), bManifestLine = Guid.NewGuid();
        db.Execute("""
            INSERT dbo.TripManifest(Id,TenantId,ManifestNumber,TripId,
                WarehouseLocationId,VehicleLocationId)
            VALUES(@manifest,@tenant,@manifestNumber,@trip,@warehouse,@vehicle);
            INSERT dbo.TripManifestLine(Id,TenantId,TripManifestId,TripId,
                WarehouseLocationId,VehicleLocationId,LineNumber,SkuId,
                StockRightId,RightKind,PlannedBaseQuantity)
            VALUES(@aManifestLine,@tenant,@manifest,@trip,@warehouse,@vehicle,
                   1,@sku,@aRight,'A',40);
            INSERT dbo.TripManifestLine(Id,TenantId,TripManifestId,TripId,
                WarehouseLocationId,VehicleLocationId,LineNumber,SkuId,
                StockRightId,RightKind,PlannedBaseQuantity)
            VALUES(@bManifestLine,@tenant,@manifest,@trip,@warehouse,@vehicle,
                   2,@sku,@bRight,'B',30);
            UPDATE dbo.TripManifest SET Status=N'LOADED',
                LoadBusinessSourceId=@loadedSource,LoadedAtUtc=SYSUTCDATETIME()
            WHERE TenantId=@tenant AND Id=@manifest;
            UPDATE dbo.TripManifestLine SET LoadBusinessSourceId=@loadedSource,
                LoadSourceLineId=@aLoad,LoadedBaseQuantity=40,LoadedCostAmount=400
            WHERE TenantId=@tenant AND Id=@aManifestLine;
            UPDATE dbo.TripManifestLine SET LoadBusinessSourceId=@loadedSource,
                LoadSourceLineId=@bLoad,LoadedBaseQuantity=30,LoadedCostAmount=300
            WHERE TenantId=@tenant AND Id=@bManifestLine;
            """, new {
                tenant, manifest, manifestNumber = Code(), trip, warehouse, vehicle,
                sku, aManifestLine, bManifestLine, aRight, bRight, loadedSource,
                aLoad, bLoad
            });
        return new Scene(customer, shipTo, sku, warehouse, vehicle, trip, order,
            orderLine, aRight, bRight, aManifestLine, bManifestLine,
            aLoad, bLoad, confirmSource, confirmLine);
    }

    private static Guid NewSource(SqlConnection db, string tenant,
        Guid trip, Guid? customer, string kind)
    {
        Guid command = Guid.NewGuid(), source = Guid.NewGuid();
        db.Execute("""
            INSERT dbo.BusinessCommand(Id,TenantId,ProducerId,CommandId,
                PayloadHashVersion,PayloadHash,ActorId,Status)
            VALUES(@command,@tenant,N'app01c1-schema',@commandCode,
                   1,@hash,N'schema-test',N'APPLIED');
            INSERT dbo.BusinessSource(Id,TenantId,BusinessCommandId,SourceKind,
                SourceNumber,ActorId,TripId,CustomerId,OccurredAtUtc)
            VALUES(@source,@tenant,@command,@kind,@sourceCode,
                   N'schema-test',@trip,@customer,SYSUTCDATETIME());
            """, new { command, source, tenant, kind, trip, customer,
                commandCode = Code(), sourceCode = Code(),
                hash = SHA256.HashData(Guid.NewGuid().ToByteArray()) });
        return source;
    }

    private static Guid NewLine(SqlConnection db, string tenant, Guid source,
        Guid sku, decimal qty, decimal? amount, int number = 1)
    {
        Guid id = Guid.NewGuid();
        db.Execute("""
            INSERT dbo.BusinessSourceLine(Id,TenantId,BusinessSourceId,
                LineNumber,SkuId,BaseQuantity,Amount)
            VALUES(@id,@tenant,@source,@number,@sku,@qty,@amount)
            """, new { id, tenant, source, number, sku, qty, amount });
        return id;
    }

    private const string InsertSnapshotSql = """
        INSERT dbo.AConfirmedLineSnapshot(TenantId,AOrderLineId,AOrderId,TripId,
            CustomerId,SkuId,ConfirmBusinessSourceId,ConfirmSourceLineId,
            ConfirmedBaseQuantity,UnitPriceSnapshot,TermsDaysSnapshot)
        VALUES(@tenant,@line,@order,@trip,@customer,@sku,@source,@sourceLine,
               @qty,@price,@terms)
        """;

    private static void InsertConfirmSnapshot(SqlConnection db, string tenant, Scene s) =>
        db.Execute(InsertSnapshotSql, new {
            tenant, line = s.OrderLine, order = s.Order, trip = s.Trip,
            customer = s.Customer, sku = s.Sku, source = s.ConfirmSource,
            sourceLine = s.ConfirmLine, qty = 40m, price = 15m, terms = 7
        });

    private const string InsertSaleSql = """
        INSERT dbo.SaleDelivery(Id,TenantId,TripId,CustomerId,ShipToSnapshotId,
            TripManifestLineId,LoadSourceLineId,StockRightId,RightKind,SkuId,
            AOrderId,AOrderLineId,BusinessSourceId,BusinessSourceKind,BusinessSourceLineId,
            BaseQuantity,PayableAmount,CostAmount,CostBasisVersion,BusinessDate)
        VALUES(@Id,@Tenant,@Trip,@Customer,@ShipTo,
               @ManifestLine,@LoadLine,@Right,@Kind,@Sku,
               @Order,@OrderLine,@Source,@SourceKind,@SourceLine,
               @Qty,@Payable,@Cost,N'SYNTHETIC_MA_V1',CONVERT(date,SYSUTCDATETIME()))
        """;

    private static SaleInput MakeSale(string tenant, Scene s, string kind, Guid id,
        Guid source, Guid sourceLine, decimal qty, decimal payable, decimal cost) =>
        new(id, tenant, s.Trip, s.Customer, s.ShipTo,
            kind == "A" ? s.AManifestLine : s.BManifestLine,
            kind == "A" ? s.ALoadLine : s.BLoadLine,
            kind == "A" ? s.ARight : s.BRight,
            kind, s.Sku, kind == "A" ? s.Order : null,
            kind == "A" ? s.OrderLine : null, source,
            kind == "A" ? "A_DELIVERY" : "B_SALE_DELIVERY",
            sourceLine, qty, payable, cost);

    private static Sale InsertSale(SqlConnection db, string tenant, Scene s,
        string kind, decimal qty, decimal payable, decimal cost)
    {
        Guid source = NewSource(db, tenant, s.Trip, s.Customer,
            kind == "A" ? "A_DELIVERY" : "B_SALE_DELIVERY");
        Guid line = NewLine(db, tenant, source, s.Sku, qty, payable);
        var input = MakeSale(tenant, s, kind, Guid.NewGuid(),
            source, line, qty, payable, cost);
        db.Execute(InsertSaleSql, input);
        return new Sale(input.Id, line, input);
    }

    private const string InsertIssueSql = """
        INSERT dbo.DeliveryStockIssue(Id,TenantId,SaleDeliveryId,
            BusinessSourceLineId,TripManifestLineId,SkuId,BaseQuantity,CostAmount)
        VALUES(@id,@tenant,@delivery,@sourceLine,@manifestLine,@Sku,@qty,@cost)
        """;

    private static Guid InsertStockIssue(SqlConnection db, string tenant, Sale sale)
    {
        Guid id = Guid.NewGuid();
        db.Execute(InsertIssueSql, new {
            id, tenant, delivery = sale.Id, sourceLine = sale.SourceLine,
            manifestLine = sale.Input.ManifestLine, sale.Input.Sku,
            qty = sale.Input.Qty, cost = sale.Input.Cost
        });
        return id;
    }

    private const string InsertOpenItemSql = """
        INSERT dbo.ReceivableOpenItem(Id,TenantId,SaleDeliveryId,
            CustomerId,BusinessSourceLineId,RightKind,OriginalAmount,DueBusinessDate)
        VALUES(@id,@tenant,@delivery,@Customer,@sourceLine,@kind,@amount,@due)
        """;

    private const string InsertReceiptSql = """
        INSERT dbo.CashReceipt(Id,TenantId,SaleDeliveryId,TripId,CustomerId,
            BusinessSourceLineId,VerificationEvidenceRef,ReceivedAmount,VerifiedAtUtc)
        VALUES(@id,@tenant,@delivery,@Trip,@Customer,
               @sourceLine,@evidence,@amount,SYSUTCDATETIME())
        """;

    private const string InsertAllocationSql = """
        INSERT dbo.ReceiptAllocation(Id,TenantId,CashReceiptId,ReceivableOpenItemId,
            CustomerId,AppliedAmount)
        VALUES(@id,@tenant,@receipt,@ar,@Customer,@amount)
        """;

    private static Guid NewGrant(SqlConnection db, string tenant, Scene s,
        Guid customer, Guid shipTo, decimal amount)
    {
        Guid source = NewSource(db, tenant, s.Trip, customer, "CREDIT_GRANT");
        Guid line = NewLine(db, tenant, source, s.Sku, 1m, amount);
        Guid grant = Guid.NewGuid();
        db.Execute("""
            INSERT dbo.BCreditGrant(Id,TenantId,TripId,CustomerId,ShipToSnapshotId,
                PoolCode,CurrencyUnit,BusinessSourceLineId,GrantedAmount)
            VALUES(@grant,@tenant,@trip,@customer,@shipTo,
                   N'B',N'SYNTHETIC_UNITLESS',@line,@amount)
            """, new { grant, tenant, trip = s.Trip, customer, shipTo, line, amount });
        return grant;
    }

    private const string InsertGrantUseSql = """
        INSERT dbo.BGrantConsumption(Id,TenantId,SaleDeliveryId,BCreditGrantId,
            TripId,CustomerId,ConsumedAmount)
        VALUES(@id,@tenant,@delivery,@grant,@Trip,@Customer,@amount)
        """;
}
