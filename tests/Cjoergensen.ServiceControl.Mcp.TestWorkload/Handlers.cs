using NServiceBus;

namespace Cjoergensen.ServiceControl.Mcp.TestWorkload;

public sealed class ChargeCustomerHandler : IHandleMessages<ChargeCustomer>
{
    public Task Handle(ChargeCustomer message, IMessageHandlerContext context)
    {
        var state = WorkloadState.Current;
        if (state.BillingFails)
        {
            throw new InvalidOperationException(WorkloadState.ChargeFailureMessage);
        }

        state.ChargedOrders.Add(message.OrderId);
        return Task.CompletedTask;
    }
}

public sealed class OrderPolicyData : ContainSagaData
{
    public Guid OrderId { get; set; }

    public string Stage { get; set; } = string.Empty;
}

public sealed class OrderPolicy : Saga<OrderPolicyData>, IAmStartedByMessages<StartOrder>, IHandleMessages<CompleteOrder>
{
    protected override void ConfigureHowToFindSaga(SagaPropertyMapper<OrderPolicyData> mapper) =>
        mapper.MapSaga(saga => saga.OrderId)
            .ToMessage<StartOrder>(message => message.OrderId)
            .ToMessage<CompleteOrder>(message => message.OrderId);

    public Task Handle(StartOrder message, IMessageHandlerContext context)
    {
        Data.OrderId = message.OrderId;
        Data.Stage = "Started";
        WorkloadState.Current.SagaIdsByOrder[message.OrderId] = Data.Id;
        return Task.CompletedTask;
    }

    public Task Handle(CompleteOrder message, IMessageHandlerContext context)
    {
        Data.Stage = "Completed";
        MarkAsComplete();
        return Task.CompletedTask;
    }
}
