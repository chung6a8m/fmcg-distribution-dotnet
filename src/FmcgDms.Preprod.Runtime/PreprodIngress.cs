using System.Net;

namespace FmcgDms.Preprod.Runtime;

/// <summary>Ingress decision is evaluated on the socket peer BEFORE forwarded headers.</summary>
public static class PreprodIngress
{
    public static bool FromTrustedProxy(IPAddress? remote, IPAddress trusted) =>
        remote is not null && remote.MapToIPv4().Equals(trusted.MapToIPv4())
        && remote.AddressFamily is System.Net.Sockets.AddressFamily.InterNetwork
            or System.Net.Sockets.AddressFamily.InterNetworkV6;

    public static bool HasSecureHost(bool isHttps, string requestHost, string configuredHost) =>
        isHttps && string.Equals(requestHost, configuredHost, StringComparison.OrdinalIgnoreCase);
}
