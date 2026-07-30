using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Amane.Mailer.Setup.Assistant;

/// <summary>
/// The isolated Kestrel host for the assistant. It is built separately from the normal Mailer
/// runtime, binds only the IPv4 loopback address, never falls back to another interface, and
/// stops as soon as the session completes, is cancelled, or times out.
/// </summary>
internal sealed class SetupAssistantHost : IAsyncDisposable
{
    private static readonly TimeSpan DeadlineSweepInterval = TimeSpan.FromSeconds(5);

    private readonly WebApplication _app;
    private readonly SetupAssistantSessionManager _sessions;
    private CancellationTokenSource? _sweeper;
    private Task? _sweeperTask;

    private SetupAssistantHost(WebApplication app, SetupAssistantSessionManager sessions)
    {
        _app = app;
        _sessions = sessions;
    }

    internal int BoundPort { get; private set; }

    internal string BaseAddress => $"http://127.0.0.1:{BoundPort}/";

    /// <summary>
    /// Builds and starts the host. A failure to bind the loopback endpoint propagates: there is
    /// no retry on <c>0.0.0.0</c>, on a LAN address, or on any other interface.
    /// </summary>
    internal static async Task<SetupAssistantHost> StartAsync(
        SetupAssistantOptions options,
        SetupAssistantSessionManager sessions,
        ISetupAssistantOperations operations,
        CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder();

        // Ignore any inherited ASPNETCORE_URLS/urls value so the listener cannot be widened by
        // ambient configuration. The explicit Listen call below is the only binding.
        builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, string.Empty);
        builder.WebHost.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Listen(SetupAssistantOptions.BindAddress, options.Port);
        });

        // No logging provider is registered, so no request line, header, form field, secret, or
        // PII value can reach a log sink even when a verbose level is configured elsewhere.
        builder.Logging.ClearProviders();
        builder.Services.AddRouting();

        var app = builder.Build();
        var allowedHosts = new List<string>();

        app.Use(async (context, next) =>
        {
            SetupAssistantSecurity.ApplySecurityHeaders(context.Response);

            if (!SetupAssistantSecurity.IsAllowedHost(context.Request, allowedHosts))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            if (SetupAssistantSecurity.IsStateChangingMethod(context.Request.Method)
                && !SetupAssistantSecurity.IsAllowedOrigin(context.Request, allowedHosts))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next(context);
        });

        app.UseRouting();
        app.MapSetupAssistant(sessions, operations);

        var host = new SetupAssistantHost(app, sessions);
        await app.StartAsync(cancellationToken);
        host.BoundPort = ResolveBoundPort(app);
        allowedHosts.AddRange(SetupAssistantOptions.BuildHostAllowlist(host.BoundPort));
        host.StartDeadlineSweeper();
        return host;
    }

    /// <summary>Blocks until the session manager signals completion, cancellation, or timeout.</summary>
    internal async Task<SetupAssistantShutdownReason> WaitForShutdownAsync(
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _sessions.ShutdownToken);
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
        }
        catch (OperationCanceledException)
        {
        }

        if (!_sessions.ShutdownToken.IsCancellationRequested)
        {
            _sessions.Stop(SetupAssistantShutdownReason.Cancelled);
        }

        return _sessions.ShutdownReason;
    }

    internal IReadOnlyList<string> BoundAddresses =>
        _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses.ToArray()
        ?? [];

    private void StartDeadlineSweeper()
    {
        _sweeper = new CancellationTokenSource();
        var token = _sweeper.Token;
        _sweeperTask = Task.Run(
            async () =>
            {
                using var timer = new PeriodicTimer(DeadlineSweepInterval);
                try
                {
                    while (await timer.WaitForNextTickAsync(token))
                    {
                        _sessions.EvaluateDeadlines();
                    }
                }
                catch (OperationCanceledException)
                {
                }
            },
            token);
    }

    private static int ResolveBoundPort(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses;
        foreach (var address in addresses ?? [])
        {
            if (Uri.TryCreate(address, UriKind.Absolute, out var uri))
            {
                return uri.Port;
            }
        }

        throw new InvalidOperationException("Setup assistant did not bind a loopback endpoint.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_sweeper is not null)
        {
            await _sweeper.CancelAsync();
            if (_sweeperTask is not null)
            {
                try
                {
                    await _sweeperTask.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception) when (_sweeperTask.IsCanceled || _sweeperTask.IsFaulted)
                {
                }
                catch (TimeoutException)
                {
                }
            }

            _sweeper.Dispose();
        }

        await _app.StopAsync(TimeSpan.FromSeconds(5));
        await _app.DisposeAsync();
    }
}
