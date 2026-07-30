using System.Net;
using System.Net.Sockets;
using Amane.Mailer.Setup.Assistant;

namespace Amane.Mailer.Tests.Setup.Assistant;

/// <summary>
/// Bind and host-isolation contract for the localhost assistant (Issue #452, ADR 0021).
/// </summary>
public sealed class SetupAssistantBindTests
{
    [Fact]
    public async Task Host_binds_only_the_ipv4_loopback_address()
    {
        await using var harness = await SetupAssistantHarness.StartAsync();

        var addresses = harness.Host.BoundAddresses;

        Assert.Single(addresses);
        var uri = new Uri(addresses[0]);
        Assert.Equal("127.0.0.1", uri.Host);
        Assert.Equal(Uri.UriSchemeHttp, uri.Scheme);
        Assert.Equal(IPAddress.Loopback, SetupAssistantOptions.BindAddress);
    }

    [Fact]
    public async Task Bound_port_is_not_reachable_through_a_non_loopback_local_address()
    {
        await using var harness = await SetupAssistantHarness.StartAsync();

        var routable = Dns.GetHostAddresses(Dns.GetHostName())
            .FirstOrDefault(address =>
                address.AddressFamily == AddressFamily.InterNetwork
                && !IPAddress.IsLoopback(address));
        Assert.SkipWhen(routable is null, "No routable IPv4 address is available on this host.");

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var connect = socket.ConnectAsync(new IPEndPoint(routable!, harness.Host.BoundPort), TestContext.Current.CancellationToken).AsTask();
        var completed = await Task.WhenAny(connect, Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));

        // A wildcard bind would accept here. A loopback-only bind refuses or never completes.
        if (completed == connect)
        {
            await Assert.ThrowsAnyAsync<SocketException>(() => connect);
        }
    }

    [Fact]
    public async Task Bind_failure_does_not_fall_back_to_another_interface()
    {
        await using var occupied = await SetupAssistantHarness.StartAsync();
        var takenPort = occupied.Host.BoundPort;

        var options = new SetupAssistantOptions { Port = takenPort };
        using var sessions = new SetupAssistantSessionManager(options);

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var second = await SetupAssistantHost.StartAsync(
                options,
                sessions,
                new FakeSetupAssistantOperations(),
                CancellationToken.None);
        });

        // The original loopback listener is still the only thing bound to that port.
        Assert.Single(occupied.Host.BoundAddresses);
        Assert.Equal("127.0.0.1", new Uri(occupied.Host.BoundAddresses[0]).Host);
    }

    [Fact]
    public async Task Two_assistants_run_on_separate_hosts_with_separate_sessions()
    {
        await using var first = await SetupAssistantHarness.StartAsync();
        await using var second = await SetupAssistantHarness.StartAsync();

        Assert.NotEqual(first.Host.BoundPort, second.Host.BoundPort);
        Assert.NotEqual(first.Sessions.OneTimeTokenText, second.Sessions.OneTimeTokenText);
    }

    [Fact]
    public void Port_configuration_rejects_values_outside_the_valid_range()
    {
        Assert.True(SetupAssistantOptions.TryResolvePort(null, out var unset));
        Assert.Equal(SetupAssistantOptions.DefaultPort, unset);

        Assert.True(SetupAssistantOptions.TryResolvePort("5280", out var explicitPort));
        Assert.Equal(5280, explicitPort);

        Assert.False(SetupAssistantOptions.TryResolvePort("0", out _));
        Assert.False(SetupAssistantOptions.TryResolvePort("-1", out _));
        Assert.False(SetupAssistantOptions.TryResolvePort("70000", out _));
        Assert.False(SetupAssistantOptions.TryResolvePort("0.0.0.0", out _));
    }
}
