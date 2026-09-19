using System.Net;
using System.Text;
using Cjoergensen.ServiceControl.Mcp.Auth;
using Cjoergensen.ServiceControl.Mcp.Client;
using Cjoergensen.ServiceControl.Mcp.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using static Cjoergensen.ServiceControl.Mcp.Tests.Support.TestKit;

namespace Cjoergensen.ServiceControl.Mcp.Tests;

public class AuthenticationTests
{
    /// <summary>Hands out a sequence of tokens, like an identity provider issuing a new one after the first is rejected.</summary>
    sealed class SequenceTokenProvider(params string[] tokens) : ITokenProvider
    {
        int index;

        public int Invalidations { get; private set; }

        public ValueTask<string?> GetTokenAsync(CancellationToken cancellationToken) => ValueTask.FromResult<string?>(tokens[Math.Min(index, tokens.Length - 1)]);

        public void Invalidate()
        {
            Invalidations++;
            index++;
        }
    }

    static HttpClient ClientFor(HttpMessageHandler inner, ITokenProvider tokens, string baseUrl, Action<ServiceControlMcpOptions>? configure = null) =>
        new(new BearerTokenHandler(tokens, Wrap(Options(configure))) { InnerHandler = inner }) { BaseAddress = new Uri(baseUrl) };

    [Fact]
    public async Task Bearer_token_is_added_to_requests()
    {
        var fake = new FakeServiceControl().Route("GET", "/api/", "{}");
        using var http = ClientFor(fake, new StaticTokenProvider("abc"), "https://sc.example/api/");

        await http.GetAsync(new Uri("heartbeats/stats", UriKind.Relative), Ct);

        Assert.Equal("Bearer abc", fake.Requests[0].Authorization);
    }

    [Fact]
    public async Task No_credentials_means_no_authorization_header()
    {
        var fake = new FakeServiceControl().Route("GET", "/api/", "{}");
        using var http = ClientFor(fake, new NoTokenProvider(), "http://sc.internal:33333/api/");

        await http.GetAsync(new Uri("heartbeats/stats", UriKind.Relative), Ct);

        Assert.Null(fake.Requests[0].Authorization);
    }

    [Fact]
    public async Task A_rejected_token_is_replaced_and_the_request_retried_once()
    {
        var fake = new FakeServiceControl().Route("POST", "/api/", request =>
            request.Headers.Authorization?.Parameter == "fresh"
                ? FakeServiceControl.Json("{}")
                : FakeServiceControl.Json("{}", HttpStatusCode.Unauthorized));
        var tokens = new SequenceTokenProvider("stale", "fresh");
        using var http = ClientFor(fake, tokens, "https://sc.example/api/");

        using var content = new StringContent("[\"id-1\"]", Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(new Uri("errors/retry", UriKind.Relative), content, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, tokens.Invalidations);
        Assert.Equal(2, fake.Requests.Count);
        Assert.Equal("Bearer stale", fake.Requests[0].Authorization);
        Assert.Equal("Bearer fresh", fake.Requests[1].Authorization);
        Assert.Equal("[\"id-1\"]", fake.Requests[1].Body);
    }

    [Fact]
    public async Task A_token_that_is_still_rejected_after_refresh_surfaces_the_401()
    {
        var fake = new FakeServiceControl().Route("GET", "/api/", "{}", HttpStatusCode.Unauthorized);
        using var http = ClientFor(fake, new SequenceTokenProvider("a", "b"), "https://sc.example/api/");

        using var response = await http.GetAsync(new Uri("heartbeats/stats", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(2, fake.Requests.Count);
    }

    [Fact]
    public async Task Tokens_are_never_sent_over_plain_http_to_a_remote_host()
    {
        var fake = new FakeServiceControl().Route("GET", "/api/", "{}");
        using var http = ClientFor(fake, new StaticTokenProvider("secret"), "http://sc.example:33333/api/");

        var exception = await Assert.ThrowsAsync<ServiceControlApiException>(() => http.GetAsync(new Uri("heartbeats/stats", UriKind.Relative), Ct));

        Assert.Equal(ServiceControlFailureKind.InsecureTransport, exception.Kind);
        Assert.Empty(fake.Requests);
        Assert.DoesNotContain("secret", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://localhost:33333/api/")]
    [InlineData("http://127.0.0.1:33333/api/")]
    [InlineData("https://sc.example/api/")]
    public async Task Tokens_may_be_sent_to_loopback_and_https(string baseUrl)
    {
        var fake = new FakeServiceControl().Route("GET", "/api/", "{}");
        using var http = ClientFor(fake, new StaticTokenProvider("t"), baseUrl);

        await http.GetAsync(new Uri("heartbeats/stats", UriKind.Relative), Ct);

        Assert.Equal("Bearer t", fake.Requests[0].Authorization);
    }

    [Fact]
    public async Task Plain_http_to_a_remote_host_is_allowed_only_by_explicit_opt_in()
    {
        var fake = new FakeServiceControl().Route("GET", "/api/", "{}");
        using var http = ClientFor(fake, new StaticTokenProvider("t"), "http://sc.example:33333/api/", o => o.AllowInsecureTransport = true);

        await http.GetAsync(new Uri("heartbeats/stats", UriKind.Relative), Ct);

        Assert.Single(fake.Requests);
    }

    [Theory]
    [InlineData("plain-token\n", "plain-token")]
    [InlineData("  spaced  ", "spaced")]
    [InlineData("""{"accessToken":"from-az","expiresOn":"x"}""", "from-az")]
    [InlineData("""{"access_token":"oauth"}""", "oauth")]
    public void Token_command_output_is_understood(string output, string expected) =>
        Assert.Equal(expected, CommandTokenProvider.ExtractToken(output));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("""{"unexpected":"shape"}""")]
    public void Unusable_token_command_output_is_rejected(string output) =>
        Assert.Throws<TokenAcquisitionException>(() => CommandTokenProvider.ExtractToken(output));

    [Fact]
    public void Jwt_expiry_is_read_without_validating_the_token()
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"exp":1790000000}""")).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790000000), JwtExpiry.TryRead($"header.{payload}.sig"));
        Assert.Null(JwtExpiry.TryRead("not-a-jwt"));
        Assert.Null(JwtExpiry.TryRead("a.@@@.c"));
    }

    [Fact]
    public async Task Token_command_is_cached_until_invalidated()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses /bin/echo.");

        using var provider = new CommandTokenProvider("/bin/echo", ["cmd-token"], TimeProvider.System, NullLogger<CommandTokenProvider>.Instance);

        Assert.Equal("cmd-token", await provider.GetTokenAsync(Ct));
        Assert.Equal("cmd-token", await provider.GetTokenAsync(Ct));

        provider.Invalidate();
        Assert.Equal("cmd-token", await provider.GetTokenAsync(Ct));
    }

    [Fact]
    public async Task A_missing_token_command_produces_a_clear_error()
    {
        using var provider = new CommandTokenProvider("/definitely/not/a/command", [], TimeProvider.System, NullLogger<CommandTokenProvider>.Instance);

        var exception = await Assert.ThrowsAsync<TokenAcquisitionException>(async () => await provider.GetTokenAsync(Ct));

        Assert.Contains("could not be started", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failing_token_command_reports_its_exit_code()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses /usr/bin/false.");

        using var provider = new CommandTokenProvider("/usr/bin/false", [], TimeProvider.System, NullLogger<CommandTokenProvider>.Instance);

        var exception = await Assert.ThrowsAsync<TokenAcquisitionException>(async () => await provider.GetTokenAsync(Ct));

        Assert.Contains("exit code 1", exception.Message, StringComparison.Ordinal);
    }
}
