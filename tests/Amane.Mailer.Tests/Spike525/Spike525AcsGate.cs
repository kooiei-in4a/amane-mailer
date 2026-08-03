namespace Amane.Mailer.Tests.Spike525;

/// <summary>
/// Env-var gate and secret-file readers for #525 gated ACS live fixtures. Mirrors the
/// Spike525Gate (Mailpit) pattern. Values are read lazily from files/env at call time and are
/// NEVER written to console, evidence, or any repository file — only booleans, short hashes,
/// and non-secret labels are ever surfaced (see Spike525Support.Evidence / #525 evidence rules).
///
/// Enabled only when the Issue #525 private-environment credential-gate script has returned
/// PASS and its environment-import script has been dot-sourced into the running process (see
/// AGENTS.md / Issue #525 for the operator workflow; the private environment directory itself
/// is out-of-repo and is never referenced here by path). Outside that flow,
/// <see cref="AcsLiveEnabled"/> is false and every fixture in Spike525AcsLiveTests
/// short-circuits (no network calls, no secret file reads).
/// </summary>
internal static class Spike525AcsGate
{
    internal static bool AcsLiveEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("AMANE_ACS_LIVE_TESTS"), "1", StringComparison.Ordinal);

    internal static string SenderAddress =>
        RequireEnv("AMANE_ACS_SPIKE_SENDER_ADDRESS");

    internal static string RecipientAddress =>
        RequireEnv("AMANE_ACS_SPIKE_RECIPIENT_ADDRESS");

    internal static string AcsConnectionString =>
        ReadSecretFile(RequireEnv("ACS_CONNECTION_STRING_FILE"));

    internal static string QueueConnectionString =>
        ReadSecretFile(RequireEnv("MAILER_BOUNCE_QUEUE_CONNECTION_STRING_FILE"));

    internal static string QueueName =>
        RequireEnv("MAILER_BOUNCE_QUEUE_NAME");

    internal static int MaxSends =>
        int.TryParse(Environment.GetEnvironmentVariable("AMANE_ACS_SPIKE_MAX_SENDS"), out var value)
            ? value
            : 0;

    /// <summary>Non-secret label for evidence only (e.g. "acs-amane"); never a connection string or key.</summary>
    internal static string? ResourceLabel =>
        Environment.GetEnvironmentVariable("AMANE_ACS_SPIKE_ACS_RESOURCE_LABEL");

    private static string RequireEnv(string key)
    {
        var value = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"#525 ACS live gate: required environment variable '{key}' is not set. " +
                "Run Test-Spike525CredentialGate.ps1 then dot-source Import-Spike525Environment.ps1 first.");
        }

        return value;
    }

    private static string ReadSecretFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                "#525 ACS live gate: secret file referenced by environment variable does not exist.");
        }

        return File.ReadAllText(path).Trim();
    }
}

/// <summary>
/// Process-wide, thread-safe cap on real ACS provider sends for a single #525 test run
/// (AMANE_ACS_SPIKE_MAX_SENDS, gate-validated to 1-30). Every fixture that is about to place a
/// real call to <c>EmailClient.SendAsync</c> (including calls expected to fail client-side or
/// server-side) MUST reserve a slot first via <see cref="Reserve"/>. Retries reserve a new slot
/// each time (#525 send-budget policy: "retryは新しい送信としてカウント"). Not reset between
/// [Fact] methods in the same test run — the whole <c>dotnet test</c> process shares one budget,
/// matching the Issue #525 requirement that the cap bounds the whole Spike session.
///
/// This counter is per OS process only (in-memory), not durable across separate
/// <c>dotnet test</c> invocations. When #525 work spans multiple invocations in the same
/// operator session, the operator/agent driving the run is responsible for adding each
/// invocation's <see cref="Used"/> total to the running cumulative count and stopping before
/// AMANE_ACS_SPIKE_MAX_SENDS is exceeded across the whole session — the same way Evidence
/// output from each run is manually reviewed before deciding whether to run another fixture.
/// </summary>
internal static class Spike525AcsSendBudget
{
    private static readonly object Gate = new();
    private static int _used;

    internal static int Used
    {
        get { lock (Gate) { return _used; } }
    }

    /// <summary>Throws if reserving one more send would exceed AMANE_ACS_SPIKE_MAX_SENDS.</summary>
    internal static int Reserve(string fixtureId)
    {
        lock (Gate)
        {
            var max = Spike525AcsGate.MaxSends;
            if (max <= 0)
            {
                throw new InvalidOperationException("#525 ACS live gate: AMANE_ACS_SPIKE_MAX_SENDS is not a valid positive integer.");
            }

            if (_used >= max)
            {
                throw new InvalidOperationException(
                    $"SPIKE_SEND_BUDGET_EXHAUSTED: fixture '{fixtureId}' would exceed AMANE_ACS_SPIKE_MAX_SENDS.");
            }

            _used++;
            return _used;
        }
    }
}
