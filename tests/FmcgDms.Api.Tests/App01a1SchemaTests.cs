using System.Security.Cryptography;
using Dapper;
using FmcgDms.Api.Persistence;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FmcgDms.Api.Tests;

/// <summary>
/// Physical LocalDB tests for APP-01A.1 schema constraints, not command acceptance.
/// Each test shares the nonce-owned two-database APP-00A fixture.
/// </summary>
[Collection("SqlScratch")]
public sealed class App01a1SchemaTests(SqlScratchFixture fixture)
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";

    [Fact]
    public void Fresh_two_tenant_schema_and_repeat_migration_are_deterministic()
    {
        string[] expectedTables =
        [
            "Customer", "ShipToSnapshot", "Sku", "StockLocation", "Trip",
            "BusinessCommand", "BusinessSource", "BusinessSourceLine",
            "AOrder", "AOrderLine", "StockRight", "AReservation", "BQuota",
            "BCreditGrant", "LedgerEffect"
        ];
        foreach (var (tenant, connectionString) in new[]
        {
            (TenantA, fixture.TenantAConnectionString),
            (TenantB, fixture.TenantBConnectionString)
        })
        {
            using var db = new SqlConnection(connectionString);
            foreach (var table in expectedTables)
            {
                Assert.Equal(1, db.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM sys.tables WHERE schema_id=SCHEMA_ID(N'dbo') AND name=@table",
                    new { table }));
            }

            Assert.Equal(1, db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM dbo.VersionInfo WHERE Version=202611160001"));
            Assert.Equal(1, db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM sys.key_constraints WHERE name='UQ_AppTenantBinding_TenantId'"));
            Assert.Equal(1, db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM sys.foreign_keys WHERE name='FK_Customer_Tenant'"));
            Assert.Equal(tenant, db.ExecuteScalar<string>(
                "SELECT TenantId FROM dbo.AppTenantBinding WHERE Slot=1"));

            ScratchMigrator.Migrate(connectionString, tenant, fixture.Nonce);
            ScratchMigrator.Migrate(connectionString, tenant, fixture.Nonce);
            Assert.Equal(1, db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM dbo.VersionInfo WHERE Version=202611160001"));
            Assert.Equal(1, db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM dbo.AppTenantBinding"));
        }
    }

    [Fact]
    public void Tenant_binding_guards_direct_writes_and_tenant_databases_are_isolated()
    {
        using var a = new SqlConnection(fixture.TenantAConnectionString);
        using var b = new SqlConnection(fixture.TenantBConnectionString);
        Guid sameCustomerId = Guid.NewGuid();
        string sameCode = NewCode();

        a.Execute("INSERT dbo.Customer(Id,TenantId,Code,Name) VALUES(@id,@tenant,@code,N'Tenant A')",
            new { id = sameCustomerId, tenant = TenantA, code = sameCode });
        b.Execute("INSERT dbo.Customer(Id,TenantId,Code,Name) VALUES(@id,@tenant,@code,N'Tenant B')",
            new { id = sameCustomerId, tenant = TenantB, code = sameCode });

        Assert.Equal("Tenant A", a.ExecuteScalar<string>(
            "SELECT Name FROM dbo.Customer WHERE Id=@id AND TenantId=@tenant",
            new { id = sameCustomerId, tenant = TenantA }));
        Assert.Equal("Tenant B", b.ExecuteScalar<string>(
            "SELECT Name FROM dbo.Customer WHERE Id=@id AND TenantId=@tenant",
            new { id = sameCustomerId, tenant = TenantB }));

        // Even a direct writer cannot add a second tenant binding to the same database.
        ExpectConstraint(a, """
            INSERT dbo.AppTenantBinding(Slot,TenantId,BoundAtUtc)
            VALUES(2,@tenant,SYSUTCDATETIME())
            """, new { tenant = TenantB }, 547);

        // Two tenant IDs cannot coexist in either owned database even via direct SQL.
        ExpectConstraint(a,
            "INSERT dbo.Customer(Id,TenantId,Code,Name) VALUES(@id,@tenant,@code,N'forbidden')",
            new { id = Guid.NewGuid(), tenant = TenantB, code = NewCode() }, 547);
        ExpectConstraint(b,
            "INSERT dbo.Sku(Id,TenantId,Code,Name,BaseUom) VALUES(@id,@tenant,@code,N'forbidden',N'EA')",
            new { id = Guid.NewGuid(), tenant = TenantA, code = NewCode() }, 547);

        byte[] before = a.QuerySingle<byte[]>(
            "SELECT RowVersion FROM dbo.Customer WHERE Id=@id", new { id = sameCustomerId });
        a.Execute("UPDATE dbo.Customer SET Name=N'Updated A' WHERE Id=@id",
            new { id = sameCustomerId });
        byte[] after = a.QuerySingle<byte[]>(
            "SELECT RowVersion FROM dbo.Customer WHERE Id=@id", new { id = sameCustomerId });
        Assert.NotEqual(Convert.ToHexString(before), Convert.ToHexString(after));
        Assert.Equal("Tenant B", b.ExecuteScalar<string>(
            "SELECT Name FROM dbo.Customer WHERE Id=@id", new { id = sameCustomerId }));
    }

    [Fact]
    public void Order_and_B_grant_cannot_use_ship_to_of_another_customer()
    {
        using var db = new SqlConnection(fixture.TenantAConnectionString);
        Guid customerA = AddCustomer(db, TenantA);
        Guid customerB = AddCustomer(db, TenantA);
        Guid shipToA = AddShipTo(db, TenantA, customerA);
        Guid location = AddLocation(db, TenantA, "VEHICLE");
        Guid trip = AddTrip(db, TenantA, location);
        Guid sku = AddSku(db, TenantA);

        ExpectConstraint(db, """
            INSERT dbo.AOrder(Id,TenantId,OrderNumber,CustomerId,ShipToSnapshotId,BusinessDate)
            VALUES(@id,@tenant,@no,@customer,@shipTo,CONVERT(date,GETUTCDATE()))
            """, new
        {
            id = Guid.NewGuid(), tenant = TenantA, no = NewCode(),
            customer = customerB, shipTo = shipToA
        }, 547);

        Guid validOrder = AddOrder(db, TenantA, customerA, shipToA, trip);
        Guid line = AddOrderLine(db, TenantA, validOrder, sku);
        Assert.Equal(1, db.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.AOrderLine WHERE Id=@id AND TenantId=@tenant",
            new { id = line, tenant = TenantA }));

        Guid sourceLine = AddSourceLine(db, TenantA, sku, customerA, trip);
        ExpectConstraint(db, """
            INSERT dbo.BCreditGrant(Id,TenantId,TripId,CustomerId,ShipToSnapshotId,
                PoolCode,CurrencyUnit,BusinessSourceLineId,GrantedAmount)
            VALUES(@id,@tenant,@trip,@customer,@shipTo,N'B',N'SYNTHETIC_UNITLESS',@source,100)
            """, new
        {
            id = Guid.NewGuid(), tenant = TenantA, trip, customer = customerB,
            shipTo = shipToA, source = sourceLine
        }, 547);

        Guid grantId = Guid.NewGuid();
        db.Execute("""
            INSERT dbo.BCreditGrant(Id,TenantId,TripId,CustomerId,ShipToSnapshotId,
                PoolCode,CurrencyUnit,BusinessSourceLineId,GrantedAmount)
            VALUES(@id,@tenant,@trip,@customer,@shipTo,N'B',N'SYNTHETIC_UNITLESS',@source,100)
            """, new
        {
            id = grantId, tenant = TenantA, trip, customer = customerA,
            shipTo = shipToA, source = sourceLine
        });
        Assert.Equal(100m, db.ExecuteScalar<decimal>(
            "SELECT GrantedAmount FROM dbo.BCreditGrant WHERE Id=@id",
            new { id = grantId }));
        ExpectConstraint(db, """
            UPDATE dbo.BCreditGrant SET ConsumedAmount=101 WHERE Id=@id
            """, new { id = grantId }, 547);
    }

    [Fact]
    public void Stock_right_source_and_A_B_kind_constraints_hold()
    {
        using var db = new SqlConnection(fixture.TenantAConnectionString);
        Guid customer = AddCustomer(db, TenantA);
        Guid shipTo = AddShipTo(db, TenantA, customer);
        Guid vehicle = AddLocation(db, TenantA, "VEHICLE");
        Guid sku = AddSku(db, TenantA);
        Guid trip = AddTrip(db, TenantA, vehicle);
        Guid order = AddOrder(db, TenantA, customer, shipTo, trip);
        Guid orderLine = AddOrderLine(db, TenantA, order, sku);
        Guid sourceLine = AddSourceLine(db, TenantA, sku, customer, trip);
        Guid aRight = AddStockRight(db, TenantA, trip, vehicle, sku, sourceLine, "A");

        db.Execute("""
            INSERT dbo.AReservation(Id,TenantId,StockRightId,AOrderLineId)
            VALUES(@id,@tenant,@right,@line)
            """, new { id = Guid.NewGuid(), tenant = TenantA, right = aRight, line = orderLine });

        // A right cannot be attached to BQuota even with a valid matching tenant.
        ExpectConstraint(db, """
            INSERT dbo.BQuota(Id,TenantId,StockRightId)
            VALUES(@id,@tenant,@right)
            """, new { id = Guid.NewGuid(), tenant = TenantA, right = aRight }, 547);

        // A physical right source cannot be issued twice, including to the other mode.
        ExpectConstraint(db, """
            INSERT dbo.StockRight(Id,TenantId,TripId,StockLocationId,SkuId,
                BusinessSourceLineId,RightKind,GrantedBaseQuantity)
            VALUES(@id,@tenant,@trip,@location,@sku,@source,'B',10)
            """, new
        {
            id = Guid.NewGuid(), tenant = TenantA, trip, location = vehicle, sku, source = sourceLine
        }, 2627, 2601);

        ExpectConstraint(db, """
            UPDATE dbo.StockRight SET ConsumedBaseQuantity=11 WHERE Id=@id
            """, new { id = aRight }, 547);

        Guid bSource = AddSourceLine(db, TenantA, sku, customer, trip);
        Guid bRight = AddStockRight(db, TenantA, trip, vehicle, sku, bSource, "B");
        db.Execute("INSERT dbo.BQuota(Id,TenantId,StockRightId) VALUES(@id,@tenant,@right)",
            new { id = Guid.NewGuid(), tenant = TenantA, right = bRight });
        ExpectConstraint(db, """
            INSERT dbo.AReservation(Id,TenantId,StockRightId,AOrderLineId)
            VALUES(@id,@tenant,@right,@line)
            """, new { id = Guid.NewGuid(), tenant = TenantA, right = bRight, line = orderLine }, 547);
    }

    [Fact]
    public void Command_source_and_ledger_ids_prevent_duplicate_database_effects()
    {
        using var db = new SqlConnection(fixture.TenantAConnectionString);
        Guid sku = AddSku(db, TenantA);
        Guid command = AddCommand(db, TenantA, "client-one", "command-once");
        ExpectConstraint(db, """
            INSERT dbo.BusinessCommand(Id,TenantId,ProducerId,CommandId,PayloadHashVersion,
                PayloadHash,ActorId,Status)
            VALUES(@id,@tenant,N'client-one',N'command-once',1,@hash,N'actor',N'RECEIVED')
            """, new
        {
            id = Guid.NewGuid(), tenant = TenantA,
            hash = SHA256.HashData(Guid.NewGuid().ToByteArray())
        }, 2627, 2601);

        Guid sourceLine = AddSourceLine(db, TenantA, sku, commandId: command);
        Guid effect = Guid.NewGuid();
        db.Execute("""
            INSERT dbo.LedgerEffect(Id,TenantId,BusinessSourceLineId,EffectKey,
                LedgerKind,SkuId,QuantityDelta)
            VALUES(@id,@tenant,@line,N'STOCK-OUT',N'STOCK',@sku,-5)
            """, new { id = effect, tenant = TenantA, line = sourceLine, sku });

        ExpectConstraint(db, """
            INSERT dbo.LedgerEffect(Id,TenantId,BusinessSourceLineId,EffectKey,
                LedgerKind,SkuId,QuantityDelta)
            VALUES(@id,@tenant,@line,N'STOCK-OUT',N'STOCK',@sku,-5)
            """, new { id = Guid.NewGuid(), tenant = TenantA, line = sourceLine, sku }, 2627, 2601);
        ExpectConstraint(db, """
            INSERT dbo.LedgerEffect(Id,TenantId,BusinessSourceLineId,EffectKey,LedgerKind)
            VALUES(@id,@tenant,@line,N'EMPTY',N'STOCK')
            """, new { id = Guid.NewGuid(), tenant = TenantA, line = sourceLine }, 547);

        Assert.Equal(1, db.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.LedgerEffect WHERE TenantId=@tenant AND BusinessSourceLineId=@line",
            new { tenant = TenantA, line = sourceLine }));

        // No command outcome, stock reservation or financial posting is implemented by this PR.
        Assert.Equal("RECEIVED", db.ExecuteScalar<string>(
            "SELECT Status FROM dbo.BusinessCommand WHERE Id=@id", new { id = command }));
        Assert.Equal(0, db.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.LedgerEffect WHERE LedgerKind IN (N'AR',N'COGS') AND BusinessSourceLineId=@line",
            new { line = sourceLine }));
    }

    private static Guid AddCustomer(SqlConnection db, string tenant)
    {
        Guid id = Guid.NewGuid();
        db.Execute("INSERT dbo.Customer(Id,TenantId,Code,Name) VALUES(@id,@tenant,@code,N'Test Customer')",
            new { id, tenant, code = NewCode() });
        return id;
    }

    private static Guid AddShipTo(SqlConnection db, string tenant, Guid customer)
    {
        Guid id = Guid.NewGuid();
        db.Execute("""
            INSERT dbo.ShipToSnapshot(Id,TenantId,CustomerId,DisplayName,Address)
            VALUES(@id,@tenant,@customer,N'Test ShipTo',N'Test Address')
            """, new { id, tenant, customer });
        return id;
    }

    private static Guid AddSku(SqlConnection db, string tenant)
    {
        Guid id = Guid.NewGuid();
        db.Execute("""
            INSERT dbo.Sku(Id,TenantId,Code,Name,BaseUom)
            VALUES(@id,@tenant,@code,N'Test SKU',N'EA')
            """, new { id, tenant, code = NewCode() });
        return id;
    }

    private static Guid AddLocation(SqlConnection db, string tenant, string kind)
    {
        Guid id = Guid.NewGuid();
        db.Execute("""
            INSERT dbo.StockLocation(Id,TenantId,Code,Kind)
            VALUES(@id,@tenant,@code,@kind)
            """, new { id, tenant, code = NewCode(), kind });
        return id;
    }

    private static Guid AddTrip(SqlConnection db, string tenant, Guid vehicle)
    {
        Guid id = Guid.NewGuid();
        db.Execute("""
            INSERT dbo.Trip(Id,TenantId,Code,BusinessDate,VehicleLocationId)
            VALUES(@id,@tenant,@code,CONVERT(date,GETUTCDATE()),@vehicle)
            """, new { id, tenant, code = NewCode(), vehicle });
        return id;
    }

    private static Guid AddOrder(SqlConnection db, string tenant, Guid customer, Guid shipTo, Guid trip)
    {
        Guid id = Guid.NewGuid();
        db.Execute("""
            INSERT dbo.AOrder(Id,TenantId,OrderNumber,CustomerId,ShipToSnapshotId,TripId,BusinessDate)
            VALUES(@id,@tenant,@number,@customer,@shipTo,@trip,CONVERT(date,GETUTCDATE()))
            """, new { id, tenant, number = NewCode(), customer, shipTo, trip });
        return id;
    }

    private static Guid AddOrderLine(SqlConnection db, string tenant, Guid order, Guid sku)
    {
        Guid id = Guid.NewGuid();
        db.Execute("""
            INSERT dbo.AOrderLine(Id,TenantId,AOrderId,LineNumber,SkuId,BaseQuantity,
                UomCode,UomToBaseFactor,UnitPrice)
            VALUES(@id,@tenant,@order,1,@sku,10,N'EA',1,15)
            """, new { id, tenant, order, sku });
        return id;
    }

    private static Guid AddCommand(SqlConnection db, string tenant, string? producer = null,
        string? clientCommand = null)
    {
        Guid id = Guid.NewGuid();
        db.Execute("""
            INSERT dbo.BusinessCommand(Id,TenantId,ProducerId,CommandId,PayloadHashVersion,
                PayloadHash,ActorId,Status)
            VALUES(@id,@tenant,@producer,@clientCommand,1,@hash,N'test-actor',N'RECEIVED')
            """, new
        {
            id, tenant,
            producer = producer ?? "test-producer",
            clientCommand = clientCommand ?? NewCode(),
            hash = SHA256.HashData(Guid.NewGuid().ToByteArray())
        });
        return id;
    }

    private static Guid AddSourceLine(SqlConnection db, string tenant, Guid sku,
        Guid? customer = null, Guid? trip = null, Guid? commandId = null)
    {
        Guid sourceId = Guid.NewGuid();
        Guid lineId = Guid.NewGuid();
        db.Execute("""
            INSERT dbo.BusinessSource(Id,TenantId,BusinessCommandId,SourceKind,SourceNumber,
                ActorId,CustomerId,TripId,OccurredAtUtc)
            VALUES(@source,@tenant,@command,N'TEST',@number,N'test-actor',
                @customer,@trip,SYSUTCDATETIME())
            """, new
        {
            source = sourceId, tenant, command = commandId ?? AddCommand(db, tenant),
            number = NewCode(), customer, trip
        });
        db.Execute("""
            INSERT dbo.BusinessSourceLine(Id,TenantId,BusinessSourceId,LineNumber,SkuId,BaseQuantity,Amount)
            VALUES(@line,@tenant,@source,1,@sku,10,100)
            """, new { line = lineId, tenant, source = sourceId, sku });
        return lineId;
    }

    private static Guid AddStockRight(SqlConnection db, string tenant, Guid trip, Guid location,
        Guid sku, Guid source, string kind)
    {
        Guid id = Guid.NewGuid();
        db.Execute("""
            INSERT dbo.StockRight(Id,TenantId,TripId,StockLocationId,SkuId,
                BusinessSourceLineId,RightKind,GrantedBaseQuantity)
            VALUES(@id,@tenant,@trip,@location,@sku,@source,@kind,10)
            """, new { id, tenant, trip, location, sku, source, kind });
        return id;
    }

    private static string NewCode() => Guid.NewGuid().ToString("N");

    private static void ExpectConstraint(SqlConnection db, string sql, object args, params int[] sqlNumbers)
    {
        SqlException exception = Assert.Throws<SqlException>(() => db.Execute(sql, args));
        Assert.Contains(exception.Number, sqlNumbers);
    }
}
