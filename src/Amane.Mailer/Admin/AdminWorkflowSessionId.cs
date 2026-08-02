using Amane.Mailer.Setup;

namespace Amane.Mailer.Admin;

/// <summary>
/// Session identifier reserved for an interactive Setup verification login. This type is kept
/// separate from ordinary 64-character Admin session identifiers so audit and cleanup paths
/// cannot accidentally expose its operation correlation value.
/// </summary>
internal readonly record struct AdminWorkflowSessionId
{
    internal const string Prefix = "setup-v1:";
    internal const int Length = 73;

    private AdminWorkflowSessionId(string value)
    {
        Value = value;
    }

    internal string Value { get; }

    internal static AdminWorkflowSessionId FromOperationId(AdminBootstrapOperationId operationId) =>
        new(Prefix + operationId.Value);

    internal static bool TryParse(string? value, out AdminWorkflowSessionId sessionId)
    {
        sessionId = default;
        if (value is null
            || value.Length != Length
            || !value.StartsWith(Prefix, StringComparison.Ordinal)
            || !AdminBootstrapOperationId.TryParse(value[Prefix.Length..], out _))
        {
            return false;
        }

        sessionId = new AdminWorkflowSessionId(value);
        return true;
    }

    public override string ToString() => "[redacted]";
}
