using System.Net;
using Cjoergensen.ServiceControl.Mcp.IntegrationTests.Platform;

namespace Cjoergensen.ServiceControl.Mcp.IntegrationTests;

/// <summary>What the HTTP host does when it is configured unsafely, and its behaviour as a local development server. No platform is needed.</summary>
public sealed class HttpHostStartupTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static readonly Dictionary<string, string> Anonymous = new() { ["Http__AllowAnonymous"] = "true" };

    [Fact]
    public async Task Anonymous_access_is_refused_when_the_server_listens_beyond_loopback()
    {
        var (exit, output) = await HttpHost.StartExpectingRefusalAsync(Anonymous, "http://0.0.0.0:5081", Ct);

        Assert.Equal(1, exit);
        Assert.Contains("Anonymous access is only allowed on loopback", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_bare_port_setting_counts_as_every_interface_for_the_anonymous_check()
    {
        var settings = new Dictionary<string, string>(Anonymous);
        var (exit, output) = await HttpHost.StartExpectingRefusalAsync(settings, "http://+:5082", Ct);

        Assert.Equal(1, exit);
        Assert.Contains("Anonymous access is only allowed on loopback", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_secured_server_must_be_told_which_audience_its_callers_tokens_are_for()
    {
        var (exit, output) = await HttpHost.StartExpectingRefusalAsync(
            new Dictionary<string, string> { ["Http__Authority"] = "https://idp.example/realms/x" }, "http://127.0.0.1:5083", Ct);

        Assert.Equal(1, exit);
        Assert.Contains("Http:Audience is not set", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bearer_tokens_are_not_accepted_over_plain_http_beyond_loopback()
    {
        var settings = new Dictionary<string, string> { ["Http__Authority"] = "https://idp.example/realms/x", ["Http__Audience"] = "mcp", ["Http__AllowedHosts__0"] = "mcp.example" };

        var (exit, output) = await HttpHost.StartExpectingRefusalAsync(settings, "http://0.0.0.0:5084", Ct);

        Assert.Equal(1, exit);
        Assert.Contains("expose callers' bearer tokens", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Listening_beyond_loopback_requires_the_allowed_host_names()
    {
        var settings = new Dictionary<string, string> { ["Http__Authority"] = "https://idp.example/realms/x", ["Http__Audience"] = "mcp", ["Http__AllowInsecureTransport"] = "true" };

        var (exit, output) = await HttpHost.StartExpectingRefusalAsync(settings, "http://0.0.0.0:5085", Ct);

        Assert.Equal(1, exit);
        Assert.Contains("Http:AllowedHosts", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Token_exchange_cannot_be_combined_with_anonymous_access()
    {
        var settings = new Dictionary<string, string>(Anonymous)
        {
            ["Auth__Mode"] = "TokenExchange",
            ["Auth__ClientId"] = "x",
            ["Auth__ClientSecret"] = "y"
        };

        var (exit, output) = await HttpHost.StartExpectingRefusalAsync(settings, "http://127.0.0.1:5086", Ct);

        Assert.Equal(1, exit);
        Assert.Contains("cannot be used with Http:AllowAnonymous", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_local_development_server_answers_health_checks_serves_mcp_and_rejects_foreign_host_names()
    {
        await using var host = await HttpHost.StartAsync(Anonymous, Ct);

        using var health = await host.SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri(host.BaseAddress, "healthz")), Ct);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        await using var mcp = await host.ConnectAsync(bearerToken: null, Ct);
        var tools = await mcp.ToolNamesAsync(Ct);
        Assert.Contains("get_health_overview", tools);
        Assert.Contains("list_failed_messages", tools);

        // A request whose Host header is not one of the server's own names is refused: the defence against DNS rebinding.
        using var foreign = new HttpRequestMessage(HttpMethod.Get, new Uri(host.BaseAddress, "healthz"));
        foreign.Headers.Host = "evil.example";
        using var refused = await host.SendAsync(foreign, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }
}
