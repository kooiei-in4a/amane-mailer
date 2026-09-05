using Amane.Mailer.Admin;
using System.Text;
using Amane.Mailer.Data.Sqlite;

namespace Amane.Mailer.Operations;

public sealed class AdminResetPasswordCommand
{
    public const int SuccessExitCode = 0;
    public const int UsageErrorExitCode = 2;
    public const int FailureExitCode = 1;

    public static bool IsAdminResetPasswordCommand(IReadOnlyList<string> args) =>
        args.Count >= 2
        && string.Equals(args[0], "admin", StringComparison.Ordinal)
        && string.Equals(args[1], "reset-password", StringComparison.Ordinal);

    public async Task<int> ExecuteAsync(
        IReadOnlyList<string> args,
        TextReader input,
        TextWriter output,
        TextWriter error,
        AdminUserRepository users,
        string configuredUsername,
        CancellationToken cancellationToken)
    {
        if (!TryParse(args, configuredUsername, out var username))
        {
            await error.WriteLineAsync("Usage: dotnet Amane.Mailer.dll admin reset-password [--username <name>]");
            return UsageErrorExitCode;
        }

        await error.WriteLineAsync("New password:");
        var password = await ReadSecretLineAsync(input, cancellationToken);
        await error.WriteLineAsync("Confirm password:");
        var confirmation = await ReadSecretLineAsync(input, cancellationToken);
        if (string.IsNullOrEmpty(password)
            || password.Length is < 12 or > 1024
            || !string.Equals(password, confirmation, StringComparison.Ordinal))
        {
            await error.WriteLineAsync("Password input is invalid.");
            return UsageErrorExitCode;
        }

        var passwordBytes = System.Text.Encoding.UTF8.GetBytes(password);
        var confirmationBytes = System.Text.Encoding.UTF8.GetBytes(confirmation!);
        try
        {
            var hash = AdminPasswordHasher.Hash(password);
            var changed = await users.ResetPasswordAsync(username, hash, cancellationToken);
            if (!changed)
            {
                await error.WriteLineAsync("Admin user was not found.");
                return FailureExitCode;
            }

            await output.WriteLineAsync("Admin password reset.");
            return SuccessExitCode;
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(passwordBytes);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(confirmationBytes);
        }
    }

    private static bool TryParse(
        IReadOnlyList<string> args,
        string configuredUsername,
        out string username)
    {
        username = configuredUsername;
        if (!IsAdminResetPasswordCommand(args))
        {
            return false;
        }

        if (args.Count == 2)
        {
            return !string.IsNullOrWhiteSpace(username);
        }

        if (args.Count == 4
            && string.Equals(args[2], "--username", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(args[3]))
        {
            username = args[3].Trim();
            return true;
        }

        return false;
    }

    private static Task<string?> ReadSecretLineAsync(
        TextReader input,
        CancellationToken cancellationToken)
    {
        if (!ReferenceEquals(input, Console.In)
            || Console.IsInputRedirected
            || Console.IsOutputRedirected)
        {
            return input.ReadLineAsync(cancellationToken).AsTask();
        }

        var value = new StringBuilder();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = Console.ReadKey(intercept: true);
            if (key.Key is ConsoleKey.Enter)
            {
                Console.Out.WriteLine();
                return Task.FromResult<string?>(value.ToString());
            }

            if (key.Key is ConsoleKey.Backspace)
            {
                if (value.Length > 0)
                {
                    value.Length--;
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                value.Append(key.KeyChar);
            }
        }
    }
}
