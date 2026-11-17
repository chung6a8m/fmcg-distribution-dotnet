using System.Data;
using System.Security.Claims;
using Dapper;
using FmcgDms.Api.Tenancy;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Persistence;

public sealed record App02CaseProposalInput(Guid CaseId, string Decision,
    string ReasonCode, string EvidenceRef, string ProposedResolution);
public sealed record App02CaseProposalOutcome(Guid ProposalId, Guid CaseId, string Status);
public sealed record App02CaseApprovalOutcome(Guid CaseId, Guid DispositionId, string Status);

/// <summary>
/// Two independent authenticated calls; nobody can self-approve via a single
/// payload. Both claimed role and currently active tenant SQL grants are checked.
/// A CASE approval NEVER directly applies money; a resumed C is re-processed.
/// </summary>
public sealed class App02a3CaseService(
    TenantConnections connections, App02a3WorkerSignal signal)
{
    private sealed class ProposalRow
    {
        public Guid ProposalId { get; set; }
        public Guid CaseId { get; set; }
        public string MakerActorId { get; set; } = "";
        public string Decision { get; set; } = "";
        public string ReasonCode { get; set; } = "";
        public string EvidenceRef { get; set; } = "";
        public string ProposedResolution { get; set; } = "";
        public string Status { get; set; } = "";
        public Guid? ActiveCaseId { get; set; }
        public string CActorId { get; set; } = "";
    }

    public async Task<App02CaseProposalOutcome> ProposeAsync(
        ClaimsPrincipal principal, App02CaseProposalInput request, CancellationToken ct)
    {
        string tenant = App02a3Sql.Tenant(principal);
        string maker = App02a3Sql.Actor(principal);
        if (!principal.HasClaim("synthetic_role", "cashier_supervisor"))
            throw new UnauthorizedAccessException("Synthetic supervisor required.");
        if (request.Decision is not ("HOLD" or "CLOSE" or "APPROVED_RESUME")
            || request.CaseId == Guid.Empty
            || !Present(request.ReasonCode, 64)
            || !Present(request.EvidenceRef, 256)
            || !Present(request.ProposedResolution, 512))
            throw new App02InvalidEventException("CASE_PROPOSAL_INVALID");
        await using SqlConnection db = await connections.OpenSyntheticScratchAsync(principal, ct);
        await using SqlTransaction tx =
            (SqlTransaction)await db.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            await App02a3Sql.RequireRole(db, tx, tenant, maker, "cashier_supervisor", ct);
            var current = await db.QuerySingleOrDefaultAsync<ProposalRow>(App02a3Sql.Cmd("""
                SELECT c.CaseId,p.Status,p.ActiveCaseId,r.ActorId AS CActorId
                FROM dbo.App02ExceptionCase c WITH(UPDLOCK,HOLDLOCK)
                JOIN dbo.App02EventProgress p WITH(UPDLOCK,HOLDLOCK)
                    ON p.TenantId=c.TenantId AND p.ReceiptId=c.ReceiptId
                JOIN dbo.App02EventReceipt r ON r.TenantId=c.TenantId
                    AND r.ReceiptId=c.ReceiptId
                WHERE c.TenantId=@Tenant AND c.CaseId=@CaseId
                """, new { Tenant = tenant, request.CaseId }, tx, ct));
            if (current is null || current.Status != "CASE"
                || current.ActiveCaseId != request.CaseId || current.CActorId == maker)
                throw new App02InvalidEventException("CASE_NOT_ACTIVE_OR_MAKER_INVALID");
            Guid proposal = Guid.NewGuid();
            await db.ExecuteAsync(App02a3Sql.Cmd("""
                INSERT dbo.App02CaseProposal(
                    ProposalId,TenantId,CaseId,MakerActorId,Decision,
                    ReasonCode,EvidenceRef,ProposedResolution,ProposedAtUtc)
                VALUES(@Proposal,@Tenant,@CaseId,@Maker,@Decision,
                    @Reason,@Evidence,@Resolution,SYSUTCDATETIME())
                """, new { Proposal = proposal, Tenant = tenant, request.CaseId,
                    Maker = maker, request.Decision, Reason = request.ReasonCode,
                    Evidence = request.EvidenceRef,
                    Resolution = request.ProposedResolution }, tx, ct));
            await tx.CommitAsync(ct);
            return new(proposal, request.CaseId, "PROPOSED");
        }
        catch
        {
            try { await tx.RollbackAsync(CancellationToken.None); }
            catch (InvalidOperationException) { }
            throw;
        }
    }

    public async Task<App02CaseApprovalOutcome> ApproveAsync(
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
            ProposalRow? proposal = await db.QuerySingleOrDefaultAsync<ProposalRow>(
                App02a3Sql.Cmd("""
                SELECT proposal.ProposalId,proposal.CaseId,proposal.MakerActorId,
                    proposal.Decision,proposal.ReasonCode,proposal.EvidenceRef,
                    proposal.ProposedResolution,p.Status,p.ActiveCaseId,
                    r.ActorId AS CActorId
                FROM dbo.App02CaseProposal proposal WITH(UPDLOCK,HOLDLOCK)
                JOIN dbo.App02ExceptionCase c WITH(UPDLOCK,HOLDLOCK)
                    ON c.TenantId=proposal.TenantId AND c.CaseId=proposal.CaseId
                JOIN dbo.App02EventProgress p WITH(UPDLOCK,HOLDLOCK)
                    ON p.TenantId=c.TenantId AND p.ReceiptId=c.ReceiptId
                JOIN dbo.App02EventReceipt r WITH(HOLDLOCK)
                    ON r.TenantId=c.TenantId AND r.ReceiptId=c.ReceiptId
                WHERE proposal.TenantId=@Tenant AND proposal.ProposalId=@Proposal
                    AND NOT EXISTS(
                        SELECT 1 FROM dbo.App02CaseApprovalLink link
                        WHERE link.TenantId=proposal.TenantId
                          AND link.ProposalId=proposal.ProposalId)
                """, new { Tenant = tenant, Proposal = proposalId }, tx, ct));
            if (proposal is null || proposal.Status != "CASE"
                || proposal.ActiveCaseId != proposal.CaseId
                || proposal.MakerActorId == checker || proposal.CActorId == checker)
                throw new App02InvalidEventException("CASE_APPROVAL_NOT_ELIGIBLE");
            await App02a3Sql.RequireRole(db, tx, tenant, proposal.MakerActorId,
                "cashier_supervisor", ct);
            Guid disposition = Guid.NewGuid();
            await db.ExecuteAsync(App02a3Sql.Cmd("""
                INSERT dbo.App02CaseDisposition(
                    DispositionId,TenantId,CaseId,MakerActorId,CheckerActorId,
                    Decision,ReasonCode,EvidenceRef,ProposedResolution,ApprovedAtUtc)
                VALUES(@Disposition,@Tenant,@CaseId,@Maker,@Checker,
                    @Decision,@Reason,@Evidence,@Resolution,SYSUTCDATETIME());
                INSERT dbo.App02CaseApprovalLink(
                    TenantId,ProposalId,DispositionId,ApprovedAtUtc)
                VALUES(@Tenant,@Proposal,@Disposition,SYSUTCDATETIME())
                """, new { Disposition = disposition, Tenant = tenant,
                    Proposal = proposalId, proposal.CaseId,
                    Maker = proposal.MakerActorId, Checker = checker,
                    proposal.Decision, Reason = proposal.ReasonCode,
                    Evidence = proposal.EvidenceRef,
                    Resolution = proposal.ProposedResolution }, tx, ct));
            string state = proposal.Decision switch
            {
                "APPROVED_RESUME" => "RECEIVED",
                "CLOSE" => "REJECTED",
                _ => "CASE"
            };
            if (state != "CASE")
                await db.ExecuteAsync(App02a3Sql.Cmd("""
                    UPDATE dbo.App02EventProgress SET Status=@State,
                        ReasonCode=@Reason,NextAttemptAtUtc=NULL,
                        StateChangedAtUtc=SYSUTCDATETIME()
                    WHERE TenantId=@Tenant AND ActiveCaseId=@CaseId
                      AND Status=N'CASE'
                    """, new { State = state, Tenant = tenant, proposal.CaseId,
                        Reason = state == "RECEIVED" ? null : proposal.ReasonCode }, tx, ct));
            await tx.CommitAsync(ct);
            if (state == "RECEIVED") signal.Wake(tenant);
            return new(proposal.CaseId, disposition, state);
        }
        catch
        {
            try { await tx.RollbackAsync(CancellationToken.None); }
            catch (InvalidOperationException) { }
            throw;
        }
    }

    private static bool Present(string? value, int max) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= max
        && value == value.Trim() && !value.Any(char.IsControl);
}
