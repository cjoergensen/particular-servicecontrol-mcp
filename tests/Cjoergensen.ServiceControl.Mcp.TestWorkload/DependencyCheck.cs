using NServiceBus.CustomChecks;

namespace Cjoergensen.ServiceControl.Mcp.TestWorkload;

/// <summary>A custom check whose result the test controls through <see cref="WorkloadState.DependencyHealthy"/>.</summary>
public sealed class DependencyCheck() : CustomCheck("Integration-Dependency", "Integration", TimeSpan.FromSeconds(2))
{
    public override Task<CheckResult> PerformCheck(CancellationToken cancellationToken = default) =>
        WorkloadState.Current.DependencyHealthy
            ? CheckResult.Pass
            : CheckResult.Failed(WorkloadState.DependencyFailureReason);
}
