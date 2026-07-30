using System.Net;

namespace Amane.Mailer.Setup.Assistant;

/// <summary>
/// Fixed transport and lifetime contract for the localhost Easy Setup Web Assistant
/// (ADR 0021 D-01/D-06). The bind address is not operator-configurable: only the loopback
/// IPv4 address is ever used, and a bind failure never falls back to another interface.
/// </summary>
internal sealed class SetupAssistantOptions
{
    /// <summary>
    /// The only address the assistant ever binds. IPv4 loopback is chosen explicitly so the
    /// IPv4/IPv6 behaviour is unambiguous and dual-stack wildcard binding cannot occur.
    /// </summary>
    internal static readonly IPAddress BindAddress = IPAddress.Loopback;

    internal const string PortEnvironmentKey = "MAILER_SETUP_ASSISTANT_PORT";

    /// <summary>Ephemeral port. The bound port is reported after the host starts.</summary>
    internal const int DefaultPort = 0;

    internal int Port { get; init; } = DefaultPort;

    /// <summary>Session is discarded when the operator stops interacting.</summary>
    internal TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Hard upper bound regardless of activity.</summary>
    internal TimeSpan AbsoluteLifetime { get; init; } = TimeSpan.FromHours(2);

    /// <summary>Window in which the printed one-time token must be redeemed.</summary>
    internal TimeSpan OneTimeTokenLifetime { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Resolves the requested port from configuration. Any invalid or out-of-range value is
    /// rejected rather than silently coerced, so the operator cannot accidentally widen exposure.
    /// </summary>
    internal static bool TryResolvePort(string? rawPort, out int port)
    {
        port = DefaultPort;
        if (string.IsNullOrWhiteSpace(rawPort))
        {
            return true;
        }

        if (!int.TryParse(rawPort.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            || parsed < 1
            || parsed > 65535)
        {
            return false;
        }

        port = parsed;
        return true;
    }

    /// <summary>
    /// Host header allowlist for the bound port. Only loopback authorities are accepted so a
    /// DNS-rebinding or proxied request with a foreign Host cannot reach the state machine.
    /// </summary>
    internal static IReadOnlyList<string> BuildHostAllowlist(int boundPort)
    {
        var port = boundPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return
        [
            $"127.0.0.1:{port}",
            $"localhost:{port}",
        ];
    }
}
