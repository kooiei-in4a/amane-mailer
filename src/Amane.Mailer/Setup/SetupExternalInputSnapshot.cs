using System.Security.Cryptography;

namespace Amane.Mailer.Setup;

/// <summary>
/// ACTIVE-independent pin of allowlisted external inputs for one apply session.
/// Values stay in process memory only; public surfaces use digests/enums.
/// </summary>
public sealed class SetupExternalInputSnapshot : IDisposable
{
    private bool _disposed;

    internal SetupExternalInputSnapshot(
        string externalInputDigest,
        string normalizedDataPath,
        string? normalizedConnectionString,
        string bindingMac,
        IReadOnlyDictionary<string, string> externalEnvironmentValues,
        DateTimeOffset pinnedAt)
    {
        ExternalInputDigest = externalInputDigest;
        NormalizedDataPath = normalizedDataPath;
        NormalizedConnectionString = normalizedConnectionString;
        BindingMac = bindingMac;
        ExternalEnvironmentValues = externalEnvironmentValues;
        PinnedAt = pinnedAt;
    }

    public string ExternalInputDigest { get; }
    internal string NormalizedDataPath { get; }
    internal string? NormalizedConnectionString { get; }
    internal string BindingMac { get; }
    internal IReadOnlyDictionary<string, string> ExternalEnvironmentValues { get; }
    public DateTimeOffset PinnedAt { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        // Clear dictionary references; managed string overwrite is not guaranteed.
        if (ExternalEnvironmentValues is Dictionary<string, string> mutable)
        {
            mutable.Clear();
        }
    }
}

/// <summary>
/// ACTIVE-dependent composed environment snapshot pinned for one generation.
/// </summary>
public sealed class SetupComposeInputSnapshot : IDisposable
{
    private bool _disposed;

    internal SetupComposeInputSnapshot(
        SetupExternalInputSnapshot externalInputSnapshot,
        string expectedActiveBundleId,
        long expectedActivationGeneration,
        IReadOnlyDictionary<string, string> composedEnvironment,
        string? recordedMetadataHostPath,
        DateTimeOffset composedAt)
    {
        ExternalInputSnapshot = externalInputSnapshot;
        ExpectedActiveBundleId = expectedActiveBundleId;
        ExpectedActivationGeneration = expectedActivationGeneration;
        ComposedEnvironment = composedEnvironment;
        RecordedMetadataHostPath = recordedMetadataHostPath;
        ComposedAt = composedAt;
    }

    public SetupExternalInputSnapshot ExternalInputSnapshot { get; }
    public string ExpectedActiveBundleId { get; }
    public long ExpectedActivationGeneration { get; }
    internal IReadOnlyDictionary<string, string> ComposedEnvironment { get; }
    internal string? RecordedMetadataHostPath { get; }
    public DateTimeOffset ComposedAt { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (ComposedEnvironment is Dictionary<string, string> mutable)
        {
            mutable.Clear();
        }
    }
}

public static class SetupExternalInputDigests
{
    public static string Sha256Hex(ReadOnlySpan<byte> canonicalBytes)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(canonicalBytes, hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
