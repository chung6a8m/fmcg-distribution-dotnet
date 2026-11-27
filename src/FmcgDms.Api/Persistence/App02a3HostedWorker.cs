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

        signal.Wake("tenant-a");
        signal.Wake("tenant-b");
        Task ticker = SafetyTicksAsync(stoppingToken);
        try
        {
            await foreach (string tenant in signal.ReadAllAsync(stoppingToken))
            {
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
                    // Durable rows remain visible for next scan after a
                    // process failure; never acknowledge an uncommitted work item.
                    App02a3RecoveryTelemetry.Sweep(logger, tenant, phase, "fail", 0, elapsed.ElapsedMilliseconds);
                    App02a3RecoveryTelemetry.Technical(logger, tenant, phase, Guid.Empty, ex);
                    logger.LogError(ex,
                        "APP-02A.3 synthetic worker sweep failed for {Tenant}", tenant);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            try { await ticker; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
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
