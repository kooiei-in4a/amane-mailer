using System.Text;
using Amane.Mailer.Setup;
using Amane.Mailer.Tests.TestSupport;

namespace Amane.Mailer.Tests.Setup;

/// <summary>
/// Exercises <see cref="SetupDurableAtomicWriter"/> against the real host file system so the
/// tmp/flush/MoveReplace/parent-flush order and the owner-only guarantees are covered on the
/// platform that will actually run apply, not only against an in-memory fake.
/// </summary>
public sealed class SetupDurableAtomicWriterHostTests
{
    [Fact]
    public void Replace_creates_a_missing_file()
    {
        using var root = new ManagedRoot();

        var result = root.Writer.TryAtomicReplaceText(root.Path, root.Under("ACTIVE"), "first");

        Assert.True(result.IsSuccess);
        Assert.Equal("first", File.ReadAllText(root.Under("ACTIVE")));
    }

    [Fact]
    public void Replace_overwrites_an_existing_file()
    {
        using var root = new ManagedRoot();
        var path = root.Under("ACTIVE");
        Assert.True(root.Writer.TryAtomicReplaceText(root.Path, path, "first").IsSuccess);

        var result = root.Writer.TryAtomicReplaceText(root.Path, path, "second");

        Assert.True(result.IsSuccess);
        Assert.Equal("second", File.ReadAllText(path));
    }

    [Fact]
    public void Replace_leaves_no_temporary_residue()
    {
        using var root = new ManagedRoot();
        var path = root.Under("ACTIVE");

        Assert.True(root.Writer.TryAtomicReplaceText(root.Path, path, "first").IsSuccess);
        Assert.True(root.Writer.TryAtomicReplaceText(root.Path, path, "second").IsSuccess);

        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal(new[] { "ACTIVE" }, Directory.GetFiles(root.Path).Select(Path.GetFileName));
    }

    [Fact]
    public void Replace_writes_owner_only_files()
    {
        using var root = new ManagedRoot();
        var path = root.Under("TX.stamp");

        Assert.True(root.Writer.TryAtomicReplaceText(root.Path, path, "stamp").IsSuccess);

        Assert.True(root.FileSystem.IsOwnerOnlyFile(path));
    }

    [Fact]
    public void Replace_reclaims_a_stale_temporary_file_from_an_interrupted_write()
    {
        using var root = new ManagedRoot();
        var path = root.Under("ACTIVE");
        File.WriteAllText(path + ".tmp", "torn-write-residue");

        var result = root.Writer.TryAtomicReplaceText(root.Path, path, "recovered");

        Assert.True(result.IsSuccess);
        Assert.Equal("recovered", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Replace_creates_a_missing_parent_directory_owner_only()
    {
        using var root = new ManagedRoot();
        var path = Path.Combine(root.Path, "verification", "last-record.json");

        var result = root.Writer.TryAtomicReplaceText(root.Path, path, "record");

        Assert.True(result.IsSuccess);
        Assert.Equal("record", File.ReadAllText(path));
        Assert.True(Directory.Exists(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public void Replace_writes_exact_utf8_bytes_without_a_bom()
    {
        using var root = new ManagedRoot();
        var path = root.Under("ACTIVE");
        const string content = "{\"schemaVersion\":1}";

        Assert.True(root.Writer.TryAtomicReplaceText(root.Path, path, content).IsSuccess);

        Assert.Equal(Encoding.UTF8.GetBytes(content), File.ReadAllBytes(path));
    }

    [Fact]
    public void Replace_accepts_empty_content()
    {
        using var root = new ManagedRoot();
        var path = root.Under("ACTIVE");

        Assert.True(root.Writer.TryAtomicReplaceText(root.Path, path, string.Empty).IsSuccess);

        Assert.Empty(File.ReadAllBytes(path));
    }

    [Fact]
    public void Json_replace_round_trips_through_the_source_generated_context()
    {
        using var root = new ManagedRoot();
        var path = root.Under("ACTIVE");
        var pointer = new SetupActivePointer
        {
            SchemaVersion = SetupActivePointer.CurrentSchemaVersion,
            BundleId = "bundle-durable01",
            ActivationGeneration = 7,
        };

        var result = root.Writer.TryAtomicReplaceJson(
            root.Path,
            path,
            pointer,
            SetupApplyJsonContext.Default.SetupActivePointer);

        Assert.True(result.IsSuccess);
        Assert.True(SetupActivePointer.TryParse(File.ReadAllText(path), out var parsed));
        Assert.Equal("bundle-durable01", parsed!.BundleId);
        Assert.Equal(7, parsed.ActivationGeneration);
    }

    [Fact]
    public void Replace_rejects_a_destination_outside_the_managed_root()
    {
        using var root = new ManagedRoot();
        using var outside = new ManagedRoot();

        var result = root.Writer.TryAtomicReplaceText(root.Path, outside.Under("ACTIVE"), "escaped");

        Assert.Equal(SetupDockerResultCode.UnsafePath, result.Code);
        Assert.False(File.Exists(outside.Under("ACTIVE")));
    }

    [Fact]
    public void Replace_rejects_a_traversal_destination()
    {
        using var root = new ManagedRoot();
        var traversal = Path.Combine(root.Path, "..", "escaped-ACTIVE");

        var result = root.Writer.TryAtomicReplaceText(root.Path, traversal, "escaped");

        Assert.Equal(SetupDockerResultCode.UnsafePath, result.Code);
        Assert.False(File.Exists(Path.Combine(root.Base, "escaped-ACTIVE")));
    }

    [Fact]
    public void Replace_rejects_a_destination_without_a_directory()
    {
        using var root = new ManagedRoot();

        var result = root.Writer.TryAtomicReplaceText(root.Path, "ACTIVE", "bare");

        Assert.Equal(SetupDockerResultCode.UnsafePath, result.Code);
    }

    [Fact]
    public void Replace_requires_a_destination_path()
    {
        using var root = new ManagedRoot();

        Assert.Throws<ArgumentException>(
            () => root.Writer.TryAtomicReplaceText(root.Path, "   ", "content"));
    }

    [Fact]
    public void Replace_requires_content()
    {
        using var root = new ManagedRoot();

        Assert.Throws<ArgumentNullException>(
            () => root.Writer.TryAtomicReplaceText(root.Path, root.Under("ACTIVE"), null!));
    }

    [Fact]
    public void Durable_delete_removes_the_file_and_reports_success()
    {
        using var root = new ManagedRoot();
        var path = root.Under("TX.stamp");
        Assert.True(root.Writer.TryAtomicReplaceText(root.Path, path, "stamp").IsSuccess);

        var result = root.Writer.TryDurableDelete(root.Path, path);

        Assert.True(result.IsSuccess);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Durable_delete_is_idempotent_for_a_missing_file()
    {
        using var root = new ManagedRoot();

        var result = root.Writer.TryDurableDelete(root.Path, root.Under("TX.stamp"));

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void Durable_delete_rejects_a_path_outside_the_managed_root()
    {
        using var root = new ManagedRoot();
        using var outside = new ManagedRoot();
        var path = outside.Under("TX.stamp");
        File.WriteAllText(path, "stamp");

        var result = root.Writer.TryDurableDelete(root.Path, path);

        Assert.Equal(SetupDockerResultCode.UnsafePath, result.Code);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Sequential_replaces_of_the_same_path_keep_the_last_write()
    {
        using var root = new ManagedRoot();
        var path = root.Under("ACTIVE");

        for (var generation = 1; generation <= 5; generation++)
        {
            Assert.True(root.Writer
                .TryAtomicReplaceText(root.Path, path, generation.ToString())
                .IsSuccess);
        }

        Assert.Equal("5", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    private sealed class ManagedRoot : IDisposable
    {
        public ManagedRoot()
        {
            // The managed root is nested one level down so a traversal test can aim just outside it
            // and still land inside the directory this fixture cleans up.
            Base = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "amane-durable-" + Guid.NewGuid().ToString("N"));
            Path = System.IO.Path.Combine(Base, "managed");
            TestSecretDirectory.CreateSecure(Base);
            TestSecretDirectory.CreateSecure(Path);
            FileSystem = new HostSetupFileSystem();
            Writer = new SetupDurableAtomicWriter(FileSystem);
        }

        public string Base { get; }

        public string Path { get; }

        public HostSetupFileSystem FileSystem { get; }

        public SetupDurableAtomicWriter Writer { get; }

        public string Under(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Base, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
