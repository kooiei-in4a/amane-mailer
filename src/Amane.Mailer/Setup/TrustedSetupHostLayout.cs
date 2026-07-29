namespace Amane.Mailer.Setup;

/// <summary>
/// Product-resolved host layout. No public constructor — callers cannot inject arbitrary roots.
/// </summary>
public sealed class TrustedSetupHostLayout
{
    internal TrustedSetupHostLayout(
        string releaseBundleRoot,
        string managedRoot,
        string statePath,
        string externalEnvPath,
        IReadOnlyList<string> composeFilePaths,
        SetupComposeTopology topology,
        TrustedReleaseInventory releaseInventory,
        string deploymentIdentity)
    {
        ReleaseBundleRoot = releaseBundleRoot;
        ManagedRoot = managedRoot;
        StatePath = statePath;
        ExternalEnvPath = externalEnvPath;
        ComposeFilePaths = composeFilePaths;
        Topology = topology;
        ReleaseInventory = releaseInventory;
        DeploymentIdentity = deploymentIdentity;
        ProjectName = SetupDockerInventory.BuildProjectName(
            releaseInventory.ProjectNamePrefix,
            deploymentIdentity);
    }

    public string ReleaseBundleRoot { get; }
    public string ManagedRoot { get; }
    public string StatePath { get; }
    public string ExternalEnvPath { get; }
    public IReadOnlyList<string> ComposeFilePaths { get; }
    public SetupComposeTopology Topology { get; }
    public TrustedReleaseInventory ReleaseInventory { get; }
    public string DeploymentIdentity { get; }
    public string ProjectName { get; }

    public string ActivePointerPath =>
        Path.Combine(StatePath, SetupBundleLayout.ActivePointerFileName);

    public string ApplyLockPath =>
        Path.Combine(StatePath, SetupApplyLock.LockFileName);

    public string ProjectDirectory => ReleaseBundleRoot;
}
