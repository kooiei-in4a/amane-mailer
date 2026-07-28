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
    public const int InspectionIncompleteExitCode = 4;

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
        var reason = result.Reason
            ?? result.BundleIntegrity.Reason
            ?? result.MountAttestation.Reason;

        // Fail-closed business issues before Manual/Managed success or incomplete paths.
        if (IsIssueReason(reason)
            || IsIssueResult(result.MountAttestation.Result)
            || IsIssueResult(result.BundleIntegrity.Result))
        {
            return InspectionIssueExitCode;
        }

        // Manual not-managed success.
        if (!result.Managed
            && string.Equals(
                result.BundleIntegrity.Result,
                SetupInspectIntegrityResult.NotManaged,
                StringComparison.Ordinal))
        {
            return SuccessExitCode;
        }

        // Managed container mount attestation succeeded; host at-rest still pending (#450).
        if (result.Managed
            && string.Equals(
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
                StringComparison.Ordinal)
            && result.Effective.FingerprintsMatchRecorded == true
            && result.Effective.CredentialStatus is SetupInspectCredentialStatus.Loaded
                or SetupInspectCredentialStatus.NotApplicable)
        {
            return SuccessExitCode;
        }

        if (IsIncompleteReason(reason)
            || string.Equals(
                result.MountAttestation.Result,
                SetupInspectIntegrityResult.NotVerified,
                StringComparison.Ordinal)
            || (result.Managed
                && string.Equals(
                    result.BundleIntegrity.Result,
                    SetupInspectIntegrityResult.NotVerified,
                    StringComparison.Ordinal)
                && !string.Equals(
                    result.BundleIntegrity.Reason,
                    SetupInspectReason.HostAtRestPending,
                    StringComparison.Ordinal)))
        {
            return InspectionIncompleteExitCode;
        }

        return InspectionIssueExitCode;
    }

    private static bool IsIssueResult(string result) =>
        result is SetupInspectIntegrityResult.Mismatch
            or SetupInspectIntegrityResult.InvalidMetadata;

    private static bool IsIssueReason(string? reason) =>
        reason is SetupInspectReason.FingerprintMismatch
            or SetupInspectReason.CredentialMissing
            or SetupInspectReason.CredentialInvalid
            or SetupInspectReason.ConfigConflict
            or SetupInspectReason.TenantsMissing
            or SetupInspectReason.MetadataMalformed
            or SetupInspectReason.UnsupportedSchemaVersion
            or SetupInspectReason.VerifierMemberSetMismatch
            or SetupInspectReason.VerifierBundleMismatch
            or SetupInspectReason.MountMismatch
            or SetupInspectReason.SecretMissing;

    private static bool IsIncompleteReason(string? reason) =>
        reason is SetupInspectReason.VerifierMissing
            or SetupInspectReason.VerifierExpired
            or SetupInspectReason.VerifierMalformed;
}
