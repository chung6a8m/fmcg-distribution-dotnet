using FmcgDms.Preprod.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
try
{
    PreprodStartupGuard.ValidateHostVariables("worker",
        Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"),
        Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"));
    PreprodStartupGuard.Validate(builder.Configuration, builder.Environment.EnvironmentName, "worker");
}
catch (RuntimeDeniedException denied)
{
    Console.Error.WriteLine("RUNTIME_START_DENIED code=" + denied.ReasonCode);
    throw;
}
var vault = new VaultAdmissionClient(builder.Configuration, "worker");
try
{
    await vault.ValidateAsync(CancellationToken.None);
}
catch (RuntimeDeniedException denied)
{
    Console.Error.WriteLine("RUNTIME_START_DENIED code=" + denied.ReasonCode);
    vault.Dispose();
    throw;
}
builder.Services.AddSingleton(vault);
builder.Services.AddHostedService<FoundationWorker>();
using var host = builder.Build();
await host.RunAsync();

/// <summary>
/// No tenant registry/fencing yet (B.4/B.5): never claim a durable job.
/// Periodically check own Vault authorization; any loss terminates the host.
/// </summary>
internal sealed class FoundationWorker(
    VaultAdmissionClient vault, ILogger<FoundationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("PREPROD foundation worker NO_GO; business dispatch disabled");
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await vault.ValidateAsync(stoppingToken);
                }
                catch (RuntimeDeniedException denied)
                {
                    logger.LogCritical("RUNTIME_STOP_DENIED code={ReasonCode}", denied.ReasonCode);
                    // A revoked workload must not appear to Docker as a clean exit.
                    Environment.ExitCode = 78;
                    throw new InvalidOperationException("Worker security admission revoked.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
