using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NServiceBus;

namespace Cjoergensen.ServiceControl.Mcp.TestWorkload;

/// <summary>
/// Runs real NServiceBus endpoints against a RabbitMQ broker that a ServiceControl platform is also attached to, so ServiceControl
/// ingests exactly what production endpoints would send. Endpoints:
/// <list type="bullet">
/// <item><c>Billing</c>: charges customers (and can be told to fail), runs the order saga and a custom check, and reports heartbeats, audit, saga audit and metrics.</item>
/// <item><c>Shipping</c>: an otherwise idle endpoint that reports heartbeats and metrics; stop it to make its heartbeat go dead.</item>
/// <item><c>Client</c>: a send-only endpoint used to put messages into Billing.</item>
/// </list>
/// </summary>
public sealed class Workload : IAsyncDisposable
{
    // Queue names are case-sensitive on RabbitMQ and match ServiceControl's container defaults.
    public const string ErrorQueue = "error";
    public const string AuditQueue = "audit";
    public const string ServiceControlQueue = "Particular.ServiceControl";
    public const string MonitoringQueue = "Particular.Monitoring";

    public const string BillingEndpoint = "Billing";
    public const string ShippingEndpoint = "Shipping";

    readonly Dictionary<string, IHost> running = [];
    readonly string sagaDirectory;
    readonly string rabbitMqConnectionString;
    readonly string? managementApiUrl;
    readonly string? learningTransportDirectory;
    IHost? clientHost;

    Workload(string sagaDirectory, string rabbitMqConnectionString, string? managementApiUrl, string? learningTransportDirectory)
    {
        this.sagaDirectory = sagaDirectory;
        this.rabbitMqConnectionString = rabbitMqConnectionString;
        this.managementApiUrl = managementApiUrl;
        this.learningTransportDirectory = learningTransportDirectory;
    }

    /// <summary>The switches a test flips to make the system fail, and the counters it reads to see what was processed.</summary>
    public static WorkloadState State => WorkloadState.Current;

    /// <param name="rabbitMqConnectionString">Connection string of the broker, as reachable from this process.</param>
    /// <param name="managementApiUrl">
    /// Base URL of the broker's management API when it is not on the default port of the broker host, for example because a container
    /// maps it to a random port. The transport verifies the broker through this API when an endpoint starts.
    /// </param>
    /// <param name="learningTransportDirectory">
    /// When set, the endpoints use NServiceBus's file-system learning transport in this folder instead of RabbitMQ; ServiceControl must
    /// then be pointed at the same folder (transport type <c>LearningTransport</c>).
    /// </param>
    public static async Task<Workload> StartAsync(
        string rabbitMqConnectionString, string? managementApiUrl = null, string? learningTransportDirectory = null, CancellationToken cancellationToken = default)
    {
        var workload = new Workload(Path.Combine(Path.GetTempPath(), "scmcp-sagas-" + Guid.NewGuid().ToString("N")), rabbitMqConnectionString, managementApiUrl, learningTransportDirectory);
        try
        {
            workload.running[BillingEndpoint] = await StartHostAsync(workload.Configure(BillingEndpoint, rabbitMqConnectionString, isBilling: true), cancellationToken).ConfigureAwait(false);
            workload.running[ShippingEndpoint] = await StartHostAsync(workload.Configure(ShippingEndpoint, rabbitMqConnectionString, isBilling: false), cancellationToken).ConfigureAwait(false);

            var clientConfiguration = workload.Configure("Client", rabbitMqConnectionString, isBilling: false, sendOnly: true);
            workload.clientHost = await StartHostAsync(clientConfiguration, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await workload.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return workload;
    }

    /// <summary>Sends a charge to Billing and returns the NServiceBus message id, so a test can recognise the message in ServiceControl.</summary>
    public Task<Guid> SendChargeAsync(Guid orderId, string customer = "Contoso") =>
        SendToBillingAsync(new ChargeCustomer { OrderId = orderId, Customer = customer });

    public Task<Guid> StartOrderAsync(Guid orderId) => SendToBillingAsync(new StartOrder { OrderId = orderId });

    public Task<Guid> CompleteOrderAsync(Guid orderId) => SendToBillingAsync(new CompleteOrder { OrderId = orderId });

    /// <summary>Starts an extra endpoint that only reports heartbeats, for tests that need an endpoint they can stop again.</summary>
    public async Task StartHeartbeatEndpointAsync(string name, CancellationToken cancellationToken = default) =>
        running[name] = await StartHostAsync(Configure(name, rabbitMqConnectionString, isBilling: false), cancellationToken).ConfigureAwait(false);

    /// <summary>Stops an endpoint so it stops reporting; its heartbeat then goes dead after ServiceControl's grace period.</summary>
    public async Task StopEndpointAsync(string name)
    {
        if (running.Remove(name, out var host))
        {
            await StopHostAsync(host).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in running.Values)
        {
            await StopHostAsync(host).ConfigureAwait(false);
        }

        running.Clear();

        if (clientHost is not null)
        {
            await StopHostAsync(clientHost).ConfigureAwait(false);
            clientHost = null;
        }

        try
        {
            Directory.Delete(sagaDirectory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // The saga store was never created.
        }
        catch (IOException)
        {
            // Best effort: a temp directory left behind is harmless.
        }
    }

    /// <summary>Each endpoint gets its own generic host, the pattern NServiceBus 10 recommends, so it can be stopped on its own.</summary>
    static async Task<IHost> StartHostAsync(EndpointConfiguration configuration, CancellationToken cancellationToken)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddNServiceBusEndpoint(configuration);

        var host = builder.Build();
        await host.StartAsync(cancellationToken).ConfigureAwait(false);
        return host;
    }

    static async Task StopHostAsync(IHost host)
    {
        await host.StopAsync().ConfigureAwait(false);
        host.Dispose();
    }

    async Task<Guid> SendToBillingAsync(object message)
    {
        var host = clientHost ?? throw new ObjectDisposedException(nameof(Workload));
        var messageId = Guid.NewGuid();
        var options = new SendOptions();
        options.SetDestination(BillingEndpoint);
        options.SetMessageId(messageId.ToString());
        await host.Services.GetRequiredService<IMessageSession>().Send(message, options).ConfigureAwait(false);
        return messageId;
    }

    EndpointConfiguration Configure(string name, string rabbitMqConnectionString, bool isBilling, bool sendOnly = false)
    {
        var configuration = new EndpointConfiguration(name);

        if (learningTransportDirectory is not null)
        {
            configuration.UseTransport(new LearningTransport { StorageDirectory = learningTransportDirectory });
        }
        else
        {
            var transport = new RabbitMQTransport(RoutingTopology.Conventional(QueueType.Quorum), rabbitMqConnectionString);
            if (managementApiUrl is not null)
            {
                transport.ManagementApiConfiguration = new ManagementApiConfiguration(managementApiUrl, "guest", "guest");
            }

            configuration.UseTransport(transport);
        }

        configuration.UseSerialization<SystemJsonSerializer>();
        configuration.UsePersistence<LearningPersistence>().SagaStorageDirectory(Path.Combine(sagaDirectory, name));
        configuration.EnableInstallers();

        // Fail fast: no retries, so a failing message reaches ServiceControl's error queue on the first attempt.
        var recoverability = configuration.Recoverability();
        recoverability.Immediate(settings => settings.NumberOfRetries(0));
        recoverability.Delayed(settings => settings.NumberOfRetries(0));

        configuration.SendFailedMessagesTo(ErrorQueue);
        configuration.AuditProcessedMessagesTo(AuditQueue);

        if (sendOnly)
        {
            // A send-only endpoint processes nothing, so it has no heartbeat or metrics to report.
            configuration.SendOnly();
        }
        else
        {
            configuration.SendHeartbeatTo(ServiceControlQueue, frequency: TimeSpan.FromSeconds(2), timeToLive: TimeSpan.FromSeconds(8));
            configuration.EnableMetrics().SendMetricDataToServiceControl(MonitoringQueue, TimeSpan.FromSeconds(2));
        }

        if (isBilling)
        {
            configuration.AuditSagaStateChanges(AuditQueue);
            configuration.ReportCustomChecksTo(ServiceControlQueue);
        }
        else
        {
            // Only Billing has behaviour; the other endpoints must not pick up its handlers, saga or custom check by scanning.
            configuration.AssemblyScanner().ExcludeTypes(typeof(ChargeCustomerHandler), typeof(OrderPolicy), typeof(DependencyCheck));
        }

        return configuration;
    }
}
