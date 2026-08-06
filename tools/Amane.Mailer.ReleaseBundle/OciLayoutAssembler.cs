using System.Text.Json;

namespace Amane.Mailer.ReleaseBundle;

public static partial class ReleaseBundlePackaging
{
    private const string OciImageIndexMediaType = "application/vnd.oci.image.index.v1+json";
    private const string OciImageManifestMediaType = "application/vnd.oci.image.manifest.v1+json";

    public sealed class OciAssemblyRequest
    {
        public required string Amd64LayoutDirectory { get; init; }
        public required string Amd64MetadataPath { get; init; }
        public required string Arm64LayoutDirectory { get; init; }
        public required string Arm64MetadataPath { get; init; }
        public required string OutputDirectory { get; init; }
        public required string ImageRepository { get; init; }
        public required string ImageTag { get; init; }
        public required string SourceCommitSha { get; init; }
        public required string MailerVersion { get; init; }
    }

    /// <summary>
    /// Assemble two independently built single-platform OCI layouts into the
    /// existing multi-platform candidate artifact. No build, registry operation,
    /// or metadata schema change occurs here: the assembler owns the final index,
    /// Buildx-compatible descriptor metadata, and identity files.
    /// </summary>
    public static PackagingValidationResult AssembleOciLayouts(
        OciAssemblyRequest request,
        out string? imageDigest)
    {
        imageDigest = null;
        try
        {
            var amd64 = ReadPlatformInput(
                request.Amd64LayoutDirectory,
                request.Amd64MetadataPath,
                "linux/amd64");
            if (!amd64.Result.Success)
            {
                return amd64.Result;
            }

            var arm64 = ReadPlatformInput(
                request.Arm64LayoutDirectory,
                request.Arm64MetadataPath,
                "linux/arm64");
            if (!arm64.Result.Success)
            {
                return arm64.Result;
            }

            var outputRoot = Path.GetFullPath(request.OutputDirectory);
            if (Directory.Exists(outputRoot)
                && Directory.EnumerateFileSystemEntries(outputRoot).Any())
            {
                return AssemblyFail("oci_assembly_output_not_empty", "Assembler output directory must be empty.");
            }

            var outputLayout = Path.Combine(outputRoot, "oci");
            Directory.CreateDirectory(Path.Combine(outputLayout, "blobs", "sha256"));

            foreach (var input in new[] { amd64.Input!, arm64.Input! })
            {
                foreach (var digest in input.GraphDigests)
                {
                    var source = BlobPath(Path.GetFullPath(input.LayoutDirectory), digest);
                    var destination = BlobPath(outputLayout, digest);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    if (File.Exists(destination))
                    {
                        var existing = File.ReadAllBytes(destination);
                        var incoming = File.ReadAllBytes(source);
                        if (!existing.AsSpan().SequenceEqual(incoming))
                        {
                            return AssemblyFail(
                                "oci_assembly_blob_collision",
                                "Platform layouts contain conflicting bytes for the same digest.");
                        }

                        continue;
                    }

                    File.Copy(source, destination);
                }
            }

            var finalIndex = new OciIndexDocument
            {
                SchemaVersion = 2,
                MediaType = OciImageIndexMediaType,
                Manifests = [amd64.Input!.ManifestDescriptor, arm64.Input!.ManifestDescriptor],
            };
            var finalIndexBytes = JsonSerializer.SerializeToUtf8Bytes(
                finalIndex,
                ReleaseBundleJsonContext.Default.OciIndexDocument);
            imageDigest = DigestBytes(finalIndexBytes);
            WriteBlob(outputLayout, imageDigest, finalIndexBytes);

            var rootIndex = new OciIndexDocument
            {
                SchemaVersion = 2,
                MediaType = OciImageIndexMediaType,
                Manifests =
                [
                    new OciDescriptor
                    {
                        MediaType = OciImageIndexMediaType,
                        Digest = imageDigest,
                        Size = finalIndexBytes.LongLength,
                    },
                ],
            };
            File.WriteAllText(
                Path.Combine(outputLayout, OciLayoutMarkerFileName),
                "{\"imageLayoutVersion\":\"1.0.0\"}\n");
            File.WriteAllBytes(
                Path.Combine(outputLayout, OciIndexFileName),
                JsonSerializer.SerializeToUtf8Bytes(
                    rootIndex,
                    ReleaseBundleJsonContext.Default.OciIndexDocument));

            var metadata = new BuildxMetadataDocument
            {
                ContainerImageDescriptor = new OciDescriptor
                {
                    MediaType = OciImageIndexMediaType,
                    Digest = imageDigest,
                    Size = finalIndexBytes.LongLength,
                },
                ContainerImageDigest = imageDigest,
            };
            File.WriteAllText(
                Path.Combine(outputRoot, "buildx-metadata.json"),
                JsonSerializer.Serialize(
                    metadata,
                    ReleaseBundleJsonContext.Default.BuildxMetadataDocument)
                + "\n");
            File.WriteAllText(Path.Combine(outputRoot, "oci-index.digest"), imageDigest + "\n");

            var identity = new ImageIdentityDocument
            {
                ImageRepository = request.ImageRepository,
                ImageTag = request.ImageTag,
                ImageDigest = imageDigest,
                SourceCommitSha = request.SourceCommitSha.ToLowerInvariant(),
                MailerVersion = request.MailerVersion,
                Platforms = ["linux/amd64", "linux/arm64"],
            };
            var identityGate = AssertImageIdentityForHostPackaging(
                identity,
                request.SourceCommitSha,
                request.MailerVersion);
            if (!identityGate.Success)
            {
                return identityGate;
            }

            File.WriteAllText(
                Path.Combine(outputRoot, "image-identity.json"),
                JsonSerializer.Serialize(
                    identity,
                    ReleaseBundleJsonContext.Default.ImageIdentityDocument)
                + "\n");

            var finalValidation = ValidateOciLayoutDirectory(
                outputLayout,
                imageDigest,
                RequiredOciPlatforms,
                metadata.ContainerImageDescriptor);
            if (!finalValidation.Success)
            {
                imageDigest = null;
                return finalValidation;
            }

            return new PackagingValidationResult { Success = true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            imageDigest = null;
            return AssemblyFail("oci_assembly_failed", "OCI assembly failed: " + ex.GetType().Name + ".");
        }
    }

    private sealed class PlatformInputResult
    {
        public PlatformInput? Input { get; init; }
        public required PackagingValidationResult Result { get; init; }
    }

    private sealed class PlatformInput
    {
        public required string LayoutDirectory { get; init; }
        public required OciDescriptor ManifestDescriptor { get; init; }
        public required HashSet<string> GraphDigests { get; init; }
    }

    private static PlatformInputResult ReadPlatformInput(
        string layoutDirectory,
        string metadataPath,
        string requiredPlatform)
    {
        if (!File.Exists(metadataPath))
        {
            return FailedPlatformInput("oci_assembly_metadata_missing", "Platform Buildx metadata-file is missing.");
        }

        var metadataResult = TryParseBuildxMetadata(
            File.ReadAllText(metadataPath),
            out var imageDigest,
            out var imageDescriptor);
        if (!metadataResult.Success || imageDigest is null)
        {
            return FailedPlatformInput(
                metadataResult.ReasonCode ?? "buildx_metadata_invalid",
                metadataResult.Message ?? "Platform Buildx metadata-file is invalid.");
        }

        var validation = ValidateOciLayoutDirectory(
            layoutDirectory,
            imageDigest,
            [requiredPlatform],
            imageDescriptor,
            allowSinglePlatformImageManifest: true);
        if (!validation.Success)
        {
            return FailedPlatformInput(
                validation.ReasonCode ?? "oci_platform_invalid",
                validation.Message ?? "Platform OCI layout is invalid.");
        }

        var rootIndex = ReadIndex(layoutDirectory);
        var bound = rootIndex.Manifests!
            .Single(d => string.Equals(d.Digest, imageDigest, StringComparison.OrdinalIgnoreCase));
        OciDescriptor manifest;
        if (string.Equals(bound.MediaType, OciImageManifestMediaType, StringComparison.Ordinal))
        {
            manifest = NormalizePlatformDescriptor(bound, requiredPlatform);
        }
        else
        {
            var nestedBytes = File.ReadAllBytes(BlobPath(Path.GetFullPath(layoutDirectory), imageDigest));
            var nested = JsonSerializer.Deserialize(
                nestedBytes,
                ReleaseBundleJsonContext.Default.OciIndexDocument)
                ?? throw new JsonException("Platform image index is unreadable.");
            var matches = (nested.Manifests ?? [])
                .Where(d => string.Equals(PlatformIdentity(d), requiredPlatform, StringComparison.Ordinal))
                .ToArray();
            if (matches.Length != 1)
            {
                return FailedPlatformInput(
                    "oci_platform_manifest_missing",
                    "Platform OCI layout must contain exactly one required image manifest.");
            }

            manifest = NormalizePlatformDescriptor(matches[0], requiredPlatform);
        }

        var graph = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var graphResult = CollectManifestGraph(layoutDirectory, manifest, graph);
        if (!graphResult.Success)
        {
            return FailedPlatformInput(
                graphResult.ReasonCode ?? "oci_platform_graph_invalid",
                graphResult.Message ?? "Platform OCI graph is invalid.");
        }

        return new PlatformInputResult
        {
            Input = new PlatformInput
            {
                LayoutDirectory = layoutDirectory,
                ManifestDescriptor = manifest,
                GraphDigests = graph,
            },
            Result = new PackagingValidationResult { Success = true },
        };
    }

    private static PackagingValidationResult CollectManifestGraph(
        string layoutDirectory,
        OciDescriptor manifestDescriptor,
        HashSet<string> graph)
    {
        if (string.IsNullOrWhiteSpace(manifestDescriptor.Digest)
            || !IsValidDigest(manifestDescriptor.Digest))
        {
            return AssemblyFail("oci_descriptor_digest_invalid", "Platform image-manifest digest is invalid.");
        }

        var digest = manifestDescriptor.Digest.ToLowerInvariant();
        if (!graph.Add(digest))
        {
            return new PackagingValidationResult { Success = true };
        }

        var blobPath = BlobPath(Path.GetFullPath(layoutDirectory), digest);
        var document = JsonSerializer.Deserialize(
            File.ReadAllBytes(blobPath),
            ReleaseBundleJsonContext.Default.OciManifestDocument);
        if (document?.Config is null || document.Manifests is { Length: > 0 })
        {
            return AssemblyFail("oci_manifest_incomplete", "Platform image-manifest is incomplete.");
        }

        foreach (var descriptor in new[] { document.Config }.Concat(document.Layers ?? []))
        {
            if (string.IsNullOrWhiteSpace(descriptor.Digest) || !IsValidDigest(descriptor.Digest))
            {
                return AssemblyFail("oci_descriptor_digest_invalid", "Platform graph descriptor digest is invalid.");
            }

            var childDigest = descriptor.Digest.ToLowerInvariant();
            var childPath = BlobPath(Path.GetFullPath(layoutDirectory), childDigest);
            if (!File.Exists(childPath))
            {
                return AssemblyFail("oci_blob_missing", "Platform graph references a missing blob.");
            }

            var bytes = File.ReadAllBytes(childPath);
            if (!string.Equals(DigestBytes(bytes), childDigest, StringComparison.Ordinal))
            {
                return AssemblyFail("oci_blob_digest_mismatch", "Platform graph blob bytes do not match the digest.");
            }

            graph.Add(childDigest);
        }

        return new PackagingValidationResult { Success = true };
    }

    private static OciIndexDocument ReadIndex(string layoutDirectory)
    {
        return JsonSerializer.Deserialize(
                   File.ReadAllBytes(Path.Combine(Path.GetFullPath(layoutDirectory), OciIndexFileName)),
                   ReleaseBundleJsonContext.Default.OciIndexDocument)
               ?? throw new JsonException("OCI layout index is unreadable.");
    }

    private static OciDescriptor NormalizePlatformDescriptor(
        OciDescriptor descriptor,
        string requiredPlatform)
    {
        if (descriptor.Platform is not null)
        {
            return descriptor;
        }

        var slash = requiredPlatform.IndexOf('/');
        return new OciDescriptor
        {
            MediaType = descriptor.MediaType,
            Digest = descriptor.Digest,
            Size = descriptor.Size,
            Platform = new OciPlatform
            {
                Os = requiredPlatform[..slash],
                Architecture = requiredPlatform[(slash + 1)..],
            },
        };
    }

    private static string? PlatformIdentity(OciDescriptor descriptor) =>
        descriptor.Platform is { Os: { Length: > 0 } os, Architecture: { Length: > 0 } architecture }
            ? os + "/" + architecture
            : null;

    private static void WriteBlob(string layoutDirectory, string digest, byte[] bytes)
    {
        var path = BlobPath(layoutDirectory, digest);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static PlatformInputResult FailedPlatformInput(string reasonCode, string message) =>
        new()
        {
            Result = new PackagingValidationResult
            {
                Success = false,
                ReasonCode = reasonCode,
                Message = message,
            },
        };

    private static PackagingValidationResult AssemblyFail(string reasonCode, string message) =>
        new()
        {
            Success = false,
            ReasonCode = reasonCode,
            Message = message,
        };
}
