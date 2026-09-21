using NServiceBus;

namespace Cjoergensen.ServiceControl.Mcp.TestWorkload;

/// <summary>Asks Billing to charge a customer. Fails while <see cref="WorkloadState.BillingFails"/> is set.</summary>
public sealed class ChargeCustomer : ICommand
{
    public Guid OrderId { get; set; }

    public string Customer { get; set; } = string.Empty;
}

/// <summary>Starts the order saga.</summary>
public sealed class StartOrder : ICommand
{
    public Guid OrderId { get; set; }
}

/// <summary>Advances the order saga to completion.</summary>
public sealed class CompleteOrder : ICommand
{
    public Guid OrderId { get; set; }
}
