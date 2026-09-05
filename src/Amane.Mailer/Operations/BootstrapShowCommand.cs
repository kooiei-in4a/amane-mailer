using Amane.Mailer.Configuration;
using Amane.Mailer.Setup;

namespace Amane.Mailer.Operations;

public sealed class BootstrapShowCommand
{
    public const int SuccessExitCode = 0;
    public const int UsageErrorExitCode = 2;
    public const int FailureExitCode = 1;

    public static bool IsBootstrapShowCommand(IReadOnlyList<string> args) =>
        args.Count == 3
        && string.Equals(args[0], "setup", StringComparison.Ordinal)
        && string.Equals(args[1], "bootstrap", StringComparison.Ordinal)
        && string.Equals(args[2], "show", StringComparison.Ordinal);

    public async Task<int> ExecuteAsync(
        IReadOnlyList<string> args,
        IConfiguration configuration,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (!IsBootstrapShowCommand(args))
        {
            await error.WriteLineAsync("Usage: dotnet Amane.Mailer.dll setup bootstrap show");
            return UsageErrorExitCode;
        }

        var state = await InstanceRuntimeStateProbe.ReadAsync(configuration, cancellationToken);
        if (!state.IsUninitialized)
        {
            await error.WriteLineAsync("Bootstrap token is unavailable.");
            return FailureExitCode;
        }

        var store = new BootstrapTokenStore(configuration);
        if (!store.TryRead(out var token))
        {
            await error.WriteLineAsync("Bootstrap token is unavailable.");
            return FailureExitCode;
        }

        await output.WriteLineAsync(token);
        return SuccessExitCode;
    }
}
