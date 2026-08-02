using System.Collections.Concurrent;
using System.Text;
using Amane.Mailer.Operations;
using Amane.Mailer.Setup;

namespace Amane.Mailer.Tests.Setup;

public sealed class SetupHostDockerAdapterTests
{
    private static readonly string TestDigest =
        "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void ClassifyDockerHost_rejects_remote_and_unknown()
    {
        Assert.Equal(
            SetupDockerResultCode.RemoteDockerRejected,
            DockerEnvironmentProbe.ClassifyDockerHost("tcp://1.2.3.4:2375").Code);
        Assert.Equal(
            SetupDockerResultCode.RemoteDockerRejected,
            DockerEnvironmentProbe.ClassifyDockerHost("ssh://user@host").Code);
        Assert.Equal(
            SetupDockerResultCode.UnsupportedDockerEnvironment,
            DockerEnvironmentProbe.ClassifyDockerHost("fd://xyz").Code);
        Assert.True(DockerEnvironmentProbe.ClassifyDockerHost(null).IsSuccess);
        Assert.True(DockerEnvironmentProbe.ClassifyDockerHost(
            "npipe:////./pipe/docker_engine").IsSuccess);
        Assert.True(DockerEnvironmentProbe.ClassifyDockerHost(
            "unix:///var/run/docker.sock").IsSuccess);
    }

    [Theory]
    [InlineData("ssh://remote-host/pipe/docker_engine")]
    [InlineData("tcp://remote-host/pipe/docker_engine")]
    [InlineData("SSH://remote-host/pipe/docker_engine")]
    [InlineData("npipe:////remote-host/pipe/docker_engine")]
    [InlineData("npipe:////./pipe/docker_engine%2f../evil")]
    public void ClassifyEndpoint_rejects_adversarial_remote_pipe_shapes(string endpoint)
    {
        var kind = DockerEnvironmentProbe.ClassifyEndpointForTests(endpoint, pretendWindows: true);
        Assert.True(
            kind is DockerEndpointKind.RemoteRejected or DockerEndpointKind.Unknown,
            $"Expected fail-closed for {endpoint}, got {kind}");
    }

    [Theory]
    [InlineData("npipe:////./pipe/docker_engine")]
    [InlineData("npipe:////./pipe/docker_engine/")]
    [InlineData("npipe:////./pipe/dockerDesktopLinuxEngine")]
    [InlineData("npipe:////./pipe/dockerDesktopWindowsEngine")]
    [InlineData("NPIPE:////./pipe/dockerDesktopLinuxEngine")]
    public void ClassifyEndpoint_allows_exact_local_windows_pipes(string endpoint)
    {
        Assert.Equal(
            DockerEndpointKind.WindowsNamedPipe,
            DockerEnvironmentProbe.ClassifyEndpointForTests(endpoint, pretendWindows: true));
    }

    [Fact]
    public void TryResolve_is_not_public_raw_root_api()
    {
        var publicMethods = typeof(TrustedSetupHostLayoutResolver)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        Assert.DoesNotContain(publicMethods, m => m.Name == "TryResolve");
        Assert.Contains(publicMethods, m => m.Name == nameof(TrustedSetupHostLayoutResolver.TryResolveInstalled));
    }

    [Fact]
    public void HostProcessRunner_types_are_internal()
    {
        Assert.False(typeof(HostProcessSpec).IsPublic);
        Assert.False(typeof(HostProcessRunner).IsPublic);
        Assert.False(typeof(IHostProcessRunner).IsPublic);
        Assert.False(typeof(HostProcessResult).IsPublic);
    }

    [Fact]
    public async Task Probe_rejects_remote_context_endpoint()
    {
        var runner = new ScriptedProcessRunner(spec =>
        {
            var joined = string.Join(' ', spec.ArgumentList);
            if (joined.Contains("context show", StringComparison.Ordinal))
            {
                return Ok("remote\n");
            }

            if (joined.Contains("context inspect", StringComparison.Ordinal))
            {
                return Ok("""{"Endpoints":{"docker":{"Host":"ssh://user@host"}}}""");
            }

            return Ok("1.0.0\n");
        });

        var probe = new DockerEnvironmentProbe(
            runner,
            getDockerHost: static () => null,
            getDockerContextEnv: static () => null,
            resolveDockerExecutable: static () => "docker");
        var (result, binding) = await probe.ProbeAsync(CancellationToken.None);
        Assert.Null(binding);
        Assert.Equal(SetupDockerResultCode.RemoteContextRejected, result.Code);
    }

    [Fact]
    public async Task Probe_fail_closed_when_context_output_malformed()
    {
        var runner = new ScriptedProcessRunner(spec =>
        {
            var joined = string.Join(' ', spec.ArgumentList);
            if (joined.Contains("context show", StringComparison.Ordinal))
            {
                return Ok("default\n");
            }

            if (joined.Contains("context inspect", StringComparison.Ordinal))
            {
                return Ok("not-json");
            }

            return Ok("1.0.0\n");
        });

        var probe = new DockerEnvironmentProbe(
            runner,
            resolveDockerExecutable: static () => "docker");
        var (result, binding) = await probe.ProbeAsync(CancellationToken.None);
        Assert.Null(binding);
        Assert.Equal(SetupDockerResultCode.OutputMalformed, result.Code);
    }

    [Fact]
    public async Task Adapter_operations_require_session_and_pin_context()
    {
        await using var harness = await CreateHarnessAsync();
        var recorded = new ConcurrentQueue<IReadOnlyList<string>>();
        harness.Runner.OnRun = spec =>
        {
            var joined = string.Join(' ', spec.ArgumentList);
            if (IsBindingProbe(joined))
            {
                return null;
            }

            recorded.Enqueue(spec.ArgumentList.ToArray());
            return Ok(string.Empty);
        };

        var result = await harness.Adapter.ValidateComposeAsync(harness.Session, CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.True(recorded.TryDequeue(out var args));
        Assert.Equal("--context", args[0]);
        Assert.Equal(harness.Session.Binding.ContextName, args[1]);
        Assert.Contains("compose", args);
        Assert.Contains("--project-directory", args);
        Assert.DoesNotContain("down", args);
        Assert.DoesNotContain("--volumes", args);
        Assert.Equal("1", harness.LastChildEnv["COMPOSE_DISABLE_ENV_FILE"]);
        Assert.Equal(
            harness.Layout.ReleaseInventory.PinnedMailerImageReference,
            harness.LastChildEnv["MAILER_IMAGE_REFERENCE"]);
        Assert.Contains(
            $"{Path.DirectorySeparatorChar}managed{Path.DirectorySeparatorChar}bundles{Path.DirectorySeparatorChar}",
            harness.LastChildEnv["MAILER_TENANTS_HOST_PATH"]!
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            args,
            a => a.EndsWith(SetupDockerInventory.ImageDigestOverlayRelativePath, StringComparison.OrdinalIgnoreCase)
                || a.Contains(SetupDockerInventory.ImageDigestOverlayRelativePath, StringComparison.OrdinalIgnoreCase));
        Assert.False(harness.LastChildEnv.ContainsKey("COMPOSE_FILE"));
        Assert.False(harness.LastChildEnv.ContainsKey("DOCKER_HOST"));
    }

    [Fact]
    public async Task Revalidate_detects_context_endpoint_drift_to_remote()
    {
        await using var harness = await CreateHarnessAsync();
        harness.Runner.OnRun = spec =>
        {
            var joined = string.Join(' ', spec.ArgumentList);
            if (joined.Contains("context inspect", StringComparison.Ordinal))
            {
                return Ok("""{"Endpoints":{"docker":{"Host":"ssh://evil.example"}}}""");
            }

            if (joined.Contains("version --format", StringComparison.Ordinal))
            {
                return Ok((harness.Session.Binding.EngineIdentity ?? "27.0.0") + "\n");
            }

            return Ok(string.Empty);
        };

        var result = await harness.Adapter.ValidateComposeAsync(harness.Session, CancellationToken.None);
        Assert.Equal(SetupDockerResultCode.RemoteContextRejected, result.Code);
    }

    [Fact]
    public async Task EnsurePinnedImage_pulls_digest_never_latest()
    {
        await using var harness = await CreateHarnessAsync();
        IReadOnlyList<string>? args = null;
        harness.Runner.OnRun = spec =>
        {
            var joined = string.Join(' ', spec.ArgumentList);
            if (IsBindingProbe(joined))
            {
                return null;
            }

            args = spec.ArgumentList.ToArray();
            return Ok(string.Empty);
        };

        var result = await harness.Adapter.EnsurePinnedImageAvailableAsync(
            harness.Session,
            CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.NotNull(args);
        Assert.Contains("pull", args);
        Assert.Contains(harness.Layout.ReleaseInventory.PinnedMailerImageReference, args);
        Assert.DoesNotContain("latest", args, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StopFailedMailer_does_not_down_or_prune()
    {
        await using var harness = await CreateHarnessAsync();
        IReadOnlyList<string>? args = null;
        harness.Runner.OnRun = spec =>
        {
            var joined = string.Join(' ', spec.ArgumentList);
            if (IsBindingProbe(joined))
            {
                return null;
            }

            args = spec.ArgumentList.ToArray();
            return Ok(string.Empty);
        };

        var result = await harness.Adapter.StopFailedMailerAsync(harness.Session, CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.NotNull(args);
        Assert.Contains("stop", args);
        Assert.Contains(SetupDockerInventory.ServiceMailer, args);
        Assert.DoesNotContain("down", args);
        Assert.DoesNotContain("prune", args);
        Assert.DoesNotContain(SetupDockerInventory.ServiceMailerAcsAdmin, args);
    }

    [Fact]
    public async Task Concurrent_session_is_rejected()
    {
        await using var harness = await CreateHarnessAsync();
        var (second, session2) = await harness.Adapter.AcquireSessionAsync(
            harness.Layout,
            harness.Session.Binding,
            CancellationToken.None);
        Assert.Null(session2);
        Assert.Equal(SetupDockerResultCode.ConcurrentSetupRejected, second.Code);
    }

    [Fact]
    public async Task Timeout_and_cancel_and_nonzero_are_classified()
    {
        await using var harness = await CreateHarnessAsync();

        harness.Runner.OnRun = spec =>
        {
            if (IsBindingProbe(string.Join(' ', spec.ArgumentList)))
            {
                return null;
            }

            return new HostProcessResult
            {
                Outcome = HostProcessOutcome.TimedOut,
                ExitCode = -1,
            };
        };
        Assert.Equal(
            SetupDockerResultCode.Timeout,
            (await harness.Adapter.ValidateComposeAsync(harness.Session, CancellationToken.None)).Code);

        harness.Runner.OnRun = spec =>
        {
            if (IsBindingProbe(string.Join(' ', spec.ArgumentList)))
            {
                return null;
            }

            return new HostProcessResult
            {
                Outcome = HostProcessOutcome.Cancelled,
                ExitCode = -1,
            };
        };
        Assert.Equal(
            SetupDockerResultCode.Cancelled,
            (await harness.Adapter.ValidateComposeAsync(harness.Session, CancellationToken.None)).Code);

        harness.Runner.OnRun = spec =>
        {
            if (IsBindingProbe(string.Join(' ', spec.ArgumentList)))
            {
                return null;
            }

            return new HostProcessResult
            {
                Outcome = HostProcessOutcome.Completed,
                ExitCode = 17,
                StandardOutput = "secret=canary-token-value",
                StandardError = "path=/private/secret/dir",
            };
        };
        var failed = await harness.Adapter.ValidateComposeAsync(harness.Session, CancellationToken.None);
        Assert.Equal(SetupDockerResultCode.ProcessFailed, failed.Code);
        Assert.DoesNotContain("canary-token-value", failed.Message ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("/private/secret/dir", failed.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Staging_verification_is_not_available()
    {
        await using var harness = await CreateHarnessAsync();
        var result = await harness.Adapter.ExecuteStagingVerificationAsync(
            harness.Session,
            CancellationToken.None);
        Assert.Equal(SetupDockerResultCode.OperationNotAvailable, result.Code);
    }

    [Fact]
    public async Task Effective_inspection_requires_verifier_document_not_path()
    {
        await using var harness = await CreateHarnessAsync();
        var method = typeof(SetupHostDockerAdapter).GetMethod(nameof(SetupHostDockerAdapter.RunEffectiveInspectionAsync));
        Assert.NotNull(method);
        var parameters = method!.GetParameters();
        Assert.Equal(3, parameters.Length);
        Assert.Equal(typeof(SetupHostDockerSession), parameters[0].ParameterType);
        Assert.Equal(typeof(SetupMountVerifierDocument), parameters[1].ParameterType);
        Assert.DoesNotContain(parameters, p => p.ParameterType == typeof(string));

        IReadOnlyList<string>? inspectionArgs = null;
        harness.Runner.OnRun = spec =>
        {
            var joined = string.Join(' ', spec.ArgumentList);
            if (IsBindingProbe(joined))
            {
                return null;
            }

            inspectionArgs = spec.ArgumentList.ToArray();
            return Ok(ValidInspectionJson);
        };
        var verifier = new SetupMountVerifierDocument
        {
            BundleId = "bundle-test",
            SessionNonce = "nonce",
            SessionKey = Convert.ToHexString(new byte[32]),
            ExpiresAtUnix = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(),
            Members =
            [
                new SetupMountVerifierMember
                {
                    MemberId = "env:MAIL_SERVICE_TOKEN",
                    ExpectedMac = Convert.ToHexString(new byte[32]),
                },
            ],
        };

        var result = await harness.Adapter.RunEffectiveInspectionAsync(
            harness.Session,
            verifier,
            CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Inspection);
        Assert.NotNull(inspectionArgs);
        Assert.DoesNotContain(
            inspectionArgs!,
            a => a.EndsWith(
                ":" + SetupBundleLayout.ContainerRecordedMetadataPath + ":ro",
                StringComparison.Ordinal));
        Assert.Contains(
            inspectionArgs!,
            a => a.EndsWith(
                ":" + SetupDockerInventory.ContainerVerifierMountPath + ":ro",
                StringComparison.Ordinal));
        Assert.Equal(
            SetupBundleLayout.ContainerRecordedMetadataPath,
            harness.LastChildEnv["MAILER_SETUP_RECORDED_METADATA_PATH"]);
        Assert.EndsWith(
            Path.Combine("metadata", SetupBundleLayout.RecordedMetadataFileName),
            harness.LastChildEnv["MAILER_SETUP_RECORDED_METADATA_HOST_PATH"],
            StringComparison.Ordinal);

        var publicEnvPassThrough = inspectionArgs!
            .Zip(inspectionArgs!.Skip(1), (left, right) => (left, right))
            .Where(pair => string.Equals(pair.left, "-e", StringComparison.Ordinal))
            .Select(pair => pair.right)
            .Where(key => !string.Equals(key, SetupDockerInventory.ContainerVerifierEnvKey, StringComparison.Ordinal))
            .ToArray();
        Assert.Contains("COMPOSE_PROJECT_NAME", publicEnvPassThrough, StringComparer.Ordinal);
        Assert.Contains("MAILER_IMAGE_REFERENCE", publicEnvPassThrough, StringComparer.Ordinal);
        Assert.Contains("MAILER_TENANTS_HOST_PATH", publicEnvPassThrough, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Active_image_mismatch_is_rejected()
    {
        await using var harness = await CreateHarnessAsync(seedMatchingActive: false);
        Assert.Equal(SetupDockerResultCode.InvalidBundleInventory, harness.ComposePin.Code);

        // Without a usable compose pin the ACTIVE-dependent operation must not run at all.
        var result = await harness.Adapter.ValidateComposeAsync(harness.Session, CancellationToken.None);
        Assert.Equal(SetupDockerResultCode.ComposeInputNotPinned, result.Code);
    }

    [Fact]
    public async Task Active_dependent_operations_refuse_to_run_without_a_compose_pin()
    {
        await using var harness = await CreateHarnessAsync(pinInputs: false);

        Assert.Equal(
            SetupDockerResultCode.ExternalInputNotPinned,
            (await harness.Adapter.ValidateComposeAsync(harness.Session, CancellationToken.None)).Code);
        Assert.Equal(
            SetupDockerResultCode.ExternalInputNotPinned,
            (await harness.Adapter.RunMigrationAsync(harness.Session, CancellationToken.None)).Code);
        Assert.Equal(
            SetupDockerResultCode.ExternalInputNotPinned,
            (await harness.Adapter.StartOrRecreateMailerAsync(harness.Session, CancellationToken.None)).Code);
        Assert.Equal(
            SetupDockerResultCode.ExternalInputNotPinned,
            (await harness.Adapter.StopFailedMailerAsync(harness.Session, CancellationToken.None)).Code);
        Assert.Equal(
            SetupDockerResultCode.ExternalInputNotPinned,
            (await harness.Adapter.VerifyExternalInputsUnchangedAsync(harness.Session, CancellationToken.None)).Code);

        // The ACTIVE-independent pull is the one operation that stays available.
        Assert.True(
            (await harness.Adapter.EnsurePinnedImageAvailableAsync(harness.Session, CancellationToken.None))
                .IsSuccess);
    }

    [Fact]
    public async Task Compose_pin_requires_external_pin_first()
    {
        await using var harness = await CreateHarnessAsync(pinInputs: false);

        Assert.Equal(
            SetupDockerResultCode.ExternalInputNotPinned,
            (await harness.Adapter.ComposeCurrentActiveInputAsync(harness.Session, CancellationToken.None)).Code);

        Assert.True(
            (await harness.Adapter.PinExternalInputsAsync(harness.Session, CancellationToken.None)).IsSuccess);
        Assert.True(
            (await harness.Adapter.ComposeCurrentActiveInputAsync(harness.Session, CancellationToken.None)).IsSuccess);
        Assert.NotNull(harness.Session.ComposeInputs);
    }

    [Fact]
    public async Task External_input_change_is_detected_without_revealing_values()
    {
        await using var harness = await CreateHarnessAsync();
        Assert.True(
            (await harness.Adapter.VerifyExternalInputsUnchangedAsync(harness.Session, CancellationToken.None))
                .IsSuccess);

        File.WriteAllText(harness.Layout.ExternalEnvPath, "MAILER_DATA_PATH=/tmp/amane-canary-moved\n");

        var changed = await harness.Adapter.VerifyExternalInputsUnchangedAsync(
            harness.Session,
            CancellationToken.None);
        Assert.Equal(SetupDockerResultCode.ExternalInputChanged, changed.Code);
        Assert.DoesNotContain("amane-canary-moved", changed.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compose_pin_for_expected_generation_rejects_a_moved_active_pointer()
    {
        await using var harness = await CreateHarnessAsync();
        var expected = new SetupActivePointer
        {
            SchemaVersion = SetupActivePointer.CurrentSchemaVersion,
            BundleId = "bundle-test01",
            ActivationGeneration = 7,
        };

        var result = await harness.Adapter.ComposeExpectedActiveInputAsync(
            harness.Session,
            expected,
            CancellationToken.None);
        Assert.Equal(SetupDockerResultCode.ActiveGenerationMismatch, result.Code);
    }

    [Fact]
    public async Task Purge_rejects_unexpected_residue_in_the_verifier_temp_directory()
    {
        await using var harness = await CreateHarnessAsync();
        Directory.CreateDirectory(harness.Layout.VerifierTempDir);
        File.WriteAllText(Path.Combine(harness.Layout.VerifierTempDir, "not-a-verifier.json"), "{}");

        var result = await harness.Adapter.PurgeStaleMountVerifiersAsync(
            harness.Session,
            CancellationToken.None);
        Assert.Equal(SetupDockerResultCode.UnsafePath, result.Code);
    }

    [Fact]
    public async Task Purge_deletes_only_well_formed_stale_verifiers()
    {
        await using var harness = await CreateHarnessAsync();
        Directory.CreateDirectory(harness.Layout.VerifierTempDir);
        var stale = SetupBundleLayout.MountVerifierPath(
            harness.Layout.ManagedRoot,
            new string('a', 32));
        File.WriteAllText(stale, "{}");

        var result = await harness.Adapter.PurgeStaleMountVerifiersAsync(
            harness.Session,
            CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.False(File.Exists(stale));
        Assert.True(harness.Session.StaleVerifiersPurged);
    }

    [Fact]
    public async Task Migration_status_inspection_deserializes_the_classification()
    {
        await using var harness = await CreateHarnessAsync();
        IReadOnlyList<string>? args = null;
        harness.Runner.OnRun = spec =>
        {
            var joined = string.Join(' ', spec.ArgumentList);
            if (IsBindingProbe(joined))
            {
                return null;
            }

            args = spec.ArgumentList.ToArray();
            return Ok("""{"schemaVersion":1,"classification":"Behind"}""");
        };

        var result = await harness.Adapter.InspectMigrationStatusAsync(
            harness.Session,
            CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.MigrationStatus);
        Assert.Equal(SetupSchemaClassification.Behind, result.MigrationStatus!.Classification);
        Assert.NotNull(args);
        Assert.Contains("--status", args!);
        Assert.DoesNotContain("up", args!);
    }

    [Fact]
    public async Task Migration_status_inspection_rejects_an_unknown_classification()
    {
        await using var harness = await CreateHarnessAsync();
        harness.Runner.OnRun = spec =>
        {
            if (IsBindingProbe(string.Join(' ', spec.ArgumentList)))
            {
                return null;
            }

            return Ok("""{"schemaVersion":1,"classification":"TotallyMadeUp"}""");
        };

        var result = await harness.Adapter.InspectMigrationStatusAsync(
            harness.Session,
            CancellationToken.None);
        Assert.Equal(SetupDockerResultCode.OutputMalformed, result.Code);
        Assert.Null(result.MigrationStatus);
    }

    [Fact]
    public async Task Readiness_wait_retries_until_the_healthcheck_passes()
    {
        await using var harness = await CreateHarnessAsync();
        var attempts = 0;
        harness.Runner.OnRun = spec =>
        {
            if (IsBindingProbe(string.Join(' ', spec.ArgumentList)))
            {
                return null;
            }

            attempts++;
            return attempts < 3
                ? new HostProcessResult { Outcome = HostProcessOutcome.Completed, ExitCode = 1 }
                : Ok(string.Empty);
        };

        var result = await harness.Adapter.AwaitMailerHealthyAsync(harness.Session, CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public void Sanitizer_redacts_canaries()
    {
        var raw = "token=super-secret-value user@example.com C:\\Users\\x\\secret /var/private/key";
        var sanitized = DockerOutputSanitizer.SanitizeForInternalUse(raw);
        Assert.DoesNotContain("super-secret-value", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("user@example.com", sanitized, StringComparison.Ordinal);
        Assert.Contains("[redacted", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void Inventory_separates_known_and_operable_services()
    {
        Assert.Contains(SetupDockerInventory.ServiceMailerAcsAdmin, SetupDockerInventory.KnownServices);
        Assert.DoesNotContain(SetupDockerInventory.ServiceMailerAcsAdmin, SetupDockerInventory.OperableServices);
        Assert.True(SetupDockerInventory.ForbiddenProfiles.Contains(SetupDockerInventory.ProfileAcsAdmin));
    }

    [Fact]
    public void TrustedLayout_has_no_public_constructor()
    {
        var ctors = typeof(TrustedSetupHostLayout).GetConstructors(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        Assert.Empty(ctors);
    }

    [Fact]
    public void HostProcessSpec_uses_argument_list_not_shell_string_api()
    {
        var props = typeof(HostProcessSpec).GetProperties();
        Assert.Contains(props, p => p.Name == nameof(HostProcessSpec.ArgumentList));
        Assert.DoesNotContain(props, p => p.Name is "Arguments" or "Command" or "Shell");
    }

    [Fact]
    public async Task Mode1_layout_requires_mailpit_overlay_and_digest()
    {
        var root = Path.Combine(Path.GetTempPath(), "amane-m1-" + Guid.NewGuid().ToString("N"));
        var fs = new HostSetupFileSystem();
        var inventory = CreateInventory(includeMailpit: false);
        var result = TrustedSetupHostLayoutResolver.CreateLayoutForTests(
            fs,
            root,
            SetupMode.LocalMailpit,
            inventory,
            "dep1",
            MinimalCompose,
            mailpitOverlayContents: "services: {}\n",
            out _);
        Assert.Equal(SetupDockerResultCode.InvalidBundleInventory, result.Code);

        inventory = CreateInventory(includeMailpit: true);
        result = TrustedSetupHostLayoutResolver.CreateLayoutForTests(
            fs,
            root + "-ok",
            SetupMode.LocalMailpit,
            inventory,
            "dep1",
            MinimalCompose,
            mailpitOverlayContents: "services:\n  mailpit:\n    image: ${MAILPIT_IMAGE}\n",
            out var layout);
        Assert.True(result.IsSuccess);
        Assert.NotNull(layout);
        Assert.Equal(4, layout!.ComposeFilePaths.Count);
        Assert.EndsWith(
            SetupDockerInventory.ImageDigestOverlayRelativePath,
            layout.ComposeFilePaths[1],
            StringComparison.Ordinal);
        Assert.EndsWith(
            SetupDockerInventory.RecordedMetadataOverlayRelativePath,
            layout.ComposeFilePaths[2],
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SetupDockerInventory.DeployComposeRelativePath, SetupMode.StagingNoSend, false)]
    [InlineData(SetupDockerInventory.ImageDigestOverlayRelativePath, SetupMode.StagingNoSend, false)]
    [InlineData(SetupDockerInventory.RecordedMetadataOverlayRelativePath, SetupMode.StagingNoSend, false)]
    [InlineData(SetupDockerInventory.MailpitOverlayRelativePath, SetupMode.LocalMailpit, true)]
    public void Trusted_layout_rejects_one_byte_modified_compose_overlay(
        string overlayRelativePath,
        SetupMode mode,
        bool includeMailpit)
    {
        var root = Path.Combine(Path.GetTempPath(), "amane-overlay-digest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var fs = new HostSetupFileSystem();
            var created = TrustedSetupHostLayoutResolver.CreateLayoutForTests(
                fs,
                root,
                mode,
                CreateInventory(includeMailpit),
                "dep1",
                MinimalCompose,
                mailpitOverlayContents: includeMailpit
                    ? "services:\n  mailpit:\n    image: ${MAILPIT_IMAGE}\n"
                    : null,
                out _);
            Assert.True(created.IsSuccess);

            var overlayPath = Path.Combine(root, overlayRelativePath);
            var bytes = File.ReadAllBytes(overlayPath);
            bytes[^1] ^= 1;
            File.WriteAllBytes(overlayPath, bytes);

            var resolved = TrustedSetupHostLayoutResolver.TryResolve(
                fs,
                root,
                mode,
                "dep1",
                out _);
            Assert.Equal(SetupDockerResultCode.InvalidBundleInventory, resolved.Code);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best effort.
            }
        }
    }

    [Fact]
    public void StartInfo_uses_ArgumentList_and_cleared_environment()
    {
        var spec = new HostProcessSpec(
            "docker",
            ["--context", "default", "compose", "version"],
            workingDirectory: null,
            HostProcessRunner.CreateMinimalDockerChildEnvironment(clearDockerOverrides: true),
            TimeSpan.FromSeconds(5));
        var psi = HostProcessRunner.CreateStartInfo(spec);
        Assert.Equal(4, psi.ArgumentList.Count);
        Assert.Equal(string.Empty, psi.Arguments);
        Assert.Equal("1", psi.Environment["COMPOSE_DISABLE_ENV_FILE"]);
        Assert.False(psi.Environment.ContainsKey("DOCKER_HOST"));
        Assert.False(psi.Environment.ContainsKey("DOCKER_CONTEXT"));
        Assert.False(psi.Environment.ContainsKey("COMPOSE_FILE"));
    }

    [Fact]
    public void Minimal_docker_child_environment_rejects_remote_overrides_from_extra()
    {
        var env = HostProcessRunner.CreateMinimalDockerChildEnvironment(
            clearDockerOverrides: true,
            extra: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["DOCKER_HOST"] = "tcp://127.0.0.1:2375",
                ["DOCKER_CONTEXT"] = "remote",
                ["COMPOSE_PROJECT_NAME"] = "amane-mailer-qual",
            });

        Assert.False(env.ContainsKey("DOCKER_HOST"));
        Assert.False(env.ContainsKey("DOCKER_CONTEXT"));
        Assert.Equal("amane-mailer-qual", env["COMPOSE_PROJECT_NAME"]);
        Assert.Equal("1", env["COMPOSE_DISABLE_ENV_FILE"]);
    }

    [Fact]
    public void Minimal_docker_child_environment_copies_windows_compose_plugin_roots()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var env = HostProcessRunner.CreateMinimalDockerChildEnvironment(clearDockerOverrides: true);

        // Docker Desktop Compose CLI plugins resolve via these roots; omit => compose_unavailable.
        AssertPresentAmbientCopied(env, "ProgramFiles");
        AssertPresentAmbientCopied(env, "ProgramData");
        AssertPresentAmbientCopied(env, "LOCALAPPDATA");
        AssertPresentAmbientCopied(env, "APPDATA");
        Assert.False(env.ContainsKey("DOCKER_HOST"));
        Assert.False(env.ContainsKey("DOCKER_CONTEXT"));
        Assert.False(env.ContainsKey("COMPOSE_FILE"));
    }

    private static void AssertPresentAmbientCopied(
        IReadOnlyDictionary<string, string?> env,
        string key)
    {
        var ambient = Environment.GetEnvironmentVariable(key);
        Assert.False(string.IsNullOrEmpty(ambient));
        Assert.True(env.ContainsKey(key));
        Assert.Equal(ambient, env[key]);
    }

    private static bool IsBindingProbe(string joinedArguments) =>
        joinedArguments.Contains("context inspect", StringComparison.Ordinal)
        || joinedArguments.Contains("version --format", StringComparison.Ordinal)
        || joinedArguments.Contains("context show", StringComparison.Ordinal)
        || joinedArguments.Contains("compose version", StringComparison.Ordinal);

    private static async Task<Harness> CreateHarnessAsync(
        bool seedMatchingActive = true,
        bool pinInputs = true)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "amane-hd-" + Guid.NewGuid().ToString("N")));
        var fs = new HostSetupFileSystem();
        var inventory = CreateInventory(includeMailpit: false);
        var layoutResult = TrustedSetupHostLayoutResolver.CreateLayoutForTests(
            fs,
            root,
            SetupMode.StagingNoSend,
            inventory,
            "depabc",
            MinimalCompose,
            mailpitOverlayContents: null,
            out var layout);
        Assert.True(layoutResult.IsSuccess);
        Assert.NotNull(layout);

        Directory.CreateDirectory(layout!.ManagedRoot);
        Directory.CreateDirectory(layout.StatePath);
        File.WriteAllText(layout.ExternalEnvPath, "MAILER_DATA_PATH=/tmp/amane-test-data\n");
        SeedSealingKey(layout);
        SeedActive(layout, matching: seedMatchingActive);

        var opsHandler = new ScriptedProcessRunner(spec =>
        {
            var joined = string.Join(' ', spec.ArgumentList);
            if (joined.Contains("context show", StringComparison.Ordinal))
            {
                return Ok("default\n");
            }

            if (joined.Contains("context inspect", StringComparison.Ordinal))
            {
                var endpoint = OperatingSystem.IsWindows()
                    ? "npipe:////./pipe/docker_engine"
                    : "unix:///var/run/docker.sock";
                return Ok($"{{\"Endpoints\":{{\"docker\":{{\"Host\":\"{endpoint}\"}}}}}}");
            }

            if (joined.Contains("version --format", StringComparison.Ordinal))
            {
                return Ok("27.0.0\n");
            }

            if (joined.Contains("compose version", StringComparison.Ordinal))
            {
                return Ok("v2.29.0\n");
            }

            return Ok(string.Empty);
        });
        opsHandler.CaptureEnvironment = true;

        var probe = new DockerEnvironmentProbe(
            opsHandler,
            getDockerHost: static () => null,
            getDockerContextEnv: static () => null,
            resolveDockerExecutable: static () => "docker");
        var adapter = new SetupHostDockerAdapter(fs, opsHandler, probe);
        var (probeResult, binding) = await adapter.CheckDockerAsync(CancellationToken.None);
        Assert.True(probeResult.IsSuccess);
        Assert.NotNull(binding);

        var (sessionResult, session) = await adapter.AcquireSessionAsync(layout, binding!, CancellationToken.None);
        Assert.True(sessionResult.IsSuccess);
        Assert.NotNull(session);

        // ACTIVE-dependent operations require pinned inputs, so the harness follows the same pin
        // order the apply engine uses. The compose pin is where a mismatched ACTIVE is detected,
        // so its result is surfaced instead of asserted here.
        var composePin = SetupDockerResult.Ok();
        if (pinInputs)
        {
            var externalPin = await adapter.PinExternalInputsAsync(session!, CancellationToken.None);
            Assert.True(externalPin.IsSuccess);
            composePin = await adapter.ComposeCurrentActiveInputAsync(session!, CancellationToken.None);
            var purge = await adapter.PurgeStaleMountVerifiersAsync(session!, CancellationToken.None);
            Assert.True(purge.IsSuccess);
        }

        return new Harness(root, layout, adapter, session!, opsHandler, composePin);
    }

    private static TrustedReleaseInventory CreateInventory(bool includeMailpit) =>
        new()
        {
            AllowedImageRepository = "ghcr.io/kooiei-in4a/amane-mailer",
            RequiredImageDigest = TestDigest,
            AllowedDisplayTag = "sha-testfixture",
            ComposeBundleVersion = "1",
            LauncherVersionMin = "1.2.0",
            LauncherVersionMax = "1.2.0",
            ProjectNamePrefix = "amane",
            MailpitImageReference = includeMailpit
                ? "axllent/mailpit@" + TestDigest
                : null,
        };

    private const string MinimalCompose =
        """
        services:
          mailer-migrate:
            image: ${MAILER_IMAGE_REPOSITORY}:${MAILER_IMAGE_TAG}
            profiles: [ops]
          mailer:
            image: ${MAILER_IMAGE_REPOSITORY}:${MAILER_IMAGE_TAG}
          mailer-acs-admin:
            image: ${MAILER_IMAGE_REPOSITORY}:${MAILER_IMAGE_TAG}
            profiles: [acs-admin]
        """;

    private const string ValidInspectionJson =
        """
        {
          "schemaVersion": 1,
          "mailerVersion": "1.0.0",
          "managed": true,
          "effective": {
            "credentialStatus": "configured"
          },
          "mountAttestation": {
            "result": "matched"
          },
          "bundleIntegrity": {
            "result": "not-evaluated"
          },
          "tenantConfigurationSource": "managed",
          "credentialSource": "file"
        }
        """;

    private static void SeedSealingKey(TrustedSetupHostLayout layout)
    {
        var path = SetupBundleLayout.HostSealingKeyPath(layout.ManagedRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        SecureFileCreate.WriteAllBytesCreateNew(path, new byte[SetupIntegritySealer.SealingKeyLength]);
    }

    private static void SeedActive(TrustedSetupHostLayout layout, bool matching)
    {
        const string bundleId = "bundle-test01";
        var bundleRoot = SetupBundleLayout.BundleRoot(layout.ManagedRoot, bundleId);
        Directory.CreateDirectory(SetupBundleLayout.EnvDir(bundleRoot));
        Directory.CreateDirectory(SetupBundleLayout.ConfigDir(bundleRoot));
        Directory.CreateDirectory(SetupBundleLayout.SecretsDir(bundleRoot));
        Directory.CreateDirectory(SetupBundleLayout.MetadataDir(bundleRoot));
        File.WriteAllText(
            Path.Combine(SetupBundleLayout.ConfigDir(bundleRoot), SetupBundleLayout.TenantsFileName),
            "{}");
        File.WriteAllText(
            Path.Combine(SetupBundleLayout.MetadataDir(bundleRoot), SetupBundleLayout.RecordedMetadataFileName),
            "{}");
        var repo = matching
            ? layout.ReleaseInventory.AllowedImageRepository
            : "evil.example/other";
        var tag = matching ? layout.ReleaseInventory.AllowedDisplayTag : "sha-evil";
        File.WriteAllText(
            Path.Combine(SetupBundleLayout.EnvDir(bundleRoot), SetupBundleLayout.ComposeEnvFileName),
            $"MAILER_IMAGE_REPOSITORY={repo}\nMAILER_IMAGE_TAG={tag}\nMAILER_PROVIDER=mailpit\n");
        File.WriteAllText(
            Path.Combine(SetupBundleLayout.EnvDir(bundleRoot), SetupBundleLayout.SecretsEnvFileName),
            "MAIL_SERVICE_TOKEN=synthetic-test-token-not-real\n");
        File.WriteAllText(
            Path.Combine(bundleRoot, SetupBundleLayout.FinalizedMarkerFileName),
            string.Empty);
        File.WriteAllText(
            layout.ActivePointerPath,
            $"{{\"bundleId\":\"{bundleId}\",\"activationGeneration\":1,\"schemaVersion\":1}}\n");
    }

    private static HostProcessResult Ok(string stdout) =>
        new()
        {
            Outcome = HostProcessOutcome.Completed,
            ExitCode = 0,
            StandardOutput = stdout,
            StandardError = string.Empty,
        };

    private sealed class Harness : IAsyncDisposable
    {
        public Harness(
            string root,
            TrustedSetupHostLayout layout,
            SetupHostDockerAdapter adapter,
            SetupHostDockerSession session,
            ScriptedProcessRunner runner,
            SetupDockerResult composePin)
        {
            Root = root;
            Layout = layout;
            Adapter = adapter;
            Session = session;
            Runner = runner;
            ComposePin = composePin;
        }

        public string Root { get; }
        public TrustedSetupHostLayout Layout { get; }
        public SetupHostDockerAdapter Adapter { get; }
        public SetupHostDockerSession Session { get; }
        public ScriptedProcessRunner Runner { get; }
        public SetupDockerResult ComposePin { get; }
        public Dictionary<string, string?> LastChildEnv => Runner.LastEnvironment;

        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // ignore
            }
        }
    }

    private sealed class ScriptedProcessRunner : IHostProcessRunner
    {
        private readonly Func<HostProcessSpec, HostProcessResult> _default;

        public ScriptedProcessRunner(Func<HostProcessSpec, HostProcessResult> defaultHandler) =>
            _default = defaultHandler;

        public Func<HostProcessSpec, HostProcessResult?>? OnRun { get; set; }
        public bool CaptureEnvironment { get; set; }
        public Dictionary<string, string?> LastEnvironment { get; private set; } = new(StringComparer.Ordinal);

        public Task<HostProcessResult> RunAsync(HostProcessSpec spec, CancellationToken cancellationToken)
        {
            if (CaptureEnvironment)
            {
                LastEnvironment = new Dictionary<string, string?>(spec.Environment, StringComparer.Ordinal);
            }

            if (OnRun is not null)
            {
                var custom = OnRun(spec);
                if (custom is not null)
                {
                    return Task.FromResult(custom);
                }
            }

            return Task.FromResult(_default(spec));
        }
    }

    private sealed class MultiplexRunner : IHostProcessRunner
    {
        private readonly IHostProcessRunner _primary;
        private readonly IHostProcessRunner _secondary;

        public MultiplexRunner(IHostProcessRunner primary, IHostProcessRunner secondary)
        {
            _primary = primary;
            _secondary = secondary;
        }

        public bool UseSecondary { get; set; }

        public Task<HostProcessResult> RunAsync(HostProcessSpec spec, CancellationToken cancellationToken)
        {
            var joined = string.Join(' ', spec.ArgumentList);
            var isBindingRevalidation =
                joined.Contains("context inspect", StringComparison.Ordinal)
                || joined.Contains("version --format", StringComparison.Ordinal);
            return (UseSecondary && !isBindingRevalidation ? _secondary : _primary)
                .RunAsync(spec, cancellationToken);
        }
    }
}
