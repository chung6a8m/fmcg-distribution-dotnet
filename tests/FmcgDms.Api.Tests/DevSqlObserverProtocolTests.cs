using System.Text.Json;
using FmcgDms.Demo;
using Xunit;

namespace FmcgDms.Api.Tests;

public sealed class DevSqlObserverProtocolTests : IDisposable
{
    private readonly string file = Path.GetTempFileName();

    private static readonly DateTimeOffset Epoch = new(2026, 11, 28, 17, 0, 0, TimeSpan.Zero);

    private void WriteProof(string kind = "sample", bool truncated = false, string source = "source",
        string run = "run", string tenant = "tenant-a", int pid = 123, int sampleMs = 1500, bool final = false)
    {
        var first = new { @event = "start", schema = "DEV_SQL_OBSERVER_V1", source, pid, observerPid = pid,
            profile = "app03-6", scope = "resume", runId = run, tenant, handshakeUtc = Epoch.AddMilliseconds(10),
            utc = Epoch.AddMilliseconds(20) };
        var available = new { @event = "available", sampleIntervalMs = 250, commandTimeoutMs = 1000,
            utc = Epoch.AddMilliseconds(100) };
        var sample = new { @event = kind, truncated, rows = Array.Empty<object>(), finalSample = final,
            startedUtc = Epoch.AddMilliseconds(200), utc = Epoch.AddMilliseconds(sampleMs),
            sqlNumber = -2, reason = "commandTimeout" };
        File.WriteAllLines(file, new[] { JsonSerializer.Serialize(first), JsonSerializer.Serialize(available),
            JsonSerializer.Serialize(sample) });
    }

    private DevSqlObserverProtocol.StartupProof? Read(int observedMs = 2000) =>
        DevSqlObserverProtocol.ReadStartup(file, "source", "run", "app03-6", "resume", "tenant-a", 123,
            Epoch, Epoch.AddMilliseconds(observedMs));

    [Theory]
    [InlineData("sample", false, "ready")]
    [InlineData("sample", true, "startedWithoutSample")]
    [InlineData("sampleUnavailable", false, "startedWithoutSample")]
    [InlineData("unavailable", false, null)]
    public void Ready_requires_identity_and_actual_sample(string kind, bool truncated, string? expected)
    {
        WriteProof(kind, truncated);
        Assert.Equal(expected, Read()?.Status);
        Assert.Null(DevSqlObserverProtocol.ReadStartup(file, "source", "other", "app03-6", "resume", "tenant-a", 123, Epoch, Epoch.AddSeconds(2)));
        Assert.Null(DevSqlObserverProtocol.ReadStartup(file, "source", "run", "app03-6", "resume", "tenant-b", 123, Epoch, Epoch.AddSeconds(2)));
        Assert.Null(DevSqlObserverProtocol.ReadStartup(file, "source", "run", "app03-6", "resume", "tenant-a", 124, Epoch, Epoch.AddSeconds(2)));
    }

    [Fact]
    public void Missing_sample_never_ready()
    {
        WriteProof();
        File.WriteAllLines(file, File.ReadAllLines(file).Take(2).ToArray());
        Assert.Null(Read());
    }

    [Theory]
    [InlineData("source")]
    [InlineData("run")]
    [InlineData("tenant")]
    [InlineData("pid")]
    [InlineData("future")]
    [InlineData("final")]
    [InlineData("malformed")]
    [InlineData("profile")]
    [InlineData("handshake")]
    public void Invalid_proof_is_never_admitted(string defect)
    {
        WriteProof(source: defect == "source" ? "wrong" : "source", run: defect == "run" ? "wrong" : "run",
            tenant: defect == "tenant" ? "tenant-b" : "tenant-a", pid: defect == "pid" ? 456 : 123,
            sampleMs: defect == "future" ? 4001 : 1500, final: defect == "final");
        if (defect == "malformed") File.WriteAllLines(file, File.ReadAllLines(file).Take(2).Append("{").ToArray());
        if (defect == "profile") File.WriteAllText(file, File.ReadAllText(file).Replace("app03-6", "dev-f01"));
        Assert.Null(defect == "handshake"
            ? DevSqlObserverProtocol.ReadStartup(file, "source", "run", "app03-6", "resume", "tenant-a", 123,
                Epoch.AddMilliseconds(11), Epoch.AddSeconds(2))
            : Read());
    }

    [Fact]
    public async Task Flushed_sample_is_admitted_without_any_stdout_callback()
    {
        WriteProof();
        int ms = 0, polls = 0;
        var decision = await DevSqlObserverProtocol.WaitForStartup(
            () => { polls++; return ms >= 1500 ? Read(ms) : null; }, () => false,
            () => TimeSpan.FromMilliseconds(ms), TimeSpan.FromSeconds(4),
            (delay, _) => { ms += (int)delay.TotalMilliseconds; return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal("ready", decision.Proof?.Status);
        Assert.Equal("none", decision.Failure);
        Assert.True(polls > 1);
        Assert.InRange(ms, 1500, 3999);
    }

    [Theory]
    [InlineData("late-read")]
    [InlineData("late-poll")]
    [InlineData("already-exited")]
    [InlineData("exits-during-read")]
    [InlineData("never-ready")]
    public async Task Deadline_and_child_liveness_fence_admission(string mode)
    {
        WriteProof();
        int ms = mode == "late-poll" ? 4000 : 2000;
        bool exited = mode == "already-exited";
        var decision = await DevSqlObserverProtocol.WaitForStartup(() =>
        {
            if (mode == "late-read") ms = 4000;
            if (mode == "exits-during-read") exited = true;
            return mode == "never-ready" ? null : Read();
        }, () => exited, () => TimeSpan.FromMilliseconds(ms), TimeSpan.FromSeconds(4),
            (delay, _) => { ms += (int)delay.TotalMilliseconds; return Task.CompletedTask; }, CancellationToken.None);
        Assert.Null(decision.Proof);
        Assert.Equal(mode.Contains("exit", StringComparison.Ordinal) ? "childExited" : "deadline", decision.Failure);
    }

    [Fact]
    public async Task Cancellation_during_proof_read_cannot_admit()
    {
        WriteProof();
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DevSqlObserverProtocol.WaitForStartup(
            () => { cancel.Cancel(); return Read(); }, () => false,
            () => TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4),
            (_, _) => Task.CompletedTask, cancel.Token));
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
