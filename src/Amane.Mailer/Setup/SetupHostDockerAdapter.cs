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

    public SetupHostDockerAdapter(
        ISetupFileSystem fileSystem,
        IHostProcessRunner? runner = null,
        DockerEnvironmentProbe? probe = null,
        ManagedComposeEnvComposer? envComposer = null)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _runner = runner ?? new HostProcessRunner();
        _probe = probe ?? new DockerEnvironmentProbe(_runner);
        _envComposer = envComposer ?? new ManagedComposeEnvComposer(_fileSystem);
    }

    public Task<(SetupDockerResult Result, DockerConnectionBinding? Binding)> CheckDockerAsync(
        CancellationToken cancellationToken) =>
        _probe.ProbeAsync(cancellationToken);

    public async Task<(SetupDockerResult Result, SetupHostDockerSession? Session)> AcquireSessionAsync(
        TrustedSetupHostLayout layout,
        DockerConnectionBinding binding,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(binding);
        cancellationToken.ThrowIfCancellationRequested();

        var revalidate = _probe.RevalidateBinding(binding);
        if (!revalidate.IsSuccess)
        {
            return (revalidate, null);
        }

        try
        {
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

            var composeEnvResult = _envComposer.TryCompose(session.Layout, out var composeEnv);
            if (!composeEnvResult.IsSuccess)
            {
                return composeEnvResult;
            }

            var env = new Dictionary<string, string>(composeEnv, StringComparer.Ordinal)
            {
                [SetupDockerInventory.ContainerVerifierEnvKey] =
                    SetupDockerInventory.ContainerVerifierMountPath,
            };

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
                cancellationToken);
        }
        finally
        {
            DockerOutputSanitizer.ZeroBuffer(verifierBytes);
            var deleteFailed = false;
            if (hostVerifierPath is not null && _fileSystem.FileExists(hostVerifierPath))
            {
                try
                {
                    _fileSystem.DeleteFile(hostVerifierPath);
                }
                catch
                {
                    deleteFailed = true;
                }

                if (!deleteFailed && _fileSystem.FileExists(hostVerifierPath))
                {
                    deleteFailed = true;
                }
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
        cancellationToken.ThrowIfCancellationRequested();
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
        var composeEnvResult = _envComposer.TryCompose(session.Layout, out var composeEnv);
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

    private async Task<SetupDockerResult> RunDockerAsync(
        SetupHostDockerSession session,
        IReadOnlyList<string> args,
        string? workingDirectory,
        IReadOnlyDictionary<string, string>? composeEnv,
        CancellationToken cancellationToken)
    {
        var revalidate = _probe.RevalidateBinding(session.Binding);
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
        catch
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.FailedUnexpected,
                "Docker process execution failed unexpectedly.");
        }

        // Sanitize internally; never return raw streams.
        _ = DockerOutputSanitizer.SanitizeForInternalUse(processResult.StandardOutput);
        _ = DockerOutputSanitizer.SanitizeForInternalUse(processResult.StandardError);

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
}
