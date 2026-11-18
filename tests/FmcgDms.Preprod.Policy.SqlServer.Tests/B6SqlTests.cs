using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FmcgDms.Preprod.Policy;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FmcgDms.Preprod.Policy.SqlServer.Tests;

/// <summary>
/// Disposable CI-only SQL witness, NOT real PREPROD nor independent B.9 oracle.
/// The fixture controls its own DBA principals and synthetic policy bounds.
/// </summary>
public sealed class B6SqlTests
{
    private const string Code = "fixture.approved";
    private const string Locked = "fixture.locked";

    [Fact]
    public async Task Two_tenant_policy_zero_CASE_immutability_and_stale_snapshot_matrix()
    {
        var ct = CancellationToken.None;
        await using var f = await Fixture.CreateAsync(ct);
        await f.SeedDefinitionAsync(Code, 0, 1, true, true, ct);
        await f.SeedDefinitionAsync(Locked, 0, 0, false, false, ct);
        var beforeA = await f.PeerHashAsync(f.TenantADatabase, ct);
        var beforeB = await f.PeerHashAsync(f.TenantBDatabase, ct);
        var source = Guid.NewGuid();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("CI-only synthetic approval-zero"));
        var proposal = Guid.NewGuid();
        var start = DateTime.UtcNow.AddSeconds(10);
        var until = start.AddHours(1);

        // Explicit UNSET can be recorded as source intent, NEVER approved/resolved.
        await f.ProposeAsync(f.MakerA, f.TenantA, Guid.NewGuid(), Code,
            "L1", null, "UNSET", null, start, until, Guid.NewGuid(), hash, ct);
        var unsetProposal = f.LastProposal;
        await Assert.ThrowsAsync<SqlException>(() =>
            f.ApproveAsync(f.CheckerA, f.TenantA, unsetProposal, ct));
        Assert.Equal(0, await f.CountSnapshotsAsync(f.TenantA, ct));

        // No missing L0, override of non-overridable L0, out-of-bounds, or backdate.
        await Assert.ThrowsAsync<SqlException>(() => f.ProposeAsync(f.MakerA,
            f.TenantA, Guid.NewGuid(), "fixture.unknown", "L1", null, "SET", 0,
            start, until, source, hash, ct));
        await Assert.ThrowsAsync<SqlException>(() => f.ProposeAsync(f.MakerA,
            f.TenantA, Guid.NewGuid(), Locked, "L1", null, "SET", 0,
            start, until, source, hash, ct));
        await Assert.ThrowsAsync<SqlException>(() => f.ProposeAsync(f.MakerA,
            f.TenantA, Guid.NewGuid(), Code, "L1", null, "SET", 2,
            start, until, source, hash, ct));
        await Assert.ThrowsAsync<SqlException>(() => f.ProposeAsync(f.MakerA,
            f.TenantA, Guid.NewGuid(), Code, "L1", null, "SET", 0,
            DateTime.UtcNow.AddMinutes(-2), until, source, hash, ct));

        await f.ProposeAsync(f.MakerA, f.TenantA, proposal, Code, "L1", null,
            "SET", 0, start, until, source, hash, ct);
        // Same login, despite possessing both Maker and Checker roles, cannot self-approve.
        var dualProposal = Guid.NewGuid();
        await f.ProposeAsync(f.DualA, f.TenantA, dualProposal, Code, "L1",
            null, "SET", 0, start, until, Guid.NewGuid(), hash, ct);
        await Assert.ThrowsAsync<SqlException>(() =>
            f.ApproveAsync(f.DualA, f.TenantA, dualProposal, ct));

        // Wrong tenant/operator, wrong authorization path and direct table reads deny.
        await Assert.ThrowsAsync<SqlException>(() => f.ProposeAsync(f.MakerA,
            f.TenantB, Guid.NewGuid(), Code, "L1", null, "SET", 0,
            start, until, source, hash, ct));
        await Assert.ThrowsAsync<SqlException>(() =>
            f.DirectReadAsAsync(f.MakerA, ct));
        await Assert.ThrowsAsync<SqlException>(() =>
            f.ApproveAsync(f.CheckerB, f.TenantA, proposal, ct));
        await Assert.ThrowsAsync<SqlException>(() =>
            f.ResolveAsync(f.ReaderA, f.TenantB, proposal, hash, ct));

        await f.ApproveAsync(f.CheckerA, f.TenantA, proposal, ct);
        await Assert.ThrowsAsync<SqlException>(() =>
            f.ApproveAsync(f.CheckerA, f.TenantA, proposal, ct)); // single use
        Assert.Equal(1, await f.CountSnapshotsAsync(f.TenantA, ct));
        await Assert.ThrowsAsync<SqlException>(() =>
            f.ResolveAsync(f.ReaderA, f.TenantA, Guid.NewGuid(), hash, ct));
        await Assert.ThrowsAsync<SqlException>(() =>
            f.ResolveAsync(f.ReaderA, f.TenantA, proposal, new byte[32], ct));

        await f.WaitUntilAsync(start, ct);
        var zero = await f.ResolveAsync(f.ReaderA, f.TenantA, proposal, hash, ct);
        Assert.Equal(0m, zero.Amount);
        Assert.True(zero.Explicit);
        Assert.Equal("L1", zero.Tier);

        // Overlapping L1 revision must reject approval without partial decision.
        var overlap = Guid.NewGuid();
        var nextStart = DateTime.UtcNow.AddSeconds(8);
        await f.ProposeAsync(f.MakerA, f.TenantA, overlap, Code, "L1", null,
            "SET", 1, nextStart, nextStart.AddMinutes(30), Guid.NewGuid(), hash, ct);
        await Assert.ThrowsAsync<SqlException>(() =>
            f.ApproveAsync(f.CheckerA, f.TenantA, overlap, ct));
        Assert.Equal(0, await f.CountApprovalAsync(overlap, ct));

        // L2 requires immutable approved CASE, matching tenant/source/hash.
        var caseId = Guid.NewGuid();
        var caseSource = Guid.NewGuid();
        var caseHash = SHA256.HashData(Encoding.UTF8.GetBytes("CI-only CASE lineage"));
        await f.OpenCaseAsync(f.CaseMakerA, f.TenantA, caseId, caseSource, caseHash, ct);
        await Assert.ThrowsAsync<SqlException>(() =>
            f.ApproveCaseAsync(f.CaseMakerA, f.TenantA, caseId, ct));
        var l2 = Guid.NewGuid();
        var l2Start = DateTime.UtcNow.AddSeconds(8);
        await f.ProposeAsync(f.MakerA, f.TenantA, l2, Code, "L2", caseId,
            "SET", 1, l2Start, l2Start.AddHours(1), caseSource, caseHash, ct);
        await Assert.ThrowsAsync<SqlException>(() =>
            f.ApproveAsync(f.CheckerA, f.TenantA, l2, ct));
        await f.ApproveCaseAsync(f.CaseCheckerA, f.TenantA, caseId, ct);
        await Assert.ThrowsAsync<SqlException>(() =>
            f.ApproveCaseAsync(f.CaseCheckerA, f.TenantA, caseId, ct));
        await f.ApproveAsync(f.CheckerA, f.TenantA, l2, ct);
        await f.WaitUntilAsync(l2Start, ct);

        var overrideValue = await f.ResolveAsync(f.ReaderA, f.TenantA, l2, caseHash, ct, caseId);
        Assert.Equal(1m, overrideValue.Amount);
        Assert.Equal("L2", overrideValue.Tier);
        // A CASE-specific override shadows L1 only for that CASE, not globally.
        // Without a CASE, L1 remains current, even when L2 is active elsewhere.
        Assert.Equal(0m, (await f.ResolveAsync(f.ReaderA, f.TenantA, proposal, hash, ct)).Amount);
        await Assert.ThrowsAsync<SqlException>(() =>
            f.ResolveAsync(f.ReaderA, f.TenantA, l2, caseHash, ct));

        // Re-labeling a previously approved source revision with a fresh ProposalId
        // must not bypass the one-use source/version boundary.
        var sourceReplayError = await Assert.ThrowsAsync<SqlException>(() => f.ProposeAsync(f.MakerA,
            f.TenantA, Guid.NewGuid(), Code, "L1", null, "SET", 1m,
            start.AddHours(2), until.AddHours(2), source, hash, ct));
        Assert.Contains(sourceReplayError.Number, new[] { 2601, 2627 });

        // An independent CASE may overlap in time without changing the policy
        // for the first CASE or the default tenant L1 policy.
        var secondCase = Guid.NewGuid();
        var secondSource = Guid.NewGuid();
        var secondHash = SHA256.HashData(Encoding.UTF8.GetBytes("CI-only independent CASE"));
        await f.OpenCaseAsync(f.CaseMakerA, f.TenantA, secondCase, secondSource, secondHash, ct);
        await f.ApproveCaseAsync(f.CaseCheckerA, f.TenantA, secondCase, ct);
        var secondProposal = Guid.NewGuid();
        var secondStart = DateTime.UtcNow.AddSeconds(8);
        await f.ProposeAsync(f.MakerA, f.TenantA, secondProposal, Code, "L2",
            secondCase, "SET", 0.5m, secondStart, secondStart.AddMinutes(30),
            secondSource, secondHash, ct);
        await f.ApproveAsync(f.CheckerA, f.TenantA, secondProposal, ct);
        await f.WaitUntilAsync(secondStart, ct);
        Assert.Equal(1m, (await f.ResolveAsync(f.ReaderA, f.TenantA,
            l2, caseHash, ct, caseId)).Amount);
        Assert.Equal(0.5m, (await f.ResolveAsync(f.ReaderA, f.TenantA,
            secondProposal, secondHash, ct, secondCase)).Amount);
        Assert.Equal(0m, (await f.ResolveAsync(f.ReaderA, f.TenantA,
            proposal, hash, ct)).Amount);
        var wrongCaseError = await Assert.ThrowsAsync<SqlException>(() => f.ResolveAsync(f.ReaderA,
            f.TenantA, l2, caseHash, ct, secondCase));
        Assert.Contains("B6_UNSET_STALE_OR_AMBIGUOUS_DENIED", wrongCaseError.Message);

        // A new revision for the SAME CASE must not allow overlapping windows.
        var overlapInCase = Guid.NewGuid();
        var nextCaseStart = DateTime.UtcNow.AddSeconds(8);
        await f.ProposeAsync(f.MakerA, f.TenantA, overlapInCase, Code, "L2",
            caseId, "SET", 0.75m, nextCaseStart, nextCaseStart.AddMinutes(30),
            caseSource, caseHash, ct, sourceVersion: 2);
        var sameCaseOverlapError = await Assert.ThrowsAsync<SqlException>(() =>
            f.ApproveAsync(f.CheckerA, f.TenantA, overlapInCase, ct));
        Assert.Contains("B6_OVERLAPPING_VERSION_DENIED", sameCaseOverlapError.Message);
        Assert.Equal(0, await f.CountApprovalAsync(overlapInCase, ct));

        // A different physical tenant independently approves explicit zero.
        var bId = Guid.NewGuid();
        var bHash = SHA256.HashData(Encoding.UTF8.GetBytes("CI-only tenant B"));
        var bStart = DateTime.UtcNow.AddSeconds(8);
        await f.ProposeAsync(f.MakerB, f.TenantB, bId, Code, "L1", null,
            "SET", 0, bStart, bStart.AddMinutes(10), Guid.NewGuid(), bHash, ct);
        await f.ApproveAsync(f.CheckerB, f.TenantB, bId, ct);
        await f.WaitUntilAsync(bStart, ct);
        Assert.Equal(0m, (await f.ResolveAsync(f.ReaderB, f.TenantB, bId, bHash, ct)).Amount);

        await f.AssertImmutableAsync(ct);
        var afterA = await f.PeerHashAsync(f.TenantADatabase, ct);
        var afterB = await f.PeerHashAsync(f.TenantBDatabase, ct);
        Assert.Equal(beforeA, afterA);
        Assert.Equal(beforeB, afterB);
        Assert.Equal(0, await f.PeerRowEffectCountAsync(f.TenantADatabase, ct));
        Assert.Equal(0, await f.PeerRowEffectCountAsync(f.TenantBDatabase, ct));
        Assert.True(await f.DifferentDatabasesAsync(ct));

        await f.WriteEvidenceAsync("APP-02B6-POLICY-WITNESS.json", new
        {
            TestRunId = f.TestRunId, SourceHeadSha = f.SourceHead,
            SchemaVersion = B6Schema.Version, EvidenceClass="GITHUB_HOSTED_LOCALDB_ONLY",
            PhysicalTenantDatabases = 2, IndependentPolicyDatabase = true,
            DistinctDatabaseIds = true, L0Immutable = true, L0BoundsEnforced = true,
            UnsetDenied = true, ExplicitApprovedZeroRetained = zero.Amount == 0m && zero.Explicit,
            MakerCheckerSeparated = true, CaseSourcePinned = true,
            OverlapAtomicDenied = true, CrossTenantDenied = true,
            StaleSnapshotDenied = true, SourceHistoryImmutable = true,
            CaseScopedL2 = true, ConcurrentSeparateCasesAllowed = true,
            SameCaseOverlapDenied = true, SourceRevisionReplayDenied = true,
            TenantAHashBefore = beforeA, TenantAHashAfter = afterA,
            TenantBHashBefore = beforeB, TenantBHashAfter = afterB,
            RealPreprod="NOT_VERIFIED", GB01ToGB05="NOT_RUN"
        }, ct);
    }

    [Fact]
    public async Task Recorded_schema_version_does_not_mask_removed_authority_procedure()
    {
        var ct = CancellationToken.None;
        await using var f = await Fixture.CreateAsync(ct);
        await f.RunAsDbaAsync(f.PolicyDatabase,
            "DROP PROCEDURE dbo.b6_Approve", ct);
        var error = await Assert.ThrowsAsync<SqlException>(() => f.ReplayMigrationAsync(ct));
        Assert.Contains("B6_SCHEMA_INTEGRITY_DENIED", error.Message);
        await f.WriteEvidenceAsync("APP-02B6-SCHEMA-WITNESS.json", new
        {
            TestRunId=f.TestRunId,SourceHeadSha=f.SourceHead,SchemaVersion=B6Schema.Version,
            RecordedVersionTamperDenied = true, EvidenceClass="GITHUB_HOSTED_LOCALDB_ONLY",
            RealPreprod="NOT_VERIFIED",GB01ToGB05="NOT_RUN"
        }, ct);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string server;
        private readonly List<string> databases = [];
        private readonly List<string> logins = [];
        public Guid TenantA { get; } = Guid.NewGuid();
        public Guid TenantB { get; } = Guid.NewGuid();
        public string PolicyDatabase { get; }
        public string TenantADatabase { get; }
        public string TenantBDatabase { get; }
        public string MakerA { get; }
        public string CheckerA { get; }
        public string ReaderA { get; }
        public string MakerB { get; }
        public string CheckerB { get; }
        public string ReaderB { get; }
        public string DualA { get; }
        public string CaseMakerA { get; }
        public string CaseCheckerA { get; }
        public Guid LastProposal { get; private set; }
        public string TestRunId { get; }
        public string SourceHead { get; }
        private string MasterConnection => Connection("master");

        private Fixture(string sqlServer, string nonce, string runId, string sourceHead)
        {
            server=sqlServer; TestRunId=runId; SourceHead=sourceHead;
            PolicyDatabase="FmcgB6Policy_"+nonce;
            TenantADatabase="FmcgB6TenA_"+nonce;
            TenantBDatabase="FmcgB6TenB_"+nonce;
            MakerA="b6makerA_"+nonce;CheckerA="b6checkerA_"+nonce;ReaderA="b6readerA_"+nonce;
            MakerB="b6makerB_"+nonce;CheckerB="b6checkerB_"+nonce;ReaderB="b6readerB_"+nonce;
            DualA="b6dualA_"+nonce;CaseMakerA="b6caseMakerA_"+nonce;
            CaseCheckerA="b6caseCheckerA_"+nonce;
        }

        public static async Task<Fixture> CreateAsync(CancellationToken ct)
        {
            var server=Environment.GetEnvironmentVariable("APP02B6_SQL_SERVER");
            if (string.IsNullOrWhiteSpace(server) ||
                !server.StartsWith("(localdb)\\", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("B.6 tests require disposable Windows SQL LocalDB");
            var runId=Environment.GetEnvironmentVariable("APP02B6_TEST_RUN_ID")
                ?? throw new InvalidOperationException("Missing TestRunId");
            var head=Environment.GetEnvironmentVariable("APP02B6_SOURCE_HEAD_SHA")
                ?? throw new InvalidOperationException("Missing source HEAD");
            var f=new Fixture(server,Guid.NewGuid().ToString("N")[..10],runId,head);
            foreach(var name in new[] {f.PolicyDatabase,f.TenantADatabase,f.TenantBDatabase})
                await f.CreateDatabaseAsync(name,ct);
            await f.ReplayMigrationAsync(ct);
            await f.ReplayMigrationAsync(ct);
            foreach(var actor in new[]{f.MakerA,f.CheckerA,f.ReaderA,f.MakerB,f.CheckerB,
                                      f.ReaderB,f.DualA,f.CaseMakerA,f.CaseCheckerA})
                await f.CreateLoginAsync(actor,ct);
            foreach(var actor in f.logins)
                await f.RunAsDbaAsync(f.PolicyDatabase,
                    "CREATE USER ["+actor+"] FOR LOGIN ["+actor+"];",
                    ct);
            foreach(var (actor,role) in new[]{
                (f.MakerA,"b6_maker"),(f.CheckerA,"b6_checker"),(f.ReaderA,"b6_reader"),
                (f.MakerB,"b6_maker"),(f.CheckerB,"b6_checker"),(f.ReaderB,"b6_reader"),
                (f.DualA,"b6_maker"),(f.DualA,"b6_checker"),
                (f.CaseMakerA,"b6_case_maker"),(f.CaseCheckerA,"b6_case_checker")})
                await f.RunAsDbaAsync(f.PolicyDatabase,
                    "ALTER ROLE "+role+" ADD MEMBER ["+actor+"];",ct);
            foreach(var (actor,tenant,prop,approve,resolve,openCase,approveCase) in new[]{
                (f.MakerA,f.TenantA,true,false,false,false,false),
                (f.CheckerA,f.TenantA,false,true,false,false,false),
                (f.ReaderA,f.TenantA,false,false,true,false,false),
                (f.MakerB,f.TenantB,true,false,false,false,false),
                (f.CheckerB,f.TenantB,false,true,false,false,false),
                (f.ReaderB,f.TenantB,false,false,true,false,false),
                (f.DualA,f.TenantA,true,true,false,false,false),
                (f.CaseMakerA,f.TenantA,false,false,false,true,false),
                (f.CaseCheckerA,f.TenantA,false,false,false,false,true)})
                await f.ExecAsync(f.PolicyDatabase,null,
                    @"INSERT dbo.B6OperatorScope
                     (TenantId,LoginName,Enabled,CanPropose,CanApprove,CanResolve,
                      CanOpenCase,CanApproveCase)
                     VALUES(@tenant,@login,1,@propose,@approve,@resolve,@open,@caseapprove)",
                    ct,Arg("@tenant",tenant),Arg("@login",actor),Arg("@propose",prop),
                    Arg("@approve",approve),Arg("@resolve",resolve),Arg("@open",openCase),
                    Arg("@caseapprove",approveCase));
            foreach(var db in new[]{f.TenantADatabase,f.TenantBDatabase})
                await f.RunAsDbaAsync(db,
                    "CREATE TABLE dbo.B6PeerWitness(Id int NOT NULL PRIMARY KEY, Payload nvarchar(20) NOT NULL);" +
                    "INSERT dbo.B6PeerWitness(Id,Payload) VALUES(1,N'ORIGINAL');",ct);
            return f;
        }

        public async Task SeedDefinitionAsync(string code,decimal min,decimal max,
            bool l1,bool l2,CancellationToken ct) =>
            await ExecAsync(PolicyDatabase,null,
                @"INSERT dbo.B6L0Policy
                    (PolicyCode,FloorValue,CeilingValue,AllowL1,AllowL2,AuthorityRef,DefinitionHash)
                  VALUES(@code,@min,@max,@l1,@l2,N'CI_SYNTHETIC_ONLY',@hash)",ct,
                Arg("@code",code),Arg("@min",min),Arg("@max",max),
                Arg("@l1",l1),Arg("@l2",l2),
                Arg("@hash",SHA256.HashData(Encoding.UTF8.GetBytes(code))));

        public async Task ProposeAsync(string login,Guid tenant,Guid id,string code,string tier,
            Guid? caseId,string state,decimal? value,DateTime from,DateTime until,
            Guid source,byte[] hash,CancellationToken ct,long sourceVersion=1)
        {
            LastProposal=id;
            await ExecAsync(PolicyDatabase,login,"dbo.b6_Propose",ct,
                Arg("@TenantId",tenant),Arg("@ProposalId",id),Arg("@PolicyCode",code),
                Arg("@Tier",tier),ArgNullGuid("@CaseId",caseId),Arg("@ValueState",state),
                ArgNullDecimal("@NumericValue",value),Arg("@EffectiveFromUtc",from),
                Arg("@EffectiveUntilUtc",until),Arg("@SourceId",source),
                Arg("@SourceHash",hash),Arg("@SourceVersion",sourceVersion));
        }

        public Task ApproveAsync(string login,Guid tenant,Guid proposal,CancellationToken ct) =>
            ExecAsync(PolicyDatabase,login,"dbo.b6_Approve",ct,
                Arg("@TenantId",tenant),Arg("@ProposalId",proposal));

        public Task OpenCaseAsync(string login,Guid tenant,Guid id,Guid source,byte[] hash,
            CancellationToken ct) =>
            ExecAsync(PolicyDatabase,login,"dbo.b6_OpenCase",ct,
                Arg("@TenantId",tenant),Arg("@CaseId",id),
                Arg("@SourceId",source),Arg("@SourceHash",hash));

        public Task ApproveCaseAsync(string login,Guid tenant,Guid id,CancellationToken ct) =>
            ExecAsync(PolicyDatabase,login,"dbo.b6_ApproveCase",ct,
                Arg("@TenantId",tenant),Arg("@CaseId",id));

        public async Task<(decimal Amount,bool Explicit,string Tier)> ResolveAsync(
            string login,Guid tenant,Guid expected,byte[] hash,CancellationToken ct,
            Guid? caseId=null)
        {
            await using var db=new SqlConnection(Connection(PolicyDatabase));
            await db.OpenAsync(ct);
            await ImpersonateAsync(db,login,ct);
            await using var cmd=new SqlCommand("dbo.b6_ResolveCurrent",db)
                {CommandType=CommandType.StoredProcedure};
            cmd.Parameters.AddRange([
                Arg("@TenantId",tenant),Arg("@PolicyCode",Code),
                Arg("@ExpectedProposalId",expected),Arg("@ExpectedSourceHash",hash),
                ArgNullGuid("@ExpectedCaseId",caseId)
            ]);
            await using var reader=await cmd.ExecuteReaderAsync(ct);
            Assert.True(await reader.ReadAsync(ct));
            var amount=reader.GetDecimal(reader.GetOrdinal("NumericValue"));
            var explicitValue=reader.GetBoolean(reader.GetOrdinal("IsExplicitlySet"));
            var tier=reader.GetString(reader.GetOrdinal("Tier")).Trim();
            var caseOrdinal=reader.GetOrdinal("CaseId");
            var resolvedCase=reader.IsDBNull(caseOrdinal)
                ? (Guid?)null : reader.GetGuid(caseOrdinal);
            Assert.Equal(tier=="L2" ? caseId : null, resolvedCase);
            Assert.False(await reader.ReadAsync(ct));
            return(amount,explicitValue,tier);
        }

        public async Task DirectReadAsAsync(string login,CancellationToken ct) =>
            await ExecAsync(PolicyDatabase,login,
                "SELECT COUNT(*) FROM dbo.B6PolicySnapshot",ct);

        public Task<int> CountSnapshotsAsync(Guid tenant,CancellationToken ct) =>
            CountAsync("SELECT COUNT(*) FROM dbo.B6PolicySnapshot WHERE TenantId=@id",tenant,ct);

        public Task<int> CountApprovalAsync(Guid id,CancellationToken ct) =>
            CountAsync("SELECT COUNT(*) FROM dbo.B6PolicyApproval WHERE ProposalId=@id",id,ct);

        private async Task<int> CountAsync(string sql,Guid id,CancellationToken ct)
        {
            await using var db=new SqlConnection(Connection(PolicyDatabase));
            await db.OpenAsync(ct);
            await using var cmd=new SqlCommand(sql,db);
            cmd.Parameters.Add(Arg("@id",id));
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        }

        public async Task AssertImmutableAsync(CancellationToken ct)
        {
            foreach(var statement in new[]{
                "UPDATE dbo.B6L0Policy SET FloorValue=0 WHERE PolicyCode=N'fixture.approved'",
                "DELETE FROM dbo.B6L0Policy WHERE PolicyCode=N'fixture.approved'",
                "UPDATE dbo.B6PolicyProposal SET ValueState='UNSET' WHERE PolicyCode=N'fixture.approved'",
                "DELETE FROM dbo.B6PolicyApproval",
                "UPDATE dbo.B6PolicySnapshot SET NumericValue=9",
                "DELETE FROM dbo.B6CaseSource",
                "DELETE FROM dbo.B6CaseApproval"})
                await Assert.ThrowsAsync<SqlException>(() =>
                    RunAsDbaAsync(PolicyDatabase,statement,ct));
        }

        public async Task<string> PeerHashAsync(string database,CancellationToken ct)
        {
            if(database!=TenantADatabase && database!=TenantBDatabase)
                throw new InvalidOperationException("CI tenant scope denied");
            await using var db=new SqlConnection(Connection(database));
            await db.OpenAsync(ct);
            await using var cmd=new SqlCommand(
                "SELECT (SELECT Id,Payload FROM dbo.B6PeerWitness ORDER BY Id FOR JSON PATH)",db);
            var text=Convert.ToString(await cmd.ExecuteScalarAsync(ct)) ?? "[]";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        }

        public async Task<int> PeerRowEffectCountAsync(string db,CancellationToken ct)
        {
            await using var c=new SqlConnection(Connection(db));
            await c.OpenAsync(ct);
            await using var cmd=new SqlCommand(
                "SELECT COUNT(*) FROM dbo.B6PeerWitness WHERE Id<>1 OR Payload<>N'ORIGINAL'",c);
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        }

        public async Task<bool> DifferentDatabasesAsync(CancellationToken ct)
        {
            await using var db=new SqlConnection(MasterConnection);
            await db.OpenAsync(ct);
            var ids=new HashSet<int>();
            foreach(var database in new[]{PolicyDatabase,TenantADatabase,TenantBDatabase})
            {
                await using var cmd=new SqlCommand("SELECT DB_ID(@name)",db);
                cmd.Parameters.Add(Arg("@name",database));
                ids.Add(Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)));
            }
            return ids.Count==3;
        }

        public async Task WaitUntilAsync(DateTime start,CancellationToken ct)
        {
            var wait=start.AddMilliseconds(200)-DateTime.UtcNow;
            if(wait>TimeSpan.Zero) await Task.Delay(wait,ct);
        }

        public Task WriteEvidenceAsync(string file,object witness,CancellationToken ct)
        {
            Directory.CreateDirectory("TestResults");
            return File.WriteAllTextAsync(Path.Combine("TestResults",file),
                JsonSerializer.Serialize(witness,new JsonSerializerOptions{WriteIndented=true}),ct);
        }

        public async Task ReplayMigrationAsync(CancellationToken ct)
        {
            var assembly=typeof(B6Schema).Assembly;
            var resource=assembly.GetManifestResourceNames().Single(
                name=>name.EndsWith("20261118_001_policy_registry.sql",StringComparison.Ordinal));
            using var reader=new StreamReader(assembly.GetManifestResourceStream(resource)!);
            await RunAsDbaAsync(PolicyDatabase,await reader.ReadToEndAsync(ct),ct);
        }

        public Task RunAsDbaAsync(string db,string sql,CancellationToken ct) =>
            ExecAsync(db,null,sql,ct);

        private async Task ExecAsync(string database,string? login,string commandText,
            CancellationToken ct,params SqlParameter[] args)
        {
            await using var db=new SqlConnection(Connection(database));
            await db.OpenAsync(ct);
            if(login is not null) await ImpersonateAsync(db,login,ct);
            await using var cmd=new SqlCommand(commandText,db)
            {
                CommandType=commandText.StartsWith("dbo.b6_",StringComparison.Ordinal)
                    ? CommandType.StoredProcedure:CommandType.Text,CommandTimeout=90
            };
            cmd.Parameters.AddRange(args);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        private async Task ImpersonateAsync(SqlConnection db,string login,CancellationToken ct)
        {
            if(!logins.Contains(login,StringComparer.Ordinal))
                throw new InvalidOperationException("Unknown CI login");
            await using var cmd=new SqlCommand("EXECUTE AS LOGIN=N'"+login+"'",db);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        private static SqlParameter Arg(string name,object value) => new(name,value);
        private static SqlParameter ArgNullGuid(string name,Guid? id) =>
            new(name,SqlDbType.UniqueIdentifier){Value=id.HasValue?id.Value:DBNull.Value};
        private static SqlParameter ArgNullDecimal(string name,decimal? amount) =>
            new(name,SqlDbType.Decimal){Precision=19,Scale=4,Value=amount.HasValue?amount.Value:DBNull.Value};

        private string Connection(string database) => new SqlConnectionStringBuilder
        {
            DataSource=server,InitialCatalog=database,IntegratedSecurity=true,
            Encrypt=SqlConnectionEncryptOption.Optional,TrustServerCertificate=true,Pooling=false
        }.ConnectionString;

        private async Task CreateDatabaseAsync(string database,CancellationToken ct)
        {
            if(!database.StartsWith("FmcgB6",StringComparison.Ordinal))
                throw new InvalidOperationException("Unsafe CI database name");
            await RunAsDbaAsync("master","CREATE DATABASE ["+database+"]",ct);
            databases.Add(database);
        }

        private async Task CreateLoginAsync(string login,CancellationToken ct)
        {
            var password=Convert.ToHexString(RandomNumberGenerator.GetBytes(20))+"aA1!";
            await RunAsDbaAsync("master",
                "CREATE LOGIN ["+login+"] WITH PASSWORD=N'"+password+"',CHECK_POLICY=OFF",ct);
            logins.Add(login);
        }

        public async ValueTask DisposeAsync()
        {
            SqlConnection.ClearAllPools();
            foreach(var database in databases.AsEnumerable().Reverse())
            {
                try
                {
                    await RunAsDbaAsync("master",
                        "IF DB_ID(N'"+database+"') IS NOT NULL BEGIN "+
                        "ALTER DATABASE ["+database+"] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;"+
                        "DROP DATABASE ["+database+"]; END",CancellationToken.None);
                }
                catch(SqlException) { /* only disposable CI DB */ }
            }
            foreach(var login in logins.AsEnumerable().Reverse())
            {
                try { await RunAsDbaAsync("master","DROP LOGIN ["+login+"]",CancellationToken.None); }
                catch(SqlException) { /* only disposable CI login */ }
            }
        }
    }
}
