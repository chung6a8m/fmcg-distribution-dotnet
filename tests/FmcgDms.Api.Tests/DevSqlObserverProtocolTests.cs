using System.Text.Json;
using FmcgDms.Demo;
using Xunit;

namespace FmcgDms.Api.Tests;

public sealed class DevSqlObserverProtocolTests : IDisposable
{
    private readonly string file = Path.GetTempFileName();

    [Theory]
    [InlineData("sample", false, "ready")]
    [InlineData("sample", true, "startedWithoutSample")]
    [InlineData("sampleUnavailable", false, "startedWithoutSample")]
    [InlineData("unavailable", false, null)]
    public void Ready_requires_identity_and_actual_sample(string kind, bool truncated, string? expected)
    {
        var start = new { @event = "start", pid = 123, profile = "app03-6", scope = "resume", runId = "run", tenant = "tenant-a" };
        File.WriteAllLines(file, new[] { JsonSerializer.Serialize(start), "{\"event\":\"available\"}",
            JsonSerializer.Serialize(new { @event = kind, truncated }) });
        Assert.Equal(expected is not null, DevSqlObserverProtocol.VerifyStartup(file, "run", "app03-6", "resume", "tenant-a", 123, out string actual));
        if (expected is not null) Assert.Equal(expected, actual);
        Assert.False(DevSqlObserverProtocol.VerifyStartup(file, "other", "app03-6", "resume", "tenant-a", 123, out _));
        Assert.False(DevSqlObserverProtocol.VerifyStartup(file, "run", "app03-6", "resume", "tenant-b", 123, out _));
        Assert.False(DevSqlObserverProtocol.VerifyStartup(file, "run", "app03-6", "resume", "tenant-a", 124, out _));
    }

    [Fact]
    public void Missing_sample_never_ready()
    {
        File.WriteAllText(file, "{\"event\":\"start\"}\n{\"event\":\"available\"}\n");
        Assert.False(DevSqlObserverProtocol.VerifyStartup(file, "run", "app03-6", "resume", "tenant-a", 123, out _));
    }

    [Theory]
    [InlineData(1, "exited", 1)]
    [InlineData(2, "jobClosed", 2)]
    [InlineData(3, "killed", 3)]
    [InlineData(4, "unconfirmed", 3)]
    public async Task Reap_bounds_all_waits(int exitsAt, string expected, int count)
    {
        int stage = 0;
        var never = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        string actual = await DevSqlObserverProtocol.Reap(() => stage++, () => stage++, () => stage++,
            () => stage == exitsAt ? Task.CompletedTask : never.Task, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20));
        Assert.Equal(expected, actual);
        Assert.Equal(count, stage);
    }

    [Fact]
    public async Task Failed_stop_and_job_close_still_try_kill()
    {
        int killed = 0;
        string actual = await DevSqlObserverProtocol.Reap(() => throw new IOException(),
            () => throw new IOException(), () => killed++, () => Task.CompletedTask,
            TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20));
        Assert.Equal("killed", actual);
        Assert.Equal(1, killed);
    }

    [Theory]
    [InlineData("app03-5", "resume", true)]
    [InlineData("app03-6", "resume", true)]
    [InlineData("dev-f01", "initial", true)]
    [InlineData("dev-f06", "initial", true)]
    [InlineData("app02d2", "initial", true)]
    [InlineData("app02d2", "D2-07-A", true)]
    [InlineData("app02d2", "D2-07-B", true)]
    [InlineData("app02d2", "D2-14-A", true)]
    [InlineData("app02d2", "D2-14-B", true)]
    [InlineData("app02d2", "D2-15-A", true)]
    [InlineData("app02d2", "D2-15-B", true)]
    [InlineData("app02d2", "A-to-B", true)]
    [InlineData("app02d2", "B-to-A", true)]
    [InlineData("dev-f01", "D2-07-A", false)]
    [InlineData("dev-f06", "resume", false)]
    [InlineData("app03-6", "initial", false)]
    [InlineData("app02d2", "D2-07-C", false)]
    [InlineData("app02d2", "../D2-07-A", false)]
    [InlineData("../dev-f01", "initial", false)]
    [InlineData("DEV-F01", "initial", false)]
    [InlineData("dev-f02", "initial", false)]
    public void Scope_is_strictly_profile_bound(string profile, string scope, bool expected)
        => Assert.Equal(expected, DevSqlObserverProtocol.IsScope(profile, scope));

    [Theory]
    [InlineData("initial", true)]
    [InlineData("origin-seed", true)]
    [InlineData("peer-seed", true)]
    [InlineData("peer-checks", true)]
    [InlineData("seed", true)]
    [InlineData("ack", true)]
    [InlineData("restart-lookup", true)]
    [InlineData("replay-checks", true)]
    [InlineData("channel-off", true)]
    [InlineData("channel-on", true)]
    [InlineData("evidence-foreign-seed", true)]
    [InlineData("evidence-prepare", true)]
    [InlineData("evidence-cash-missing", true)]
    [InlineData("evidence-cash-amount", true)]
    [InlineData("evidence-cash-pending", true)]
    [InlineData("evidence-cash-future", true)]
    [InlineData("evidence-cash-self", true)]
    [InlineData("evidence-cash-foreign", true)]
    [InlineData("evidence-bank-missing", true)]
    [InlineData("evidence-bank-pending", true)]
    [InlineData("evidence-credit-missing", true)]
    [InlineData("evidence-bank-binding", true)]
    [InlineData("evidence-credit-origin", true)]
    [InlineData("evidence-credit-reserve", true)]
    [InlineData("evidence-credit-excess", true)]
    [InlineData("evidence-credit-foreign", true)]
    [InlineData("evidence-posting", true)]
    [InlineData("evidence-cash-reuse", true)]
    [InlineData("evidence-credit-reuse", true)]
    [InlineData("../seed", false)]
    [InlineData("evidence-cash-unknown", false)]
    [InlineData("private-customer", false)]
    [InlineData("EVIDENCE-CASH-MISSING", false)]
    [InlineData("", false)]
    public void Phase_is_fixed_and_redacted(string phase, bool expected)
        => Assert.Equal(expected, DevSqlObserverProtocol.IsD2Phase(phase));

    public void Dispose() => File.Delete(file);
}
