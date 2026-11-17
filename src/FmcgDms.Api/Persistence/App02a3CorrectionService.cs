using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using FmcgDms.Api.Tenancy;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Persistence;

public sealed record App02CorrectionProposalInput(
    Guid OriginalAcceptanceId, string ReasonCode, string EvidenceRef, string Note);
public sealed record App02CorrectionProposalOutcome(Guid ProposalId, string Status);
public sealed record App02CorrectionOutcome(Guid CorrectionSourceId, string Status);

/// <summary>
/// Audit-only immutable correction, never a reversal of already verified fund
/// or custody amounts. Two independent signed and currently granted actors
/// must approve a new source; the original acceptance is never modified.
/// </summary>
public sealed class App02a3CorrectionService(TenantConnections connections)
{
    private sealed class Original
    {
        public Guid AcceptanceId { get; set; }
        public Guid TripId { get; set; }
        public string CActorId { get; set; } = "";
        public string Status { get; set; } = "";
    }
    private sealed class Proposal
    {
        public Guid ProposalId { get; set; }
        public Guid OriginalAcceptanceId { get; set; }
        public string MakerActorId { get; set; } = "";
        public string CActorId { get; set; } = "";
        public Guid TripId { get; set; }
        public string Status { get; set; } = "";
        public string ReasonCode { get; set; } = "";
        public string EvidenceRef { get; set; } = "";
        public string Note { get; set; } = "";
    }

    public async Task<App02CorrectionProposalOutcome> ProposeAsync(
        ClaimsPrincipal principal, App02CorrectionProposalInput input, CancellationToken ct)
    {
        string tenant = App02a3Sql.Tenant(principal);
        string maker = App02a3Sql.Actor(principal);
        if (!principal.HasClaim("synthetic_role", "cashier_supervisor"))
            throw new UnauthorizedAccessException("Synthetic supervisor role required.");
        if (input.OriginalAcceptanceId == Guid.Empty
            || !Valid(input.ReasonCode, 64) || !Valid(input.EvidenceRef, 256)
            || !Valid(input.Note, 512))
            throw new App02InvalidEventException("CORRECTION_PROPOSAL_INVALID");

        await using SqlConnection db = await connections.OpenSyntheticScratchAsync(principal, ct);
        await using SqlTransaction tx =
            (SqlTransaction)await db.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            await App02a3Sql.RequireRole(db, tx, tenant, maker, "cashier_supervisor", ct);
            Original? original = await db.QuerySingleOrDefaultAsync<Original>(
                App02a3Sql.Cmd("""
                SELECT a.AcceptanceId,a.TripId,r.ActorId AS CActorId,p.Status
                FROM dbo.App02AcceptanceApplication a WITH(UPDLOCK,HOLDLOCK)
                JOIN dbo.App02EventReceipt r ON r.TenantId=a.TenantId
                    AND r.ReceiptId=a.CReceiptId
                JOIN dbo.App02EventProgress p ON p.TenantId=r.TenantId
                    AND p.ReceiptId=r.ReceiptId
                WHERE a.TenantId=@Tenant AND a.AcceptanceId=@Acceptance
                """, new { Tenant = tenant, Acceptance = input.OriginalAcceptanceId }, tx, ct));
            if (original is null || original.Status != "APPLIED" || original.CActorId == maker)
                throw new App02InvalidEventException("CORRECTION_ORIGINAL_NOT_ELIGIBLE");
            Guid proposal = Guid.NewGuid();
            await db.ExecuteAsync(App02a3Sql.Cmd("""
                INSERT dbo.App02CorrectionProposal(
                    ProposalId,TenantId,OriginalAcceptanceId,MakerActorId,
                    ReasonCode,EvidenceRef,Note)
                VALUES(@Proposal,@Tenant,@Original,@Maker,@Reason,@Evidence,@Note)
                """, new { Proposal = proposal, Tenant = tenant,
                    Original = input.OriginalAcceptanceId, Maker = maker,
                    Reason = input.ReasonCode, Evidence = input.EvidenceRef,
                    input.Note }, tx, ct));
            await tx.CommitAsync(ct);
            return new(proposal, "PROPOSED_NONPOSTING");
        }
        catch
        {
            try { await tx.RollbackAsync(CancellationToken.None); }
            catch (InvalidOperationException) { }
            throw;
        }
    }

    public async Task<App02CorrectionOutcome> ApproveAsync(
        ClaimsPrincipal principal, Guid proposalId, CancellationToken ct)
    {
        string tenant = App02a3Sql.Tenant(principal);
        string checker = App02a3Sql.Actor(principal);
        if (!principal.HasClaim("synthetic_role", "case_checker"))
            throw new UnauthorizedAccessException("Independent synthetic checker required.");

        await using SqlConnection db = await connections.OpenSyntheticScratchAsync(principal, ct);
        await using SqlTransaction tx =
            (SqlTransaction)await db.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            await App02a3Sql.RequireRole(db, tx, tenant, checker, "case_checker", ct);
            Proposal? proposal = await db.QuerySingleOrDefaultAsync<Proposal>(
                App02a3Sql.Cmd("""
                SELECT p.ProposalId,p.OriginalAcceptanceId,p.MakerActorId,
                    p.ReasonCode,p.EvidenceRef,p.Note,r.ActorId AS CActorId,
                    a.TripId,progress.Status
                FROM dbo.App02CorrectionProposal p WITH(UPDLOCK,HOLDLOCK)
                JOIN dbo.App02AcceptanceApplication a WITH(HOLDLOCK)
                    ON a.TenantId=p.TenantId
                    AND a.AcceptanceId=p.OriginalAcceptanceId
                JOIN dbo.App02EventReceipt r WITH(HOLDLOCK)
                    ON r.TenantId=a.TenantId AND r.ReceiptId=a.CReceiptId
                JOIN dbo.App02EventProgress progress WITH(HOLDLOCK)
                    ON progress.TenantId=r.TenantId AND progress.ReceiptId=r.ReceiptId
                WHERE p.TenantId=@Tenant AND p.ProposalId=@Proposal
                    AND NOT EXISTS (
                        SELECT 1 FROM dbo.App02CorrectionApproval approval
                        WHERE approval.TenantId=p.TenantId AND approval.ProposalId=p.ProposalId)
                """, new { Tenant = tenant, Proposal = proposalId }, tx, ct));
            if (proposal is null || proposal.Status != "APPLIED"
                || proposal.MakerActorId == checker || proposal.CActorId == checker)
                throw new App02InvalidEventException("CORRECTION_APPROVAL_NOT_ELIGIBLE");
            await App02a3Sql.RequireRole(db, tx, tenant, proposal.MakerActorId,
                "cashier_supervisor", ct);
            Guid command = Guid.NewGuid(), source = Guid.NewGuid(), line = Guid.NewGuid();
            string identity = proposal.ProposalId.ToString("N");
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(
                string.Join("\0", "APP02-CORRECTION-v1", tenant, identity,
                    proposal.OriginalAcceptanceId.ToString("N"),
                    proposal.MakerActorId, checker, proposal.ReasonCode,
                    proposal.EvidenceRef, proposal.Note)));
            await db.ExecuteAsync(App02a3Sql.Cmd("""
                INSERT dbo.BusinessCommand(
                    Id,TenantId,ProducerId,CommandId,PayloadHashVersion,
                    PayloadHash,ActorId,Status,OutcomeCode,CompletedAtUtc)
                VALUES(@Command,@Tenant,N'APP02_CORRECTION',
                    @CommandKey,1,@Hash,@Checker,N'APPLIED',
                    N'CASH_HANDOVER_CORRECTION_RECORDED',SYSUTCDATETIME());
                INSERT dbo.BusinessSource(
                    Id,TenantId,BusinessCommandId,SourceKind,SourceNumber,
                    ActorId,TripId,OccurredAtUtc)
                VALUES(@Source,@Tenant,@Command,N'CASH_HANDOVER_CORRECTION',
                    @SourceNumber,@Checker,@Trip,SYSUTCDATETIME());
                INSERT dbo.BusinessSourceLine(
                    Id,TenantId,BusinessSourceId,LineNumber,Amount)
                VALUES(@Line,@Tenant,@Source,1,0);
                INSERT dbo.App02CorrectionLink(
                    TenantId,CorrectionSourceId,OriginalAcceptanceId,
                    MakerActorId,CheckerActorId,ReasonCode,EvidenceRef)
                VALUES(@Tenant,@Source,@Original,@Maker,@Checker,@Reason,@Evidence);
                INSERT dbo.App02CorrectionApproval(
                    TenantId,ProposalId,CheckerActorId,CorrectionSourceId,ApprovedAtUtc)
                VALUES(@Tenant,@Proposal,@Checker,@Source,SYSUTCDATETIME())
                """, new { Tenant = tenant, Command = command, Hash = hash,
                    CommandKey = "APP02CORR-" + identity,
                    SourceNumber = "APP02CORR-" + identity,
                    Source = source, Line = line, Trip = proposal.TripId,
                    Checker = checker, Original = proposal.OriginalAcceptanceId,
                    Maker = proposal.MakerActorId, Reason = proposal.ReasonCode,
                    Evidence = proposal.EvidenceRef, Proposal = proposalId }, tx, ct));
            await tx.CommitAsync(ct);
            return new(source, "CORRECTION_AUDIT_RECORDED");
        }
        catch
        {
            try { await tx.RollbackAsync(CancellationToken.None); }
            catch (InvalidOperationException) { }
            throw;
        }
    }

    private static bool Valid(string? value, int max) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= max
        && value == value.Trim() && !value.Any(char.IsControl);
}
