using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FmcgDms.Preprod.Operations;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FmcgDms.Preprod.Operations.SqlServer.Tests;

/// <summary>
/// Disposable Windows LocalDB only. No actual PREPROD workload principals,
/// immutable external storage, alert sink, legal archive or B.9 oracle.
/// </summary>
public sealed class B8LocalDbTests
{
    private static readonly byte[] RedactedHash =
        SHA256.HashData(Encoding.UTF8.GetBytes("ci-only-redacted-evidence"));

    [Fact]
    public async Task Physical_two_tenant_append_roles_hold_rollback_and_external_anchor_contract()
    {
        await using var f = await Fixture.CreateAsync();
        var peerBeforeA = await f.PeerHashAsync(f.PeerA);
        var peerBeforeB = await f.PeerHashAsync(f.PeerB);
        Assert.NotEqual(peerBeforeA, peerBeforeB); // CI tenants must have distinct, nonaliased content
        var entryA = Guid.NewGuid();
        Assert.Equal(1, await f.AppendAsync(f.Writer, f.TenantA, entryA));
        Assert.Equal(2, await f.AppendAsync(f.Writer, f.TenantA, Guid.NewGuid()));
        Assert.Equal(1, await f.AppendAsync(f.WriterB, f.TenantB, Guid.NewGuid()));

        // Same immutable EntryId and wrong-tenant caller never mutate head/chain.
        await Assert.ThrowsAsync<SqlException>(async () =>
        {
            await f.AppendAsync(f.Writer, f.TenantA, entryA);
        });
        await Assert.ThrowsAsync<SqlException>(async () =>
        {
            await f.AppendAsync(f.Writer, f.TenantB, Guid.NewGuid());
        });
        await Assert.ThrowsAsync<SqlException>(async () =>
        {
            await f.ExecAsAsync(f.Writer, "SELECT COUNT(*) FROM dbo.B8AuditEntry");
        });
        Assert.Equal(3, await f.AppendAsync(f.Writer, f.TenantA, Guid.NewGuid()));
        // Two independent SQL connections must serialize a single tenant append
        // without duplicate sequence or a lost update to its hash-chain head.
        var concurrentlyAppended = await Task.WhenAll(
            f.AppendAsync(f.Writer, f.TenantA, Guid.NewGuid()),
            f.AppendAsync(f.Writer, f.TenantA, Guid.NewGuid()));
        Assert.Equal(new[] { 4, 5 }, concurrentlyAppended.OrderBy(x => x).ToArray());

        // Even db_owner UPDATE/DELETE is intercepted by append-only triggers.
        await Assert.ThrowsAsync<SqlException>(async () =>
        {
            await f.ExecAsync("UPDATE dbo.B8AuditEntry SET EntrySha256=0x00 WHERE TenantId=@t",
                new SqlParameter("@t", f.TenantA));
        });
        await Assert.ThrowsAsync<SqlException>(async () =>
        {
            await f.ExecAsync("DELETE FROM dbo.B8AuditEntry WHERE TenantId=@t",
                new SqlParameter("@t", f.TenantA));
        });

        // The CI test uses an ephemeral signer only. It is NOT an independent
        // off-host anchor or PREPROD G-B04 acceptance witness.
        var rowsA = await f.ReadAsync(f.TenantA);
        var rowsB = await f.ReadAsync(f.TenantB);
        Assert.Equal(5, rowsA.Count);
        Assert.Single(rowsB);
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var anchor = new B8AuditAnchor(f.TenantA, rowsA.Count, rowsA[^1].EntrySha256,
            Guid.NewGuid(), "ci-ephemeral-v1", []);
        var digest = B8AuditVerifier.ComputeAnchorDigest(anchor);
        anchor = anchor with
        {
            Signature = signer.SignHash(digest, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
        };
        var receipt = new B8AnchorReceipt(anchor.AnchorId, digest, "ci-only/not-offhost",
            DateTime.UtcNow, true);
        var verified = B8AuditVerifier.Verify(rowsA, anchor, receipt,
            signer.ExportSubjectPublicKeyInfo());
        Assert.True(verified.CryptographicallyConsistent,
            string.Join(",", verified.Denials));
        Assert.False(verified.IsPhysicalPreprodVerified);
        Assert.Contains("B8_CHAIN_HASH_TAMPERED",
            B8AuditVerifier.Verify(
                rowsA.Select((x, i) => i == 1
                    ? x with { PayloadSha256 = new byte[32] } : x).ToArray(),
                anchor, receipt, signer.ExportSubjectPublicKeyInfo()).Denials);
        Assert.Contains("B8_CHAIN_TAIL_OR_EXTERNAL_ANCHOR_MISMATCH",
            B8AuditVerifier.Verify(rowsA.SkipLast(1).ToArray(),
                anchor, receipt, signer.ExportSubjectPublicKeyInfo()).Denials);

        // Hold writer separated from normal event writer, no table DML and no
        // legal-hold release/purge operation granted or implemented.
        await Assert.ThrowsAsync<SqlException>(async () =>
        {
            await f.HoldAsync(f.Writer, f.TenantA);
        });
        await Assert.ThrowsAsync<SqlException>(async () =>
        {
            await f.HoldAsync(f.Holder, f.TenantB);
        });
        await f.HoldAsync(f.Holder, f.TenantA);
        Assert.Equal(1, await f.ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.B8LegalHold WHERE TenantId=@t",
            new SqlParameter("@t", f.TenantA)));
        await Assert.ThrowsAsync<SqlException>(async () =>
        {
            await f.ExecAsync("DELETE FROM dbo.B8LegalHold WHERE TenantId=@t",
                new SqlParameter("@t", f.TenantA));
        });
        Assert.Equal(0, await f.ScalarAsync<int>(
            "SELECT COUNT(*) FROM sys.procedures WHERE name LIKE 'b8_%Purge%'"));

        var peerAfterA = await f.PeerHashAsync(f.PeerA);
        var peerAfterB = await f.PeerHashAsync(f.PeerB);
        Assert.Equal(peerBeforeA, peerAfterA);
        Assert.Equal(peerBeforeB, peerAfterB);
        await f.WriteWitnessAsync("APP-02B8-AUDIT-WITNESS.json", new
        {
            TestRunId = f.TestRunId, SourceHeadSha = f.SourceHead,
            SchemaVersion = B8Schema.Version,
            EvidenceClass = "GITHUB_HOSTED_DISPOSABLE_LOCALDB_ONLY",
            PhysicalTenantDatabases = 2,
            IndependentAuditDatabase = true,
            AppendOnlyTriggerDenied = true,
            CrossTenantRoleDenied = true,
            DuplicateRollbackNoGap = true,
            ConcurrentAppendAtomic = true,
            PeerDistinct = peerBeforeA != peerBeforeB,
            CrossLanguageSha256ChainVerified = verified.CryptographicallyConsistent,
            TamperAndTruncationDetected = true,
            LegalHoldImmutable = true,
            PurgeEndpointAbsent = true,
            PeerAHashBefore = peerBeforeA, PeerAHashAfter = peerAfterA,
            PeerBHashBefore = peerBeforeB, PeerBHashAfter = peerAfterB,
            OffHostAnchor = "NOT_PERFORMED",
            RealPreprod = "NOT_VERIFIED", G_B04 = "NOT_RUN"
        });
    }

    [Fact]
    public async Task Recorded_migration_cannot_mask_dropped_guard_or_unknown_version()
    {
        await using var f = await Fixture.CreateAsync();
        await f.ExecAsync(f.MigrationSql); // replay is idempotent only when intact
        Assert.Equal(1, await f.ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.__B8SchemaHistory WHERE SchemaVersion=@v",
            new SqlParameter("@v", B8Schema.Version)));
        await f.ExecAsync("DISABLE TRIGGER dbo.b8_ImmutableAudit ON dbo.B8AuditEntry");
        var disabled = await Assert.ThrowsAsync<SqlException>(async () =>
        {
            await f.ExecAsync(f.MigrationSql);
        });
        Assert.Contains("B8_RECORDED_SCHEMA_CORRUPTED", disabled.Message);
        await f.ExecAsync("ENABLE TRIGGER dbo.b8_ImmutableAudit ON dbo.B8AuditEntry");
        await f.ExecAsync(f.MigrationSql);
        await f.ExecAsync("DROP TRIGGER dbo.b8_ImmutableAudit");
        var denied = await Assert.ThrowsAsync<SqlException>(async () =>
        {
            await f.ExecAsync(f.MigrationSql);
        });
        Assert.Contains("B8_RECORDED_SCHEMA_CORRUPTED", denied.Message);
        await f.ExecAsync(
            "INSERT dbo.__B8SchemaHistory(SchemaVersion) VALUES(N'B8_UNKNOWN')");
        var deniedUnknown = await Assert.ThrowsAsync<SqlException>(async () =>
        {
            await f.ExecAsync(f.MigrationSql);
        });
        Assert.Contains("B8_UNKNOWN_SCHEMA_VERSION_DENIED", deniedUnknown.Message);
        await f.WriteWitnessAsync("APP-02B8-SCHEMA-WITNESS.json", new
        {
            TestRunId=f.TestRunId, SourceHeadSha=f.SourceHead,
            SchemaVersion=B8Schema.Version, IntactReplayIdempotent=true,
            MissingTriggerReplayDenied=true, DisabledTriggerReplayDenied=true,
            UnknownVersionReplayDenied=true,
            EvidenceClass="GITHUB_HOSTED_DISPOSABLE_LOCALDB_ONLY",
            RealPreprod="NOT_VERIFIED", G_B04="NOT_RUN"
        });
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private const string Server = @"(localdb)\MSSQLLocalDB";
        public Guid TenantA { get; } = Guid.NewGuid();
        public Guid TenantB { get; } = Guid.NewGuid();
        public string PeerA => "FmcgPpT_" + TenantA.ToString("N");
        public string PeerB => "FmcgPpT_" + TenantB.ToString("N");
        public string Control { get; } = "B8CI_" + Guid.NewGuid().ToString("N");
        public string Writer { get; } = "b8w_" + Guid.NewGuid().ToString("N")[..12];
        public string WriterB { get; } = "b8b_" + Guid.NewGuid().ToString("N")[..12];
        public string Holder { get; } = "b8h_" + Guid.NewGuid().ToString("N")[..12];
        public string SourceHead => Environment.GetEnvironmentVariable("APP02B8_SOURCE_HEAD_SHA")
            ?? "LOCAL_SOURCE_ONLY";
        public string TestRunId => Environment.GetEnvironmentVariable("APP02B8_TEST_RUN_ID")
            ?? "LOCAL_UNVERIFIED";
        public string MigrationSql { get; }

        private Fixture()
        {
            var requested = Environment.GetEnvironmentVariable("APP02B8_SQL_SERVER");
            if (requested != Server)
                throw new InvalidOperationException("B8_LOCALDB_ONLY_NO_PREPROD_TARGET");
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            string? path = null;
            while (directory != null)
            {
                var candidate = Path.Combine(directory.FullName,
                    "src", "FmcgDms.Preprod.Operations", "Migrations",
                    "20261118_001_immutable_audit.sql");
                if (File.Exists(candidate)) { path = candidate; break; }
                directory = directory.Parent;
            }
            MigrationSql = File.ReadAllText(path ??
                throw new FileNotFoundException("B8 operator SQL source missing"));
        }

        private static string ConnectionString(string db) =>
            $"Data Source={Server};Initial Catalog={db};Integrated Security=True;Encrypt=False;TrustServerCertificate=True;Pooling=False";

        private async Task<SqlConnection> OpenAsync(string db = "")
        {
            var connection = new SqlConnection(ConnectionString(db.Length == 0 ? Control : db));
            await connection.OpenAsync();
            return connection;
        }

        public static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture();
            await f.ExecMasterAsync($"CREATE DATABASE [{f.Control}];");
            await f.ExecMasterAsync($"CREATE DATABASE [{f.PeerA}];");
            await f.ExecMasterAsync($"CREATE DATABASE [{f.PeerB}];");
            await f.ExecAsync(f.MigrationSql);
            foreach (var name in new[] { f.Writer, f.WriterB, f.Holder })
            {
                // Synthetic throwaway LocalDB logins, no real credential or
                // operator secret is embedded in the repository.
                var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)) + "!aA1";
                await f.ExecMasterAsync($"CREATE LOGIN [{name}] WITH PASSWORD=N'{password}', CHECK_POLICY=OFF");
                await f.ExecAsync($"CREATE USER [{name}] FOR LOGIN [{name}]");
            }
            await f.ExecAsync($"ALTER ROLE b8_writer ADD MEMBER [{f.Writer}];" +
                              $"ALTER ROLE b8_writer ADD MEMBER [{f.WriterB}];" +
                              $"ALTER ROLE b8_hold ADD MEMBER [{f.Holder}];");
            await f.ExecAsync(@"INSERT dbo.B8WriterScope(TenantId,LoginName,CanAppend,CanHold,Enabled)
                VALUES (@a,@writer,1,0,1),(@b,@writerB,1,0,1),(@a,@holder,0,1,1)",
                new SqlParameter("@a", f.TenantA), new SqlParameter("@b", f.TenantB),
                new SqlParameter("@writer", f.Writer),
                new SqlParameter("@writerB", f.WriterB),
                new SqlParameter("@holder", f.Holder));
            foreach (var peer in new[] { f.PeerA, f.PeerB })
            {
                await using var con = await f.OpenAsync(peer);
                using var cmd = con.CreateCommand();
                cmd.CommandText = "CREATE TABLE dbo.B8CiPeerSentinel(Id int NOT NULL PRIMARY KEY, Value nvarchar(64) NOT NULL);" +
                    "INSERT dbo.B8CiPeerSentinel VALUES(1,@tenantMarker);";
                cmd.Parameters.AddWithValue("@tenantMarker",
                    peer == f.PeerA ? f.TenantA.ToString("N") : f.TenantB.ToString("N"));
                await cmd.ExecuteNonQueryAsync();
            }
            return f;
        }

        public async Task<int> AppendAsync(string login, Guid tenant, Guid entry)
        {
            await using var con = await OpenAsync();
            using var cmd = con.CreateCommand();
            cmd.CommandText = $"EXECUTE AS LOGIN = N'{login}';" +
                "EXEC dbo.b8_Append @TenantId,@EntryId,@Kind,@CorrelationId,@SourceId," +
                "@PolicyVersionId,@MessageId,@PayloadSha256; REVERT;";
            cmd.Parameters.AddWithValue("@TenantId", tenant);
            cmd.Parameters.AddWithValue("@EntryId", entry);
            cmd.Parameters.AddWithValue("@Kind", (short)B8AuditKind.DurableIntake);
            cmd.Parameters.AddWithValue("@CorrelationId", Guid.NewGuid());
            cmd.Parameters.AddWithValue("@SourceId", Guid.NewGuid());
            cmd.Parameters.AddWithValue("@PolicyVersionId", DBNull.Value);
            cmd.Parameters.AddWithValue("@MessageId", Guid.NewGuid());
            cmd.Parameters.Add("@PayloadSha256", SqlDbType.Binary, 32).Value = RedactedHash;
            var result = await cmd.ExecuteScalarAsync();
            return Convert.ToInt32(result);
        }

        public async Task HoldAsync(string login, Guid tenant)
        {
            await using var con = await OpenAsync();
            using var cmd = con.CreateCommand();
            cmd.CommandText = $"EXECUTE AS LOGIN = N'{login}';" +
                "EXEC dbo.b8_PlaceHold @TenantId,@HoldId,@EvidenceClass," +
                "@ManifestSha256,@AuthoritySha256; REVERT;";
            cmd.Parameters.AddWithValue("@TenantId", tenant);
            cmd.Parameters.AddWithValue("@HoldId", Guid.NewGuid());
            cmd.Parameters.AddWithValue("@EvidenceClass", (byte)B8EvidenceClass.DurableEventAndCase);
            cmd.Parameters.Add("@ManifestSha256", SqlDbType.Binary, 32).Value = RedactedHash;
            cmd.Parameters.Add("@AuthoritySha256", SqlDbType.Binary, 32).Value = RedactedHash;
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task ExecAsAsync(string login, string sql)
        {
            await ExecAsync($"EXECUTE AS LOGIN = N'{login}'; {sql}; REVERT;");
        }

        public async Task ExecAsync(string sql, params SqlParameter[] parameters)
        {
            await using var con = await OpenAsync();
            using var cmd = con.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = 60;
            cmd.Parameters.AddRange(parameters);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<T> ScalarAsync<T>(string sql, params SqlParameter[] parameters)
        {
            await using var con = await OpenAsync();
            using var cmd = con.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddRange(parameters);
            return (T)Convert.ChangeType((await cmd.ExecuteScalarAsync())!, typeof(T));
        }

        private async Task ExecMasterAsync(string sql)
        {
            await using var con = await OpenAsync("master");
            using var cmd = con.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = 60;
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<List<B8AuditEntry>> ReadAsync(Guid tenant)
        {
            var result = new List<B8AuditEntry>();
            await using var con = await OpenAsync();
            using var cmd = con.CreateCommand();
            cmd.CommandText = @"SELECT TenantId,Sequence,EntryId,Kind,PrincipalSha256,
                CorrelationId,SourceId,PolicyVersionId,MessageId,PayloadSha256,
                PreviousSha256,EntrySha256 FROM dbo.B8AuditEntry
                WHERE TenantId=@tenant ORDER BY Sequence";
            cmd.Parameters.AddWithValue("@tenant", tenant);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                result.Add(new B8AuditEntry(reader.GetGuid(0), reader.GetInt64(1),
                    reader.GetGuid(2), (B8AuditKind)reader.GetInt16(3), (byte[])reader[4],
                    reader.GetGuid(5), reader.IsDBNull(6) ? null : reader.GetGuid(6),
                    reader.IsDBNull(7) ? null : reader.GetGuid(7),
                    reader.IsDBNull(8) ? null : reader.GetGuid(8),
                    (byte[])reader[9], (byte[])reader[10], (byte[])reader[11]));
            return result;
        }

        public async Task<string> PeerHashAsync(string database)
        {
            await using var con = await OpenAsync(database);
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT CONCAT(Id,':',Value) FROM dbo.B8CiPeerSentinel ORDER BY Id";
            var witness = (string?)await cmd.ExecuteScalarAsync();
            return Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(witness ?? "MISSING")));
        }

        public async Task WriteWitnessAsync(string file, object value)
        {
            var output = Path.Combine("TestResults", file);
            Directory.CreateDirectory("TestResults");
            await File.WriteAllTextAsync(output,
                JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var db in new[] { Control, PeerA, PeerB })
            {
                try
                {
                    await ExecMasterAsync($"ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;" +
                                          $"DROP DATABASE [{db}];");
                }
                catch { /* disposable runner cleanup best effort, not PREPROD */ }
            }
            foreach (var login in new[] { Writer, WriterB, Holder })
            {
                try { await ExecMasterAsync($"DROP LOGIN [{login}]"); }
                catch { /* disposable runner cleanup */ }
            }
        }
    }
}
