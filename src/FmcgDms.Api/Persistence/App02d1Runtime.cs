namespace FmcgDms.Api.Persistence;

internal static class App02d1Runtime
{
    internal static bool Enabled(IConfiguration configuration, IHostEnvironment environment)
    {
        if (!configuration.GetValue<bool>("App02D1:Enabled")) return false;
        if (App02c6Clock.GetConfiguredPath(configuration, environment) is null)
            throw new InvalidOperationException("D.1 requires the guarded synthetic IntegrationTest session and its nonce-owned clock file.");
        return true;
    }
}
