using System.Diagnostics;
using System.Threading.Channels;

namespace FmcgDms.Api.Persistence;

/// <summary>
/// Best-effort in-process wakeups are NOT the durable queue: SQL receipt,
/// progress and outbound delivery rows are the source of truth. Startup and
/// 15-minute safety scans recover a lost wakeup or an OS process restart.
/// </summary>
public sealed class App02a3WorkerSignal
{
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public void Wake(string tenant)
    {
        if (tenant is "tenant-a" or "tenant-b")
            _queue.Writer.TryWrite(tenant);
    }

    public IAsyncEnumerable<string> ReadAllAsync(CancellationToken ct) =>
        _queue.Reader.ReadAllAsync(ct);

    internal bool TryRead(out string? tenant) => _queue.Reader.TryRead(out tenant);

    internal async ValueTask<bool> WaitForWakeAsync(DateTimeOffset? due, CancellationToken ct)
    {
        if (due is null)
            return await _queue.Reader.WaitToReadAsync(ct);

        TimeSpan delay = due.Value - DateTimeOffset.UtcNow;
        if (delay <= TimeSpan.Zero) return true;
        // The safety ticker remains independent; cap the cancellable wait as
        // well so a distant SQL hint or clock change cannot disable it.
        if (delay > App02a3ProcessingService.SafetyScanInterval)
            delay = App02a3ProcessingService.SafetyScanInterval;
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(delay);
        try { return await _queue.Reader.WaitToReadAsync(timer.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timer.IsCancellationRequested)
        {
            // No orphaned Channel read: cancellation is awaited before the
            // loop rechecks both the queue and the SQL scheduling hints.
            return true;
        }
    }
}

/// <summary>
/// LocalDB-only synthetic worker. Opt-in (App02Worker:Enabled) and guarded by
/// Development/IntegrationTest host; even internal SQL access still passes
/// the strict tenant/nonce owner guard. No production worker or provider.
/// </summary>
public sealed class App02a3HostedWorker(
    IServiceScopeFactory scopes, App02a3WorkerSignal signal,
    IConfiguration config, IHostEnvironment environment,
    ILogger<App02a3HostedWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!(environment.IsDevelopment() || environment.IsEnvironment("IntegrationTest"))
            || !config.GetValue<bool>("SyntheticAuth:Enabled")
            || !config.GetValue<bool>("App02Worker:Enabled"))
            return;

        bool reverse = Rel75Probe.ReverseStartup(config, environment);
        signal.Wake(reverse ? "tenant-b" : "tenant-a");
        signal.Wake(reverse ? "tenant-a" : "tenant-b");
        var schedule = new App02a3DueWakeSchedule();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        Task ticker = SafetyTicksAsync(lifetime.Token);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                string tenant;
                // A due tenant cannot be starved behind an unbounded queue of
                // best-effort event hints. At most one hint per tenant is armed.
                if (schedule.TakeDue(DateTimeOffset.UtcNow) is { } wake)
                {
                    tenant = wake.Tenant;
                    if (wake.RefreshOnly)
                    {
                        await RefreshDueAsync(schedule, tenant, false, stoppingToken);
                        continue;
                    }
                }
                else if (signal.TryRead(out string? queued) && queued is not null)
                    tenant = queued;
                else
                {
                    if (!await signal.WaitForWakeAsync(schedule.Next, stoppingToken))
                        break;
                    continue;
                }

                bool sweepFailed = false;
                string phase = "processing";
                var elapsed = Stopwatch.StartNew();
                try
                {
                    App02a3RecoveryTelemetry.Sweep(logger, tenant, phase, "start", 0, 0);
                    using IServiceScope scope = scopes.CreateScope();
                    var processor = scope.ServiceProvider
                        .GetRequiredService<App02a3ProcessingService>();
                    var outbound = scope.ServiceProvider
                        .GetRequiredService<App02a3OutboxService>();
                    DateTimeOffset now = DateTimeOffset.UtcNow;
                    int processed = await processor.SweepTenantAsync(tenant, now,
                        triggeredByEvent: true, stoppingToken);
                    App02a3RecoveryTelemetry.Sweep(logger, tenant, phase, "end", processed, elapsed.ElapsedMilliseconds);
                    phase = "outbox";
                    elapsed.Restart();
                    App02a3RecoveryTelemetry.Sweep(logger, tenant, phase, "start", 0, 0);
                    int handled = await outbound.SweepTenantAsync(tenant, now, stoppingToken);
                    App02a3RecoveryTelemetry.Sweep(logger, tenant, phase, "end", handled, elapsed.ElapsedMilliseconds);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    sweepFailed = true;
                    App02a3RecoveryTelemetry.Sweep(logger, tenant, phase, "fail", 0, elapsed.ElapsedMilliseconds);
                    App02a3RecoveryTelemetry.Technical(logger, tenant, phase, Guid.Empty, ex);
                    logger.LogError(ex,
                        "APP-02A.3 synthetic worker sweep failed for {Tenant}", tenant);
                }
                // After each ordinary or scheduled sweep, rediscover the
                // earliest committed C retry (including after an outbox error).
                // Never keep a stale timer after the SQL row is terminal.
                await RefreshDueAsync(schedule, tenant, sweepFailed, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            await lifetime.CancelAsync();
            try { await ticker; }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }
    }

    private async Task RefreshDueAsync(App02a3DueWakeSchedule schedule, string tenant,
        bool sweepFailed, CancellationToken ct)
    {
        try
        {
            using IServiceScope scope = scopes.CreateScope();
            var processor = scope.ServiceProvider.GetRequiredService<App02a3ProcessingService>();
            DateTimeOffset? due = await processor.ReadNextTechnicalRetryDueAsync(tenant, ct);
            schedule.SetFromSql(tenant, due, DateTimeOffset.UtcNow, sweepFailed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // A read-only discovery failure does not grant permission to
            // reapply an event; retry discovery only, with a bounded interval.
            schedule.SetReadFailure(tenant, DateTimeOffset.UtcNow);
            try { logger.LogWarning("REL756_DUE_READ_FAILED tenant={Tenant}", tenant); }
            catch (Exception) { /* Do not lose the scheduled discovery. */ }
        }
    }

    private async Task SafetyTicksAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(App02a3ProcessingService.SafetyScanInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            signal.Wake("tenant-a");
            signal.Wake("tenant-b");
        }
    }
}
