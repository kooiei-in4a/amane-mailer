using System.Text;

using System.Text.Json;

using Amane.Mailer.Setup;

using Amane.Mailer.Setup.NonInteractive;

using Amane.Mailer.Tests.TestSupport;



namespace Amane.Mailer.Tests.Setup.NonInteractive;



internal static class SetupNonInteractiveTestSupport

{

    internal const string SyntheticServiceToken = "synthetic-mail-token-not-real";

    internal const string SyntheticAcsConnectionString =

        "endpoint=https://synthetic.example.communication.azure.com/;accesskey=SYNTHETICACCESSKEY000000000000000000000000000000=";



    internal static readonly Guid DefaultTenantId = Guid.Parse("00000000-0000-0000-0000-000000000101");



    internal static SetupNonInteractiveInput BuildLocalMailpitInput(Guid? tenantId = null) =>

        new()

        {

            Mode = SetupMode.LocalMailpit,

            TenantId = tenantId ?? DefaultTenantId,

            TenantName = "example-develop",

            SourceService = "example-service",

            SenderEmail = "noreply@example.com",

            SenderDisplayName = "Example Service",

            ServiceToken = SyntheticServiceToken,

        };



    internal static string BuildLocalMailpitJson(Guid? tenantId = null)

    {

        var id = (tenantId ?? DefaultTenantId).ToString("D");

        return $$"""

            {

              "schemaVersion": 1,

              "mode": "local-mailpit",

              "tenant": {

                "tenantId": "{{id}}",

                "tenantName": "example-develop",

                "sourceService": "example-service",

                "senderEmail": "noreply@example.com",

                "senderDisplayName": "Example Service"

              },

              "serviceToken": "{{SyntheticServiceToken}}"

            }

            """;

    }



    internal static string WriteOwnerOnlyConfig(ISetupFileSystem fileSystem, string absolutePath, string json)

    {

        var fullPath = Path.GetFullPath(absolutePath);

        var directory = Path.GetDirectoryName(fullPath)

            ?? throw new InvalidOperationException("Config path must include a directory.");

        fileSystem.CreateOwnerOnlyDirectory(directory);

        fileSystem.WriteProtectedFileCreateNew(fullPath, Encoding.UTF8.GetBytes(json));

        return fullPath;

    }



    internal static string WriteOwnerOnlyConfigOnHost(string absolutePath, string json)

    {

        var host = new HostSetupFileSystem();

        var fullPath = WriteOwnerOnlyConfig(host, absolutePath, json);

        if (!OperatingSystem.IsWindows())

        {

            host.SetUnixFileModeOwnerOnly(fullPath, executableDirectory: false);

        }



        return fullPath;

    }



    internal static string WriteOwnerOnlyConfigInMemory(MemorySetupFileSystem fileSystem, string absolutePath, string json) =>

        WriteOwnerOnlyConfig(fileSystem, absolutePath, json);



    internal static string BuildStagingVerificationJson()

    {

        return $$"""

            {

              "schemaVersion": 1,

              "mode": "staging-verification",

              "tenant": {

                "tenantId": "{{DefaultTenantId:D}}",

                "tenantName": "example-staging",

                "sourceService": "example-service",

                "senderEmail": "noreply@example.com",

                "senderDisplayName": "Example Service"

              },

              "serviceToken": "synthetic-staging-token-not-real",

              "acsConnectionString": "{{SyntheticAcsConnectionString}}",

              "environmentConfirmation": "Staging",

              "intentConfirmation": "MAILER-ACS-REGISTER",

              "stagingRecipientEmail": "qa-recipient@example.com",

              "stagingIntentConfirmation": "MAILER-ACS-TEST-SEND"

            }

            """;

    }



    internal static SetupNonInteractiveResult DeserializeResult(string json) =>

        JsonSerializer.Deserialize(json, SetupNonInteractiveJsonContext.Default.SetupNonInteractiveResult)

        ?? throw new InvalidOperationException("Result JSON was empty.");

}



internal sealed class MemorySetupFileSystem : ISetupFileSystem

{

    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);



    internal bool OwnerOnly { get; set; } = true;



    internal Func<string, SetupLinkInspectionResult>? InspectOverride { get; set; }



    public bool DirectoryExists(string path) => _directories.Contains(Normalize(path));



    public bool FileExists(string path) => _files.ContainsKey(Normalize(path));



    public SetupLinkInspectionResult InspectSymlinkOrReparsePoint(string path) =>

        InspectOverride?.Invoke(Normalize(path)) ?? SetupLinkInspectionResult.NotALink;



    public IEnumerable<string> EnumerateFileSystemEntries(string path)

    {

        var prefix = Normalize(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        return _files.Keys

            .Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))

            .Concat(_directories.Where(dir => dir.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));

    }



    public void CreateOwnerOnlyDirectory(string path)

    {

        var normalized = Normalize(path);

        _directories.Add(normalized);

        var parent = Path.GetDirectoryName(normalized);

        while (!string.IsNullOrEmpty(parent))

        {

            _directories.Add(parent);

            parent = Path.GetDirectoryName(parent);

        }

    }



    public void WriteProtectedFileCreateNew(string path, ReadOnlySpan<byte> content) =>

        _files[Normalize(path)] = content.ToArray();



    public void WriteProtectedFileCreateNew(string path, string content) =>

        WriteProtectedFileCreateNew(path, Encoding.UTF8.GetBytes(content));



    public byte[] ReadAllBytes(string path) =>

        _files.TryGetValue(Normalize(path), out var content)

            ? content

            : throw new FileNotFoundException("Memory file not found.", path);



    public void DeleteFile(string path) => _files.Remove(Normalize(path));



    public void DeleteDirectoryRecursive(string path)

    {

        var normalized = Normalize(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        foreach (var key in _files.Keys.Where(key => key.StartsWith(normalized, StringComparison.OrdinalIgnoreCase)).ToList())

        {

            _files.Remove(key);

        }



        foreach (var dir in _directories.Where(dir => dir.StartsWith(normalized, StringComparison.OrdinalIgnoreCase)).ToList())

        {

            _directories.Remove(dir);

        }

    }



    public void MoveReplace(string sourcePath, string destinationPath)

    {

        var source = Normalize(sourcePath);

        var destination = Normalize(destinationPath);

        if (!_files.TryGetValue(source, out var content))

        {

            throw new FileNotFoundException("Memory move source not found.", sourcePath);

        }



        _files.Remove(source);

        _files[destination] = content;

    }



    public void FlushDirectory(string path)

    {

    }



    public void FlushFile(string path)

    {

    }



    public FileStream OpenExclusiveGenerationLock(string path)

    {

        var normalized = Normalize(path);

        CreateOwnerOnlyDirectory(Path.GetDirectoryName(normalized)!);

        return new FileStream(Path.GetTempFileName(), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

    }



    public void SetUnixOwnership(string path, uint userId, uint groupId)

    {

    }



    public void SetUnixFileModeOwnerOnly(string path, bool executableDirectory)

    {

    }



    public bool TryGetUnixFileMode(string path, out UnixFileMode mode)

    {

        mode = default;

        return false;

    }



    public bool IsOwnerOnlyFile(string path) => OwnerOnly && FileExists(path);



    public uint? GetEffectiveUnixUserId() => 1000;



    private static string Normalize(string path) => Path.GetFullPath(path);

}
