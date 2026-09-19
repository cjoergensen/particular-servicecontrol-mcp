using Cjoergensen.ServiceControl.Mcp.Client;
using Microsoft.Extensions.Options;

namespace Cjoergensen.ServiceControl.Mcp.Tests.Support;

public static class TestKit
{
    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static ServiceControlMcpOptions Options(Action<ServiceControlMcpOptions>? configure = null)
    {
        var options = new ServiceControlMcpOptions();
        configure?.Invoke(options);
        return options;
    }

    public static IOptions<ServiceControlMcpOptions> Wrap(ServiceControlMcpOptions options) => Microsoft.Extensions.Options.Options.Create(options);

    public static ServiceControlClient PrimaryClient(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:33333/api/") });

    public static MonitoringClient MonitoringClient(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:33633/") });
}

/// <summary>A clock tests control, so time-window arguments are deterministic.</summary>
public sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>A clock the test moves by hand, for behaviour that depends on tokens expiring or polling intervals elapsing.</summary>
public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    DateTimeOffset now = start;

    public override DateTimeOffset GetUtcNow() => now;

    public void Advance(TimeSpan by) => now += by;
}
