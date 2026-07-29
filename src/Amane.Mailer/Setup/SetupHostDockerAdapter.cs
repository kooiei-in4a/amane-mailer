using System.Text.Json;

namespace Amane.Mailer.Setup;

/// <summary>
/// Fixed host Docker adapter. Callers supply sessions and typed documents — never raw paths,
/// argv, compose files, images, or environment dictionaries.
/// </summary>
public sealed class SetupHostDockerAdapter
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    private readonly ISetupFileSystem _fileSystem;
    private readonly IHostProcessRunner _runner;
    private readonly DockerEnvironmentProbe _probe;
    private readonly ManagedComposeEnvComposer _envComposer;

    public SetupHostDockerAdapter(ISetupFileSystem fileSystem)
        : this(fileSystem, new HostProcessRunner(), probe: null, envComposer: null)
    {
    }

    internal SetupHostDockerAdapter(
        ISetupFileSystem fileSystem,
        IHostProcessRunner runner,
        DockerEnvironmentProbe? probe = null,
        ManagedComposeEnvComposer? envComposer = null)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _probe = probe ?? new DockerEnvironmentProbe(_runner);
        _envComposer = envComposer ?? new ManagedComposeEnvComposer(_fileSystem);
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

        byte[]? verifierBytes = null;
        string? hostVerifierPath = null;
        SetupDockerResult? operationResult = null;
        try
        {
            verifierBytes = JsonSerializer.SerializeToUtf8Bytes(
                verifier,
                SetupHostDockerJsonContext.Default.SetupMountVerifierDocument);

            var verifierDir = Path.Combine(session.Layout.ManagedRoot, "tmp");
            if (!_fileSystem.DirectoryExists(verifierDir))
            {
                _fileSystem.CreateOwnerOnlyDirectory(verifierDir);
            }

            hostVerifierPath = Path.GetFullPath(
                Path.Combine(verifierDir, $"mount-verifier-{Guid.NewGuid():N}.json"));
            if (!SetupPathGuard.TryEnsurePathSafeUnderRoot(
                    _fileSystem, session.Layout.ManagedRoot, hostVerifierPath, out _, out _))
            {
                return SetupDockerResult.Fail(
                    SetupDockerResultCode.UnsafePath,
                    "Verifier temp path rejected.");
            }

            _fileSystem.WriteProtectedFileCreateNew(hostVerifierPath, verifierBytes);

            var composeEnvResult = _envComposer.TryCompose(
                session.Layout,
                out var composeEnv,
                out _);
            if (!composeEnvResult.IsSuccess)
            {
                return composeEnvResult;
            }

            var env = new Dictionary<string, string>(composeEnv, StringComparer.Ordinal)
            {
                [SetupDockerInventory.ContainerVerifierEnvKey] =
                    SetupDockerInventory.ContainerVerifierMountPath,
            };

            // Recorded metadata mount/env come from compose.recorded-metadata.yml (same as
            // normal mailer). One-shot delta is the ephemeral verifier mount/env only.
            var args = BuildComposeArgPrefix(session)
                .Concat([
                    "run", "--rm", "--no-deps", "--pull", "never",
                    "-v", $"{hostVerifierPath}:{SetupDockerInventory.ContainerVerifierMountPath}:ro",
                    "-e", SetupDockerInventory.ContainerVerifierEnvKey,
                    SetupDockerInventory.ServiceMailer,
                    "setup", "inspect-effective", "--format", "json",
                ])
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
            "Staging verification is owned by Issue #451 and is not available yet."));
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

    private async Task<SetupDockerResult> RunComposeOperationAsync(
        SetupHostDockerSession session,
        IReadOnlyList<string> composeArgs,
        CancellationToken cancellationToken)
    {
        session.ThrowIfDisposed();
        try
        {
            var composeEnvResult = _envComposer.TryCompose(
                session.Layout,
                out var composeEnv,
                out _);
            if (!composeEnvResult.IsSuccess)
            {
                return composeEnvResult;
            }

            return await RunDockerAsync(
                session,
                PrefixedDockerArgs(session, composeArgs),
                session.Layout.ProjectDirectory,
                composeEnv,
                cancellationToken);
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
        bool deserializeInspection = false)
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
            DefaultTimeout);

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
