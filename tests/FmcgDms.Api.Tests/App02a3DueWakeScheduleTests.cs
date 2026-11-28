using FmcgDms.Api.Persistence;
using Xunit;

namespace FmcgDms.Api.Tests;

public sealed class App02a3DueWakeScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 28, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Replaces_stale_hints_and_selects_each_tenant_at_its_own_due_time()
    {
        var schedule = new App02a3DueWakeSchedule();
        Assert.Null(schedule.Next);
        schedule.SetFromSql("tenant-a", Now.AddSeconds(60), Now);
        schedule.SetFromSql("tenant-b", Now.AddSeconds(30), Now);
        Assert.Equal(Now.AddSeconds(30), schedule.Next);
        Assert.Null(schedule.TakeDue(Now.AddSeconds(29)));
        // An event sweep discovers an earlier committed deadline.
        schedule.SetFromSql("tenant-a", Now.AddSeconds(10), Now);
        Assert.Equal(Now.AddSeconds(10), schedule.Next);
        Assert.Equal("tenant-a", schedule.TakeDue(Now.AddSeconds(10))?.Tenant);
        Assert.Equal(Now.AddSeconds(30), schedule.Next);
        schedule.SetFromSql("tenant-b", null, Now.AddSeconds(11)); // terminal SQL row
        Assert.Null(schedule.Next);
        Assert.Null(schedule.TakeDue(Now.AddHours(1)));
    }

    [Fact]
    public void Overdue_work_yields_and_discovery_failure_only_retries_the_read()
    {
        var schedule = new App02a3DueWakeSchedule();
        schedule.SetFromSql("tenant-a", Now.AddSeconds(-1), Now);
        schedule.SetFromSql("tenant-b", Now.AddSeconds(-2), Now);
        Assert.Null(schedule.TakeDue(Now));
        var later = Now.Add(App02a3DueWakeSchedule.OverdueYield);
        var first = Assert.IsType<App02a3DueWakeSchedule.Wake>(schedule.TakeDue(later));
        Assert.False(first.RefreshOnly);
        // A still-overdue batch yields instead of starving the other tenant.
        schedule.SetFromSql(first.Tenant, Now.AddSeconds(-1), later);
        var second = Assert.IsType<App02a3DueWakeSchedule.Wake>(schedule.TakeDue(later));
        Assert.NotEqual(first.Tenant, second.Tenant);
        schedule.SetFromSql(first.Tenant, Now.AddSeconds(-1), later, sweepFailed: true);
        schedule.SetReadFailure(second.Tenant, later);
        var retryAt = later.Add(App02a3DueWakeSchedule.ReadFailureDelay);
        Assert.Null(schedule.TakeDue(retryAt.AddMilliseconds(-1)));
        var ready = new[] {
            Assert.IsType<App02a3DueWakeSchedule.Wake>(schedule.TakeDue(retryAt)),
            Assert.IsType<App02a3DueWakeSchedule.Wake>(schedule.TakeDue(retryAt))
        };
        Assert.Single(ready, x => x.RefreshOnly && x.Tenant == second.Tenant);
        Assert.Single(ready, x => !x.RefreshOnly && x.Tenant == first.Tenant);
        Assert.Null(schedule.Next);
        Assert.Throws<ArgumentOutOfRangeException>(() => schedule.SetReadFailure("foreign", Now));
    }

    [Fact]
    public async Task Timer_wait_does_not_consume_event_and_cancellation_does_not_leave_a_reader()
    {
        var signal = new App02a3WorkerSignal();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        Assert.True(await signal.WaitForWakeAsync(DateTimeOffset.UtcNow.AddMilliseconds(50), deadline.Token));
        Assert.False(signal.TryRead(out _));

        var wait = signal.WaitForWakeAsync(DateTimeOffset.UtcNow.AddSeconds(30), deadline.Token);
        signal.Wake("tenant-b");
        Assert.True(await wait);
        Assert.True(signal.TryRead(out string? tenant));
        Assert.Equal("tenant-b", tenant);

        using var stop = new CancellationTokenSource();
        var cancelled = signal.WaitForWakeAsync(null, stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await cancelled; });
        signal.Wake("tenant-a");
        Assert.True(await signal.WaitForWakeAsync(null, deadline.Token));
        Assert.True(signal.TryRead(out tenant));
        Assert.Equal("tenant-a", tenant);
    }
}
