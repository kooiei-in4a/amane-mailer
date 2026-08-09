using Amane.Mailer.Data.Sqlite.Models;

namespace Amane.Mailer.Data.Sqlite;

/// <summary>
/// Outcome of <see cref="MailPlainSubmissionStore.TryPrepareProviderInvocationAsync"/>
/// (ADR 0023 D-04/D-05, Issue #546).
/// </summary>
public enum PlainProviderInvocationOutcome
{
    /// <summary>
    /// A durable <c>Started</c> marker was committed and all canonical recipients were moved to
    /// <c>Pending</c> in the same transaction. The caller must invoke the provider next.
    /// </summary>
    Started,

    /// <summary>
    /// One or more canonical recipients were suppressed. Provider invocation was skipped
    /// entirely (all-or-nothing) and the request already converged terminally to
    /// <c>Failed</c>/<c>DefinitelyNotSubmitted</c> in the same transaction. The caller must not
    /// call the provider and has nothing further to finalize.
    /// </summary>
    Suppressed,

    /// <summary>
    /// Evidence already exists for this request (Started/DefinitelyNotSubmitted/Accepted/
    /// DefinitelyRejected/Unknown). The caller must converge from <see cref="ExistingEvidence"/>
    /// without invoking the provider.
    /// </summary>
    ExistingEvidence,

    /// <summary>
    /// The caller's claim no longer fences the request (lease expired or lock token superseded).
    /// No writes were made; the caller must not call the provider.
    /// </summary>
    FenceFailed,
}

public sealed record PlainProviderInvocationResult(
    PlainProviderInvocationOutcome Outcome,
    MailPlainSubmissionRow? ExistingEvidence = null,
    IReadOnlyList<MailRequestRecipientRow>? Recipients = null);
