using Cjoergensen.ServiceControl.Mcp.TestWorkload;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Testcontainers.RabbitMq;

namespace Cjoergensen.ServiceControl.Mcp.IntegrationTests.Platform;

/// <summary>
/// A real ServiceControl platform plus the NServiceBus workload that feeds it, shared by every test in the collection.
/// <para>
/// By default the platform is started in containers using ServiceControl's file-system learning transport: no broker, so it is the simplest
/// and fastest topology, and the transport is not what these tests exercise. Set <c>SCMCP_IT_TRANSPORT=rabbitmq</c> for a RabbitMQ broker
/// like Particular's PlatformContainerExamples. To iterate quickly against a
/// platform that is already running, set <c>SCMCP_IT_URL</c> (primary instance), <c>SCMCP_IT_RABBITMQ</c> (broker connection string reachable
/// from this machine) and optionally <c>SCMCP_IT_MONITORING_URL</c> and <c>SCMCP_IT_RABBITMQ_MANAGEMENT</c> (when the broker's management API is not on its default port). Set <c>SCMCP_IT_SC_TAG</c> to test another ServiceControl version.
/// </para>
/// </summary>
public sealed class PlatformFixture : IAsyncLifetime, IPlatform
{
    // The version the suite was written against. Pinned so a new ServiceControl release cannot break CI unannounced.
    internal const string DefaultTag = "6.21.0";

    const string TransportType = "RabbitMQ.QuorumConventionalRouting";
    const string InternalBroker = "host=rabbitmq;username=guest;password=guest";
    const string InternalDatabase = "http://servicecontrol-db:8080";
    const string ServiceControlQueue = TestWorkload.Workload.ServiceControlQueue;

    /// <summary>Heartbeats older than this count as dead. Short so the dead-heartbeat test does not wait the default 40 seconds.</summary>
    public static readonly TimeSpan HeartbeatGracePeriod = TimeSpan.FromSeconds(10);

    readonly List<IAsyncDisposable> owned = [];

    public string PrimaryUrl { get; private set; } = string.Empty;

    public string? MonitoringUrl { get; private set; }

    public Workload Workload { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        string brokerForWorkload;
        string? managementApiUrl;
        string? learningDirectory = null;

        var externalUrl = Environment.GetEnvironmentVariable("SCMCP_IT_URL");
        if (!string.IsNullOrWhiteSpace(externalUrl))
        {
            PrimaryUrl = externalUrl;
            MonitoringUrl = Environment.GetEnvironmentVariable("SCMCP_IT_MONITORING_URL");
            brokerForWorkload = Environment.GetEnvironmentVariable("SCMCP_IT_RABBITMQ")
                ?? throw new InvalidOperationException("SCMCP_IT_RABBITMQ must be set when SCMCP_IT_URL points at an existing platform.");
            managementApiUrl = Environment.GetEnvironmentVariable("SCMCP_IT_RABBITMQ_MANAGEMENT");
        }
        else
        {
            var tag = Environment.GetEnvironmentVariable("SCMCP_IT_SC_TAG") ?? DefaultTag;
            if (!string.Equals(Environment.GetEnvironmentVariable("SCMCP_IT_TRANSPORT"), "rabbitmq", StringComparison.OrdinalIgnoreCase))
            {
                learningDirectory = SharedFolder.Create(owned);
                (PrimaryUrl, MonitoringUrl) = await StartContainersWithLearningTransportAsync(tag, learningDirectory, ct);
                brokerForWorkload = string.Empty;
                managementApiUrl = null;
            }
            else
            {
                (PrimaryUrl, MonitoringUrl, brokerForWorkload, managementApiUrl) = await StartContainersAsync(tag, ct);
            }
        }

        Workload = await Workload.StartAsync(brokerForWorkload, managementApiUrl, learningDirectory, ct);
        owned.Add(Workload);
    }

    public async ValueTask DisposeAsync()
    {
        // Stop the endpoints first, then the platform, in reverse order of creation.
        for (var i = owned.Count - 1; i >= 0; i--)
        {
            await owned[i].DisposeAsync();
        }
    }

    async Task<(string Primary, string Monitoring, string Broker, string ManagementApi)> StartContainersAsync(string tag, CancellationToken ct)
    {
        var network = new NetworkBuilder().Build();
        owned.Add(network);
        await network.CreateAsync(ct);

        // guest/guest, as in Particular's compose files: ServiceControl also uses these to reach the broker's management API.
        var rabbit = new RabbitMqBuilder("rabbitmq:3-management")
            .WithUsername("guest")
            .WithPassword("guest")
            .WithPortBinding(15672, assignRandomHostPort: true) // management API, used by the transport to verify the broker
            .WithNetwork(network)
            .WithNetworkAliases("rabbitmq")
            .Build();
        var database = new ContainerBuilder($"particular/servicecontrol-ravendb:{tag}")
            .WithNetwork(network)
            .WithNetworkAliases("servicecontrol-db")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilContainerIsHealthy())
            .Build();
        owned.Add(rabbit);
        owned.Add(database);
        await Task.WhenAll(rabbit.StartAsync(ct), database.StartAsync(ct));

        var primary = ServiceControlContainer($"particular/servicecontrol:{tag}", network, "servicecontrol", 33333, "/api", new Dictionary<string, string>
        {
            ["RAVENDB_CONNECTIONSTRING"] = InternalDatabase,
            ["REMOTEINSTANCES"] = """[{"api_uri":"http://servicecontrol-audit:44444/api"}]""",
            ["SERVICECONTROL_HEARTBEATGRACEPERIOD"] = HeartbeatGracePeriod.ToString("c", System.Globalization.CultureInfo.InvariantCulture)
        });
        var audit = ServiceControlContainer($"particular/servicecontrol-audit:{tag}", network, "servicecontrol-audit", 44444, "/api", new Dictionary<string, string>
        {
            ["RAVENDB_CONNECTIONSTRING"] = InternalDatabase,
            ["SERVICECONTROLQUEUEADDRESS"] = ServiceControlQueue
        });
        var monitoring = ServiceControlContainer($"particular/servicecontrol-monitoring:{tag}", network, "servicecontrol-monitoring", 33633, "/", []);
        owned.Add(primary);
        owned.Add(audit);
        owned.Add(monitoring);
        await Task.WhenAll(primary.StartAsync(ct), audit.StartAsync(ct), monitoring.StartAsync(ct));

        return (
            $"http://{primary.Hostname}:{primary.GetMappedPublicPort(33333)}",
            $"http://{monitoring.Hostname}:{monitoring.GetMappedPublicPort(33633)}",
            rabbit.GetConnectionString(),
            $"http://{rabbit.Hostname}:{rabbit.GetMappedPublicPort(15672)}");
    }

    /// <summary>
    /// The default topology: no broker. ServiceControl's own learning transport (file-system based, non-production) reads the folder the
    /// workload writes to. Relies on file events and write permissions crossing a Docker bind mount.
    /// </summary>
    async Task<(string Primary, string Monitoring)> StartContainersWithLearningTransportAsync(string tag, string folder, CancellationToken ct)
    {
        var network = new NetworkBuilder().Build();
        owned.Add(network);
        await network.CreateAsync(ct);

        var database = new ContainerBuilder($"particular/servicecontrol-ravendb:{tag}")
            .WithNetwork(network)
            .WithNetworkAliases("servicecontrol-db")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilContainerIsHealthy())
            .Build();
        owned.Add(database);
        await database.StartAsync(ct);

        var learning = new Dictionary<string, string> { ["TRANSPORTTYPE"] = "LearningTransport", ["CONNECTIONSTRING"] = "/data" };
        var primary = ServiceControlContainer($"particular/servicecontrol:{tag}", network, "servicecontrol", 33333, "/api", learning.Concat(new Dictionary<string, string>
        {
            ["RAVENDB_CONNECTIONSTRING"] = InternalDatabase,
            ["REMOTEINSTANCES"] = """[{"api_uri":"http://servicecontrol-audit:44444/api"}]""",
            ["SERVICECONTROL_HEARTBEATGRACEPERIOD"] = HeartbeatGracePeriod.ToString("c", System.Globalization.CultureInfo.InvariantCulture)
        }).ToDictionary(), folder);
        var audit = ServiceControlContainer($"particular/servicecontrol-audit:{tag}", network, "servicecontrol-audit", 44444, "/api", learning.Concat(new Dictionary<string, string>
        {
            ["RAVENDB_CONNECTIONSTRING"] = InternalDatabase,
            ["SERVICECONTROLQUEUEADDRESS"] = ServiceControlQueue
        }).ToDictionary(), folder);
        var monitoring = ServiceControlContainer($"particular/servicecontrol-monitoring:{tag}", network, "servicecontrol-monitoring", 33633, "/", learning, folder);
        owned.Add(primary);
        owned.Add(audit);
        owned.Add(monitoring);
        await Task.WhenAll(primary.StartAsync(ct), audit.StartAsync(ct), monitoring.StartAsync(ct));

        return (
            $"http://{primary.Hostname}:{primary.GetMappedPublicPort(33333)}",
            $"http://{monitoring.Hostname}:{monitoring.GetMappedPublicPort(33633)}");
    }

    static IContainer ServiceControlContainer(
        string image, INetwork network, string alias, ushort port, string readinessPath, Dictionary<string, string> environment, string? bindMountFolder = null)
    {
        var builder = new ContainerBuilder(image)
            .WithNetwork(network)
            .WithNetworkAliases(alias)
            .WithPortBinding(port, assignRandomHostPort: true)
            .WithEnvironment(bindMountFolder is null
                ? new Dictionary<string, string> { ["TRANSPORTTYPE"] = TransportType, ["CONNECTIONSTRING"] = InternalBroker }
                : [])
            .WithEnvironment(environment)
            .WithCommand("--setup-and-run")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(port).ForPath(readinessPath)));

        return (bindMountFolder is null ? builder : builder.WithBindMount(bindMountFolder, "/data")).Build();
    }
}

[CollectionDefinition(Name)]
public sealed class PlatformCollectionDefinition : ICollectionFixture<PlatformFixture>
{
    public const string Name = "ServiceControl platform";
}
