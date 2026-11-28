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

    public void Dispose() => File.Delete(file);
}
