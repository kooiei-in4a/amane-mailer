using System.Text.Json;
using System.Text.RegularExpressions;
using Amane.Mailer.Admin;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Operations;
using Amane.Mailer.Operations.AdminBootstrap;
using Amane.Mailer.Setup;
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
            Assert.True(store.PromotePendingToCurrent(root, current).IsSuccess);

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
    public void Armed_source_already_active_aborts_pending_without_touching_current()
    {
        var root = TempRoot();
        Directory.CreateDirectory(root);
        try
        {
            var store = new AdminBootstrapOwnershipStore(new HostSetupFileSystem());
            var currentOperation = AdminBootstrapOperationId.Create();
            var current = Ownership(
                currentOperation,
                AdminBootstrapOwnershipState.Succeeded,
                "bundle-current");
            Assert.True(store.WritePending(root, current).IsSuccess);
            Assert.True(store.PromotePendingToCurrent(root, current).IsSuccess);

            var pendingOperation = AdminBootstrapOperationId.Create();
            var pending = Ownership(
                pendingOperation,
                AdminBootstrapOwnershipState.Armed,
                "bundle-candidate");
            Assert.True(store.WritePending(root, pending).IsSuccess);

            // Recovery path for armed + source ACTIVE deletes pending and leaves current.
            Assert.True(store.DeletePending(root).IsSuccess);
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
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
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

    [Fact]
    public void Workflow_canonical_result_has_no_operation_or_session_fields()
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
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("operationId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sessionId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("setup-v1:", json, StringComparison.Ordinal);
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
}
