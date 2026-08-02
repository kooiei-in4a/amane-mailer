using System.Security.Cryptography;
using System.Text.Json;

namespace Amane.Mailer.Setup;

/// <summary>
/// Fixed host Docker adapter. Callers supply sessions and typed documents — never raw paths,
/// argv, compose files, images, or environment dictionaries.
/// </summary>
/// <remarks>
/// Issue #450 makes every ACTIVE-dependent operation require a pinned compose snapshot so one apply
/// session cannot straddle two activation generations or two external input revisions.
/// </remarks>
public sealed class SetupHostDockerAdapter
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Total budget for container readiness after a recreate.</summary>
    internal static readonly TimeSpan ReadinessOverallTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Per-attempt budget for one healthcheck exec. Never the 5 minute default.</summary>
    internal static readonly TimeSpan ReadinessAttemptTimeout = TimeSpan.FromSeconds(10);

    internal static readonly TimeSpan ReadinessRetryDelay = TimeSpan.FromSeconds(2);

    private readonly ISetupFileSystem _fileSystem;
    private readonly IHostProcessRunner _runner;
    private readonly DockerEnvironmentProbe _probe;
    private readonly ManagedComposeEnvComposer _envComposer;
    private readonly TimeProvider _timeProvider;

    public SetupHostDockerAdapter(ISetupFileSystem fileSystem)
        : this(fileSystem, new HostProcessRunner(), probe: null, envComposer: null)
    {
    }

    internal SetupHostDockerAdapter(
        ISetupFileSystem fileSystem,
        IHostProcessRunner runner,
        DockerEnvironmentProbe? probe = null,
        ManagedComposeEnvComposer? envComposer = null,
        TimeProvider? timeProvider = null)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _probe = probe ?? new DockerEnvironmentProbe(_runner);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _envComposer = envComposer ?? new ManagedComposeEnvComposer(_fileSystem, _timeProvider);
    }

    public async Task<(SetupDockerResult Result, DockerConnectionBinding? Binding)> CheckDockerAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            return await _probe.ProbeAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return (Cancelled(), null);
        }
        catch (IOException)
        {
            return (AdapterIoFailure(), null);
        }
        catch (UnauthorizedAccessException)
        {
            return (AdapterIoFailure(), null);
        }
        catch (JsonException)
        {
            return (AdapterDataFailure(), null);
        }
    }

    public async Task<(SetupDockerResult Result, SetupHostDockerSession? Session)> AcquireSessionAsync(
        TrustedSetupHostLayout layout,
        DockerConnectionBinding binding,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(binding);
        try
        {
            var revalidate = await _probe.RevalidateBindingAsync(binding, cancellationToken);
            if (!revalidate.IsSuccess)
            {
                return (revalidate, null);
            }

            if (!_fileSystem.DirectoryExists(layout.ManagedRoot))
            {
                _fileSystem.CreateOwnerOnlyDirectory(layout.ManagedRoot);
            }

            if (!_fileSystem.DirectoryExists(layout.StatePath))
            {
                _fileSystem.CreateOwnerOnlyDirectory(layout.StatePath);
            }

            var applyLock = SetupApplyLock.Acquire(_fileSystem, layout);
            var session = new SetupHostDockerSession(layout, binding, applyLock);
            return (SetupDockerResult.Ok("Setup apply session acquired."), session);
        }
        catch (SetupDockerException ex)
        {
            return (ex.ToResult(), null);
        }
        catch (OperationCanceledException)
        {
            return (Cancelled(), null);
        }
        catch (IOException)
        {
            return (AdapterIoFailure(), null);
        }
        catch (UnauthorizedAccessException)
        {
            return (AdapterIoFailure(), null);
        }
        catch (JsonException)
        {
            return (AdapterDataFailure(), null);
        }
        catch
        {
            return (SetupDockerResult.Fail(
                SetupDockerResultCode.FailedUnexpected,
                "Failed to acquire a setup apply session."), null);
        }
    }

    /// <summary>
    /// Pins the ACTIVE-independent external input layer once for the session. Must run before any
    /// compose pin or external-drift comparison.
    /// </summary>
    public Task<SetupDockerResult> PinExternalInputsAsync(
        SetupHostDockerSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.ThrowIfDisposed();
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(Cancelled());
        }

        SetupExternalInputSnapshot? snapshot = null;
        try
        {
            var pin = TryPinExternal(session.Layout, out snapshot);
            if (!pin.IsSuccess || snapshot is null)
            {
                return Task.FromResult(pin);
            }

            session.SetExternalInputs(snapshot);
            snapshot = null;
            return Task.FromResult(SetupDockerResult.Ok("External inputs pinned."));
        }
        finally
        {
            snapshot?.Dispose();
        }
    }

    /// <summary>
    /// Composes and pins the environment for the ACTIVE generation currently on disk.
    /// </summary>
    public Task<SetupDockerResult> ComposeCurrentActiveInputAsync(
        SetupHostDockerSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.ThrowIfDisposed();
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(Cancelled());
        }

        var external = session.ExternalInputs;
        if (external is null)
        {
            return Task.FromResult(ExternalNotPinned());
        }

        try
        {
            if (!_envComposer.TryReadActivePointer(session.Layout, out var active, out var activeResult)
                || active is null)
            {
                return Task.FromResult(activeResult);
            }

            return Task.FromResult(ComposeForPointer(session, external, active));
        }
        catch (IOException)
        {
            return Task.FromResult(AdapterIoFailure());
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(AdapterIoFailure());
        }
    }

    /// <summary>
    /// Composes and pins the environment for an explicitly expected ACTIVE generation, refusing when
    /// the on-disk pointer no longer matches.
    /// </summary>
    public Task<SetupDockerResult> ComposeExpectedActiveInputAsync(
        SetupHostDockerSession session,
        SetupActivePointer expected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(expected);
        session.ThrowIfDisposed();
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(Cancelled());
        }

        var external = session.ExternalInputs;
        if (external is null)
        {
            return Task.FromResult(ExternalNotPinned());
        }

        try
        {
            var match = VerifyOnDiskActiveMatches(session.Layout, expected.BundleId, expected.ActivationGeneration);
            if (!match.IsSuccess)
            {
                return Task.FromResult(match);
            }

            return Task.FromResult(ComposeForPointer(session, external, expected));
        }
        catch (IOException)
        {
            return Task.FromResult(AdapterIoFailure());
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(AdapterIoFailure());
        }
    }

    /// <summary>
    /// Re-reads the external layer and compares the canonical digest against the session pin.
    /// Values are never returned; only match/no-match is observable.
    /// </summary>
    public Task<SetupDockerResult> VerifyExternalInputsUnchangedAsync(
        SetupHostDockerSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.ThrowIfDisposed();
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(Cancelled());
        }

        var pinned = session.ExternalInputs;
        if (pinned is null)
        {
            return Task.FromResult(ExternalNotPinned());
        }

        SetupExternalInputSnapshot? current = null;
        try
        {
            var repin = TryPinExternal(session.Layout, out current);
            if (!repin.IsSuccess || current is null)
            {
                return Task.FromResult(repin);
            }

            if (!string.Equals(current.ExternalInputDigest, pinned.ExternalInputDigest, StringComparison.Ordinal))
            {
                return Task.FromResult(SetupDockerResult.Fail(
                    SetupDockerResultCode.ExternalInputChanged,
                    "Allowlisted external input changed during the apply session."));
            }

            return Task.FromResult(SetupDockerResult.Ok("External inputs unchanged."));
        }
        finally
        {
            current?.Dispose();
        }
    }

    /// <summary>
    /// Removes stale ephemeral mount verifiers from <c>managed/tmp</c> and fails closed on any other
    /// residue. Effective inspection refuses to run until this succeeds.
    /// </summary>
    public Task<SetupDockerResult> PurgeStaleMountVerifiersAsync(
        SetupHostDockerSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.ThrowIfDisposed();
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(Cancelled());
        }

        var tempDir = Path.GetFullPath(session.Layout.VerifierTempDir);
        try
        {
            if (!SetupPathGuard.TryEnsurePathSafeUnderRoot(
                    _fileSystem, session.Layout.ManagedRoot, tempDir, out _, out _))
            {
                return Task.FromResult(SetupDockerResult.Fail(
                    SetupDockerResultCode.UnsafePath,
                    "Verifier temp directory path rejected."));
            }

            if (!_fileSystem.DirectoryExists(tempDir))
            {
                session.MarkStaleVerifiersPurged();
                return Task.FromResult(SetupDockerResult.Ok("No verifier temp directory to purge."));
            }

            foreach (var entry in _fileSystem.EnumerateFileSystemEntries(tempDir))
            {
                var fullEntry = Path.GetFullPath(entry);
                var fileName = Path.GetFileName(fullEntry);
                if (_fileSystem.DirectoryExists(fullEntry)
                    || !SetupBundleLayout.IsMountVerifierFileName(fileName)
                    || SetupPathGuard.IsUnsafeLink(_fileSystem.InspectSymlinkOrReparsePoint(fullEntry)))
                {
                    return Task.FromResult(SetupDockerResult.Fail(
                        SetupDockerResultCode.UnsafePath,
                        "Unexpected residue exists in the managed verifier temp directory."));
                }

                _fileSystem.DeleteFile(fullEntry);
            }

            _fileSystem.FlushDirectory(tempDir);
            session.MarkStaleVerifiersPurged();
            return Task.FromResult(SetupDockerResult.Ok("Stale mount verifiers purged."));
        }
        catch (IOException)
        {
            return Task.FromResult(AdapterIoFailure());
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(AdapterIoFailure());
        }
    }

    public Task<SetupDockerResult> ValidateComposeAsync(
        SetupHostDockerSession session,
        CancellationToken cancellationToken) =>
        RunComposeOperationAsync(
            session,
            BuildComposeArgPrefix(session).Concat(["config", "--quiet"]).ToArray(),
            cancellationToken);

    public Task<SetupDockerResult> EnsurePinnedImageAvailableAsync(
        SetupHostDockerSession session,
        CancellationToken cancellationToken)
    {
        // ACTIVE-independent: the pinned digest comes from the trusted release inventory only.
        session.ThrowIfDisposed();
        var image = session.Layout.ReleaseInventory.PinnedMailerImageReference;
        var args = PrefixedDockerArgs(session, ["pull", image]);
        return RunDockerAsync(session, args, workingDirectory: null, composeEnv: null, cancellationToken);
    }

    public Task<SetupDockerResult> RunMigrationAsync(
        SetupHostDockerSession session,
        CancellationToken cancellationToken)
    {
        var args = BuildComposeArgPrefix(session)
            .Concat(["--profile", SetupDockerInventory.ProfileOps, "run", "--rm", "--pull", "never",
                SetupDockerInventory.ServiceMailerMigrate])
            .ToArray();
        return RunComposeOperationAsync(session, args, cancellationToken);
    }

    /// <summary>
    /// Read-only schema classification via the ops profile. Fixed argv; never applies migrations.
    /// </summary>
    public async Task<SetupDockerResult> InspectMigrationStatusAsync(
        SetupHostDockerSession session,
        CancellationToken cancellationToken)
    {
        var args = BuildComposeArgPrefix(session)
            .Concat(["--profile", SetupDockerInventory.ProfileOps, "run", "--rm", "--pull", "never",
                SetupDockerInventory.ServiceMailerMigrate,
                "db", "migrate", "--status", "--format", "json"])
            .ToArray();

        return await RunComposeOperationAsync(
            session,
            args,
            cancellationToken,
            deserializeMigrationStatus: true);
    }

    public Task<SetupDockerResult> StartOrRecreateMailerAsync(
        SetupHostDockerSession session,
        CancellationToken cancellationToken)
    {
        var args = BuildComposeArgPrefix(session)
            .Concat(["up", "-d", "--force-recreate", "--no-deps", "--pull", "never",
                SetupDockerInventory.ServiceMailer])
            .ToArray();
        return RunComposeOperationAsync(session, args, cancellationToken);
    }

    public Task<SetupDockerResult> StopFailedMailerAsync(
        SetupHostDockerSession session,
        CancellationToken cancellationToken)
    {
        var args = BuildComposeArgPrefix(session)
            .Concat(["stop", SetupDockerInventory.ServiceMailer])
            .ToArray();
        return RunComposeOperationAsync(session, args, cancellationToken);
    }

    /// <summary>
    /// Polls the in-container healthcheck until it passes or the overall readiness budget expires.
    /// Uses the pinned compose environment and a short per-attempt timeout, never the default.
    /// </summary>
    public async Task<SetupDockerResult> AwaitMailerHealthyAsync(
        SetupHostDockerSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.ThrowIfDisposed();

        var args = BuildComposeArgPrefix(session)
            .Concat(["exec", "-T", SetupDockerInventory.ServiceMailer,
                SetupDockerInventory.ContainerMailerEntrypointPath, "healthcheck"])
            .ToArray();

        var deadline = _timeProvider.GetUtcNow() + ReadinessOverallTimeout;
        SetupDockerResult last = SetupDockerResult.Fail(
            SetupDockerResultCode.Timeout,
            "Mailer readiness did not pass within the allowed budget.");

        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Cancelled();
            }

            last = await RunComposeOperationAsync(
                session,
                args,
                cancellationToken,
                attemptTimeout: ReadinessAttemptTimeout);
            if (last.IsSuccess)
            {
                return SetupDockerResult.Ok(
                    "Mailer readiness passed.",
                    session.Binding.EngineKind,
                    session.Binding.ComposeMajorVersion);
            }

            // Input drift, pin, and path failures are terminal — retrying cannot fix them.
            if (last.Code is SetupDockerResultCode.ComposeInputNotPinned
                or SetupDockerResultCode.ExternalInputNotPinned
                or SetupDockerResultCode.ExternalInputChanged
                or SetupDockerResultCode.ActiveGenerationMismatch
                or SetupDockerResultCode.UnsafePath
                or SetupDockerResultCode.InvalidBundleInventory
                or SetupDockerResultCode.RemoteContextRejected
                or SetupDockerResultCode.RemoteDockerRejected
                or SetupDockerResultCode.Cancelled)
            {
                return last;
            }

            if (_timeProvider.GetUtcNow() + ReadinessRetryDelay >= deadline)
            {
                return SetupDockerResult.Fail(
                    SetupDockerResultCode.Timeout,
                    "Mailer readiness did not pass within the allowed budget.");
            }

            try
            {
                await Task.Delay(ReadinessRetryDelay, _timeProvider, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return Cancelled();
            }
        }
    }

    public async Task<SetupDockerResult> RunEffectiveInspectionAsync(
        SetupHostDockerSession session,
        SetupMountVerifierDocument verifier,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        session.ThrowIfDisposed();

        if (verifier.SchemaVersion != SetupMountVerifierDocument.CurrentSchemaVersion
            || string.IsNullOrWhiteSpace(verifier.BundleId)
            || string.IsNullOrWhiteSpace(verifier.SessionKey)
            || string.IsNullOrWhiteSpace(verifier.SessionNonce)
            || verifier.Members is null
            || verifier.Members.Count == 0)
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.InvalidBundleInventory,
                "Mount verifier document is incomplete.");
        }

        if (!session.StaleVerifiersPurged)
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.FailedUnexpected,
                "Stale mount verifiers were not purged before effective inspection.");
        }

        var pinned = ResolvePinnedComposeEnvironment(session, out var pinFailure);
        if (pinned is null)
        {
            return pinFailure!;
        }

        byte[]? verifierBytes = null;
        string? hostVerifierPath = null;
        SetupDockerResult? operationResult = null;
        try
        {
            verifierBytes = JsonSerializer.SerializeToUtf8Bytes(
                verifier,
                SetupHostDockerJsonContext.Default.SetupMountVerifierDocument);

            var verifierDir = Path.GetFullPath(session.Layout.VerifierTempDir);
            if (!_fileSystem.DirectoryExists(verifierDir))
            {
                _fileSystem.CreateOwnerOnlyDirectory(verifierDir);
            }

            hostVerifierPath = Path.GetFullPath(
                SetupBundleLayout.MountVerifierPath(session.Layout.ManagedRoot, Guid.NewGuid().ToString("N")));
            if (!SetupPathGuard.TryEnsurePathSafeUnderRoot(
                    _fileSystem, session.Layout.ManagedRoot, hostVerifierPath, out _, out _))
            {
                return SetupDockerResult.Fail(
                    SetupDockerResultCode.UnsafePath,
                    "Verifier temp path rejected.");
            }

            _fileSystem.WriteProtectedFileCreateNew(hostVerifierPath, verifierBytes);

            var env = new Dictionary<string, string>(pinned, StringComparer.Ordinal)
            {
                [SetupDockerInventory.ContainerVerifierEnvKey] =
                    SetupDockerInventory.ContainerVerifierMountPath,
            };

            // Recorded metadata mount/env come from compose.recorded-metadata.yml (same as
            // normal mailer). One-shot delta is the ephemeral verifier mount/env only.
            var args = BuildComposeArgPrefix(session)
                .Concat(BuildInspectEffectiveRunArgs(hostVerifierPath, pinned))
                .ToArray();

            operationResult = await RunDockerAsync(
                session,
                PrefixedDockerArgs(session, args),
                session.Layout.ProjectDirectory,
                env,
                cancellationToken,
                deserializeInspection: true);
        }
        catch (OperationCanceledException)
        {
            operationResult = Cancelled();
        }
        catch (IOException)
        {
            operationResult = AdapterIoFailure();
        }
        catch (UnauthorizedAccessException)
        {
            operationResult = AdapterIoFailure();
        }
        catch (JsonException)
        {
            operationResult = AdapterDataFailure();
        }
        finally
        {
            DockerOutputSanitizer.ZeroBuffer(verifierBytes);
            var deleteFailed = false;
            try
            {
                if (hostVerifierPath is not null && _fileSystem.FileExists(hostVerifierPath))
                {
                    _fileSystem.DeleteFile(hostVerifierPath);

                    if (_fileSystem.FileExists(hostVerifierPath))
                    {
                        deleteFailed = true;
                    }
                }
            }
            catch (IOException)
            {
                deleteFailed = true;
            }
            catch (UnauthorizedAccessException)
            {
                deleteFailed = true;
            }

            if (deleteFailed && operationResult is { IsSuccess: true })
            {
                operationResult = SetupDockerResult.Fail(
                    SetupDockerResultCode.FailedUnexpected,
                    "Ephemeral mount verifier could not be deleted after inspection.");
            }
        }

        return operationResult
            ?? SetupDockerResult.Fail(
                SetupDockerResultCode.FailedUnexpected,
                "Effective inspection did not produce a result.");
    }

    public Task<SetupDockerResult> ExecuteStagingVerificationAsync(
        SetupHostDockerSession session,
        CancellationToken cancellationToken)
    {
        session.ThrowIfDisposed();
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(Cancelled());
        }

        return Task.FromResult(SetupDockerResult.Fail(
            SetupDockerResultCode.OperationNotAvailable,
            "Staging verification is owned by AcsStagingVerificationOperation / AcsSetupWorkflow (#451); it is not a Docker host operation."));
    }

    internal static List<string> BuildComposeArgPrefix(SetupHostDockerSession session)
    {
        var args = new List<string> { "compose", "--project-directory", session.Layout.ProjectDirectory };
        foreach (var composePath in session.Layout.ComposeFilePaths)
        {
            args.Add("-f");
            args.Add(composePath);
        }

        return args;
    }

    /// <summary>
    /// One-shot inspect-effective must observe the same pinned public compose env as the bundle
    /// fingerprint. Compose interpolation alone does not inject those keys into the container.
    /// </summary>
    internal static IEnumerable<string> BuildInspectEffectiveRunArgs(
        string hostVerifierPath,
        IReadOnlyDictionary<string, string> pinnedComposeEnv)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostVerifierPath);
        ArgumentNullException.ThrowIfNull(pinnedComposeEnv);

        yield return "run";
        yield return "--rm";
        yield return "--no-deps";
        yield return "--pull";
        yield return "never";
        yield return "-v";
        yield return $"{hostVerifierPath}:{SetupDockerInventory.ContainerVerifierMountPath}:ro";
        yield return "-e";
        yield return SetupDockerInventory.ContainerVerifierEnvKey;

        foreach (var key in ManagedEnvKeyCatalog.PublicNonSecretKeys.OrderBy(static k => k, StringComparer.Ordinal))
        {
            if (pinnedComposeEnv.ContainsKey(key))
            {
                yield return "-e";
                yield return key;
            }
        }

        yield return SetupDockerInventory.ServiceMailer;
        yield return "setup";
        yield return "inspect-effective";
        yield return "--format";
        yield return "json";
    }

    internal static string[] PrefixedDockerArgs(SetupHostDockerSession session, IReadOnlyList<string> args)
    {
        var list = new List<string>(2 + args.Count)
        {
            "--context",
            session.Binding.ContextName,
        };
        list.AddRange(args);
        return list.ToArray();
    }

    private SetupDockerResult ComposeForPointer(
        SetupHostDockerSession session,
        SetupExternalInputSnapshot external,
        SetupActivePointer pointer)
    {
        SetupComposeInputSnapshot? snapshot = null;
        try
        {
            if (!_envComposer.TryComposeWithActivePointer(
                    session.Layout,
                    external,
                    pointer,
                    out snapshot,
                    out var composeResult)
                || snapshot is null)
            {
                return composeResult;
            }

            session.SetComposeInputs(snapshot);
            snapshot = null;
            return SetupDockerResult.Ok("Compose inputs pinned.");
        }
        finally
        {
            snapshot?.Dispose();
        }
    }

    private SetupDockerResult TryPinExternal(
        TrustedSetupHostLayout layout,
        out SetupExternalInputSnapshot? snapshot)
    {
        snapshot = null;
        byte[]? sealingKey = null;
        try
        {
            var load = TryLoadSealingKey(layout, out sealingKey);
            if (!load.IsSuccess || sealingKey is null)
            {
                return load;
            }

            return _envComposer.TryPinExternalLayer(layout, sealingKey, out snapshot, out var pinResult)
                ? SetupDockerResult.Ok()
                : pinResult;
        }
        catch (IOException)
        {
            return AdapterIoFailure();
        }
        catch (UnauthorizedAccessException)
        {
            return AdapterIoFailure();
        }
        finally
        {
            if (sealingKey is not null)
            {
                CryptographicOperations.ZeroMemory(sealingKey);
            }
        }
    }

    private SetupDockerResult TryLoadSealingKey(TrustedSetupHostLayout layout, out byte[]? sealingKey)
    {
        sealingKey = null;
        var path = Path.GetFullPath(SetupBundleLayout.HostSealingKeyPath(layout.ManagedRoot));
        if (!SetupPathGuard.TryEnsurePathSafeUnderRoot(
                _fileSystem, layout.ManagedRoot, path, out _, out _))
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.UnsafePath,
                "Host sealing key path rejected.");
        }

        if (!_fileSystem.FileExists(path) || !_fileSystem.IsOwnerOnlyFile(path))
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.InvalidBundleInventory,
                "Host sealing key is missing or not owner-only.");
        }

        var bytes = _fileSystem.ReadAllBytes(path);
        if (bytes.Length != SetupIntegritySealer.SealingKeyLength)
        {
            CryptographicOperations.ZeroMemory(bytes);
            return SetupDockerResult.Fail(
                SetupDockerResultCode.InvalidBundleInventory,
                "Host sealing key is invalid.");
        }

        sealingKey = bytes;
        return SetupDockerResult.Ok();
    }

    private IReadOnlyDictionary<string, string>? ResolvePinnedComposeEnvironment(
        SetupHostDockerSession session,
        out SetupDockerResult? failure)
    {
        failure = null;
        if (session.ExternalInputs is null)
        {
            failure = ExternalNotPinned();
            return null;
        }

        var pinned = session.ComposeInputs;
        if (pinned is null)
        {
            failure = SetupDockerResult.Fail(
                SetupDockerResultCode.ComposeInputNotPinned,
                "Compose inputs must be pinned for the target activation generation first.");
            return null;
        }

        var match = VerifyOnDiskActiveMatches(
            session.Layout,
            pinned.ExpectedActiveBundleId,
            pinned.ExpectedActivationGeneration);
        if (!match.IsSuccess)
        {
            failure = match;
            return null;
        }

        return pinned.ComposedEnvironment;
    }

    private SetupDockerResult VerifyOnDiskActiveMatches(
        TrustedSetupHostLayout layout,
        string expectedBundleId,
        long expectedGeneration)
    {
        if (!_envComposer.TryReadActivePointer(layout, out var active, out var activeResult) || active is null)
        {
            return activeResult;
        }

        if (!string.Equals(active.BundleId, expectedBundleId, StringComparison.Ordinal)
            || active.ActivationGeneration != expectedGeneration)
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.ActiveGenerationMismatch,
                "On-disk ACTIVE no longer matches the pinned activation generation.");
        }

        return SetupDockerResult.Ok();
    }

    private async Task<SetupDockerResult> RunComposeOperationAsync(
        SetupHostDockerSession session,
        IReadOnlyList<string> composeArgs,
        CancellationToken cancellationToken,
        bool deserializeMigrationStatus = false,
        TimeSpan? attemptTimeout = null)
    {
        session.ThrowIfDisposed();
        try
        {
            var composeEnv = ResolvePinnedComposeEnvironment(session, out var pinFailure);
            if (composeEnv is null)
            {
                return pinFailure!;
            }

            return await RunDockerAsync(
                session,
                PrefixedDockerArgs(session, composeArgs),
                session.Layout.ProjectDirectory,
                composeEnv,
                cancellationToken,
                deserializeMigrationStatus: deserializeMigrationStatus,
                attemptTimeout: attemptTimeout);
        }
        catch (OperationCanceledException)
        {
            return Cancelled();
        }
        catch (IOException)
        {
            return AdapterIoFailure();
        }
        catch (UnauthorizedAccessException)
        {
            return AdapterIoFailure();
        }
        catch (JsonException)
        {
            return AdapterDataFailure();
        }
    }

    private async Task<SetupDockerResult> RunDockerAsync(
        SetupHostDockerSession session,
        IReadOnlyList<string> args,
        string? workingDirectory,
        IReadOnlyDictionary<string, string>? composeEnv,
        CancellationToken cancellationToken,
        bool deserializeInspection = false,
        bool deserializeMigrationStatus = false,
        TimeSpan? attemptTimeout = null)
    {
        SetupDockerResult revalidate;
        try
        {
            revalidate = await _probe.RevalidateBindingAsync(session.Binding, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return Cancelled();
        }
        catch (IOException)
        {
            return AdapterIoFailure();
        }
        catch (UnauthorizedAccessException)
        {
            return AdapterIoFailure();
        }
        catch (JsonException)
        {
            return AdapterDataFailure();
        }

        if (!revalidate.IsSuccess)
        {
            return revalidate;
        }

        // Destructive token guard for non-inspect builders (inspect may include -v).
        if (args.Any(static a =>
                string.Equals(a, "down", StringComparison.OrdinalIgnoreCase)
                || string.Equals(a, "prune", StringComparison.OrdinalIgnoreCase)
                || string.Equals(a, "--volumes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(a, "rmi", StringComparison.OrdinalIgnoreCase)))
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.FailedUnexpected,
                "Refusing to execute a destructive Docker operation.");
        }

        if (args.Any(static a => SetupDockerInventory.IsAcsAdminService(a)))
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.FailedUnexpected,
                "Refusing to operate the ACS admin Compose service.");
        }

        var childEnv = HostProcessRunner.CreateMinimalDockerChildEnvironment(
            clearDockerOverrides: true,
            extra: composeEnv?.ToDictionary(
                static p => p.Key,
                static p => (string?)p.Value,
                StringComparer.Ordinal));

        var spec = new HostProcessSpec(
            session.Binding.DockerExecutablePath,
            args,
            workingDirectory,
            childEnv,
            attemptTimeout ?? DefaultTimeout);

        HostProcessResult processResult;
        try
        {
            processResult = await _runner.RunAsync(spec, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return Cancelled();
        }
        catch (IOException)
        {
            return AdapterIoFailure();
        }
        catch (UnauthorizedAccessException)
        {
            return AdapterIoFailure();
        }
        catch (JsonException)
        {
            return AdapterDataFailure();
        }
        catch
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.FailedUnexpected,
                "Docker process execution failed unexpectedly.");
        }

        // Sanitize internally; never return raw streams.
        _ = DockerOutputSanitizer.SanitizeForInternalUse(processResult.StandardOutput);
        _ = DockerOutputSanitizer.SanitizeForInternalUse(processResult.StandardError);

        if (processResult.Outcome == HostProcessOutcome.Completed
            && processResult.ExitCode == 0
            && deserializeInspection)
        {
            try
            {
                var inspection = JsonSerializer.Deserialize(
                    processResult.StandardOutput ?? string.Empty,
                    SetupHostDockerJsonContext.Default.SetupInspectEffectiveResult);
                return inspection is null
                    ? SetupDockerResult.Fail(
                        SetupDockerResultCode.OutputMalformed,
                        "Effective inspection output was malformed.")
                    : SetupDockerResult.Ok(
                        "Docker operation succeeded.",
                        session.Binding.EngineKind,
                        session.Binding.ComposeMajorVersion,
                        inspection);
            }
            catch (JsonException)
            {
                return SetupDockerResult.Fail(
                    SetupDockerResultCode.OutputMalformed,
                    "Effective inspection output was malformed.");
            }
        }

        if (processResult.Outcome == HostProcessOutcome.Completed
            && processResult.ExitCode == 0
            && deserializeMigrationStatus)
        {
            try
            {
                var status = JsonSerializer.Deserialize(
                    processResult.StandardOutput ?? string.Empty,
                    SetupApplyJsonContext.Default.SetupMigrationStatusDocument);
                if (status is null
                    || status.SchemaVersion != SetupMigrationStatusDocument.CurrentSchemaVersion
                    || !SetupMigrationStatusDocument.IsKnownClassification(status.Classification))
                {
                    return SetupDockerResult.Fail(
                        SetupDockerResultCode.OutputMalformed,
                        "Migration status output was malformed.");
                }

                return SetupDockerResult.Ok(
                    "Docker operation succeeded.",
                    session.Binding.EngineKind,
                    session.Binding.ComposeMajorVersion,
                    inspection: null,
                    migrationStatus: status);
            }
            catch (JsonException)
            {
                return SetupDockerResult.Fail(
                    SetupDockerResultCode.OutputMalformed,
                    "Migration status output was malformed.");
            }
        }

        return processResult.Outcome switch
        {
            HostProcessOutcome.TimedOut => SetupDockerResult.Fail(
                SetupDockerResultCode.Timeout,
                "Docker operation timed out."),
            HostProcessOutcome.Cancelled => SetupDockerResult.Fail(
                SetupDockerResultCode.Cancelled,
                "Docker operation was cancelled."),
            HostProcessOutcome.OutputLimitExceeded => SetupDockerResult.Fail(
                SetupDockerResultCode.OutputLimitExceeded,
                "Docker operation output exceeded the allowed limit."),
            HostProcessOutcome.FailedToStart => SetupDockerResult.Fail(
                SetupDockerResultCode.DockerUnavailable,
                "Docker CLI could not be started."),
            HostProcessOutcome.Completed when processResult.ExitCode == 0 => SetupDockerResult.Ok(
                "Docker operation succeeded.",
                session.Binding.EngineKind,
                session.Binding.ComposeMajorVersion),
            HostProcessOutcome.Completed => SetupDockerResult.Fail(
                SetupDockerResultCode.ProcessFailed,
                "Docker operation exited with a non-zero status."),
            _ => SetupDockerResult.Fail(
                SetupDockerResultCode.FailedUnexpected,
                "Docker operation ended in an unexpected state."),
        };
    }

    private static SetupDockerResult ExternalNotPinned() =>
        SetupDockerResult.Fail(
            SetupDockerResultCode.ExternalInputNotPinned,
            "External inputs must be pinned before this operation.");

    private static SetupDockerResult Cancelled() =>
        SetupDockerResult.Fail(
            SetupDockerResultCode.Cancelled,
            "Docker operation was cancelled.");

    private static SetupDockerResult AdapterIoFailure() =>
        SetupDockerResult.Fail(
            SetupDockerResultCode.FailedUnexpected,
            "Docker adapter file access failed.");

    private static SetupDockerResult AdapterDataFailure() =>
        SetupDockerResult.Fail(
            SetupDockerResultCode.OutputMalformed,
            "Docker adapter data was malformed.");
}
