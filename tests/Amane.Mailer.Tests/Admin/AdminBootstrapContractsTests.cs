using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amane.Mailer.Admin;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Operations;
using Amane.Mailer.Operations.AdminBootstrap;
using Amane.Mailer.Setup;
using Amane.Mailer.Tests.Setup;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests.Admin;

public sealed class AdminBootstrapContractsTests
{
    [Fact]
    public void Workflow_session_identifier_is_typed_fixed_length_and_redacted()
    {
        var operationId = AdminBootstrapOperationId.Create();
        var sessionId = AdminWorkflowSessionId.FromOperationId(operationId);

        Assert.True(
            Regex.IsMatch(operationId.Value, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant),
            "Generated operation identifier format was invalid.");
        Assert.Equal(64, operationId.Value.Length);
        Assert.True(
            Regex.IsMatch(
                sessionId.Value,
                "^setup-v1:[0-9a-f]{64}$",
                RegexOptions.CultureInvariant),
            "Generated workflow session identifier format was invalid.");
        Assert.Equal(73, sessionId.Value.Length);
        Assert.True(AdminWorkflowSessionId.TryParse(sessionId.Value, out var parsed));
        Assert.True(
            string.Equals(sessionId.Value, parsed.Value, StringComparison.Ordinal),
            "Workflow session identifier did not round-trip.");
        Assert.Equal("[redacted]", operationId.ToString());
        Assert.Equal("[redacted]", sessionId.ToString());
        Assert.False(AdminWorkflowSessionId.TryParse(operationId.Value, out _));
    }

    [Fact]
    public void Workflow_session_audit_uses_fixed_target()
    {
        var operationId = AdminBootstrapOperationId.Create();
        var sessionId = AdminWorkflowSessionId.FromOperationId(operationId);
        var audit = AdminAuthenticationHandlers.BuildAuthAuditEvent(
            new DefaultHttpContext(),
            new MailerAdminOptions(),
            TimeProvider.System,
            AdminAuditLog.EventTypes.LoginSucceeded,
            AdminAuditLog.Results.Success,
            "admin",
            sessionId.Value);

        Assert.Equal(AdminAuditLog.TargetTypes.AdminSession, audit.TargetType);
        Assert.Equal(AdminAuthenticationHandlers.WorkflowSessionAuditTarget, audit.TargetId);
        Assert.True(
            audit.TargetId is null
            || !audit.TargetId.Contains(operationId.Value, StringComparison.Ordinal),
            "Audit target exposed workflow correlation.");
    }

    [Fact]
    public void Public_setup_documents_do_not_serialize_operation_id()
    {
        var operationId = AdminBootstrapOperationId.Create();
        var recorded = Recorded(operationId);
        var rawRecorded = JsonSerializer.Serialize(
            recorded,
            SetupJsonContext.Default.SetupRecordedMetadata);
        Assert.True(
            rawRecorded.Contains(operationId.Value, StringComparison.Ordinal),
            "Internal recorded metadata omitted its operation guard.");

        var inspection = new SetupInspectEffectiveResult
        {
            MailerVersion = "test",
            Managed = true,
            Recorded = new SetupInspectRecordedSummary
            {
                SetupBundleId = recorded.BundleId,
                ConfigurationFingerprint = recorded.ConfigurationFingerprint,
                Mode = recorded.Mode,
                SchemaVersion = recorded.SchemaVersion,
            },
            Effective = new SetupInspectEffectiveSummary
            {
                ConfigurationFingerprint = recorded.ConfigurationFingerprint,
                CredentialStatus = "present",
                FingerprintsMatchRecorded = true,
            },
            MountAttestation = new SetupInspectAttestationSummary { Result = "matched" },
            BundleIntegrity = new SetupInspectAttestationSummary { Result = "provisional" },
            TenantConfigurationSource = "managed",
            CredentialSource = "file",
        };
        var inspectionJson = JsonSerializer.Serialize(
            inspection,
            SetupInspectJsonContext.Default.SetupInspectEffectiveResult);
        var verificationJson = JsonSerializer.Serialize(
            new SetupVerificationRecord
            {
                SchemaVersion = SetupVerificationRecord.CurrentSchemaVersion,
                Status = SetupVerificationRecord.StatusCommitted,
                BundleId = recorded.BundleId,
                ActivationGeneration = 1,
                FingerprintComparison = SetupVerificationRecord.FingerprintMatched,
                HostAtRest = SetupIntegrityMerger.Matched,
                MountAttestation = SetupIntegrityMerger.Matched,
                BundleIntegrity = SetupIntegrityMerger.Matched,
                RuntimeIdentityBinding = SetupRuntimeIdentityBindingResult.Matched,
                Readiness = SetupVerificationRecord.ReadinessPassed,
                SendReadyEvaluation = SetupVerificationRecord.SendReadyNotEvaluated,
                CommittedAt = "2026-07-30T00:00:00Z",
            },
            SetupApplyJsonContext.Default.SetupVerificationRecord);

        Assert.True(
            !inspectionJson.Contains(operationId.Value, StringComparison.Ordinal),
            "Public inspection exposed workflow correlation.");
        Assert.True(
            !verificationJson.Contains(operationId.Value, StringComparison.Ordinal),
            "Verification record exposed workflow correlation.");
    }

    [Fact]
    public void Scope_fingerprint_is_order_and_duplicate_independent()
    {
        var first = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var second = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

        Assert.Equal(
            AdminBootstrapScopeFingerprint.Compute([first, second]),
            AdminBootstrapScopeFingerprint.Compute([second, first, first]));
        Assert.NotEqual(
            AdminBootstrapScopeFingerprint.Compute([first]),
            AdminBootstrapScopeFingerprint.Compute([second]));
    }

    [Fact]
    public void Ownership_pending_does_not_overwrite_current()
    {
        var root = TempRoot();
        try
        {
            var fileSystem = new HostSetupFileSystem();
            fileSystem.CreateOwnerOnlyDirectory(root);
            fileSystem.CreateOwnerOnlyDirectory(SetupBundleLayout.StateDir(root));
            var store = new AdminBootstrapOwnershipStore(fileSystem);
            var currentOperation = AdminBootstrapOperationId.Create();
            var current = Ownership(currentOperation, AdminBootstrapOwnershipState.Succeeded, "bundle-current");
            Assert.True(store.PromotePendingToCurrent(root, current).IsFullySucceeded);

            var pendingOperation = AdminBootstrapOperationId.Create();
            var pending = Ownership(pendingOperation, AdminBootstrapOwnershipState.Prepared, "bundle-pending");
            Assert.True(store.WritePending(root, pending).IsSuccess);

            Assert.True(
                string.Equals(
                    currentOperation.Value,
                    store.ReadCurrent(root).Document?.OperationId,
                    StringComparison.Ordinal),
                "Current ownership changed during pending creation.");
            Assert.True(
                string.Equals(
                    pendingOperation.Value,
                    store.ReadPending(root).Document?.OperationId,
                    StringComparison.Ordinal),
                "Pending ownership operation did not round-trip.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Fresh_guarded_sync_uses_null_absence_and_restarts_as_no_op()
    {
        var root = TempRoot();
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Mailer"] = $"Data Source={databasePath}",
            })
            .Build();
        var factory = new SqliteConnectionFactory(configuration);
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            await new SqlMigrationRunner(factory).ApplyPendingAsync(cancellationToken);
            var database = new AdminBootstrapDatabase(factory, TimeProvider.System);
            var before = await database.InspectReadOnlyAsync(cancellationToken);
            Assert.Equal(AdminBootstrapDatabaseClassification.Fresh, before.Classification);
            Assert.Null(before.AdminConfigCredentialEpoch);
            Assert.Null(before.AdminUserCredentialEpoch);
            Assert.Null(before.ScopeFingerprint);

            var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            var operationId = AdminBootstrapOperationId.Create();
            var expectation = new SetupAdminBootstrapExpectation
            {
                OperationId = operationId.Value,
                Before = before.ToExpectationState(includeFreshSessionGuard: true),
                After = new SetupAdminDatabaseExpectationState
                {
                    Classification = AdminBootstrapDatabaseClassification.ManagedSameUser,
                    AdminConfigCount = 1,
                    AdminUserCount = 1,
                    AdminConfigCredentialEpoch = 0,
                    AdminUserCredentialEpoch = 0,
                    ScopeFingerprint = AdminBootstrapScopeFingerprint.Compute([tenantId]),
                },
            };
            var hash = AdminPasswordHasher.Hash("test-admin-password");

            Assert.Equal(
                0,
                await database.EnsureExpectedStateAsync(
                    expectation,
                    "admin",
                    hash,
                    [tenantId],
                    cancellationToken));
            Assert.Equal(
                0,
                await database.EnsureExpectedStateAsync(
                    expectation,
                    "admin",
                    hash,
                    [tenantId],
                    cancellationToken));

            await using var connection = await factory.OpenConnectionAsync(cancellationToken);
            await using var sessions = connection.CreateCommand();
            sessions.CommandText = "SELECT COUNT(*) FROM admin_sessions;";
            Assert.Equal(0L, await sessions.ExecuteScalarAsync(cancellationToken));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Existing_workflow_session_is_revoked_and_never_reinserted()
    {
        var (root, factory) = await CreateMigratedDatabaseAsync();
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var repository = new AdminSessionRepository(factory);
            var operationId = AdminBootstrapOperationId.Create();
            var workflowId = AdminWorkflowSessionId.FromOperationId(operationId);
            var now = DateTimeOffset.UtcNow;
            var session = Session(workflowId.Value, now);

            Assert.Equal(
                AdminWorkflowSessionCreateResult.Created,
                await repository.CreateWorkflowSessionAsync(
                    workflowId,
                    session,
                    3,
                    now,
                    cancellationToken));
            Assert.Equal(
                AdminWorkflowSessionCreateResult.RecoveryRequired,
                await repository.CreateWorkflowSessionAsync(
                    workflowId,
                    session,
                    3,
                    now.AddSeconds(1),
                    cancellationToken));
            Assert.Equal(
                AdminWorkflowSessionCreateResult.RecoveryRequired,
                await repository.CreateWorkflowSessionAsync(
                    workflowId,
                    session,
                    3,
                    now.AddSeconds(2),
                    cancellationToken));

            var persisted = await repository.GetSessionAsync(workflowId.Value, cancellationToken);
            Assert.NotNull(persisted);
            Assert.NotNull(persisted.RevokedAt);
            Assert.Equal(AdminSessionRevokeReasons.SetupVerificationRecovery, persisted.RevokeReason);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Parallel_workflow_session_creation_creates_once_then_requires_recovery()
    {
        var (root, factory) = await CreateMigratedDatabaseAsync();
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var repository = new AdminSessionRepository(factory);
            var workflowId = AdminWorkflowSessionId.FromOperationId(
                AdminBootstrapOperationId.Create());
            var now = DateTimeOffset.UtcNow;
            var results = await Task.WhenAll(
                repository.CreateWorkflowSessionAsync(
                    workflowId,
                    Session(workflowId.Value, now),
                    3,
                    now,
                    cancellationToken),
                repository.CreateWorkflowSessionAsync(
                    workflowId,
                    Session(workflowId.Value, now),
                    3,
                    now,
                    cancellationToken));

            Assert.Contains(AdminWorkflowSessionCreateResult.Created, results);
            Assert.Contains(AdminWorkflowSessionCreateResult.RecoveryRequired, results);
            Assert.Equal(2, results.Length);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Exact_delta_rejects_disallowed_compose_and_secret_diffs()
    {
        var sourceCompose = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MAILER_HTTP_PORT"] = "8080",
            ["AMANE_ADMIN_ENABLED"] = "false",
            ["AMANE_ADMIN_PII_LIST_MODE"] = "masked",
            ["BUNDLE_PATH"] = "bundles/source/data",
        };
        var sourceSecrets = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AMANE_ADMIN_PASSWORD_HASH"] = "old-hash",
            ["OTHER_SECRET"] = "keep-me",
        };
        var allowedCompose = new Dictionary<string, string>(sourceCompose, StringComparer.Ordinal)
        {
            ["AMANE_ADMIN_ENABLED"] = "true",
            ["AMANE_ADMIN_USERNAME"] = "admin",
            ["AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS"] = "127.0.0.1",
            ["AMANE_ADMIN_ALLOW_HTTP"] = "true",
            ["BUNDLE_PATH"] = "bundles/candidate/data",
        };
        var allowedSecrets = new Dictionary<string, string>(sourceSecrets, StringComparer.Ordinal)
        {
            ["AMANE_ADMIN_PASSWORD_HASH"] = "new-hash",
        };

        Assert.True(
            AdminDerivedBundleDiff.TryValidate(
                "source",
                "candidate",
                sourceCompose,
                sourceSecrets,
                allowedCompose,
                allowedSecrets,
                "{}",
                "{}",
                null,
                null,
                null,
                null,
                out _));

        allowedCompose["MAILER_HTTP_PORT"] = "9090";
        Assert.False(
            AdminDerivedBundleDiff.TryValidate(
                "source",
                "candidate",
                sourceCompose,
                sourceSecrets,
                allowedCompose,
                allowedSecrets,
                "{}",
                "{}",
                null,
                null,
                null,
                null,
                out var reason));
        Assert.Equal("admin_derived_disallowed_env_diff", reason);

        allowedCompose["MAILER_HTTP_PORT"] = "8080";
        allowedCompose["AMANE_ADMIN_PII_LIST_MODE"] = "raw";
        Assert.False(
            AdminDerivedBundleDiff.TryValidate(
                "source",
                "candidate",
                sourceCompose,
                sourceSecrets,
                allowedCompose,
                allowedSecrets,
                "{}",
                "{}",
                null,
                null,
                null,
                null,
                out reason));
        Assert.Equal("admin_derived_disallowed_env_diff", reason);
    }

    [Fact]
    public void Credential_lease_materializes_once_then_disposes_buffer()
    {
        using var lease = new AdminBootstrapCredentialLease("temporary-password");
        Assert.Equal("temporary-password", lease.Materialize());
        lease.Dispose();
        Assert.Throws<ObjectDisposedException>(() => lease.Materialize());
    }

    [Fact]
    public void Local_development_endpoint_requires_loopback_http()
    {
        Assert.True(
            TrustedAdminAccessEndpoint.TryCreate(
                AdminAccessProfile.LocalDevelopment,
                new Uri("http://127.0.0.1:8080/"),
                out var endpoint));
        Assert.NotNull(endpoint);
        Assert.False(
            TrustedAdminAccessEndpoint.TryCreate(
                AdminAccessProfile.LocalDevelopment,
                new Uri("https://127.0.0.1:8080/"),
                out _));
        Assert.False(
            TrustedAdminAccessEndpoint.TryCreate(
                AdminAccessProfile.LocalDevelopment,
                new Uri("http://example.invalid/"),
                out _));
        Assert.False(
            TrustedAdminAccessEndpoint.TryCreate(
                AdminAccessProfile.ProductionHttps,
                new Uri("http://admin.example.invalid/"),
                out _));
    }

    [Fact]
    public void Exact_delta_rejects_empty_value_key_presence_changes()
    {
        var source = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MAILER_HTTP_PORT"] = "8080",
        };
        var candidate = new Dictionary<string, string>(source, StringComparer.Ordinal)
        {
            ["DISALLOWED_KEY"] = "",
        };

        Assert.False(
            AdminDerivedBundleDiff.TryValidate(
                "source",
                "candidate",
                source,
                new Dictionary<string, string>(StringComparer.Ordinal),
                candidate,
                new Dictionary<string, string>(StringComparer.Ordinal),
                "{}",
                "{}",
                null,
                null,
                null,
                null,
                out var reason));
        Assert.Equal("admin_derived_disallowed_env_diff", reason);
    }

    [Fact]
    public async Task Pending_prepared_write_claim_is_atomic_under_concurrency()
    {
        var root = TempRoot();
        Directory.CreateDirectory(root);
        try
        {
            var store = new AdminBootstrapOwnershipStore(new HostSetupFileSystem());
            var first = Ownership(
                AdminBootstrapOperationId.Create(),
                AdminBootstrapOwnershipState.Prepared,
                "bundle-a");
            var second = Ownership(
                AdminBootstrapOperationId.Create(),
                AdminBootstrapOwnershipState.Prepared,
                "bundle-b");
            using var start = new ManualResetEventSlim(false);
            var firstWrite = Task.Run(
                () =>
                {
                    start.Wait();
                    return store.WritePendingPrepared(root, first);
                },
                TestContext.Current.CancellationToken);
            var secondWrite = Task.Run(
                () =>
                {
                    start.Wait();
                    return store.WritePendingPrepared(root, second);
                },
                TestContext.Current.CancellationToken);
            start.Set();
            var results = await Task.WhenAll(firstWrite, secondWrite);
            Assert.Single(results, static result => result.IsSuccess);
            var pending = store.ReadPending(root);
            Assert.Equal(AdminBootstrapOwnershipReadKind.Valid, pending.Kind);
            Assert.True(
                string.Equals(
                    pending.Document!.OperationId,
                    first.OperationId,
                    StringComparison.Ordinal)
                || string.Equals(
                    pending.Document.OperationId,
                    second.OperationId,
                    StringComparison.Ordinal),
                "Atomic pending claim persisted an unexpected operation.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Promotion_keeps_committed_current_when_pending_delete_fails()
    {
        var root = TempRoot();
        Directory.CreateDirectory(root);
        try
        {
            var inner = new HostSetupFileSystem();
            var store = new AdminBootstrapOwnershipStore(
                new FaultingSetupFileSystem(
                    inner,
                    failDeletePath: SetupBundleLayout.AdminBootstrapPendingPath(root)));
            var pending = Ownership(
                AdminBootstrapOperationId.Create(),
                AdminBootstrapOwnershipState.Succeeded,
                "bundle-candidate");
            Assert.True(store.WritePending(root, pending).IsSuccess);
            var promote = store.PromotePendingToCurrent(root, pending);
            Assert.Equal(
                AdminBootstrapPromotionKind.CurrentCommittedPendingCleanupRequired,
                promote.Kind);
            Assert.True(promote.CurrentCommitted);
            Assert.False(promote.IsFullySucceeded);
            Assert.Equal(AdminBootstrapOwnershipReadKind.Valid, store.ReadPending(root).Kind);
            Assert.Equal(AdminBootstrapOwnershipReadKind.Valid, store.ReadCurrent(root).Kind);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Login_redirect_rejects_cross_origin_absolute_location()
    {
        Assert.True(
            TrustedAdminAccessEndpoint.TryCreate(
                AdminAccessProfile.LocalDevelopment,
                new Uri("http://127.0.0.1:8080/"),
                out var endpoint));
        using var response = new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("https://evil.example/admin") },
        };
        Assert.False(
            AdminAccessVerifier.IsExpectedSameOriginRedirect(endpoint!, response, "/admin"));

        using var sameOrigin = new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("http://127.0.0.1:8080/admin") },
        };
        Assert.True(
            AdminAccessVerifier.IsExpectedSameOriginRedirect(endpoint!, sameOrigin, "/admin"));

        using var networkPath = new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("//evil.example/admin", UriKind.Relative) },
        };
        Assert.False(
            AdminAccessVerifier.IsExpectedSameOriginRedirect(endpoint!, networkPath, "/admin"));
    }

    [Fact]
    public async Task Recover_armed_source_active_probes_route_and_preserves_current()
    {
        var (root, factory) = await CreateMigratedDatabaseAsync();
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var store = new AdminBootstrapOwnershipStore(new HostSetupFileSystem());
            var currentOperation = AdminBootstrapOperationId.Create();
            var current = Ownership(
                currentOperation,
                AdminBootstrapOwnershipState.Succeeded,
                "bundle-current");
            Assert.True(store.WritePending(root, current).IsSuccess);
            Assert.True(store.PromotePendingToCurrent(root, current).IsFullySucceeded);

            var pendingOperation = AdminBootstrapOperationId.Create();
            var pending = Ownership(
                pendingOperation,
                AdminBootstrapOwnershipState.Armed,
                "bundle-candidate");
            Assert.True(store.WritePending(root, pending).IsSuccess);

            var probeCount = 0;
            var workflow = CreateRecoveryWorkflow(
                root,
                factory,
                store,
                new FakeVerifiedWorkflowApplyEngine(
                    SetupApplyResult.Create(
                        SetupApplyResultCode.RollbackSucceeded,
                        SetupManagedDeploymentState.Active,
                        reasonCode: "source_already_active",
                        activationGeneration: 1,
                        configRollbackStatus: SetupConfigRollbackStatus.Succeeded)),
                () =>
                {
                    Interlocked.Increment(ref probeCount);
                    return new StaticResponseHandler(HttpStatusCode.NotFound);
                });
            Assert.True(
                TrustedAdminAccessEndpoint.TryCreate(
                    AdminAccessProfile.LocalDevelopment,
                    new Uri("http://127.0.0.1:8080/"),
                    out var endpoint));
            var result = await workflow.RecoverAsync(
                CreateLayout(root),
                endpoint!,
                cancellationToken);

            Assert.False(result.ManualActionRequired);
            Assert.Equal("disabled", result.AdminExposure);
            Assert.Equal(1, probeCount);
            var currentRead = store.ReadCurrent(root);
            var pendingRead = store.ReadPending(root);
            Assert.Equal(AdminBootstrapOwnershipReadKind.Valid, currentRead.Kind);
            Assert.Equal(AdminBootstrapOwnershipReadKind.Missing, pendingRead.Kind);
            Assert.Equal(AdminBootstrapOwnershipState.Succeeded, currentRead.Document!.State);
            Assert.True(
                string.Equals(
                    currentOperation.Value,
                    currentRead.Document.OperationId,
                    StringComparison.Ordinal),
                "Current ownership changed during armed+source abort.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Recover_db_reclassification_failure_retains_needs_intervention_pending()
    {
        var (root, factory) = await CreateMigratedDatabaseAsync();
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var store = new AdminBootstrapOwnershipStore(new HostSetupFileSystem());
            var pending = Ownership(
                AdminBootstrapOperationId.Create(),
                AdminBootstrapOwnershipState.Armed,
                "bundle-candidate");
            Assert.True(store.WritePending(root, pending).IsSuccess);
            var workflow = CreateRecoveryWorkflow(
                root,
                factory,
                store,
                new FakeVerifiedWorkflowApplyEngine(
                    SetupApplyResult.Create(
                        SetupApplyResultCode.RollbackSucceeded,
                        SetupManagedDeploymentState.Active,
                        reasonCode: "source_already_active",
                        activationGeneration: 1,
                        configRollbackStatus: SetupConfigRollbackStatus.Succeeded)),
                () => new StaticResponseHandler(HttpStatusCode.NotFound),
                _ => Task.FromException<AdminBootstrapDatabaseSnapshot>(
                    new IOException("Injected DB reclassification failure.")));
            Assert.True(
                TrustedAdminAccessEndpoint.TryCreate(
                    AdminAccessProfile.LocalDevelopment,
                    new Uri("http://127.0.0.1:8080/"),
                    out var endpoint));

            var result = await workflow.RecoverAsync(
                CreateLayout(root),
                endpoint!,
                cancellationToken);

            Assert.True(result.ManualActionRequired);
            Assert.Equal("unknown", result.AdminDatabaseState);
            var retained = store.ReadPending(root);
            Assert.Equal(AdminBootstrapOwnershipReadKind.Valid, retained.Kind);
            Assert.Equal(AdminBootstrapOwnershipState.NeedsIntervention, retained.Document!.State);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Recover_committed_current_keeps_pending_when_exposure_is_unknown()
    {
        var (root, factory) = await CreateMigratedDatabaseAsync();
        try
        {
            var store = new AdminBootstrapOwnershipStore(new HostSetupFileSystem());
            var operation = AdminBootstrapOperationId.Create();
            var current = Ownership(
                operation,
                AdminBootstrapOwnershipState.Succeeded,
                "bundle-candidate");
            Assert.True(store.WritePending(root, current).IsSuccess);
            Assert.True(store.PromotePendingToCurrent(root, current).IsFullySucceeded);
            Assert.True(store.WritePending(root, current).IsSuccess);
            var workflow = CreateRecoveryWorkflow(
                root,
                factory,
                store,
                new FakeVerifiedWorkflowApplyEngine(
                    SetupApplyResult.Create(
                        SetupApplyResultCode.RollbackSucceeded,
                        SetupManagedDeploymentState.Active)),
                () => new StaticResponseHandler(HttpStatusCode.ServiceUnavailable));
            Assert.True(
                TrustedAdminAccessEndpoint.TryCreate(
                    AdminAccessProfile.LocalDevelopment,
                    new Uri("http://127.0.0.1:8080/"),
                    out var endpoint));

            var result = await workflow.RecoverAsync(
                CreateLayout(root),
                endpoint!,
                TestContext.Current.CancellationToken);

            Assert.True(result.ManualActionRequired);
            Assert.Equal("unknown", result.AdminExposure);
            Assert.Equal(AdminBootstrapOwnershipReadKind.Valid, store.ReadCurrent(root).Kind);
            Assert.Equal(AdminBootstrapOwnershipReadKind.Valid, store.ReadPending(root).Kind);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Apply_internal_rollback_reclassifies_database_and_probes_disabled_route()
    {
        var (root, factory) = await CreateMigratedDatabaseAsync();
        try
        {
            var store = new AdminBootstrapOwnershipStore(new HostSetupFileSystem());
            var pending = Ownership(
                AdminBootstrapOperationId.Create(),
                AdminBootstrapOwnershipState.Armed,
                "bundle-candidate");
            Assert.True(store.WritePending(root, pending).IsSuccess);
            var probeCount = 0;
            var workflow = CreateRecoveryWorkflow(
                root,
                factory,
                store,
                new FakeVerifiedWorkflowApplyEngine(
                    SetupApplyResult.Create(
                        SetupApplyResultCode.RollbackSucceeded,
                        SetupManagedDeploymentState.Active)),
                () =>
                {
                    Interlocked.Increment(ref probeCount);
                    return new StaticResponseHandler(HttpStatusCode.NotFound);
                });
            using var credential = new AdminBootstrapCredentialLease("test-password");
            var request = CreateRequest(root, credential);
            var apply = SetupApplyResult.Create(
                SetupApplyResultCode.RollbackSucceeded,
                SetupManagedDeploymentState.Active,
                reasonCode: "effective_inspection_failed",
                activationGeneration: 1,
                configRollbackStatus: SetupConfigRollbackStatus.Succeeded);

            var result = await workflow.ConvergeFailedApplyAsync(
                request,
                pending,
                SourceAdminDisposition.DisabledMain,
                apply);

            Assert.False(result.ManualActionRequired);
            Assert.Equal(AdminBootstrapDatabaseClassification.Fresh, result.AdminDatabaseState);
            Assert.Equal("disabled", result.AdminExposure);
            Assert.Equal(1, probeCount);
            Assert.Equal(AdminBootstrapOwnershipReadKind.Missing, store.ReadPending(root).Kind);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Apply_internal_rollback_db_read_failure_retains_pending_as_unknown()
    {
        var (root, factory) = await CreateMigratedDatabaseAsync();
        try
        {
            var store = new AdminBootstrapOwnershipStore(new HostSetupFileSystem());
            var pending = Ownership(
                AdminBootstrapOperationId.Create(),
                AdminBootstrapOwnershipState.Armed,
                "bundle-candidate");
            Assert.True(store.WritePending(root, pending).IsSuccess);
            var workflow = CreateRecoveryWorkflow(
                root,
                factory,
                store,
                new FakeVerifiedWorkflowApplyEngine(
                    SetupApplyResult.Create(
                        SetupApplyResultCode.RollbackSucceeded,
                        SetupManagedDeploymentState.Active)),
                () => new StaticResponseHandler(HttpStatusCode.NotFound),
                _ => Task.FromException<AdminBootstrapDatabaseSnapshot>(
                    new IOException("Injected postflight DB read failure.")));
            using var credential = new AdminBootstrapCredentialLease("test-password");
            var result = await workflow.ConvergeFailedApplyAsync(
                CreateRequest(root, credential),
                pending,
                SourceAdminDisposition.DisabledMain,
                SetupApplyResult.Create(
                    SetupApplyResultCode.RollbackSucceeded,
                    SetupManagedDeploymentState.Active,
                    reasonCode: "readiness_failed",
                    activationGeneration: 1,
                    configRollbackStatus: SetupConfigRollbackStatus.Succeeded));

            Assert.True(result.ManualActionRequired);
            Assert.Equal("unknown", result.AdminDatabaseState);
            var retained = store.ReadPending(root);
            Assert.Equal(AdminBootstrapOwnershipReadKind.Valid, retained.Kind);
            Assert.Equal(AdminBootstrapOwnershipState.NeedsIntervention, retained.Document!.State);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Apply_internal_rollback_db_mismatch_does_not_mint_residual_authority()
    {
        var (root, factory) = await CreateMigratedDatabaseAsync();
        try
        {
            var store = new AdminBootstrapOwnershipStore(new HostSetupFileSystem());
            var pending = Ownership(
                AdminBootstrapOperationId.Create(),
                AdminBootstrapOwnershipState.Armed,
                "bundle-candidate");
            Assert.True(store.WritePending(root, pending).IsSuccess);
            var mismatched = new AdminBootstrapDatabaseSnapshot(
                AdminBootstrapDatabaseClassification.ManagedSameUser,
                1,
                1,
                7,
                7,
                "manual-admin",
                "manual-hash",
                "manual-hash",
                new string('f', 64),
                false);
            var workflow = CreateRecoveryWorkflow(
                root,
                factory,
                store,
                new FakeVerifiedWorkflowApplyEngine(
                    SetupApplyResult.Create(
                        SetupApplyResultCode.RollbackSucceeded,
                        SetupManagedDeploymentState.Active)),
                () => new StaticResponseHandler(HttpStatusCode.NotFound),
                _ => Task.FromResult(mismatched));
            using var credential = new AdminBootstrapCredentialLease("test-password");

            var result = await workflow.ConvergeFailedApplyAsync(
                CreateRequest(root, credential),
                pending,
                SourceAdminDisposition.DisabledMain,
                SetupApplyResult.Create(
                    SetupApplyResultCode.RollbackSucceeded,
                    SetupManagedDeploymentState.Active,
                    reasonCode: "readiness_failed",
                    activationGeneration: 1,
                    configRollbackStatus: SetupConfigRollbackStatus.Succeeded));

            Assert.True(result.ManualActionRequired);
            var retained = store.ReadPending(root);
            Assert.Equal(AdminBootstrapOwnershipReadKind.Valid, retained.Kind);
            Assert.Equal(AdminBootstrapOwnershipState.NeedsIntervention, retained.Document!.State);
            Assert.Equal(AdminBootstrapOwnershipReadKind.Missing, store.ReadCurrent(root).Kind);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Managed_same_user_generation_refresh_updates_current_authority()
    {
        var root = TempRoot();
        Directory.CreateDirectory(root);
        try
        {
            var store = new AdminBootstrapOwnershipStore(new HostSetupFileSystem());
            var operation = AdminBootstrapOperationId.Create();
            var current = Ownership(
                operation,
                AdminBootstrapOwnershipState.Succeeded,
                "bundle-source");
            Assert.True(store.WritePending(root, current).IsSuccess);
            Assert.True(store.PromotePendingToCurrent(root, current).IsFullySucceeded);

            Assert.True(
                store.TryUpdateSucceededCurrentGeneration(
                    root,
                    operation.Value,
                    "bundle-source",
                    12).IsSuccess);
            var refreshed = store.ReadCurrent(root).Document!;
            Assert.Equal(12, refreshed.Candidate.ExpectedActivationGeneration);
            Assert.Equal(1, refreshed.Source.ActivationGeneration);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Managed_same_user_generation_update_failure_preserves_pending_recovery_authority()
    {
        var root = TempRoot();
        Directory.CreateDirectory(root);
        try
        {
            var inner = new HostSetupFileSystem();
            var seed = new AdminBootstrapOwnershipStore(inner);
            var operation = AdminBootstrapOperationId.Create();
            var current = Ownership(
                operation,
                AdminBootstrapOwnershipState.Succeeded,
                "bundle-source");
            Assert.True(seed.WritePending(root, current).IsSuccess);
            Assert.True(seed.PromotePendingToCurrent(root, current).IsFullySucceeded);

            var pending = Ownership(
                AdminBootstrapOperationId.Create(),
                AdminBootstrapOwnershipState.Armed,
                "bundle-candidate");
            Assert.True(seed.WritePending(root, pending).IsSuccess);

            var faulting = new AdminBootstrapOwnershipStore(
                new FaultingSetupFileSystem(
                    inner,
                    failMoveDestination: SetupBundleLayout.AdminBootstrapCurrentPath(root)));
            Assert.False(
                faulting.RefreshSucceededCurrentGenerationAndDeletePending(
                    root,
                    operation.Value,
                    "bundle-source",
                    12).IsSuccess);
            Assert.Equal(AdminBootstrapOwnershipReadKind.Valid, seed.ReadPending(root).Kind);
            var unchanged = seed.ReadCurrent(root).Document!;
            Assert.Equal(2, unchanged.Candidate.ExpectedActivationGeneration);
            Assert.Equal(1, unchanged.Source.ActivationGeneration);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Execute_refuses_promotion_when_database_changes_after_postflight()
    {
        var (root, factory) = await CreateMigratedDatabaseAsync();
        try
        {
            var store = new AdminBootstrapOwnershipStore(new HostSetupFileSystem());
            var source = SeedActiveSourceBundle(root);
            var reads = 0;
            var workflow = CreateExecuteWorkflow(
                root,
                factory,
                store,
                source,
                _ => Task.FromResult(
                    Interlocked.Increment(ref reads) switch
                    {
                        1 => FreshSnapshot(),
                        2 => AppliedSnapshot("effective-hash"),
                        // A concurrent Manual rotation lands between postflight and promotion.
                        _ => AppliedSnapshot("effective-hash") with { AdminUserCredentialEpoch = 5 },
                    }));
            using var credential = new AdminBootstrapCredentialLease("test-password");

            var result = await workflow.ExecuteAsync(
                CreateRequest(root, credential),
                TestContext.Current.CancellationToken);

            Assert.Equal("database_authority_changed", result.ReasonCode);
            Assert.True(result.ManualActionRequired);
            Assert.Equal(AdminBootstrapOwnershipReadKind.Missing, store.ReadCurrent(root).Kind);
            var retained = store.ReadPending(root);
            Assert.Equal(AdminBootstrapOwnershipReadKind.Valid, retained.Kind);
            Assert.Equal(AdminBootstrapOwnershipState.NeedsIntervention, retained.Document!.State);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Execute_promotes_when_final_database_authority_still_matches_candidate()
    {
        var (root, factory) = await CreateMigratedDatabaseAsync();
        try
        {
            var store = new AdminBootstrapOwnershipStore(new HostSetupFileSystem());
            var source = SeedActiveSourceBundle(root);
            var reads = 0;
            var workflow = CreateExecuteWorkflow(
                root,
                factory,
                store,
                source,
                _ => Task.FromResult(
                    Interlocked.Increment(ref reads) == 1
                        ? FreshSnapshot()
                        : AppliedSnapshot(CandidateEffectiveHash(root, store))));
            using var credential = new AdminBootstrapCredentialLease("test-password");

            var result = await workflow.ExecuteAsync(
                CreateRequest(root, credential),
                TestContext.Current.CancellationToken);

            Assert.Null(result.ReasonCode);
            Assert.False(result.ManualActionRequired);
            Assert.Equal(AdminBootstrapResultCode.Succeeded, result.Code);
            Assert.Equal(AdminBootstrapOwnershipReadKind.Missing, store.ReadPending(root).Kind);
            var current = store.ReadCurrent(root);
            Assert.Equal(AdminBootstrapOwnershipReadKind.Valid, current.Kind);
            Assert.Equal(AdminBootstrapOwnershipState.Succeeded, current.Document!.State);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Recover_committed_current_requires_candidate_and_database_authority()
    {
        var (root, factory) = await CreateMigratedDatabaseAsync();
        try
        {
            var store = new AdminBootstrapOwnershipStore(new HostSetupFileSystem());
            var operation = AdminBootstrapOperationId.Create();
            var current = Ownership(
                operation,
                AdminBootstrapOwnershipState.Succeeded,
                "bundle-candidate");
            Assert.True(store.WritePending(root, current).IsSuccess);
            Assert.True(store.PromotePendingToCurrent(root, current).IsFullySucceeded);
            Assert.True(store.WritePending(root, current).IsSuccess);
            var probeCount = 0;
            var workflow = CreateRecoveryWorkflow(
                root,
                factory,
                store,
                new FakeVerifiedWorkflowApplyEngine(
                    SetupApplyResult.Create(
                        SetupApplyResultCode.RollbackSucceeded,
                        SetupManagedDeploymentState.Active)),
                () =>
                {
                    Interlocked.Increment(ref probeCount);
                    return new StaticResponseHandler(HttpStatusCode.OK);
                });
            Assert.True(
                TrustedAdminAccessEndpoint.TryCreate(
                    AdminAccessProfile.LocalDevelopment,
                    new Uri("http://127.0.0.1:8080/"),
                    out var endpoint));

            var result = await workflow.RecoverAsync(
                CreateLayout(root),
                endpoint!,
                TestContext.Current.CancellationToken);

            Assert.True(result.ManualActionRequired);
            Assert.Equal("candidate_integrity_changed", result.ReasonCode);
            Assert.Equal(0, probeCount);
            Assert.Equal(AdminBootstrapOwnershipReadKind.Valid, store.ReadPending(root).Kind);
            Assert.Equal(AdminBootstrapOwnershipReadKind.Valid, store.ReadCurrent(root).Kind);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Recover_session_cleaned_does_not_promote_without_final_database_authority()
    {
        var (root, factory) = await CreateMigratedDatabaseAsync();
        try
        {
            var store = new AdminBootstrapOwnershipStore(new HostSetupFileSystem());
            var pending = Ownership(
                AdminBootstrapOperationId.Create(),
                AdminBootstrapOwnershipState.SessionCleaned,
                "bundle-candidate");
            Assert.True(store.WritePending(root, pending).IsSuccess);
            var workflow = CreateRecoveryWorkflow(
                root,
                factory,
                store,
                new FakeVerifiedWorkflowApplyEngine(
                    SetupApplyResult.Create(
                        SetupApplyResultCode.RollbackSucceeded,
                        SetupManagedDeploymentState.Active,
                        configRollbackStatus: SetupConfigRollbackStatus.Succeeded)),
                () => new StaticResponseHandler(HttpStatusCode.NotFound));
            Assert.True(
                TrustedAdminAccessEndpoint.TryCreate(
                    AdminAccessProfile.LocalDevelopment,
                    new Uri("http://127.0.0.1:8080/"),
                    out var endpoint));

            var result = await workflow.RecoverAsync(
                CreateLayout(root),
                endpoint!,
                TestContext.Current.CancellationToken);

            Assert.True(result.ManualActionRequired);
            Assert.Equal(AdminBootstrapResultCode.ManualActionRequired, result.Code);
            Assert.Equal(AdminBootstrapOwnershipReadKind.Missing, store.ReadCurrent(root).Kind);
            var retained = store.ReadPending(root);
            Assert.Equal(AdminBootstrapOwnershipReadKind.Valid, retained.Kind);
            Assert.Equal(AdminBootstrapOwnershipState.NeedsIntervention, retained.Document!.State);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Residual_candidate_authority_requires_finalized_bundle_integrity()
    {
        var root = TempRoot();
        Directory.CreateDirectory(root);
        try
        {
            var fileSystem = new HostSetupFileSystem();
            var core = new SetupCore(fileSystem);
            var layout = CreateLayout(root);
            var source = core.GenerateBundle(SetupTestFixtures.LocalMailpitRequest(root));
            Assert.Equal(SetupResultCode.Succeeded, source.Code);

            var operationId = AdminBootstrapOperationId.Create();
            var hash = AdminPasswordHasher.Hash("managed-password");
            var expectation = new SetupAdminBootstrapExpectation
            {
                OperationId = operationId.Value,
                Before = FreshState(),
                After = FreshState(),
            };
            var derived = core.GenerateAdminDerivedBundle(
                layout,
                source.BundleId!,
                SourceAdminDisposition.DisabledMain,
                runtimeFileOwnership: null,
                new SetupAdminBundleDelta
                {
                    Username = "admin",
                    PasswordHash = hash,
                    AllowedLocalAddress = "127.0.0.1",
                    AllowHttp = true,
                    Expectation = expectation,
                });
            Assert.Equal(SetupResultCode.Succeeded, derived.Code);

            var classifier = new AdminBootstrapSourceClassifier(
                fileSystem,
                new AdminBootstrapOwnershipStore(fileSystem));
            var residual = Ownership(
                operationId,
                AdminBootstrapOwnershipState.ResidualAfterConfigRollback,
                derived.BundleId!) with
            {
                Candidate = new AdminBootstrapCandidateAuthority
                {
                    BundleId = derived.BundleId!,
                    ExpectedActivationGeneration = 2,
                    ConfigurationFingerprint = derived.ConfigurationFingerprint!,
                },
                ExpectedDatabase = expectation,
            };

            Assert.True(
                classifier.TryReadVerifiedCandidateAdminAuthority(
                    layout,
                    residual,
                    out _,
                    out var username,
                    out var candidateHash));
            Assert.Equal("admin", username);
            Assert.Equal(hash, candidateHash);

            Assert.False(
                classifier.TryReadVerifiedCandidateAdminAuthority(
                    layout,
                    residual with
                    {
                        Candidate = residual.Candidate with
                        {
                            ConfigurationFingerprint = "sha256:" + new string('b', 64),
                        },
                    },
                    out _,
                    out _,
                    out _),
                "A candidate fingerprint mismatch must not be adopted.");

            var composePath = Path.Combine(
                SetupBundleLayout.EnvDir(SetupBundleLayout.BundleRoot(root, derived.BundleId!)),
                SetupBundleLayout.ComposeEnvFileName);
            var composeLines = File.ReadAllLines(composePath).ToList();
            var usernameIndex = composeLines.FindIndex(static line =>
                line.StartsWith("AMANE_ADMIN_USERNAME=", StringComparison.Ordinal));
            Assert.True(usernameIndex >= 0, "Generated compose.env must contain AMANE_ADMIN_USERNAME.");
            Assert.Equal("AMANE_ADMIN_USERNAME=\"admin\"", composeLines[usernameIndex]);
            composeLines[usernameIndex] = "AMANE_ADMIN_USERNAME=\"manual-admin\"";
            File.WriteAllLines(composePath, composeLines);
            Assert.False(
                classifier.TryReadVerifiedCandidateAdminAuthority(
                    layout,
                    residual,
                    out _,
                    out _,
                    out _),
                "A well-formed compose.env username rewrite must fail recomputed fingerprint matching.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Production_rollback_accepts_session_cleaned_pending_state()
    {
        var root = TempRoot();
        Directory.CreateDirectory(root);
        try
        {
            var fileSystem = new HostSetupFileSystem();
            var layout = CreateLayout(root);
            var runner = new RejectingProcessRunner();
            var probe = new DockerEnvironmentProbe(
                runner,
                getDockerHost: static () => null,
                getDockerContextEnv: static () => null,
                resolveDockerExecutable: static () => "docker");
            var adapter = new SetupHostDockerAdapter(fileSystem, runner, probe);
            var engine = (ISetupVerifiedWorkflowApplyEngine)new SetupApplyEngine(fileSystem, adapter);
            var sessionCleaned = Ownership(
                AdminBootstrapOperationId.Create(),
                AdminBootstrapOwnershipState.SessionCleaned,
                "bundle-candidate");
            var prepared = Ownership(
                AdminBootstrapOperationId.Create(),
                AdminBootstrapOwnershipState.Prepared,
                "bundle-candidate");

            var accepted = await engine.RecoverAdminBootstrapRollbackAsync(
                layout,
                sessionCleaned,
                TestContext.Current.CancellationToken);
            Assert.NotEqual(
                "pending_rollback_authority_invalid",
                accepted.ReasonCode);

            var rejected = await engine.RecoverAdminBootstrapRollbackAsync(
                layout,
                prepared,
                TestContext.Current.CancellationToken);
            Assert.Equal("pending_rollback_authority_invalid", rejected.ReasonCode);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Recover_same_operation_cleanup_keeps_apply_lock_until_pending_delete()
    {
        var (root, factory) = await CreateMigratedDatabaseAsync();
        try
        {
            var fileSystem = new HostSetupFileSystem();
            var core = new SetupCore(fileSystem);
            var layout = CreateLayout(root);
            var source = core.GenerateBundle(SetupTestFixtures.LocalMailpitRequest(root));
            Assert.Equal(SetupResultCode.Succeeded, source.Code);

            var operationId = AdminBootstrapOperationId.Create();
            var hash = AdminPasswordHasher.Hash("managed-password");
            var expectation = new SetupAdminBootstrapExpectation
            {
                OperationId = operationId.Value,
                Before = FreshState(),
                After = new SetupAdminDatabaseExpectationState
                {
                    Classification = AdminBootstrapDatabaseClassification.ManagedSameUser,
                    AdminConfigCount = 1,
                    AdminUserCount = 1,
                    AdminConfigCredentialEpoch = 0,
                    AdminUserCredentialEpoch = 0,
                    ScopeFingerprint = AdminBootstrapScopeFingerprint.Compute(
                        [Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd")]),
                    FreshHasAnyAdminSessionRows = null,
                },
            };
            var derived = core.GenerateAdminDerivedBundle(
                layout,
                source.BundleId!,
                SourceAdminDisposition.DisabledMain,
                runtimeFileOwnership: null,
                new SetupAdminBundleDelta
                {
                    Username = "admin",
                    PasswordHash = hash,
                    AllowedLocalAddress = "127.0.0.1",
                    AllowHttp = true,
                    Expectation = expectation,
                });
            Assert.Equal(SetupResultCode.Succeeded, derived.Code);

            var ownershipDoc = Ownership(
                operationId,
                AdminBootstrapOwnershipState.Succeeded,
                derived.BundleId!) with
            {
                Candidate = new AdminBootstrapCandidateAuthority
                {
                    BundleId = derived.BundleId!,
                    ExpectedActivationGeneration = 2,
                    ConfigurationFingerprint = derived.ConfigurationFingerprint!,
                },
                ExpectedDatabase = expectation,
            };

            FakeRecoveryLease? lease = null;
            var deleteUnderLease = false;
            var watchingDeletes = false;
            var pendingPath = SetupBundleLayout.AdminBootstrapPendingPath(root);
            var trackingFs = new TrackingDeleteFileSystem(
                fileSystem,
                pendingPath,
                () =>
                {
                    if (!watchingDeletes)
                        return;

                    Assert.NotNull(lease);
                    Assert.False(lease!.Disposed, "Pending delete must run while APPLY.lock is held.");
                    deleteUnderLease = true;
                });
            var trackingStore = new AdminBootstrapOwnershipStore(trackingFs);
            // Re-seed ownership through the tracking store so deletes are observed.
            Assert.True(trackingStore.WritePending(root, ownershipDoc).IsSuccess);
            Assert.True(trackingStore.PromotePendingToCurrent(root, ownershipDoc).IsFullySucceeded);
            Assert.True(trackingStore.WritePending(root, ownershipDoc).IsSuccess);

            var applyEngine = new FakeVerifiedWorkflowApplyEngine(
                SetupApplyResult.Create(
                    SetupApplyResultCode.RollbackSucceeded,
                    SetupManagedDeploymentState.Active),
                onLeaseAcquired: acquired =>
                {
                    lease = acquired;
                    watchingDeletes = true;
                });
            var workflow = new AdminBootstrapWorkflow(
                new SetupCore(trackingFs),
                trackingFs,
                new AdminBootstrapDatabase(factory, TimeProvider.System),
                new AdminBootstrapSourceClassifier(trackingFs, trackingStore),
                trackingStore,
                applyEngine,
                new AdminAccessVerifier(static () => new AdminAccessScriptHandler()),
                new AdminSessionRepository(factory),
                TimeProvider.System,
                _ => Task.FromResult(AppliedSnapshot(hash)));
            Assert.True(
                TrustedAdminAccessEndpoint.TryCreate(
                    AdminAccessProfile.LocalDevelopment,
                    new Uri("http://127.0.0.1:8080/"),
                    out var endpoint));

            var result = await workflow.RecoverAsync(
                layout,
                endpoint!,
                TestContext.Current.CancellationToken);

            Assert.Equal(AdminBootstrapResultCode.Succeeded, result.Code);
            Assert.True(deleteUnderLease);
            Assert.NotNull(lease);
            Assert.True(lease!.Disposed);
            Assert.Equal(AdminBootstrapOwnershipReadKind.Missing, trackingStore.ReadPending(root).Kind);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Residual_authority_rejects_manual_epoch_scope_username_and_hash_drift()
    {
        var operationId = AdminBootstrapOperationId.Create();
        var tenant = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        var hash = AdminPasswordHasher.Hash("managed-password");
        var expected = new SetupAdminDatabaseExpectationState
        {
            Classification = AdminBootstrapDatabaseClassification.ManagedSameUser,
            AdminConfigCount = 1,
            AdminUserCount = 1,
            AdminConfigCredentialEpoch = 0,
            AdminUserCredentialEpoch = 0,
            ScopeFingerprint = AdminBootstrapScopeFingerprint.Compute([tenant]),
        };
        var expectation = new SetupAdminBootstrapExpectation
        {
            OperationId = operationId.Value,
            Before = FreshState(),
            After = expected,
        };
        var residual = Ownership(
            operationId,
            AdminBootstrapOwnershipState.ResidualAfterConfigRollback,
            "bundle-candidate") with
        {
            ExpectedDatabase = expectation,
        };
        var database = new AdminBootstrapDatabaseSnapshot(
            AdminBootstrapDatabaseClassification.ManagedSameUser,
            1,
            1,
            0,
            0,
            "admin",
            hash,
            hash,
            expected.ScopeFingerprint,
            false);

        Assert.True(
            AdminBootstrapSourceClassifier.MatchesResidualDatabaseAuthority(
                residual,
                expectation,
                database,
                "admin",
                hash));
        Assert.False(
            AdminBootstrapSourceClassifier.MatchesResidualDatabaseAuthority(
                residual,
                expectation,
                database with { AdminUserCredentialEpoch = 1 },
                "admin",
                hash));
        Assert.False(
            AdminBootstrapSourceClassifier.MatchesResidualDatabaseAuthority(
                residual,
                expectation,
                database with { ScopeFingerprint = new string('f', 64) },
                "admin",
                hash));
        Assert.False(
            AdminBootstrapSourceClassifier.MatchesResidualDatabaseAuthority(
                residual,
                expectation,
                database,
                "manual-admin",
                hash));
        Assert.False(
            AdminBootstrapSourceClassifier.MatchesResidualDatabaseAuthority(
                residual,
                expectation,
                database,
                "admin",
                AdminPasswordHasher.Hash("manual-password")));
    }

    [Fact]
    public void Workflow_canonical_result_projection_omits_operation_and_session_fields()
    {
        var result = new AdminBootstrapWorkflowResult
        {
            Code = AdminBootstrapResultCode.Succeeded,
            AccessProfile = "local-development",
            ConfigRollback = SetupConfigRollbackStatus.NotApplicable,
            AdminDatabaseState = AdminBootstrapDatabaseClassification.ManagedSameUser,
            AdminExposure = "enabled",
            LoginVerification = "succeeded",
            SetupStatusVerification = "succeeded",
            VerificationSessionCleanup = "succeeded",
            ManualActionRequired = false,
            ReasonCode = null,
        };
        var projection = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["code"] = result.Code,
            ["accessProfile"] = result.AccessProfile,
            ["configRollback"] = result.ConfigRollback,
            ["adminDatabaseState"] = result.AdminDatabaseState,
            ["adminExposure"] = result.AdminExposure,
            ["loginVerification"] = result.LoginVerification,
            ["setupStatusVerification"] = result.SetupStatusVerification,
            ["verificationSessionCleanup"] = result.VerificationSessionCleanup,
            ["manualActionRequired"] = result.ManualActionRequired,
            ["reasonCode"] = result.ReasonCode,
        };
        var json = JsonSerializer.Serialize(projection);
        Assert.DoesNotContain("operationId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sessionId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("setup-v1:", json, StringComparison.Ordinal);
        Assert.Contains("admin.bootstrap.succeeded", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Managed_same_user_hash_mismatch_fails_without_writes_or_revokes()
    {
        var (root, factory) = await CreateMigratedDatabaseAsync();
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var database = new AdminBootstrapDatabase(factory, TimeProvider.System);
            var tenantId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
            var hash = AdminPasswordHasher.Hash("managed-password");
            var operationId = AdminBootstrapOperationId.Create();
            var before = await database.InspectReadOnlyAsync(cancellationToken);
            var expectation = new SetupAdminBootstrapExpectation
            {
                OperationId = operationId.Value,
                Before = before.ToExpectationState(includeFreshSessionGuard: true),
                After = new SetupAdminDatabaseExpectationState
                {
                    Classification = AdminBootstrapDatabaseClassification.ManagedSameUser,
                    AdminConfigCount = 1,
                    AdminUserCount = 1,
                    AdminConfigCredentialEpoch = 0,
                    AdminUserCredentialEpoch = 0,
                    ScopeFingerprint = AdminBootstrapScopeFingerprint.Compute([tenantId]),
                },
            };
            Assert.Equal(
                0,
                await database.EnsureExpectedStateAsync(
                    expectation,
                    "admin",
                    hash,
                    [tenantId],
                    cancellationToken));

            var after = await database.InspectReadOnlyAsync(cancellationToken);
            var reapply = new SetupAdminBootstrapExpectation
            {
                OperationId = AdminBootstrapOperationId.Create().Value,
                Before = after.ToExpectationState(includeFreshSessionGuard: false),
                After = after.ToExpectationState(includeFreshSessionGuard: false),
            };

            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await database.EnsureExpectedStateAsync(
                    reapply,
                    "admin",
                    AdminPasswordHasher.Hash("different-password"),
                    [tenantId],
                    cancellationToken));

            await using var connection = await factory.OpenConnectionAsync(cancellationToken);
            await using var sessions = connection.CreateCommand();
            sessions.CommandText = "SELECT COUNT(*) FROM admin_sessions;";
            Assert.Equal(0L, await sessions.ExecuteScalarAsync(cancellationToken));

            await using var epochs = connection.CreateCommand();
            epochs.CommandText = "SELECT credential_epoch FROM admin_config LIMIT 1;";
            Assert.Equal(0L, await epochs.ExecuteScalarAsync(cancellationToken));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Fresh_with_existing_session_rows_is_partial_and_rejected()
    {
        var (root, factory) = await CreateMigratedDatabaseAsync();
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var database = new AdminBootstrapDatabase(factory, TimeProvider.System);
            var repository = new AdminSessionRepository(factory);
            var now = DateTimeOffset.UtcNow;
            var workflowId = AdminWorkflowSessionId.FromOperationId(AdminBootstrapOperationId.Create());
            Assert.Equal(
                AdminWorkflowSessionCreateResult.Created,
                await repository.CreateWorkflowSessionAsync(
                    workflowId,
                    Session(workflowId.Value, now),
                    3,
                    now,
                    cancellationToken));

            var before = await database.InspectReadOnlyAsync(cancellationToken);
            Assert.True(before.HasAnyAdminSessionRows);
            Assert.Equal(
                AdminBootstrapDatabaseClassification.PartialOrInconsistent,
                before.Classification);

            var tenantId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
            var expectation = new SetupAdminBootstrapExpectation
            {
                OperationId = AdminBootstrapOperationId.Create().Value,
                Before = new SetupAdminDatabaseExpectationState
                {
                    Classification = AdminBootstrapDatabaseClassification.Fresh,
                    AdminConfigCount = 0,
                    AdminUserCount = 0,
                    AdminConfigCredentialEpoch = null,
                    AdminUserCredentialEpoch = null,
                    ScopeFingerprint = null,
                    FreshHasAnyAdminSessionRows = false,
                },
                After = new SetupAdminDatabaseExpectationState
                {
                    Classification = AdminBootstrapDatabaseClassification.ManagedSameUser,
                    AdminConfigCount = 1,
                    AdminUserCount = 1,
                    AdminConfigCredentialEpoch = 0,
                    AdminUserCredentialEpoch = 0,
                    ScopeFingerprint = AdminBootstrapScopeFingerprint.Compute([tenantId]),
                },
            };

            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await database.EnsureExpectedStateAsync(
                    expectation,
                    "admin",
                    AdminPasswordHasher.Hash("fresh-password"),
                    [tenantId],
                    cancellationToken));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static SetupRecordedMetadata Recorded(AdminBootstrapOperationId operationId) =>
        new()
        {
            SchemaVersion = 2,
            BundleId = "bundle-admin",
            ConfigurationFingerprint = "sha256:test",
            Mode = "local-mailpit",
            CreatedAt = "2026-07-30T00:00:00Z",
            AdminBootstrapRequested = true,
            AdminBootstrapExpectation = new SetupAdminBootstrapExpectation
            {
                OperationId = operationId.Value,
                Before = FreshState(),
                After = FreshState(),
            },
        };

    private static AdminBootstrapOwnershipDocument Ownership(
        AdminBootstrapOperationId operationId,
        string state,
        string candidateBundleId) =>
        new()
        {
            OperationId = operationId.Value,
            State = state,
            Source = new AdminBootstrapSourceAuthority
            {
                BundleId = "bundle-source",
                ActivationGeneration = 1,
                ConfigurationFingerprint = "sha256:source",
                RecordedSchemaVersion = 2,
                ImageIdentity = "example.invalid/mailer:test",
                ComposeIdentity = "compose-test",
                RuntimeIdentityBindingDigest = new string('a', 64),
                AdminDisposition = SourceAdminDisposition.DisabledMain,
                CapturedAt = "2026-07-30T00:00:00Z",
            },
            Candidate = new AdminBootstrapCandidateAuthority
            {
                BundleId = candidateBundleId,
                ExpectedActivationGeneration = 2,
                ConfigurationFingerprint = "sha256:candidate",
            },
            ExpectedDatabase = new SetupAdminBootstrapExpectation
            {
                OperationId = operationId.Value,
                Before = FreshState(),
                After = FreshState(),
            },
            LastTransitionAt = "2026-07-30T00:00:00Z",
        };

    private static SetupAdminDatabaseExpectationState FreshState() =>
        new()
        {
            Classification = AdminBootstrapDatabaseClassification.Fresh,
            AdminConfigCount = 0,
            AdminUserCount = 0,
            AdminConfigCredentialEpoch = null,
            AdminUserCredentialEpoch = null,
            ScopeFingerprint = null,
            FreshHasAnyAdminSessionRows = false,
        };

    private static string TempRoot() =>
        Path.Combine(
            Path.GetTempPath(),
            "amane-admin-bootstrap-tests",
            Guid.NewGuid().ToString("N"));

    private static AdminSessionRow Session(string sessionId, DateTimeOffset now) =>
        new(
            sessionId,
            "admin",
            now,
            now,
            now.AddHours(1),
            now.AddMinutes(30),
            null,
            null,
            0);

    private static async Task<(string Root, SqliteConnectionFactory Factory)>
        CreateMigratedDatabaseAsync()
    {
        var root = TempRoot();
        Directory.CreateDirectory(root);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Mailer"] = $"Data Source={Path.Combine(root, "mailer.db")}",
            })
            .Build();
        var factory = new SqliteConnectionFactory(configuration);
        await new SqlMigrationRunner(factory).ApplyPendingAsync(
            TestContext.Current.CancellationToken);
        return (root, factory);
    }

    private static AdminBootstrapWorkflow CreateRecoveryWorkflow(
        string root,
        SqliteConnectionFactory factory,
        AdminBootstrapOwnershipStore ownership,
        ISetupVerifiedWorkflowApplyEngine applyEngine,
        Func<HttpMessageHandler> handlerFactory,
        Func<CancellationToken, Task<AdminBootstrapDatabaseSnapshot>>? inspectDatabase = null)
    {
        var fileSystem = new HostSetupFileSystem();
        var database = new AdminBootstrapDatabase(factory, TimeProvider.System);
        return new AdminBootstrapWorkflow(
            new SetupCore(fileSystem),
            fileSystem,
            database,
            new AdminBootstrapSourceClassifier(fileSystem, ownership),
            ownership,
            applyEngine,
            new AdminAccessVerifier(handlerFactory),
            new AdminSessionRepository(factory),
            TimeProvider.System,
            inspectDatabase);
    }

    private static TrustedSetupHostLayout CreateLayout(string root)
    {
        var state = SetupBundleLayout.StateDir(root);
        if (!Directory.Exists(state))
            new HostSetupFileSystem().CreateOwnerOnlyDirectory(state);
        return new TrustedSetupHostLayout(
            root,
            root,
            state,
            Path.Combine(root, ".env"),
            [],
            SetupComposeTopology.DeployWithMailpit,
            new TrustedReleaseInventory
            {
                AllowedImageRepository = SetupImageDefaults.DefaultRepository,
                RequiredImageDigest = "sha256:" + new string('a', 64),
                AllowedDisplayTag = "test",
                ComposeBundleVersion = "test",
                LauncherVersionMin = "1.0.0",
                LauncherVersionMax = "1.0.0",
                ProjectNamePrefix = "amane-test",
            },
            "admin-bootstrap-test");
    }

    private static AdminBootstrapDatabaseSnapshot FreshSnapshot() =>
        new(
            AdminBootstrapDatabaseClassification.Fresh,
            0,
            0,
            null,
            null,
            null,
            null,
            null,
            null,
            false);

    /// <summary>
    /// The state a candidate runtime leaves behind for the fresh bootstrap path: one managed config
    /// row, one managed user row, epoch 0, and the requested tenant scope.
    /// </summary>
    private static AdminBootstrapDatabaseSnapshot AppliedSnapshot(string effectiveHash) =>
        new(
            AdminBootstrapDatabaseClassification.ManagedSameUser,
            1,
            1,
            0,
            0,
            "admin",
            effectiveHash,
            effectiveHash,
            AdminBootstrapScopeFingerprint.Compute(
                [Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd")]),
            false);

    private static string CandidateEffectiveHash(string root, AdminBootstrapOwnershipStore store)
    {
        var pending = store.ReadPending(root);
        Assert.Equal(AdminBootstrapOwnershipReadKind.Valid, pending.Kind);
        var secrets = File.ReadAllBytes(Path.Combine(
            SetupBundleLayout.EnvDir(
                SetupBundleLayout.BundleRoot(root, pending.Document!.Candidate.BundleId)),
            SetupBundleLayout.SecretsEnvFileName));
        Assert.True(ManagedComposeEnvComposer.TryParseEnvFile(secrets, out var parsed, out _));
        return parsed["AMANE_ADMIN_PASSWORD_HASH"];
    }

    private static TrustedVerifiedActiveBundle SeedActiveSourceBundle(string root)
    {
        var fileSystem = new HostSetupFileSystem();
        var generated = new SetupCore(fileSystem)
            .GenerateBundle(SetupTestFixtures.LocalMailpitRequest(root));
        Assert.Equal(SetupResultCode.Succeeded, generated.Code);

        var layout = CreateLayout(root);
        File.WriteAllText(
            layout.ActivePointerPath,
            $"{{\"schemaVersion\":1,\"bundleId\":\"{generated.BundleId}\",\"activationGeneration\":1}}\n");
        Assert.True(
            SetupBundleStaticValidator.TryValidateFinalizedBundle(
                fileSystem,
                layout,
                generated.BundleId!,
                out var recorded,
                out _).IsSuccess);
        Assert.True(SetupActivePointer.TryParse(
            File.ReadAllText(layout.ActivePointerPath),
            out var active));
        return new TrustedVerifiedActiveBundle(
            active!,
            recorded!,
            new SetupVerificationRecord
            {
                SchemaVersion = SetupVerificationRecord.CurrentSchemaVersion,
                Status = SetupVerificationRecord.StatusCommitted,
                BundleId = generated.BundleId!,
                ActivationGeneration = 1,
                FingerprintComparison = SetupVerificationRecord.FingerprintMatched,
                HostAtRest = SetupIntegrityMerger.Matched,
                MountAttestation = SetupIntegrityMerger.Matched,
                BundleIntegrity = SetupIntegrityMerger.Matched,
                RuntimeIdentityBinding = SetupIntegrityMerger.Matched,
                Readiness = SetupVerificationRecord.ReadinessPassed,
                SendReadyEvaluation = SetupVerificationRecord.SendReadyNotEvaluated,
            },
            new SetupRuntimeIdentityBindingStamp
            {
                SchemaVersion = SetupRuntimeIdentityBindingStamp.CurrentSchemaVersion,
                BundleId = generated.BundleId!,
                ActivationGeneration = 1,
                BindingMac = new string('a', 64),
            },
            SourceAdminDisposition.DisabledMain);
    }

    private static AdminBootstrapWorkflow CreateExecuteWorkflow(
        string root,
        SqliteConnectionFactory factory,
        AdminBootstrapOwnershipStore ownership,
        TrustedVerifiedActiveBundle source,
        Func<CancellationToken, Task<AdminBootstrapDatabaseSnapshot>> inspectDatabase)
    {
        var fileSystem = new HostSetupFileSystem();
        return new AdminBootstrapWorkflow(
            new SetupCore(fileSystem),
            fileSystem,
            new AdminBootstrapDatabase(factory, TimeProvider.System),
            new AdminBootstrapSourceClassifier(fileSystem, ownership),
            ownership,
            new LeasedWorkflowApplyEngine(new FakeWorkflowLease(source)),
            new AdminAccessVerifier(static () => new AdminAccessScriptHandler()),
            new AdminSessionRepository(factory),
            TimeProvider.System,
            inspectDatabase);
    }

    private static AdminBootstrapRequest CreateRequest(
        string root,
        AdminBootstrapCredentialLease credential)
    {
        Assert.True(
            TrustedAdminAccessEndpoint.TryCreate(
                AdminAccessProfile.LocalDevelopment,
                new Uri("http://127.0.0.1:8080/"),
                out var endpoint));
        return new AdminBootstrapRequest
        {
            Layout = CreateLayout(root),
            AccessEndpoint = endpoint!,
            EnvironmentName = "Development",
            Username = "admin",
            Credential = credential,
            AllowedLocalAddress = "127.0.0.1",
            AllowHttp = true,
            Interactive = true,
            LoopbackOnlyPublished = true,
            ApprovedReverseProxy = false,
            ServerLocalAddressConfirmed = true,
            TenantIds = [Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd")],
        };
    }

    private sealed class FakeWorkflowLease(TrustedVerifiedActiveBundle source)
        : ISetupVerifiedWorkflowLease
    {
        public TrustedVerifiedActiveBundle Source { get; } = source;

        public Task<SetupApplyResult> ApplyCandidateAsync(
            string candidateBundleId,
            AdminBootstrapOwnershipDocument pending,
            CancellationToken cancellationToken) =>
            Task.FromResult(SetupApplyResult.Create(
                SetupApplyResultCode.ApplySucceeded,
                SetupManagedDeploymentState.Active,
                bundleId: candidateBundleId,
                activationGeneration: pending.Candidate.ExpectedActivationGeneration,
                configurationApplied: true,
                verificationCommitted: true));

        public Task<SetupApplyResult> RollbackToSourceAsync(string reasonCode) =>
            Task.FromResult(SetupApplyResult.Create(
                SetupApplyResultCode.RollbackSucceeded,
                SetupManagedDeploymentState.Active,
                reasonCode: reasonCode,
                bundleId: Source.Active.BundleId,
                activationGeneration: Source.Active.ActivationGeneration + 2,
                configRollbackStatus: SetupConfigRollbackStatus.Succeeded));

        public Task<SetupAuthorityCheckResult> VerifyCandidateStillCurrentAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(SetupAuthorityCheckResult.Current());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class LeasedWorkflowApplyEngine(ISetupVerifiedWorkflowLease lease)
        : ISetupVerifiedWorkflowApplyEngine
    {
        public Task<SetupVerifiedWorkflowLeaseResult> AcquireVerifiedWorkflowLeaseAsync(
            TrustedSetupHostLayout layout,
            SourceAdminDisposition sourceDisposition,
            CancellationToken cancellationToken) =>
            Task.FromResult(new SetupVerifiedWorkflowLeaseResult
            {
                Result = SetupApplyResult.Create(
                    SetupApplyResultCode.ApplySucceeded,
                    SetupManagedDeploymentState.Active),
                Lease = lease,
            });

        public Task<SetupApplyResult> RecoverAdminBootstrapRollbackAsync(
            TrustedSetupHostLayout layout,
            AdminBootstrapOwnershipDocument pending,
            CancellationToken cancellationToken) =>
            Task.FromResult(SetupApplyResult.Create(
                SetupApplyResultCode.RollbackSucceeded,
                SetupManagedDeploymentState.Active,
                configRollbackStatus: SetupConfigRollbackStatus.Succeeded));

        public Task<SetupVerifiedRecoveryLeaseResult> AcquireRecoveryAuthorityLeaseAsync(
            TrustedSetupHostLayout layout,
            AdminBootstrapOwnershipDocument document,
            CancellationToken cancellationToken) =>
            Task.FromResult(new SetupVerifiedRecoveryLeaseResult
            {
                Authority = SetupAuthorityCheckResult.Current(),
                Lease = new FakeRecoveryLease(),
            });

        public Task<SetupAuthorityCheckResult> VerifyPendingCandidateAsync(
            TrustedSetupHostLayout layout,
            AdminBootstrapOwnershipDocument pending,
            CancellationToken cancellationToken) =>
            Task.FromResult(SetupAuthorityCheckResult.Current());
    }

    /// <summary>
    /// Serves the minimum same-origin Admin login and setup-status responses the verifier accepts.
    /// </summary>
    private sealed class AdminAccessScriptHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var response = path switch
            {
                "/admin/login" => Html(
                    "<form action=\"/admin/api/login\" method=\"post\">"
                    + "<input name=\"__RequestVerificationToken\" value=\"synthetic-token\" />"
                    + "</form>"),
                "/admin/api/login" => Redirect(),
                "/admin/setup-status" => Html("<main aria-label=\"Setup status\"></main>"),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
            response.RequestMessage = request;
            return Task.FromResult(response);
        }

        private static HttpResponseMessage Html(string body) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "text/html"),
            };

        private static HttpResponseMessage Redirect()
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("/admin", UriKind.Relative);
            return response;
        }
    }

    private sealed class FakeRecoveryLease(Action? onDispose = null) : ISetupVerifiedRecoveryLease
    {
        internal bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            onDispose?.Invoke();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeVerifiedWorkflowApplyEngine(
        SetupApplyResult recoveryResult,
        SetupAuthorityCheckResult? candidateAuthority = null,
        Action<FakeRecoveryLease>? onLeaseAcquired = null)
        : ISetupVerifiedWorkflowApplyEngine
    {
        internal FakeRecoveryLease? LastRecoveryLease { get; private set; }

        public Task<SetupVerifiedWorkflowLeaseResult> AcquireVerifiedWorkflowLeaseAsync(
            TrustedSetupHostLayout layout,
            SourceAdminDisposition sourceDisposition,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                new SetupVerifiedWorkflowLeaseResult
                {
                    Result = SetupApplyResult.Create(
                        SetupApplyResultCode.PreflightFailed,
                        SetupManagedDeploymentState.NotInspected),
                });

        public Task<SetupApplyResult> RecoverAdminBootstrapRollbackAsync(
            TrustedSetupHostLayout layout,
            AdminBootstrapOwnershipDocument pending,
            CancellationToken cancellationToken)
        {
            Assert.True(
                pending.State is AdminBootstrapOwnershipState.Armed
                    or AdminBootstrapOwnershipState.DatabaseObserved
                    or AdminBootstrapOwnershipState.AccessVerified
                    or AdminBootstrapOwnershipState.SessionCleaned,
                "Production SetupApplyEngine rejects unknown pending rollback states.");
            return Task.FromResult(recoveryResult);
        }

        public Task<SetupVerifiedRecoveryLeaseResult> AcquireRecoveryAuthorityLeaseAsync(
            TrustedSetupHostLayout layout,
            AdminBootstrapOwnershipDocument document,
            CancellationToken cancellationToken)
        {
            var authority = candidateAuthority ?? SetupAuthorityCheckResult.Current();
            if (!authority.IsCurrent)
            {
                return Task.FromResult(new SetupVerifiedRecoveryLeaseResult
                {
                    Authority = authority,
                });
            }

            LastRecoveryLease = new FakeRecoveryLease();
            onLeaseAcquired?.Invoke(LastRecoveryLease);
            return Task.FromResult(new SetupVerifiedRecoveryLeaseResult
            {
                Authority = authority,
                Lease = LastRecoveryLease,
            });
        }

        public Task<SetupAuthorityCheckResult> VerifyPendingCandidateAsync(
            TrustedSetupHostLayout layout,
            AdminBootstrapOwnershipDocument pending,
            CancellationToken cancellationToken) =>
            Task.FromResult(candidateAuthority ?? SetupAuthorityCheckResult.Current());
    }

    private sealed class StaticResponseHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                new HttpResponseMessage(statusCode)
                {
                    RequestMessage = request,
                    Content = new StringContent(string.Empty),
                });
    }

    private sealed class FaultingSetupFileSystem(
        ISetupFileSystem inner,
        string? failDeletePath = null,
        string? failMoveDestination = null) : ISetupFileSystem
    {
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public bool FileExists(string path) => inner.FileExists(path);
        public SetupLinkInspectionResult InspectSymlinkOrReparsePoint(string path) =>
            inner.InspectSymlinkOrReparsePoint(path);
        public IEnumerable<string> EnumerateFileSystemEntries(string path) =>
            inner.EnumerateFileSystemEntries(path);
        public void CreateOwnerOnlyDirectory(string path) => inner.CreateOwnerOnlyDirectory(path);
        public void WriteProtectedFileCreateNew(string path, ReadOnlySpan<byte> content) =>
            inner.WriteProtectedFileCreateNew(path, content);
        public void WriteProtectedFileCreateNew(string path, string content) =>
            inner.WriteProtectedFileCreateNew(path, content);
        public byte[] ReadAllBytes(string path) => inner.ReadAllBytes(path);

        public void DeleteFile(string path)
        {
            if (PathsEqual(path, failDeletePath))
                throw new IOException("Injected durable delete failure.");
            inner.DeleteFile(path);
        }

        public void DeleteDirectoryRecursive(string path) => inner.DeleteDirectoryRecursive(path);

        public void MoveReplace(string sourcePath, string destinationPath)
        {
            if (PathsEqual(destinationPath, failMoveDestination))
                throw new IOException("Injected atomic replace failure.");
            inner.MoveReplace(sourcePath, destinationPath);
        }

        public void FlushDirectory(string path) => inner.FlushDirectory(path);
        public void FlushFile(string path) => inner.FlushFile(path);
        public FileStream OpenExclusiveGenerationLock(string path) =>
            inner.OpenExclusiveGenerationLock(path);
        public void SetUnixOwnership(string path, uint userId, uint groupId) =>
            inner.SetUnixOwnership(path, userId, groupId);
        public void SetUnixFileModeOwnerOnly(string path, bool executableDirectory) =>
            inner.SetUnixFileModeOwnerOnly(path, executableDirectory);
        public bool TryGetUnixFileMode(string path, out UnixFileMode mode) =>
            inner.TryGetUnixFileMode(path, out mode);
        public bool IsOwnerOnlyFile(string path) => inner.IsOwnerOnlyFile(path);
        public uint? GetEffectiveUnixUserId() => inner.GetEffectiveUnixUserId();

        private static bool PathsEqual(string path, string? expected) =>
            expected is not null
            && string.Equals(
                Path.GetFullPath(path),
                Path.GetFullPath(expected),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
    }

    private sealed class TrackingDeleteFileSystem(
        ISetupFileSystem inner,
        string watchDeletePath,
        Action onWatchedDelete) : ISetupFileSystem
    {
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public bool FileExists(string path) => inner.FileExists(path);
        public SetupLinkInspectionResult InspectSymlinkOrReparsePoint(string path) =>
            inner.InspectSymlinkOrReparsePoint(path);
        public IEnumerable<string> EnumerateFileSystemEntries(string path) =>
            inner.EnumerateFileSystemEntries(path);
        public void CreateOwnerOnlyDirectory(string path) => inner.CreateOwnerOnlyDirectory(path);
        public void WriteProtectedFileCreateNew(string path, ReadOnlySpan<byte> content) =>
            inner.WriteProtectedFileCreateNew(path, content);
        public void WriteProtectedFileCreateNew(string path, string content) =>
            inner.WriteProtectedFileCreateNew(path, content);
        public byte[] ReadAllBytes(string path) => inner.ReadAllBytes(path);

        public void DeleteFile(string path)
        {
            if (string.Equals(
                    Path.GetFullPath(path),
                    Path.GetFullPath(watchDeletePath),
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
            {
                onWatchedDelete();
            }

            inner.DeleteFile(path);
        }

        public void DeleteDirectoryRecursive(string path) => inner.DeleteDirectoryRecursive(path);
        public void MoveReplace(string sourcePath, string destinationPath) =>
            inner.MoveReplace(sourcePath, destinationPath);
        public void FlushDirectory(string path) => inner.FlushDirectory(path);
        public void FlushFile(string path) => inner.FlushFile(path);
        public FileStream OpenExclusiveGenerationLock(string path) =>
            inner.OpenExclusiveGenerationLock(path);
        public void SetUnixOwnership(string path, uint userId, uint groupId) =>
            inner.SetUnixOwnership(path, userId, groupId);
        public void SetUnixFileModeOwnerOnly(string path, bool executableDirectory) =>
            inner.SetUnixFileModeOwnerOnly(path, executableDirectory);
        public bool TryGetUnixFileMode(string path, out UnixFileMode mode) =>
            inner.TryGetUnixFileMode(path, out mode);
        public bool IsOwnerOnlyFile(string path) => inner.IsOwnerOnlyFile(path);
        public uint? GetEffectiveUnixUserId() => inner.GetEffectiveUnixUserId();
    }

    private sealed class RejectingProcessRunner : IHostProcessRunner
    {
        public Task<HostProcessResult> RunAsync(
            HostProcessSpec spec,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HostProcessResult
            {
                Outcome = HostProcessOutcome.FailedToStart,
                ExitCode = -1,
                StandardError = "docker unavailable for unit test",
            });
    }
}
