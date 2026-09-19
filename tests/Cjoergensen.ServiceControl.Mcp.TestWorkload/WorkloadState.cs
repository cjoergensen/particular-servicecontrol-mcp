using System.Collections.Concurrent;

namespace Cjoergensen.ServiceControl.Mcp.TestWorkload;

/// <summary>
/// Switches and counters shared between the test and the endpoints it runs in the same process. This is how a test makes the
/// system misbehave on purpose (fail payments, fail a custom check) and then observes recovery.
/// </summary>
public sealed class WorkloadState
{
    volatile bool billingFails;
    volatile bool dependencyHealthy = true;

    /// <summary>When set, <c>ChargeCustomer</c> throws, so the message ends up in the error queue.</summary>
    public bool BillingFails
    {
        get => billingFails;
        set => billingFails = value;
    }

    /// <summary>The state the custom check reports.</summary>
    public bool DependencyHealthy
    {
        get => dependencyHealthy;
        set => dependencyHealthy = value;
    }

    /// <summary>Orders Billing charged successfully, so a test can prove a retried message was really processed.</summary>
    public ConcurrentBag<Guid> ChargedOrders { get; } = [];

    /// <summary>Set by the saga handler so a test can find the saga id to look up.</summary>
    public ConcurrentDictionary<Guid, Guid> SagaIdsByOrder { get; } = [];

    /// <summary>The failure reason the custom check reports while unhealthy.</summary>
    public const string DependencyFailureReason = "Integration test dependency is unavailable";

    /// <summary>The exception message thrown while payments fail.</summary>
    public const string ChargeFailureMessage = "Card gateway timed out";

    // The endpoints are created by NServiceBus, which cannot inject constructor arguments into custom checks discovered by
    // scanning; a single process-wide instance is the simplest reliable bridge in a test-only project.
    public static WorkloadState Current { get; } = new();
}
