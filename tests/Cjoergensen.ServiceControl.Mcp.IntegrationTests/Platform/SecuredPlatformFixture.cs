using System.Globalization;
using System.Net;
using Cjoergensen.ServiceControl.Mcp.Client;
using Cjoergensen.ServiceControl.Mcp.Tests.Pki;
using Cjoergensen.ServiceControl.Mcp.TestWorkload;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Cjoergensen.ServiceControl.Mcp.IntegrationTests.Platform;

/// <summary>
/// A ServiceControl platform configured the way a security-conscious installation is: HTTPS with a private certificate authority, OIDC
/// authentication against a real identity provider (Keycloak), and role-based authorization (reader / writer / admin).
/// <para>
/// The hard part is that the issuer URL inside tokens must be identical for ServiceControl (in its container), the tests and the MCP
/// server (on the host). Every container therefore shares Keycloak's network namespace, so <c>https://localhost:PORT</c> means the same
/// thing everywhere, with no DNS tricks, no /etc/hosts edits and no need for internet access. Only Keycloak's port must be fixed; set
/// <c>SCMCP_IT_KEYCLOAK_PORT</c> if the default is taken.
/// </para>
/// </summary>
public sealed class SecuredPlatformFixture : IAsyncLifetime, IPlatform
{
    const string KeycloakImage = "quay.io/keycloak/keycloak:26.7.4";
    const string Database = "http://servicecontrol-db:8080";
    const string InsidePfx = "/usr/share/ParticularSoftware/certificate.pfx";
    const string InsideCaBundle = "/etc/ssl/certs/ca-bundle.crt";
    const string InsideKeycloakPfx = "/opt/keycloak/conf/server.pfx";

    readonly List<IAsyncDisposable> owned = [];
    TestPki pki = null!;

    public string CaCertificatePath { get; private set; } = string.Empty;

    public string Authority { get; private set; } = string.Empty;

    public string PrimaryUrl { get; private set; } = string.Empty;

    public string? MonitoringUrl { get; private set; }

    public KeycloakClient Keycloak { get; private set; } = null!;

    public Workload Workload { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var tag = Environment.GetEnvironmentVariable("SCMCP_IT_SC_TAG") ?? PlatformFixture.DefaultTag;
        var keycloakPort = int.Parse(Environment.GetEnvironmentVariable("SCMCP_IT_KEYCLOAK_PORT") ?? "18443", CultureInfo.InvariantCulture);

        pki = TestPki.Create();
        CaCertificatePath = Path.Combine(Path.GetTempPath(), "scmcp-ca-" + Guid.NewGuid().ToString("N") + ".pem");
        await File.WriteAllTextAsync(CaCertificatePath, pki.CaPem, ct);
        owned.Add(new FileCleanup(CaCertificatePath));

        Authority = $"https://localhost:{keycloakPort}/realms/{KeycloakRealm.Name}";
        Keycloak = new KeycloakClient(Authority, CaCertificatePath);
        var folder = SharedFolder.Create(owned);

        var network = new DotNet.Testcontainers.Builders.NetworkBuilder().Build();
        owned.Add(network);
        await network.CreateAsync(ct);

        var database = new ContainerBuilder($"particular/servicecontrol-ravendb:{tag}")
            .WithNetwork(network)
            .WithNetworkAliases("servicecontrol-db")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilContainerIsHealthy())
            .Build();
        owned.Add(database);
        await database.StartAsync(ct);

        // Keycloak owns the network namespace; the ServiceControl instances join it, so they all see the identity provider at localhost.
        // The ServiceControl ports are published here too, because the containers that share this namespace cannot publish their own.
        var keycloak = new ContainerBuilder(KeycloakImage)
            .WithNetwork(network)
            .WithNetworkAliases("keycloak")
            .WithPortBinding(keycloakPort, keycloakPort)
            .WithPortBinding(33333, assignRandomHostPort: true)
            .WithPortBinding(44444, assignRandomHostPort: true)
            .WithPortBinding(33633, assignRandomHostPort: true)
            .WithEnvironment("KC_BOOTSTRAP_ADMIN_USERNAME", "admin")
            .WithEnvironment("KC_BOOTSTRAP_ADMIN_PASSWORD", "admin")
            .WithEnvironment("KC_HTTPS_PORT", keycloakPort.ToString(CultureInfo.InvariantCulture))
            .WithEnvironment("KC_HTTPS_KEY_STORE_FILE", InsideKeycloakPfx)
            .WithEnvironment("KC_HTTPS_KEY_STORE_PASSWORD", pki.PfxPassword)
            .WithEnvironment("KC_HTTPS_KEY_STORE_TYPE", "PKCS12")
            .WithEnvironment("KC_HOSTNAME", $"https://localhost:{keycloakPort}")
            .WithResourceMapping(pki.ServerPfx, InsideKeycloakPfx)
            .WithResourceMapping(System.Text.Encoding.UTF8.GetBytes(KeycloakRealm.Json()), "/opt/keycloak/data/import/realm.json")
            .WithCommand("start-dev", "--import-realm")
            .Build();
        owned.Add(keycloak);
        await keycloak.StartAsync(ct);
        await WaitForAsync($"{Authority}/.well-known/openid-configuration", TimeSpan.FromMinutes(3), "Keycloak", ct);

        var primary = ServiceControl(keycloak, $"particular/servicecontrol:{tag}", folder, new Dictionary<string, string>
        {
            ["RAVENDB_CONNECTIONSTRING"] = Database,
            ["REMOTEINSTANCES"] = """[{"api_uri":"https://localhost:44444/api"}]""",
            ["SERVICECONTROL_HEARTBEATGRACEPERIOD"] = PlatformFixture.HeartbeatGracePeriod.ToString("c", CultureInfo.InvariantCulture),
            ["SERVICECONTROL_HTTPS_ENABLED"] = "true",
            ["SERVICECONTROL_HTTPS_CERTIFICATEPATH"] = InsidePfx,
            ["SERVICECONTROL_HTTPS_CERTIFICATEPASSWORD"] = pki.PfxPassword,
            ["SERVICECONTROL_AUTHENTICATION_ENABLED"] = "true",
            ["SERVICECONTROL_AUTHENTICATION_AUTHORITY"] = Authority,
            ["SERVICECONTROL_AUTHENTICATION_AUDIENCE"] = KeycloakRealm.Audience,
            ["SERVICECONTROL_AUTHENTICATION_ROLEBASEDAUTHORIZATIONENABLED"] = "true",
            ["SERVICECONTROL_AUTHENTICATION_ROLESCLAIM"] = "realm_access.roles",
            ["SERVICECONTROL_AUTHENTICATION_SERVICEPULSE_CLIENTID"] = "servicepulse",
            ["SERVICECONTROL_AUTHENTICATION_SERVICEPULSE_AUTHORITY"] = Authority,
            ["SERVICECONTROL_AUTHENTICATION_SERVICEPULSE_APISCOPES"] = """["servicecontrol"]"""
        });
        var audit = ServiceControl(keycloak, $"particular/servicecontrol-audit:{tag}", folder, new Dictionary<string, string>
        {
            ["RAVENDB_CONNECTIONSTRING"] = Database,
            ["SERVICECONTROLQUEUEADDRESS"] = Workload.ServiceControlQueue,
            ["SERVICECONTROL_AUDIT_HTTPS_ENABLED"] = "true",
            ["SERVICECONTROL_AUDIT_HTTPS_CERTIFICATEPATH"] = InsidePfx,
            ["SERVICECONTROL_AUDIT_HTTPS_CERTIFICATEPASSWORD"] = pki.PfxPassword,
            ["SERVICECONTROL_AUDIT_AUTHENTICATION_ENABLED"] = "true",
            ["SERVICECONTROL_AUDIT_AUTHENTICATION_AUTHORITY"] = Authority,
            ["SERVICECONTROL_AUDIT_AUTHENTICATION_AUDIENCE"] = KeycloakRealm.Audience,
            ["SERVICECONTROL_AUDIT_AUTHENTICATION_ROLEBASEDAUTHORIZATIONENABLED"] = "true",
            ["SERVICECONTROL_AUDIT_AUTHENTICATION_ROLESCLAIM"] = "realm_access.roles"
        });
        var monitoring = ServiceControl(keycloak, $"particular/servicecontrol-monitoring:{tag}", folder, new Dictionary<string, string>
        {
            ["MONITORING_HTTPS_ENABLED"] = "true",
            ["MONITORING_HTTPS_CERTIFICATEPATH"] = InsidePfx,
            ["MONITORING_HTTPS_CERTIFICATEPASSWORD"] = pki.PfxPassword,
            ["MONITORING_AUTHENTICATION_ENABLED"] = "true",
            ["MONITORING_AUTHENTICATION_AUTHORITY"] = Authority,
            ["MONITORING_AUTHENTICATION_AUDIENCE"] = KeycloakRealm.Audience,
            ["MONITORING_AUTHENTICATION_ROLEBASEDAUTHORIZATIONENABLED"] = "true",
            ["MONITORING_AUTHENTICATION_ROLESCLAIM"] = "realm_access.roles"
        });
        owned.Add(primary);
        owned.Add(audit);
        owned.Add(monitoring);
        await Task.WhenAll(primary.StartAsync(ct), audit.StartAsync(ct), monitoring.StartAsync(ct));

        PrimaryUrl = $"https://localhost:{keycloak.GetMappedPublicPort(33333)}";
        MonitoringUrl = $"https://localhost:{keycloak.GetMappedPublicPort(33633)}";
        await WaitForAsync($"{PrimaryUrl}/api", TimeSpan.FromMinutes(3), "the ServiceControl primary instance", ct);
        await WaitForAsync($"https://localhost:{keycloak.GetMappedPublicPort(44444)}/api", TimeSpan.FromMinutes(3), "the ServiceControl audit instance", ct);
        await WaitForAsync($"{MonitoringUrl}/", TimeSpan.FromMinutes(3), "the ServiceControl monitoring instance", ct);

        Workload = await Workload.StartAsync(string.Empty, managementApiUrl: null, learningTransportDirectory: folder, ct);
        owned.Add(Workload);
    }

    public async ValueTask DisposeAsync()
    {
        for (var i = owned.Count - 1; i >= 0; i--)
        {
            await owned[i].DisposeAsync();
        }

        Keycloak?.Dispose();
        pki?.Dispose();
    }

    IContainer ServiceControl(IContainer keycloak, string image, string folder, Dictionary<string, string> environment) =>
        new ContainerBuilder(image)
            .WithEnvironment(new Dictionary<string, string> { ["TRANSPORTTYPE"] = "LearningTransport", ["CONNECTIONSTRING"] = "/data", ["SSL_CERT_FILE"] = InsideCaBundle })
            .WithEnvironment(environment)
            .WithCommand("--setup-and-run")
            .WithBindMount(folder, "/data")
            .WithResourceMapping(pki.ServerPfx, InsidePfx)
            .WithResourceMapping(System.Text.Encoding.UTF8.GetBytes(pki.CaPem), InsideCaBundle)
            // Testcontainers always creates the host configuration; the annotation just does not promise it.
            .WithCreateParameterModifier(parameters => parameters.HostConfig!.NetworkMode = $"container:{keycloak.Id}")
            .Build();

    /// <summary>Polls a URL, over TLS trusting the test CA, until it answers with success.</summary>
    async Task WaitForAsync(string url, TimeSpan timeout, string what, CancellationToken ct)
    {
        using var http = new HttpClient(TlsTrust.CreateHandler(CaCertificatePath)) { Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow + timeout;
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var response = await http.GetAsync(url, ct);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }

                last = new InvalidOperationException($"HTTP {(int)response.StatusCode}");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                last = ex;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        throw new TimeoutException($"{what} did not become ready at {url} within {timeout.TotalSeconds:0}s. Last problem: {last?.Message}");
    }

    sealed class FileCleanup(string path) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            File.Delete(path);
            return ValueTask.CompletedTask;
        }
    }
}

[CollectionDefinition(Name)]
public sealed class SecuredPlatformCollectionDefinition : ICollectionFixture<SecuredPlatformFixture>
{
    public const string Name = "Secured ServiceControl platform";
}
