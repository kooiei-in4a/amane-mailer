using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Amane.Mailer.Tests.Qualification;

/// <summary>
/// Docker-backed, value-free fixtures used only by the G583 MIG01/MIG02 adapter.
/// A normal developer/CI test run does not synthesize a result: without the
/// producer-provided input and a Linux Docker engine, these tests skip.
/// </summary>
public sealed class G583MigrationDockerFixtureTests
{
    [Fact]
    public Task Qualification_fixture_G583_MIG_01_win_docker() =>
        RunAsync("G583-MIG-01", "win-docker");

    [Fact]
    public Task Qualification_fixture_G583_MIG_01_linux_docker() =>
        RunAsync("G583-MIG-01", "linux-docker");

    [Fact]
    public Task Qualification_fixture_G583_MIG_02_win_docker() =>
        RunAsync("G583-MIG-02", "win-docker");

    [Fact]
    public Task Qualification_fixture_G583_MIG_02_linux_docker() =>
        RunAsync("G583-MIG-02", "linux-docker");

    private static async Task RunAsync(string scenarioId, string variantId)
    {
        var input = ReadInputOrSkip();
        ValidateInput(input, scenarioId, variantId);
        var platform = await RequireDockerEnvironmentAsync(variantId, input.Candidate, TestContext.Current.CancellationToken);
        var root = Path.Combine(Path.GetTempPath(), "amane-g583-migration-docker", Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(root);
            var databasePath = Path.Combine(root, "mailer.db");
            Assert.False(File.Exists(databasePath));

            var selectedManifestDigest = await ResolveSelectedManifestAsync(
                input.Candidate.ImageReference,
                platform.ContainerPlatform,
                input.Candidate.SelectedManifests,
                TestContext.Current.CancellationToken);

            IReadOnlyList<AppliedMigration> before;
            object? baseline;
            if (input.MigrationMode == "upgrade")
            {
                Assert.NotNull(input.Baseline);
                await RunMigrationAsync(
                    input.Baseline!.ImageReference,
                    platform.ContainerPlatform,
                    root,
                    TestContext.Current.CancellationToken);
                before = await ReadAppliedMigrationsAsync(databasePath, TestContext.Current.CancellationToken);
                AssertExactInventory(before, input.Baseline.Inventory);
                baseline = new
                {
                    releaseTag = input.Baseline.ReleaseTag,
                    releaseCommitSha = input.Baseline.ReleaseCommitSha,
                    ociIndexDigest = input.Baseline.OciIndexDigest,
                    inventory = input.Baseline.Inventory.Select(static item => item.FileName).ToArray(),
                };
            }
            else
            {
                before = [];
                baseline = null;
            }

            await RunMigrationAsync(
                input.Candidate.ImageReference,
                platform.ContainerPlatform,
                root,
                TestContext.Current.CancellationToken);
            var afterFirstApply = await ReadAppliedMigrationsAsync(databasePath, TestContext.Current.CancellationToken);
            AssertExactInventory(afterFirstApply, input.CandidateFullInventory);

            // A second candidate mailer-migrate execution must leave no pending migration.
            await RunMigrationAsync(
                input.Candidate.ImageReference,
                platform.ContainerPlatform,
                root,
                TestContext.Current.CancellationToken);
            var afterSecondApply = await ReadAppliedMigrationsAsync(databasePath, TestContext.Current.CancellationToken);
            Assert.Equal(afterFirstApply, afterSecondApply);

            var candidateArtifactIdentity = await VerifyCandidateImageIdentityAsync(
                input.Candidate.ImageReference,
                input.Candidate.ReleaseCommitSha,
                TestContext.Current.CancellationToken);
            Assert.True(candidateArtifactIdentity);

            var appliedDuring = input.MigrationMode == "fresh"
                ? afterFirstApply
                : afterFirstApply.Where(item => !before.Contains(item)).ToArray();
            var checksums = afterFirstApply.ToDictionary(
                static item => item.FileName,
                static item => item.Checksum,
                StringComparer.Ordinal);
            var observations = new
            {
                initialDatabaseState = input.MigrationMode == "fresh" ? "absent" : "v1.2.0-001..013",
                migrationService = "mailer-migrate",
                migrationCommand = new[] { "db", "migrate" },
                beforeInventory = before.Select(static item => item.FileName).ToArray(),
                appliedInventory = appliedDuring.Select(static item => item.FileName).ToArray(),
                finalInventory = afterFirstApply.Select(static item => item.FileName).ToArray(),
                checksums,
                missingMigrations = Array.Empty<string>(),
                unexpectedMigrations = Array.Empty<string>(),
                pendingMigrations = Array.Empty<string>(),
                lastApplied = afterFirstApply[^1].FileName,
                currentSchemaReady = true,
                candidateArtifactIdentity,
                baseline,
            };
            var fixtureResult = new
            {
                schemaVersion = 1,
                kind = "g583-migration-docker-fixture",
                fixtureId = input.FixtureId,
                fixtureRevision = input.FixtureRevision,
                scenarioId = input.ScenarioId,
                variantId = input.VariantId,
                laneVariant = input.VariantId,
                contractVersion = input.ContractVersion,
                result = "PASS",
                operationExitCode = 0,
                platform = new
                {
                    hostPlatform = platform.HostPlatform,
                    dockerEngineOS = platform.DockerEngineOS,
                    containerPlatform = platform.ContainerPlatform,
                    measurements = new
                    {
                        hostPlatform = platform.HostMeasurement,
                        dockerEngine = new { OSType = platform.DockerEngineOS },
                        containerImage = new { OS = "linux", Architecture = platform.ContainerArchitecture },
                        selectedOciDescriptor = new { platform = platform.ContainerPlatform, manifestDigest = selectedManifestDigest },
                    },
                },
                artifactIdentity = new
                {
                    candidateId = input.Candidate.CandidateId,
                    releaseCommitSha = input.Candidate.ReleaseCommitSha,
                    ociIndexDigest = input.Candidate.OciIndexDigest,
                    selectedManifestDigest,
                },
                migration = observations,
            };
            WriteResultIfRequested(fixtureResult);
        }
        catch
        {
            WriteFailureIfRequested(input);
            throw;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static FixtureInput ReadInputOrSkip()
    {
        var path = Environment.GetEnvironmentVariable("AMANE_G583_MIGRATION_FIXTURE_INPUT_PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            Assert.Skip("G583 MIG Docker fixtures require producer-supplied candidate input.");
        }

        try
        {
            var input = JsonSerializer.Deserialize<FixtureInput>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return input ?? throw new InvalidOperationException("Fixture input was empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Fixture input was invalid JSON.", exception);
        }
    }

    private static void ValidateInput(FixtureInput input, string scenarioId, string variantId)
    {
        Assert.Equal(1, input.SchemaVersion);
        Assert.Equal(scenarioId, input.ScenarioId);
        Assert.Equal(variantId, input.VariantId);
        Assert.Equal("g583-s5a-platform-v1", input.ContractVersion);
        Assert.Equal("1", input.FixtureRevision);
        Assert.Equal(scenarioId == "G583-MIG-01" ? "fresh" : "upgrade", input.MigrationMode);
        Assert.Matches("^[0-9a-f]{64}$", input.Candidate.CandidateId);
        Assert.Matches("^[0-9a-f]{40}$", input.Candidate.ReleaseCommitSha);
        Assert.Matches("^sha256:[0-9a-f]{64}$", input.Candidate.OciIndexDigest);
        Assert.EndsWith("@" + input.Candidate.OciIndexDigest, input.Candidate.ImageReference, StringComparison.Ordinal);
        Assert.Equal(new[] { "linux/amd64", "linux/arm64" }, input.Candidate.SelectedManifests.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(5, input.CandidateDeltaInventory.Count);
        Assert.Equal(18, input.CandidateFullInventory.Count);
        Assert.Equal("018_admin_user_capabilities.sql", input.CandidateFullInventory[^1].FileName);

        if (input.MigrationMode == "upgrade")
        {
            Assert.NotNull(input.Baseline);
            Assert.Equal("v1.2.0", input.Baseline!.ReleaseTag);
            Assert.Equal("c173db1d03725e754c4432d02b7c43ceed98c3c0", input.Baseline.ReleaseCommitSha);
            Assert.Equal("sha256:ded98629afda63d1f736807cc942e5d92c6cdf08cfc33beba2f2b277d19b2759", input.Baseline.OciIndexDigest);
            Assert.EndsWith("@" + input.Baseline.OciIndexDigest, input.Baseline.ImageReference, StringComparison.Ordinal);
            Assert.Equal(13, input.Baseline.Inventory.Count);
            Assert.Equal("013_provider_queue_dead_letters.sql", input.Baseline.Inventory[^1].FileName);
        }
        else
        {
            Assert.Null(input.Baseline);
        }
    }

    private static async Task<DockerPlatform> RequireDockerEnvironmentAsync(
        string variantId,
        CandidateIdentity candidate,
        CancellationToken cancellationToken)
    {
        var host = GetHostPlatform();
        if ((variantId == "win-docker" && host.HostPlatform != "windows-x64")
            || (variantId == "linux-docker" && host.HostPlatform is not ("linux-x64" or "linux-arm64")))
        {
            Assert.Skip("Host platform does not match this bound Docker lane.");
        }

        var info = await DockerAsync(["info", "--format", "{{.OSType}}"], cancellationToken);
        if (info.ExitCode != 0 || !string.Equals(info.StandardOutput.Trim(), "linux", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Skip("A Linux Docker engine is required for this qualification lane.");
        }

        var containerPlatform = host.HostPlatform switch
        {
            "windows-x64" => "linux/amd64",
            "linux-x64" => "linux/amd64",
            "linux-arm64" => "linux/arm64",
            _ => throw new InvalidOperationException("Unsupported host platform."),
        };
        if (!candidate.SelectedManifests.ContainsKey(containerPlatform))
        {
            Assert.Skip("Candidate has no selected manifest for this concrete Docker platform.");
        }

        return new DockerPlatform(
            host.HostPlatform,
            "linux",
            containerPlatform,
            containerPlatform.Split('/')[1],
            host.Measurement);
    }

    private static HostProbe GetHostPlatform()
    {
        var architecture = RuntimeInformation.OSArchitecture;
        if (OperatingSystem.IsWindows() && architecture == Architecture.X64)
        {
            return new HostProbe("windows-x64", new { os = "windows", architecture = "amd64" });
        }

        if (OperatingSystem.IsLinux() && architecture == Architecture.X64)
        {
            return new HostProbe("linux-x64", new { os = "linux", architecture = "amd64" });
        }

        if (OperatingSystem.IsLinux() && architecture == Architecture.Arm64)
        {
            return new HostProbe("linux-arm64", new { os = "linux", architecture = "arm64" });
        }

        Assert.Skip("G583 MIG Docker fixtures support only windows-x64, linux-x64, or linux-arm64 hosts.");
        throw new InvalidOperationException("Unreachable after test skip.");
    }

    private static async Task<string> ResolveSelectedManifestAsync(
        string imageReference,
        string containerPlatform,
        IReadOnlyDictionary<string, string> selectedManifests,
        CancellationToken cancellationToken)
    {
        var result = await DockerAsync(["buildx", "imagetools", "inspect", imageReference, "--raw"], cancellationToken);
        Assert.Equal(0, result.ExitCode);
        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            if (!document.RootElement.TryGetProperty("manifests", out var manifests)
                || manifests.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("OCI index did not contain a descriptor list.");
            }

            foreach (var descriptor in manifests.EnumerateArray())
            {
                if (!descriptor.TryGetProperty("platform", out var platform)
                    || !platform.TryGetProperty("os", out var os)
                    || !platform.TryGetProperty("architecture", out var architecture)
                    || !descriptor.TryGetProperty("digest", out var digest))
                {
                    continue;
                }

                var measured = $"{os.GetString()?.ToLowerInvariant()}/{architecture.GetString()?.ToLowerInvariant()}";
                if (measured == containerPlatform)
                {
                    var selected = digest.GetString();
                    Assert.NotNull(selected);
                    Assert.Equal(selectedManifests[containerPlatform], selected);
                    return selected;
                }
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("OCI descriptor probe returned invalid JSON.", exception);
        }

        throw new InvalidOperationException("OCI descriptor probe did not select the measured platform.");
    }

    private static async Task RunMigrationAsync(
        string imageReference,
        string containerPlatform,
        string dataDirectory,
        CancellationToken cancellationToken)
    {
        var result = await DockerAsync(
            [
                "run", "--rm", "--platform", containerPlatform, "--user", "0:0",
                "--volume", $"{dataDirectory}:/app/data",
                "--env", "ASPNETCORE_ENVIRONMENT=Production",
                "--env", "ConnectionStrings__Mailer=Data Source=/app/data/mailer.db",
                "--env", "MAILER_TENANTS_PATH=/app/config/mailer/tenants.example.json",
                "--env", "MAIL_SERVICE_TOKEN=local-mail-service-token",
                "--env", "MAILER_PROVIDER=mailpit",
                "--env", "ACS_CONNECTION_STRING=",
                imageReference,
                "db", "migrate",
            ],
            cancellationToken);
        Assert.Equal(0, result.ExitCode);
    }

    private static async Task<bool> VerifyCandidateImageIdentityAsync(
        string imageReference,
        string expectedReleaseCommitSha,
        CancellationToken cancellationToken)
    {
        var result = await DockerAsync(
            ["image", "inspect", "--format", "{{ index .Config.Labels \"org.opencontainers.image.revision\" }}", imageReference],
            cancellationToken);
        return result.ExitCode == 0
            && string.Equals(result.StandardOutput.Trim(), expectedReleaseCommitSha, StringComparison.Ordinal);
    }

    private static async Task<IReadOnlyList<AppliedMigration>> ReadAppliedMigrationsAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadWrite;Pooling=False");
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version, checksum FROM schema_migrations ORDER BY version;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var applied = new List<AppliedMigration>();
        while (await reader.ReadAsync(cancellationToken))
        {
            applied.Add(new AppliedMigration(reader.GetString(0), reader.GetString(1)));
        }

        return applied;
    }

    private static void AssertExactInventory(
        IReadOnlyList<AppliedMigration> actual,
        IReadOnlyList<MigrationFile> expected)
    {
        Assert.Equal(expected.Select(static item => item.FileName), actual.Select(static item => item.FileName));
        Assert.Equal(expected.Select(static item => item.Sha256), actual.Select(static item => item.Checksum));
    }

    private static async Task<DockerResult> DockerAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            Assert.Skip("Docker CLI could not be started for the G583 MIG fixture.");
        }

        var standardOutput = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        _ = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new DockerResult(process.ExitCode, standardOutput);
    }

    private static void WriteResultIfRequested(object result)
    {
        var outputPath = Environment.GetEnvironmentVariable("AMANE_G583_MIGRATION_FIXTURE_RESULT_PATH");
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            File.WriteAllText(outputPath, JsonSerializer.Serialize(result));
        }
    }

    private static void WriteFailureIfRequested(FixtureInput input)
    {
        var outputPath = Environment.GetEnvironmentVariable("AMANE_G583_MIGRATION_FIXTURE_RESULT_PATH");
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            File.WriteAllText(
                outputPath,
                JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    kind = "g583-migration-docker-fixture",
                    fixtureId = input.FixtureId,
                    fixtureRevision = input.FixtureRevision,
                    scenarioId = input.ScenarioId,
                    variantId = input.VariantId,
                    laneVariant = input.VariantId,
                    contractVersion = input.ContractVersion,
                    result = "FAIL",
                    operationExitCode = 1,
                }));
        }
    }

    private sealed record FixtureInput(
        int SchemaVersion,
        string ScenarioId,
        string VariantId,
        string ContractVersion,
        string FixtureId,
        string FixtureRevision,
        string MigrationMode,
        CandidateIdentity Candidate,
        BaselineIdentity? Baseline,
        List<MigrationFile> CandidateDeltaInventory,
        List<MigrationFile> CandidateFullInventory);

    private sealed record CandidateIdentity(
        string CandidateId,
        string ReleaseCommitSha,
        string OciIndexDigest,
        Dictionary<string, string> SelectedManifests,
        string ImageReference);

    private sealed record BaselineIdentity(
        string ReleaseTag,
        string ReleaseCommitSha,
        string OciIndexDigest,
        string ImageReference,
        List<MigrationFile> Inventory);

    private sealed record MigrationFile(string FileName, string Sha256);

    private sealed record AppliedMigration(string FileName, string Checksum);

    private sealed record HostProbe(string HostPlatform, object Measurement);

    private sealed record DockerPlatform(
        string HostPlatform,
        string DockerEngineOS,
        string ContainerPlatform,
        string ContainerArchitecture,
        object HostMeasurement);

    private sealed record DockerResult(int ExitCode, string StandardOutput);
}
