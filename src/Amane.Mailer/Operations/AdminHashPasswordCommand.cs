using Amane.Mailer.Admin;

namespace Amane.Mailer.Operations;

public sealed class AdminHashPasswordCommand
{
    public const int SuccessExitCode = 0;
    public const int UsageErrorExitCode = 2;

    public static bool IsAdminHashPasswordCommand(IReadOnlyList<string> args) =>
        args.Count >= 2
        && string.Equals(args[0], "admin", StringComparison.Ordinal)
        && string.Equals(args[1], "hash-password", StringComparison.Ordinal);

    public async Task<int> ExecuteAsync(
        IReadOnlyList<string> args,
        TextReader input,
        TextWriter output,
        TextWriter error)
    {
        if (args.Count != 2 || !IsAdminHashPasswordCommand(args))
        {
            await error.WriteLineAsync("Usage: dotnet Amane.Mailer.dll admin hash-password");
            return UsageErrorExitCode;
        }

        await error.WriteLineAsync("Password:");
        var password = await input.ReadLineAsync();
        await error.WriteLineAsync("Confirm password:");
        var confirmation = await input.ReadLineAsync();

        if (string.IsNullOrEmpty(password))
        {
            await error.WriteLineAsync("Password must not be empty.");
            return UsageErrorExitCode;
        }

        if (!string.Equals(password, confirmation, StringComparison.Ordinal))
        {
            await error.WriteLineAsync("Passwords do not match.");
            return UsageErrorExitCode;
        }

        await output.WriteLineAsync(AdminPasswordHasher.Hash(password));
        return SuccessExitCode;
    }
}
