using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Setup;

/// <summary>
/// CLI: <c>setup inspect-effective --format json</c> (issue #447 / ADR 0021 D-05).
/// stdout is a single JSON document only; stderr is fixed/sanitized.
/// </summary>
public static class SetupInspectEffectiveCommand
{
    public const int SuccessExitCode = 0;
    public const int InspectionIssueExitCode = 1;
    public const int UsageErrorExitCode = 2;
    public const int UnexpectedFailureExitCode = 3;

    public static bool IsInspectEffectiveCommand(IReadOnlyList<string> args) =>
        args.Count >= 2
        && string.Equals(args[0], "setup", StringComparison.Ordinal)
        && string.Equals(args[1], "inspect-effective", StringComparison.Ordinal);

    public static bool TryParseArguments(
        IReadOnlyList<string> args,
        out string? usageError)
    {
        usageError = null;
        if (!IsInspectEffectiveCommand(args))
        {
            usageError = "Not an inspect-effective command.";
            return false;
        }

        var formatSeen = false;
        for (var i = 2; i < args.Count; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--format", StringComparison.Ordinal))
            {
                if (i + 1 >= args.Count)
                {
                    usageError = "Missing value for --format. Expected: json.";
                    return false;
                }

                i++;
                if (!string.Equals(args[i], "json", StringComparison.Ordinal))
                {
                    usageError = "Unsupported --format value. Expected: json.";
                    return false;
                }

                formatSeen = true;
                continue;
            }

            if (arg.StartsWith('-'))
            {
                usageError = "Unknown argument for setup inspect-effective.";
                return false;
            }

            usageError = "Unexpected positional argument for setup inspect-effective.";
            return false;
        }

        if (!formatSeen)
        {
            usageError = "Missing required --format json.";
            return false;
        }

        return true;
    }

    public static Task<int> ExecuteAsync(
        IConfiguration configuration,
        TextWriter output,
        TextWriter error,
        TimeProvider? timeProvider = null)
    {
        try
        {
            var result = SetupInspectEffectiveEngine.Inspect(configuration, timeProvider);
            var json = JsonSerializer.Serialize(result, SetupInspectJsonContext.Default.SetupInspectEffectiveResult);
            output.Write(json);
            if (!json.EndsWith('\n'))
            {
                output.WriteLine();
            }

            return Task.FromResult(ResolveExitCode(result));
        }
        catch
        {
            error.WriteLine("setup inspect-effective failed: unexpected diagnostic error (details omitted).");
            return Task.FromResult(UnexpectedFailureExitCode);
        }
    }

    internal static int ResolveExitCode(SetupInspectEffectiveResult result)
    {
        if (string.Equals(
                result.BundleIntegrity.Result,
                SetupInspectIntegrityResult.NotManaged,
                StringComparison.Ordinal)
            && !result.Managed)
        {
            return SuccessExitCode;
        }

        if (string.Equals(
                result.MountAttestation.Result,
                SetupInspectIntegrityResult.Matched,
                StringComparison.Ordinal)
            && string.Equals(
                result.BundleIntegrity.Result,
                SetupInspectIntegrityResult.NotVerified,
                StringComparison.Ordinal)
            && string.Equals(
                result.BundleIntegrity.Reason,
                SetupInspectReason.HostAtRestPending,
                StringComparison.Ordinal))
        {
            // Mount attestation succeeded; host integration is owned by #450.
            return SuccessExitCode;
        }

        if (string.Equals(
                result.MountAttestation.Result,
                SetupInspectIntegrityResult.NotVerified,
                StringComparison.Ordinal)
            && string.Equals(
                result.MountAttestation.Reason,
                SetupInspectReason.VerifierMissing,
                StringComparison.Ordinal)
            && result.Managed)
        {
            // Managed without ephemeral verifier is expected until host apply (#449/#450).
            return SuccessExitCode;
        }

        if (string.Equals(result.BundleIntegrity.Result, SetupInspectIntegrityResult.Mismatch, StringComparison.Ordinal)
            || string.Equals(result.BundleIntegrity.Result, SetupInspectIntegrityResult.InvalidMetadata, StringComparison.Ordinal)
            || string.Equals(result.Reason, SetupInspectReason.ConfigConflict, StringComparison.Ordinal)
            || string.Equals(result.Reason, SetupInspectReason.TenantsMissing, StringComparison.Ordinal))
        {
            return InspectionIssueExitCode;
        }

        return SuccessExitCode;
    }
}
