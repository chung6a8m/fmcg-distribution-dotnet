using System.Security.Cryptography;
using Dapper;
using FmcgDms.Api.Persistence;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FmcgDms.Api.Tests;

/// <summary>
/// APP-01B.1 physical LocalDB schema tests. No manifest command or T1/T2
/// business acceptance is claimed here. The SQL fixture owns two nonce DBs.
/// </summary>
[Collection("SqlScratch")]
public sealed class App01b1PhysicalStockSchemaTests(SqlScratchFixture fixture)
{
    private const string A = "tenant-a";
    private const string B = "tenant-b";

    [Fact]
    public void Two_tenant_migration_is_fresh_and_repeatable()
    {
        foreach (var (tenant, connection) in new[]
        {
            (A, fixture.TenantAConnectionString),
            (B, fixture.TenantBConnectionString)
        })
        {
            using var db = new SqlConnection(connection);
            foreach (string table in new[]
            {
                "PhysicalStockOpening", "PhysicalStockBalance", "TripManifest",
                "TripManifestLine", "PhysicalStockMovement"
            })
                Assert.Equal(1, db.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM sys.tables WHERE schema_id=SCHEMA_ID(N'dbo') AND name=@table",
                    new { table }));

            Assert.Equal(1, db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM dbo.VersionInfo WHERE Version=202611160004"));
            Assert.Equal(5, db.ExecuteScalar<int>("""
                SELECT COUNT(*) FROM sys.triggers
                WHERE name IN (N'TR_PhysicalStockOpening_Immutable',
                    N'TR_PhysicalStockMovement_Immutable',
                    N'TR_TripManifest_ImmutableLoaded',
                    N'TR_TripManifestLine_ImmutableLoaded',
                    N'TR_TripManifestLine_InsertOnlyDraft')
                """));
            Assert.Equal(1, db.ExecuteScalar<int>("""
                SELECT COUNT(*) FROM sys.foreign_keys
                WHERE name=N'FK_TripManifestLine_ExactSourceLine'
                """));
            ScratchMigrator.Migrate(connection, tenant, fixture.Nonce);
            ScratchMigrator.Migrate(connection, tenant, fixture.Nonce);
            Assert.Equal(1, db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM dbo.VersionInfo WHERE Version=202611160004"));
            Assert.Equal(tenant, db.ExecuteScalar<string>(
                "SELECT TenantId FROM dbo.AppTenantBinding WHERE Slot=1"));
        }
    }

    [Fact]
    public void Opening_and_live_physical_balances_are_not_stock_pool_entitlements()
    {
        using var db = new SqlConnection(fixture.TenantAConnectionString);
        Guid sku = AddSku(db, A);
        Guid warehouse = AddLocation(db, A, "WAREHOUSE");
        Guid vehicle = AddLocation(db, A, "VEHICLE");

        Open(db, A, warehouse, sku, 100m, 1000m);
        Open(db, A, vehicle, sku, 0m, 0m);
        db.Execute("""
            INSERT dbo.StockPool(TenantId,StockLocationId,SkuId,OnHandBaseQuantity,AllocatedBaseQuantity)
            VALUES(@tenant,@warehouse,@sku,100,70)
            """, new { tenant = A, warehouse, sku });

        Assert.Equal((100m, 1000m), Balance(db, A, warehouse, sku));
        Assert.Equal((0m, 0m), Balance(db, A, vehicle, sku));
        Assert.Equal(70m, db.ExecuteScalar<decimal>("""
            SELECT AllocatedBaseQuantity FROM dbo.StockPool
            WHERE TenantId=@tenant AND StockLocationId=@warehouse AND SkuId=@sku
            """, new { tenant = A, warehouse, sku }));

        // A right allocation changes no physical balance or cost in APP-01A.
        db.Execute("""
            UPDATE dbo.StockPool SET AllocatedBaseQuantity=65
            WHERE TenantId=@tenant AND StockLocationId=@warehouse AND SkuId=@sku
            """, new { tenant = A, warehouse, sku });
        Assert.Equal((100m, 1000m), Balance(db, A, warehouse, sku));
        Assert.Equal((0m, 0m), Balance(db, A, vehicle, sku));
        Assert.Equal(100m, db.ExecuteScalar<decimal>("""
            SELECT BaseQuantity FROM dbo.PhysicalStockOpening
            WHERE TenantId=@tenant AND StockLocationId=@warehouse AND SkuId=@sku
            """, new { tenant = A, warehouse, sku }));
    }

    [Fact]
    public void Manifest_header_enforces_warehouse_role_trip_vehicle_and_tenant()
    {
        using var db = new SqlConnection(fixture.TenantAConnectionString);
        Guid warehouse = AddLocation(db, A, "WAREHOUSE");
        Guid vehicle = AddLocation(db, A, "VEHICLE");
        Guid otherVehicle = AddLocation(db, A, "VEHICLE");
        Guid trip = AddTrip(db, A, vehicle);
        Guid roleTestTrip = AddTrip(db, A, vehicle);
        Guid vehicleTestTrip = AddTrip(db, A, vehicle);
        Guid tenantTestTrip = AddTrip(db, A, vehicle);
        Guid valid = AddManifest(db, A, trip, warehouse, vehicle);

        Assert.Equal("DRAFT", db.ExecuteScalar<string>(
            "SELECT Status FROM dbo.TripManifest WHERE Id=@valid", new { valid }));
        Assert.Equal(0, db.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM dbo.PhysicalStockMovement m
            JOIN dbo.TripManifestLine l ON l.TenantId=m.TenantId
                AND l.Id=m.TripManifestLineId
            WHERE l.TenantId=@tenant AND l.TripManifestId=@valid
            """, new { tenant = A, valid }));

        Error(db, """
            INSERT dbo.TripManifest(Id,TenantId,ManifestNumber,TripId,
                WarehouseLocationId,VehicleLocationId)
            VALUES(@id,@tenant,@number,@trip,@warehouse,@vehicle)
            """, new
        {
            id = Guid.NewGuid(), tenant = A, number = Code(), trip = roleTestTrip,
            warehouse = otherVehicle, vehicle
        }, 547); // A VEHICLE cannot be used as a WAREHOUSE.

        Error(db, """
            INSERT dbo.TripManifest(Id,TenantId,ManifestNumber,TripId,
                WarehouseLocationId,VehicleLocationId)
            VALUES(@id,@tenant,@number,@trip,@warehouse,@vehicle)
            """, new
        {
            id = Guid.NewGuid(), tenant = A, number = Code(), trip = vehicleTestTrip,
            warehouse, vehicle = otherVehicle
        }, 547); // Must be the Trip's configured vehicle.

        Error(db, """
            INSERT dbo.TripManifest(Id,TenantId,ManifestNumber,TripId,
                WarehouseLocationId,VehicleLocationId)
            VALUES(@id,@tenant,@number,@trip,@warehouse,@vehicle)
            """, new
        {
            id = Guid.NewGuid(), tenant = B, number = Code(), trip = tenantTestTrip,
            warehouse, vehicle
        }, 547); // Cross-tenant root cannot be inserted in tenant A DB.

        Error(db, """
            INSERT dbo.TripManifest(Id,TenantId,ManifestNumber,TripId,
                WarehouseLocationId,VehicleLocationId)
            VALUES(@id,@tenant,@number,@trip,@warehouse,@vehicle)
            """, new
        {
            id = Guid.NewGuid(), tenant = A, number = Code(), trip, warehouse, vehicle
        }, 2627, 2601); // Exactly one manifest for a Trip.
    }

    [Fact]
    public void Manifest_line_cannot_swap_A_B_right_trip_warehouse_or_sku()
    {
        using var db = new SqlConnection(fixture.TenantAConnectionString);
        int financialEffectsBefore = db.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.LedgerEffect WHERE TenantId=@tenant AND LedgerKind IN (N'AR',N'COGS')",
            new { tenant = A });
        Guid warehouse = AddLocation(db, A, "WAREHOUSE");
        Guid vehicle = AddLocation(db, A, "VEHICLE");
        Guid otherWarehouse = AddLocation(db, A, "WAREHOUSE");
        Guid sku = AddSku(db, A);
        Guid otherSku = AddSku(db, A);
        Guid trip = AddTrip(db, A, vehicle);
        Guid otherTrip = AddTrip(db, A, vehicle);
        Guid manifest = AddManifest(db, A, trip, warehouse, vehicle);
        Guid aRight = AddRight(db, A, trip, warehouse, sku, "A", 40m);
        Guid bRight = AddRight(db, A, trip, warehouse, sku, "B", 30m);
        Guid unusedBRight = AddRight(db, A, trip, warehouse, sku, "B", 5m);
        AddManifestLine(db, A, manifest, trip, warehouse, vehicle, 1, sku, aRight, "A", 40m);
        AddManifestLine(db, A, manifest, trip, warehouse, vehicle, 2, sku, bRight, "B", 30m);

        Assert.Equal(2, db.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.TripManifestLine WHERE TenantId=@tenant AND TripManifestId=@manifest",
            new { tenant = A, manifest }));
        Error(db, LineInsert, LineArgs(A, manifest, trip, warehouse, vehicle,
            3, sku, aRight, "A", 40m), 2627, 2601); // Right cannot load twice.
        Error(db, LineInsert, LineArgs(A, manifest, trip, warehouse, vehicle,
            3, sku, unusedBRight, "A", 5m), 547); // Cannot relabel B as A.
        Error(db, LineInsert, LineArgs(A, manifest, trip, warehouse, vehicle,
            3, otherSku, unusedBRight, "B", 5m), 547);
        Error(db, LineInsert, LineArgs(A, manifest, otherTrip, warehouse, vehicle,
            3, sku, unusedBRight, "B", 5m), 547);
        Error(db, LineInsert, LineArgs(A, manifest, trip, otherWarehouse, vehicle,
            3, sku, unusedBRight, "B", 5m), 547);
        Error(db, LineInsert, LineArgs(B, manifest, trip, warehouse, vehicle,
            3, sku, unusedBRight, "B", 5m), 547);

        Assert.Equal(0, db.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM dbo.PhysicalStockMovement m
            JOIN dbo.TripManifestLine l ON l.TenantId=m.TenantId
                AND l.Id=m.TripManifestLineId
            WHERE l.TenantId=@tenant AND l.TripManifestId=@manifest
            """, new { tenant = A, manifest }));
        Assert.Equal(financialEffectsBefore, db.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.LedgerEffect WHERE TenantId=@tenant AND LedgerKind IN (N'AR',N'COGS')",
            new { tenant = A }));
    }

    [Fact]
    public void Loaded_source_line_must_match_manifest_trip_sku_quantity_cost_and_line_number()
    {
        using var db = new SqlConnection(fixture.TenantAConnectionString);
        Guid warehouse = AddLocation(db, A, "WAREHOUSE");
        Guid vehicle = AddLocation(db, A, "VEHICLE");
        Guid sku = AddSku(db, A);
        Guid trip = AddTrip(db, A, vehicle);
        Guid manifest = AddManifest(db, A, trip, warehouse, vehicle);
        Guid right = AddRight(db, A, trip, warehouse, sku, "A", 40m);
        Guid manifestLine = AddManifestLine(db, A, manifest, trip, warehouse,
            vehicle, 1, sku, right, "A", 40m);
        Guid source = AddSource(db, A, trip, "MANIFEST_LOAD");
        Guid exactSourceLine = AddSourceLine(db, A, source, 1, sku, 40m, 400m);
        Guid otherLineNumber = AddSourceLine(db, A, source, 2, sku, 40m, 400m);
        Guid wrongKindSource = AddSource(db, A, trip, "STOCK_ALLOC");
        Guid foreignTrip = AddTrip(db, A, vehicle);
        Guid wrongTripSource = AddSource(db, A, foreignTrip, "MANIFEST_LOAD");

        Error(db, """
            UPDATE dbo.TripManifest SET Status=N'LOADED',LoadedAtUtc=SYSUTCDATETIME(),
                LoadBusinessSourceId=@source WHERE Id=@manifest
            """, new { source = wrongTripSource, manifest }, 547);

        Error(db, """
            UPDATE dbo.TripManifest SET Status=N'LOADED',LoadedAtUtc=SYSUTCDATETIME(),
                LoadBusinessSourceId=@source WHERE Id=@manifest
            """, new { source = wrongKindSource, manifest }, 547);

        db.Execute("""
            UPDATE dbo.TripManifest SET Status=N'LOADED',LoadedAtUtc=SYSUTCDATETIME(),
                LoadBusinessSourceId=@source WHERE Id=@manifest
            """, new { source, manifest });

        // The header is final; staged lines may only transition to a load
        // without deleting or rewriting their original right/plan.
        Error(db, """
            DELETE dbo.TripManifestLine WHERE TenantId=@tenant AND Id=@manifestLine
            """, new { tenant = A, manifestLine }, 51044);
        Error(db, """
            UPDATE dbo.TripManifestLine SET PlannedBaseQuantity=41
            WHERE TenantId=@tenant AND Id=@manifestLine
            """, new { tenant = A, manifestLine }, 51044);

        Guid extraRight = AddRight(db, A, trip, warehouse, sku, "B", 10m);
        Error(db, LineInsert, LineArgs(A, manifest, trip, warehouse, vehicle,
            2, sku, extraRight, "B", 10m), 51045); // LOADED header is closed to inserts.
        Error(db, """
            UPDATE dbo.TripManifestLine SET StockRightId=@extraRight,RightKind='B'
            WHERE TenantId=@tenant AND Id=@manifestLine
            """, new { extraRight, tenant = A, manifestLine }, 51044);
        Error(db, """
            UPDATE dbo.TripManifestLine SET PlannedBaseQuantity=41,
                LoadBusinessSourceId=@source,LoadSourceLineId=@sourceLine,
                LoadedBaseQuantity=40,LoadedCostAmount=400
            WHERE TenantId=@tenant AND Id=@manifestLine
            """, new
        {
            tenant = A, manifestLine, source, sourceLine = exactSourceLine
        }, 51044); // Cannot sneak restaging into the load transition.

        Error(db, LoadLineSql, new
        {
            manifestLine, source, sourceLine = otherLineNumber, qty = 40m, cost = 400m
        }, 547);
        Error(db, LoadLineSql, new
        {
            manifestLine, source, sourceLine = exactSourceLine, qty = 39m, cost = 400m
        }, 547);
        Error(db, LoadLineSql, new
        {
            manifestLine, source, sourceLine = exactSourceLine, qty = 40m, cost = 401m
        }, 547);

        db.Execute(LoadLineSql, new
        {
            manifestLine, source, sourceLine = exactSourceLine, qty = 40m, cost = 400m
        });
        Error(db, MovementInsert, new
        {
            id = Guid.NewGuid(), tenant = A, manifestLine,
            sourceLine = otherLineNumber, qty = 40m, cost = 400m
        }, 547);
        Error(db, MovementInsert, new
        {
            id = Guid.NewGuid(), tenant = A, manifestLine,
            sourceLine = exactSourceLine, qty = 40m, cost = 399m
        }, 547);
        db.Execute(MovementInsert, new
        {
            id = Guid.NewGuid(), tenant = A, manifestLine,
            sourceLine = exactSourceLine, qty = 40m, cost = 400m
        });
        Error(db, MovementInsert, new
        {
            id = Guid.NewGuid(), tenant = A, manifestLine,
            sourceLine = exactSourceLine, qty = 40m, cost = 400m
        }, 2627, 2601);

        // Once posted, neither movement nor its loaded evidence may be rewritten.
        Error(db, """
            UPDATE dbo.PhysicalStockMovement SET RecordedAtUtc=SYSUTCDATETIME()
            WHERE TenantId=@tenant AND TripManifestLineId=@manifestLine
            """, new { tenant = A, manifestLine }, 51042);
        Error(db, """
            UPDATE dbo.TripManifestLine SET PlannedBaseQuantity=41
            WHERE Id=@manifestLine
            """, new { manifestLine }, 51044);
        Error(db, """
            UPDATE dbo.TripManifest SET ManifestNumber=@name WHERE Id=@manifest
            """, new { name = Code(), manifest }, 51043);

        Assert.Equal((40m, 400m), db.QuerySingle<(decimal, decimal)>("""
            SELECT m.BaseQuantity, m.CostAmount
            FROM dbo.PhysicalStockMovement m
            JOIN dbo.TripManifestLine l ON l.TenantId=m.TenantId
                AND l.Id=m.TripManifestLineId
            JOIN dbo.TripManifest h ON h.TenantId=l.TenantId
                AND h.Id=l.TripManifestId
            WHERE h.TenantId=@tenant AND h.Id=@manifest
                AND h.WarehouseLocationId=@warehouse AND h.VehicleLocationId=@vehicle
            """, new { tenant = A, manifest, warehouse, vehicle }));
    }

    [Fact]
    public void Nonnegative_decimal_state_and_immutable_evidence_are_enforced()
    {
        using var db = new SqlConnection(fixture.TenantAConnectionString);
        Guid warehouse = AddLocation(db, A, "WAREHOUSE");
        Guid vehicle = AddLocation(db, A, "VEHICLE");
        Guid sku = AddSku(db, A);

        Error(db, OpeningInsert, new
        {
            tenant = A, location = warehouse, sku, qty = 0m, cost = 0.0001m
        }, 547); // Cost becomes nonzero with zero quantity.
        Error(db, OpeningInsert, new
        {
            tenant = A, location = warehouse, sku, qty = -1m, cost = 0m
        }, 547);
        Open(db, A, warehouse, sku, 100.125m, 1001.2500m);
        Open(db, A, vehicle, sku, 0m, 0m);
        Assert.Equal((100.125m, 1001.2500m), Balance(db, A, warehouse, sku));
        Error(db, """
            UPDATE dbo.PhysicalStockOpening SET CostAmount=999
            WHERE TenantId=@tenant AND StockLocationId=@location AND SkuId=@sku
            """, new { tenant = A, location = warehouse, sku }, 51041);
        Error(db, """
            UPDATE dbo.PhysicalStockBalance SET OnHandBaseQuantity=-1
            WHERE TenantId=@tenant AND StockLocationId=@location AND SkuId=@sku
            """, new { tenant = A, location = warehouse, sku }, 547);
        Error(db, """
            UPDATE dbo.PhysicalStockBalance SET OnHandCostAmount=1
            WHERE TenantId=@tenant AND StockLocationId=@location AND SkuId=@sku
            """, new { tenant = A, location = vehicle, sku }, 547);
        Error(db, """
            DELETE dbo.PhysicalStockOpening
            WHERE TenantId=@tenant AND StockLocationId=@location AND SkuId=@sku
            """, new { tenant = A, location = warehouse, sku }, 547, 51041);
        Assert.Equal((100.125m, 1001.2500m), Balance(db, A, warehouse, sku));
    }

    [Fact]
    public void Two_physical_tenant_databases_allow_same_ids_but_no_cross_writes()
    {
        using var dbA = new SqlConnection(fixture.TenantAConnectionString);
        using var dbB = new SqlConnection(fixture.TenantBConnectionString);
        Guid sku = Guid.NewGuid(), warehouse = Guid.NewGuid();
        AddSku(dbA, A, sku);
        AddSku(dbB, B, sku);
        AddLocation(dbA, A, "WAREHOUSE", warehouse);
        AddLocation(dbB, B, "WAREHOUSE", warehouse);
        Open(dbA, A, warehouse, sku, 100m, 1000m);
        Open(dbB, B, warehouse, sku, 7m, 70m);
        Assert.Equal((100m, 1000m), Balance(dbA, A, warehouse, sku));
        Assert.Equal((7m, 70m), Balance(dbB, B, warehouse, sku));

        Error(dbA, OpeningInsert, new
        {
            tenant = B, location = warehouse, sku, qty = 1m, cost = 10m
        }, 547);
        Error(dbB, """
            INSERT dbo.PhysicalStockBalance
                (TenantId,StockLocationId,SkuId,OnHandBaseQuantity,OnHandCostAmount)
            VALUES(@tenant,@location,@sku,1,10)
            """, new { tenant = A, location = warehouse, sku }, 547);
        dbA.Execute("""
            UPDATE dbo.PhysicalStockBalance SET OnHandBaseQuantity=90,OnHandCostAmount=900
            WHERE TenantId=@tenant AND StockLocationId=@location AND SkuId=@sku
            """, new { tenant = A, location = warehouse, sku });
        Assert.Equal((90m, 900m), Balance(dbA, A, warehouse, sku));
        Assert.Equal((7m, 70m), Balance(dbB, B, warehouse, sku));
    }

    private const string OpeningInsert = """
        INSERT dbo.PhysicalStockOpening
            (TenantId,StockLocationId,SkuId,SnapshotCode,BaseQuantity,CostAmount)
        VALUES(@tenant,@location,@sku,N'AC03-T0',@qty,@cost)
        """;
    private const string LineInsert = """
        INSERT dbo.TripManifestLine
            (Id,TenantId,TripManifestId,TripId,WarehouseLocationId,
             VehicleLocationId,LineNumber,SkuId,StockRightId,RightKind,PlannedBaseQuantity)
        VALUES(@id,@tenant,@manifest,@trip,@warehouse,@vehicle,
            @lineNumber,@sku,@right,@kind,@qty)
        """;
    private const string LoadLineSql = """
        UPDATE dbo.TripManifestLine
        SET LoadBusinessSourceId=@source,LoadSourceLineId=@sourceLine,
            LoadedBaseQuantity=@qty,LoadedCostAmount=@cost
        WHERE Id=@manifestLine
        """;
    private const string MovementInsert = """
        INSERT dbo.PhysicalStockMovement
            (Id,TenantId,TripManifestLineId,LoadSourceLineId,BaseQuantity,CostAmount)
        VALUES(@id,@tenant,@manifestLine,@sourceLine,@qty,@cost)
        """;

    private static string Code() => Guid.NewGuid().ToString("N");

    private static Guid AddSku(SqlConnection db, string tenant, Guid? id = null)
    {
        Guid sku = id ?? Guid.NewGuid();
        db.Execute("""
            INSERT dbo.Sku(Id,TenantId,Code,Name,BaseUom)
            VALUES(@sku,@tenant,@code,N'APP01B1 test SKU',N'EA')
            """, new { sku, tenant, code = Code() });
        return sku;
    }

    private static Guid AddLocation(SqlConnection db, string tenant,
        string kind, Guid? id = null)
    {
        Guid location = id ?? Guid.NewGuid();
        db.Execute("""
            INSERT dbo.StockLocation(Id,TenantId,Code,Kind)
            VALUES(@location,@tenant,@code,@kind)
            """, new { location, tenant, code = Code(), kind });
        return location;
    }

    private static Guid AddTrip(SqlConnection db, string tenant, Guid vehicle)
    {
        Guid trip = Guid.NewGuid();
        db.Execute("""
            INSERT dbo.Trip(Id,TenantId,Code,BusinessDate,VehicleLocationId)
            VALUES(@trip,@tenant,@code,CONVERT(date,SYSUTCDATETIME()),@vehicle)
            """, new { trip, tenant, code = Code(), vehicle });
        return trip;
    }

    private static Guid AddManifest(SqlConnection db, string tenant, Guid trip,
        Guid warehouse, Guid vehicle)
    {
        Guid id = Guid.NewGuid();
        db.Execute("""
            INSERT dbo.TripManifest(Id,TenantId,ManifestNumber,TripId,
                WarehouseLocationId,VehicleLocationId)
            VALUES(@id,@tenant,@number,@trip,@warehouse,@vehicle)
            """, new { id, tenant, number = Code(), trip, warehouse, vehicle });
        return id;
    }

    private static object LineArgs(string tenant, Guid manifest, Guid trip,
        Guid warehouse, Guid vehicle, int lineNumber, Guid sku, Guid right,
        string kind, decimal qty) =>
        new { id = Guid.NewGuid(), tenant, manifest, trip, warehouse,
            vehicle, lineNumber, sku, right, kind, qty };

    private static Guid AddManifestLine(SqlConnection db, string tenant,
        Guid manifest, Guid trip, Guid warehouse, Guid vehicle, int lineNumber,
        Guid sku, Guid right, string kind, decimal qty)
    {
        var id = Guid.NewGuid();
        db.Execute(LineInsert, new { id, tenant, manifest, trip, warehouse,
            vehicle, lineNumber, sku, right, kind, qty });
        return id;
    }

    private static Guid AddCommand(SqlConnection db, string tenant)
    {
        Guid id = Guid.NewGuid();
        db.Execute("""
            INSERT dbo.BusinessCommand(Id,TenantId,ProducerId,CommandId,
                PayloadHashVersion,PayloadHash,ActorId,Status)
            VALUES(@id,@tenant,N'app01b1-tests',@command,1,@hash,N'test-actor',N'RECEIVED')
            """, new
        {
            id, tenant, command = Code(),
            hash = SHA256.HashData(Guid.NewGuid().ToByteArray())
        });
        return id;
    }

    private static Guid AddSource(SqlConnection db, string tenant,
        Guid trip, string kind)
    {
        Guid source = Guid.NewGuid();
        Guid command = AddCommand(db, tenant);
        db.Execute("""
            INSERT dbo.BusinessSource(Id,TenantId,BusinessCommandId,SourceKind,
                SourceNumber,ActorId,TripId,OccurredAtUtc)
            VALUES(@source,@tenant,@command,@kind,@number,N'test-actor',
                @trip,SYSUTCDATETIME())
            """, new { source, tenant, command, kind, number = Code(), trip });
        return source;
    }

    private static Guid AddSourceLine(SqlConnection db, string tenant,
        Guid source, int number, Guid sku, decimal qty, decimal cost)
    {
        Guid id = Guid.NewGuid();
        db.Execute("""
            INSERT dbo.BusinessSourceLine(Id,TenantId,BusinessSourceId,LineNumber,
                SkuId,BaseQuantity,Amount)
            VALUES(@id,@tenant,@source,@number,@sku,@qty,@cost)
            """, new { id, tenant, source, number, sku, qty, cost });
        return id;
    }

    private static Guid AddRight(SqlConnection db, string tenant, Guid trip,
        Guid warehouse, Guid sku, string kind, decimal qty)
    {
        Guid source = AddSource(db, tenant, trip, "STOCK_ALLOC");
        Guid sourceLine = AddSourceLine(db, tenant, source, 1, sku, qty, 0m);
        Guid right = Guid.NewGuid();
        db.Execute("""
            INSERT dbo.StockRight(Id,TenantId,TripId,StockLocationId,SkuId,
                BusinessSourceLineId,RightKind,GrantedBaseQuantity)
            VALUES(@right,@tenant,@trip,@warehouse,@sku,@sourceLine,@kind,@qty)
            """, new { right, tenant, trip, warehouse, sku, sourceLine, kind, qty });
        return right;
    }

    private static void Open(SqlConnection db, string tenant, Guid location,
        Guid sku, decimal qty, decimal cost)
    {
        db.Execute(OpeningInsert, new { tenant, location, sku, qty, cost });
        db.Execute("""
            INSERT dbo.PhysicalStockBalance
                (TenantId,StockLocationId,SkuId,OnHandBaseQuantity,OnHandCostAmount)
            VALUES(@tenant,@location,@sku,@qty,@cost)
            """, new { tenant, location, sku, qty, cost });
    }

    private static (decimal, decimal) Balance(SqlConnection db, string tenant,
        Guid location, Guid sku) =>
        db.QuerySingle<(decimal, decimal)>("""
            SELECT OnHandBaseQuantity, OnHandCostAmount FROM dbo.PhysicalStockBalance
            WHERE TenantId=@tenant AND StockLocationId=@location AND SkuId=@sku
            """, new { tenant, location, sku });

    private static void Error(SqlConnection db, string sql, object values, params int[] expected)
    {
        var ex = Assert.Throws<SqlException>(() => db.Execute(sql, values));
        Assert.Contains(ex.Number, expected);
    }
}
