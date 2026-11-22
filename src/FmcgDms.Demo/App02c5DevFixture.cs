using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

internal static class App02c5DevFixture
{
    internal static async Task VerifyCurrentAuthority(DemoRun run, HttpClient client, string webUrl,
        string token, string actor, CancellationToken ct)
    {
        const string tenant = "tenant-a";
        await run.Verify(tenant);
        await using var db = new SqlConnection(run.Connection(tenant));
        using var original = await Report("/ar");
        original.EnsureSuccessStatusCode();
        using var originalBody = JsonDocument.Parse(await original.Content.ReadAsStringAsync(ct));
        string cutoff = originalBody.RootElement.GetProperty("asOf").GetString()!;
        string pinned = "/ar?download=true&asOf=" + Uri.EscapeDataString(cutoff);
        const string grant = """
            UPDATE dbo.App02ActorRoleGrant SET IsActive=@active,ChangedBy=N'c5-dev-oracle',ChangedAtUtc=SYSUTCDATETIME()
            WHERE TenantId=@tenant AND ActorId=@actor AND RoleCode=N'sales'
            """;
        const string assignment = """
            UPDATE dbo.C4DeliveryWriter SET ActorId=@writer
            WHERE TenantId=@tenant AND TripId=(SELECT Id FROM dbo.Trip WHERE TenantId=@tenant AND Code=N'C2-TRIP')
            """;
        try
        {
            if (await db.ExecuteAsync(grant, new { tenant, actor, active = false }) != 1)
                throw new InvalidOperationException("Expected a single current Sales grant.");
            using var denied = await Report(pinned);
            if (denied.StatusCode != HttpStatusCode.Forbidden)
                throw new InvalidOperationException("Historical AR export bypassed revoked Sales grant.");
        }
        finally { await db.ExecuteAsync(grant, new { tenant, actor, active = true }); }
        try
        {
            if (await db.ExecuteAsync(assignment, new { tenant, writer = "c5-dev-other-writer" }) != 1)
                throw new InvalidOperationException("Expected a single current trip assignment.");
            using var unassigned = await Report(pinned);
            unassigned.EnsureSuccessStatusCode();
            using var body = JsonDocument.Parse(await unassigned.Content.ReadAsStringAsync(ct));
            if (body.RootElement.GetProperty("expected").ValueKind != JsonValueKind.Null
                || body.RootElement.GetProperty("knownBalance").ValueKind != JsonValueKind.Null
                || body.RootElement.GetProperty("sources").GetArrayLength() != 0)
                throw new InvalidOperationException("Historical AR export exposed an unassigned trip.");
        }
        finally { await db.ExecuteAsync(assignment, new { tenant, writer = actor }); }
        Console.WriteLine("APP-02C.5 historical AR export respects Sales revocation and current trip assignment.");

        async Task<HttpResponseMessage> Report(string suffix)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, webUrl + "/api/synthetic/app02c5" + suffix);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await client.SendAsync(request, ct);
        }
    }
}
