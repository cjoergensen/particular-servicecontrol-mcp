using System.Net;
using System.Text;
using System.Text.Json;
using Cjoergensen.ServiceControl.Mcp.Auth;
using Cjoergensen.ServiceControl.Mcp.Tests.Support;
using static Cjoergensen.ServiceControl.Mcp.Tests.Support.TestKit;

namespace Cjoergensen.ServiceControl.Mcp.Tests;

/// <summary>The token providers, against a fake identity provider that behaves like a real one (OIDC discovery, token and device endpoints).</summary>
public class IdentityProviderTests
{
    const string Authority = "https://idp.example/realms/test";

    /// <summary>A minimal OAuth server whose behaviour each test scripts. Records what it was sent.</summary>
    sealed class FakeIdp
    {
        public FakeServiceControl Http { get; } = new();

        public Func<RecordedRequest, HttpResponseMessage> OnToken { get; set; } = _ => Reply(new { access_token = "token-1", expires_in = 300 });

        public Func<RecordedRequest, HttpResponseMessage> OnDevice { get; set; } = _ => Reply(new
        {
            device_code = "dev-1",
            user_code = "ABCD-EFGH",
            verification_uri = $"{Authority}/device",
            verification_uri_complete = $"{Authority}/device?user_code=ABCD-EFGH",
            expires_in = 600,
            interval = 5
        });

        public IEnumerable<RecordedRequest> TokenRequests => Http.Requests.Where(r => r.Uri.AbsolutePath.EndsWith("/token", StringComparison.Ordinal));

        public FakeIdp(bool withDeviceEndpoint = true)
        {
            Http.Route("GET", "/api/authentication/configuration", JsonSerializer.Serialize(new
            {
                enabled = true,
                role_based_authorization_enabled = true,
                authority = Authority,
                audience = "servicecontrol",
                scopes = "servicecontrol openid offline_access"
            }));
            Http.Route("GET", "/realms/test/.well-known/openid-configuration", JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["issuer"] = Authority,
                ["token_endpoint"] = $"{Authority}/token",
                ["device_authorization_endpoint"] = withDeviceEndpoint ? $"{Authority}/device-auth" : null
            }));
            Http.Route("POST", "/realms/test/token", request => OnToken(ToRecord(request)));
            Http.Route("POST", "/realms/test/device-auth", request => OnDevice(ToRecord(request)));
        }

        static RecordedRequest ToRecord(HttpRequestMessage request) =>
            new(request.Method.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), request.Content?.ReadAsStringAsync().GetAwaiter().GetResult());

        public static HttpResponseMessage Reply(object body, HttpStatusCode status = HttpStatusCode.OK) =>
            FakeServiceControl.Json(JsonSerializer.Serialize(body), status);

        public IdentityProviderClient Client(ServiceControlMcpOptions options, out ServiceControlDiscovery discovery)
        {
            var wrapped = Wrap(options);
            discovery = new ServiceControlDiscovery(new HttpClient(Http), wrapped);
            return new IdentityProviderClient(new HttpClient(Http), discovery, wrapped);
        }
    }

    static ServiceControlMcpOptions Options(Action<AuthOptions> configure) =>
        TestKit.Options(o =>
        {
            o.Url = "https://servicecontrol.example";
            configure(o.Auth);
        });

    static string Form(RecordedRequest request, string key) =>
        request.Body!.Split('&').Select(p => p.Split('=', 2)).Where(p => p[0] == key).Select(p => Uri.UnescapeDataString(p[1].Replace('+', ' '))).SingleOrDefault() ?? string.Empty;

    // ---- client credentials -------------------------------------------------------------------------------------

    [Fact]
    public async Task Client_credentials_authenticate_with_http_basic_and_return_the_token()
    {
        var idp = new FakeIdp();
        var options = Options(a => { a.ClientId = "bot"; a.ClientSecret = "s3cr3t/+&"; });
        using var provider = new ClientCredentialsTokenProvider(idp.Client(options, out _), options.Auth, TimeProvider.System);

        var token = await provider.GetTokenAsync(Ct);

        Assert.Equal("token-1", token);
        var request = Assert.Single(idp.TokenRequests);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"bot:{Uri.EscapeDataString("s3cr3t/+&")}")), request.Authorization);
        Assert.Equal("client_credentials", Form(request, "grant_type"));
        Assert.DoesNotContain("s3cr3t", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scope_and_audience_are_sent_only_when_configured()
    {
        var idp = new FakeIdp();
        var options = Options(a => { a.ClientId = "bot"; a.ClientSecret = "x"; a.Scope = "api://sc/.default"; a.Audience = "https://sc"; });
        using var provider = new ClientCredentialsTokenProvider(idp.Client(options, out _), options.Auth, TimeProvider.System);

        await provider.GetTokenAsync(Ct);

        var request = Assert.Single(idp.TokenRequests);
        Assert.Equal("api://sc/.default", Form(request, "scope"));
        Assert.Equal("https://sc", Form(request, "audience"));

        var plain = new FakeIdp();
        var plainOptions = Options(a => { a.ClientId = "bot"; a.ClientSecret = "x"; });
        using var plainProvider = new ClientCredentialsTokenProvider(plain.Client(plainOptions, out _), plainOptions.Auth, TimeProvider.System);
        await plainProvider.GetTokenAsync(Ct);
        Assert.DoesNotContain("scope=", Assert.Single(plain.TokenRequests).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_client_credentials_token_is_cached_until_shortly_before_it_expires_then_renewed()
    {
        var idp = new FakeIdp();
        var calls = 0;
        idp.OnToken = _ => FakeIdp.Reply(new { access_token = $"token-{++calls}", expires_in = 300 });
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var options = Options(a => { a.ClientId = "bot"; a.ClientSecret = "x"; });
        using var provider = new ClientCredentialsTokenProvider(idp.Client(options, out _), options.Auth, clock);

        Assert.Equal("token-1", await provider.GetTokenAsync(Ct));
        clock.Advance(TimeSpan.FromSeconds(200));
        Assert.Equal("token-1", await provider.GetTokenAsync(Ct));

        clock.Advance(TimeSpan.FromSeconds(50)); // 250s in: within the 60s refresh margin of a 300s token
        Assert.Equal("token-2", await provider.GetTokenAsync(Ct));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Invalidating_a_client_credentials_token_fetches_a_new_one()
    {
        var idp = new FakeIdp();
        var calls = 0;
        idp.OnToken = _ => FakeIdp.Reply(new { access_token = $"token-{++calls}", expires_in = 300 });
        var options = Options(a => { a.ClientId = "bot"; a.ClientSecret = "x"; });
        using var provider = new ClientCredentialsTokenProvider(idp.Client(options, out _), options.Auth, TimeProvider.System);

        await provider.GetTokenAsync(Ct);
        provider.Invalidate();

        Assert.Equal("token-2", await provider.GetTokenAsync(Ct));
    }

    [Fact]
    public async Task A_refused_client_gives_an_actionable_message_that_never_contains_the_secret()
    {
        var idp = new FakeIdp { OnToken = _ => FakeIdp.Reply(new { error = "invalid_client", error_description = "Invalid client secret" }, HttpStatusCode.Unauthorized) };
        var options = Options(a => { a.ClientId = "bot"; a.ClientSecret = "TOP-SECRET-VALUE"; });
        using var provider = new ClientCredentialsTokenProvider(idp.Client(options, out _), options.Auth, TimeProvider.System);

        var exception = await Assert.ThrowsAsync<TokenAcquisitionException>(async () => await provider.GetTokenAsync(Ct));

        Assert.Contains("invalid_client", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Auth:ClientSecret", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("TOP-SECRET-VALUE", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("TOP-SECRET-VALUE", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_authority_setting_overrides_what_servicecontrol_advertises()
    {
        var idp = new FakeIdp();
        var options = Options(a => { a.ClientId = "bot"; a.ClientSecret = "x"; a.Authority = Authority; });
        var client = idp.Client(options, out _);

        await client.GetMetadataAsync(Ct);

        // ServiceControl was never asked: the configured authority was enough.
        Assert.DoesNotContain(idp.Http.Requests, r => r.Uri.AbsolutePath.Contains("authentication/configuration", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Secrets_are_never_sent_to_an_identity_provider_over_plain_http()
    {
        var idp = new FakeIdp();
        var options = Options(a => { a.ClientId = "bot"; a.ClientSecret = "x"; a.Authority = "http://idp.example/realms/test"; });
        using var provider = new ClientCredentialsTokenProvider(idp.Client(options, out _), options.Auth, TimeProvider.System);

        var exception = await Assert.ThrowsAsync<TokenAcquisitionException>(async () => await provider.GetTokenAsync(Ct));

        Assert.Contains("plain HTTP", exception.Message, StringComparison.Ordinal);
        Assert.Empty(idp.Http.Requests);
    }

    [Fact]
    public async Task No_advertised_authority_is_reported_clearly()
    {
        var idp = new FakeIdp();
        idp.Http.Override("GET", "/api/authentication/configuration", """{ "enabled": false }""");
        var options = Options(a => { a.ClientId = "bot"; a.ClientSecret = "x"; });
        using var provider = new ClientCredentialsTokenProvider(idp.Client(options, out _), options.Auth, TimeProvider.System);

        var exception = await Assert.ThrowsAsync<TokenAcquisitionException>(async () => await provider.GetTokenAsync(Ct));

        Assert.Contains("Auth:Authority", exception.Message, StringComparison.Ordinal);
    }

    // ---- device code --------------------------------------------------------------------------------------------

    static DeviceCodeTokenProvider Device(FakeIdp idp, ManualTimeProvider clock, Action<AuthOptions>? configure = null)
    {
        var options = Options(a =>
        {
            a.ClientId = "cli";
            configure?.Invoke(a);
        });
        var client = idp.Client(options, out var discovery);
        return new DeviceCodeTokenProvider(client, discovery, options.Auth, clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<DeviceCodeTokenProvider>.Instance);
    }

    [Fact]
    public async Task Device_sign_in_first_fails_with_instructions_the_agent_can_relay_using_servicecontrols_scopes()
    {
        var idp = new FakeIdp();
        using var provider = Device(idp, new ManualTimeProvider(DateTimeOffset.UtcNow));

        var exception = await Assert.ThrowsAsync<TokenAcquisitionException>(async () => await provider.GetTokenAsync(Ct));

        Assert.Contains("ABCD-EFGH", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"{Authority}/device", exception.Message, StringComparison.Ordinal);
        Assert.Contains("repeat this request", exception.Message, StringComparison.Ordinal);
        var start = idp.Http.Requests.Single(r => r.Uri.AbsolutePath.EndsWith("/device-auth", StringComparison.Ordinal));
        Assert.Equal("servicecontrol openid offline_access", Form(start, "scope"));
        Assert.Equal("cli", Form(start, "client_id"));
        Assert.Null(start.Authorization);
    }

    [Fact]
    public async Task Device_polling_respects_the_interval_and_returns_the_token_once_the_user_approves()
    {
        var idp = new FakeIdp();
        var pending = true;
        idp.OnToken = request => pending
            ? FakeIdp.Reply(new { error = "authorization_pending" }, HttpStatusCode.BadRequest)
            : FakeIdp.Reply(new { access_token = "user-token", refresh_token = "r1", expires_in = 300 });
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var provider = Device(idp, clock);

        await Assert.ThrowsAsync<TokenAcquisitionException>(async () => await provider.GetTokenAsync(Ct)); // starts sign-in
        Assert.Empty(idp.TokenRequests);

        // Asked again before the interval has passed: the provider must not hammer the identity provider.
        await Assert.ThrowsAsync<TokenAcquisitionException>(async () => await provider.GetTokenAsync(Ct));
        Assert.Empty(idp.TokenRequests);

        clock.Advance(TimeSpan.FromSeconds(6));
        var stillPending = await Assert.ThrowsAsync<TokenAcquisitionException>(async () => await provider.GetTokenAsync(Ct));
        Assert.Contains("still pending", stillPending.Message, StringComparison.Ordinal);
        Assert.Single(idp.TokenRequests);

        pending = false;
        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal("user-token", await provider.GetTokenAsync(Ct));
        var poll = idp.TokenRequests.Last();
        Assert.Equal("urn:ietf:params:oauth:grant-type:device_code", Form(poll, "grant_type"));
        Assert.Equal("dev-1", Form(poll, "device_code"));
    }

    [Fact]
    public async Task A_slow_down_answer_lengthens_the_polling_interval()
    {
        var idp = new FakeIdp { OnToken = _ => FakeIdp.Reply(new { error = "slow_down" }, HttpStatusCode.BadRequest) };
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var provider = Device(idp, clock);

        await Assert.ThrowsAsync<TokenAcquisitionException>(async () => await provider.GetTokenAsync(Ct));
        clock.Advance(TimeSpan.FromSeconds(6));
        await Assert.ThrowsAsync<TokenAcquisitionException>(async () => await provider.GetTokenAsync(Ct));
        Assert.Single(idp.TokenRequests);

        clock.Advance(TimeSpan.FromSeconds(6)); // interval is now 10s: 6s is not enough
        await Assert.ThrowsAsync<TokenAcquisitionException>(async () => await provider.GetTokenAsync(Ct));
        Assert.Single(idp.TokenRequests);
    }

    [Fact]
    public async Task Access_denied_is_reported_and_the_next_request_starts_a_fresh_sign_in()
    {
        var idp = new FakeIdp { OnToken = _ => FakeIdp.Reply(new { error = "access_denied" }, HttpStatusCode.BadRequest) };
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var provider = Device(idp, clock);

        await Assert.ThrowsAsync<TokenAcquisitionException>(async () => await provider.GetTokenAsync(Ct));
        clock.Advance(TimeSpan.FromSeconds(6));
        var denied = await Assert.ThrowsAsync<TokenAcquisitionException>(async () => await provider.GetTokenAsync(Ct));
        Assert.Contains("declined", denied.Message, StringComparison.Ordinal);

        var restarted = await Assert.ThrowsAsync<TokenAcquisitionException>(async () => await provider.GetTokenAsync(Ct));
        Assert.Contains("Sign-in is required", restarted.Message, StringComparison.Ordinal);
        Assert.Equal(2, idp.Http.Requests.Count(r => r.Uri.AbsolutePath.EndsWith("/device-auth", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task An_expired_device_code_restarts_sign_in_without_polling()
    {
        var idp = new FakeIdp();
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var provider = Device(idp, clock);

        await Assert.ThrowsAsync<TokenAcquisitionException>(async () => await provider.GetTokenAsync(Ct));
        clock.Advance(TimeSpan.FromMinutes(11)); // the code was valid for 10 minutes

        var restarted = await Assert.ThrowsAsync<TokenAcquisitionException>(async () => await provider.GetTokenAsync(Ct));

        Assert.Contains("Sign-in is required", restarted.Message, StringComparison.Ordinal);
        Assert.Empty(idp.TokenRequests);
    }

    [Fact]
    public async Task A_refresh_token_renews_the_session_without_asking_the_user_again()
    {
        var idp = new FakeIdp();
        var grants = new List<string>();
        idp.OnToken = request =>
        {
            grants.Add(Form(request, "grant_type"));
            return Form(request, "grant_type") == "refresh_token"
                ? FakeIdp.Reply(new { access_token = "refreshed", expires_in = 300 })
                : FakeIdp.Reply(new { access_token = "first", refresh_token = "r1", expires_in = 300 });
        };
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var provider = Device(idp, clock);

        await Assert.ThrowsAsync<TokenAcquisitionException>(async () => await provider.GetTokenAsync(Ct));
        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal("first", await provider.GetTokenAsync(Ct));

        clock.Advance(TimeSpan.FromSeconds(290)); // first token has expired
        Assert.Equal("refreshed", await provider.GetTokenAsync(Ct));

        Assert.Equal(["urn:ietf:params:oauth:grant-type:device_code", "refresh_token"], grants);
        Assert.Equal("r1", Form(idp.TokenRequests.Last(), "refresh_token"));
    }

    [Fact]
    public async Task A_revoked_refresh_token_falls_back_to_a_new_sign_in()
    {
        var idp = new FakeIdp();
        idp.OnToken = request => Form(request, "grant_type") == "refresh_token"
            ? FakeIdp.Reply(new { error = "invalid_grant" }, HttpStatusCode.BadRequest)
            : FakeIdp.Reply(new { access_token = "first", refresh_token = "r1", expires_in = 300 });
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var provider = Device(idp, clock);

        await Assert.ThrowsAsync<TokenAcquisitionException>(async () => await provider.GetTokenAsync(Ct));
        clock.Advance(TimeSpan.FromSeconds(6));
        await provider.GetTokenAsync(Ct);
        clock.Advance(TimeSpan.FromSeconds(290));

        var again = await Assert.ThrowsAsync<TokenAcquisitionException>(async () => await provider.GetTokenAsync(Ct));

        Assert.Contains("Sign-in is required", again.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task When_offline_access_is_refused_the_next_sign_in_does_not_ask_for_it()
    {
        var idp = new FakeIdp();
        var refuse = true;
        idp.OnToken = _ => refuse
            ? FakeIdp.Reply(new { error = "not_allowed", error_description = "Offline tokens not allowed for the user or client" }, HttpStatusCode.BadRequest)
            : FakeIdp.Reply(new { access_token = "ok", expires_in = 300 });
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var provider = Device(idp, clock);

        await Assert.ThrowsAsync<TokenAcquisitionException>(async () => await provider.GetTokenAsync(Ct));
        var starts = () => idp.Http.Requests.Where(r => r.Uri.AbsolutePath.EndsWith("/device-auth", StringComparison.Ordinal)).ToList();
        Assert.Contains("offline_access", Form(starts()[0], "scope"), StringComparison.Ordinal);

        clock.Advance(TimeSpan.FromSeconds(6));
        var failed = await Assert.ThrowsAsync<TokenAcquisitionException>(async () => await provider.GetTokenAsync(Ct));
        Assert.Contains("not_allowed", failed.Message, StringComparison.Ordinal);
        Assert.Contains("offline", failed.Message, StringComparison.OrdinalIgnoreCase);

        refuse = false;
        await Assert.ThrowsAsync<TokenAcquisitionException>(async () => await provider.GetTokenAsync(Ct)); // starts the new sign-in
        Assert.DoesNotContain("offline_access", Form(starts()[1], "scope"), StringComparison.Ordinal);
        Assert.Contains("openid", Form(starts()[1], "scope"), StringComparison.Ordinal);

        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal("ok", await provider.GetTokenAsync(Ct));
    }

    [Fact]
    public async Task An_explicitly_configured_scope_is_never_rewritten()
    {
        var idp = new FakeIdp { OnToken = _ => FakeIdp.Reply(new { error = "invalid_scope", error_description = "offline_access not allowed" }, HttpStatusCode.BadRequest) };
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var provider = Device(idp, clock, a => a.Scope = "api://x/.default offline_access");

        await Assert.ThrowsAsync<TokenAcquisitionException>(async () => await provider.GetTokenAsync(Ct));
        clock.Advance(TimeSpan.FromSeconds(6));
        await Assert.ThrowsAsync<TokenAcquisitionException>(async () => await provider.GetTokenAsync(Ct));
        await Assert.ThrowsAsync<TokenAcquisitionException>(async () => await provider.GetTokenAsync(Ct)); // a fresh sign-in

        var starts = idp.Http.Requests.Where(r => r.Uri.AbsolutePath.EndsWith("/device-auth", StringComparison.Ordinal)).ToList();
        Assert.All(starts, start => Assert.Equal("api://x/.default offline_access", Form(start, "scope")));
    }

    [Fact]
    public async Task A_provider_without_device_support_says_so()
    {
        var idp = new FakeIdp(withDeviceEndpoint: false);
        using var provider = Device(idp, new ManualTimeProvider(DateTimeOffset.UtcNow));

        var exception = await Assert.ThrowsAsync<TokenAcquisitionException>(async () => await provider.GetTokenAsync(Ct));

        Assert.Contains("device authorization", exception.Message, StringComparison.Ordinal);
    }

    // ---- token exchange -----------------------------------------------------------------------------------------

    sealed class FakeSubject : ISubjectTokenAccessor
    {
        public string? Subject { get; set; } = "caller-token-alice";

        public string? GetSubjectToken() => Subject;
    }

    static TokenExchangeTokenProvider Exchange(FakeIdp idp, FakeSubject subject, ManualTimeProvider clock, Action<AuthOptions>? configure = null)
    {
        var options = Options(a =>
        {
            a.ClientId = "mcp-server";
            a.ClientSecret = "server-secret";
            configure?.Invoke(a);
        });
        var client = idp.Client(options, out var discovery);
        return new TokenExchangeTokenProvider(client, discovery, subject, options.Auth, clock);
    }

    [Fact]
    public async Task A_standard_exchange_sends_the_callers_token_for_the_audience_servicecontrol_advertises()
    {
        var idp = new FakeIdp { OnToken = _ => FakeIdp.Reply(new { access_token = "for-servicecontrol", expires_in = 300 }) };
        using var provider = Exchange(idp, new FakeSubject(), new ManualTimeProvider(DateTimeOffset.UtcNow));

        var token = await provider.GetTokenAsync(Ct);

        Assert.Equal("for-servicecontrol", token);
        var request = Assert.Single(idp.TokenRequests);
        Assert.Equal("urn:ietf:params:oauth:grant-type:token-exchange", Form(request, "grant_type"));
        Assert.Equal("caller-token-alice", Form(request, "subject_token"));
        Assert.Equal("urn:ietf:params:oauth:token-type:access_token", Form(request, "subject_token_type"));
        Assert.Equal("servicecontrol", Form(request, "audience")); // from ServiceControl's discovery document
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("mcp-server:server-secret")), request.Authorization);
    }

    [Fact]
    public async Task The_callers_token_is_only_ever_sent_to_the_token_endpoint_never_to_servicecontrol_or_discovery()
    {
        var idp = new FakeIdp();
        using var provider = Exchange(idp, new FakeSubject(), new ManualTimeProvider(DateTimeOffset.UtcNow));

        await provider.GetTokenAsync(Ct);

        var leaks = idp.Http.Requests.Where(r => !r.Uri.AbsolutePath.EndsWith("/token", StringComparison.Ordinal))
            .Where(r => (r.Body?.Contains("caller-token-alice", StringComparison.Ordinal) ?? false) || (r.Authorization?.Contains("caller-token-alice", StringComparison.Ordinal) ?? false));
        Assert.Empty(leaks);
    }

    [Fact]
    public async Task An_explicit_audience_and_scope_override_the_defaults()
    {
        var idp = new FakeIdp();
        using var provider = Exchange(idp, new FakeSubject(), new ManualTimeProvider(DateTimeOffset.UtcNow), a => { a.Audience = "https://sc.internal"; a.Scope = "read"; });

        await provider.GetTokenAsync(Ct);

        var request = Assert.Single(idp.TokenRequests);
        Assert.Equal("https://sc.internal", Form(request, "audience"));
        Assert.Equal("read", Form(request, "scope"));
    }

    [Fact]
    public async Task An_on_behalf_of_exchange_uses_the_microsoft_flow()
    {
        var idp = new FakeIdp();
        using var provider = Exchange(idp, new FakeSubject(), new ManualTimeProvider(DateTimeOffset.UtcNow),
            a => { a.ExchangeStyle = ExchangeStyle.OnBehalfOf; a.Scope = "api://servicecontrol/.default"; });

        await provider.GetTokenAsync(Ct);

        var request = Assert.Single(idp.TokenRequests);
        Assert.Equal("urn:ietf:params:oauth:grant-type:jwt-bearer", Form(request, "grant_type"));
        Assert.Equal("caller-token-alice", Form(request, "assertion"));
        Assert.Equal("on_behalf_of", Form(request, "requested_token_use"));
        Assert.Equal("api://servicecontrol/.default", Form(request, "scope"));
    }

    [Fact]
    public async Task Each_caller_gets_their_own_exchanged_token_and_it_is_cached_per_caller()
    {
        var idp = new FakeIdp();
        idp.OnToken = request => FakeIdp.Reply(new { access_token = "exchanged-for-" + Form(request, "subject_token"), expires_in = 300 });
        var subject = new FakeSubject();
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var provider = Exchange(idp, subject, clock);

        subject.Subject = "alice";
        Assert.Equal("exchanged-for-alice", await provider.GetTokenAsync(Ct));
        subject.Subject = "bob";
        Assert.Equal("exchanged-for-bob", await provider.GetTokenAsync(Ct));
        subject.Subject = "alice";
        Assert.Equal("exchanged-for-alice", await provider.GetTokenAsync(Ct));

        Assert.Equal(2, idp.TokenRequests.Count()); // alice's second call was served from the cache

        clock.Advance(TimeSpan.FromSeconds(250)); // inside the refresh margin of a 300s token
        await provider.GetTokenAsync(Ct);
        Assert.Equal(3, idp.TokenRequests.Count());
    }

    [Fact]
    public async Task Invalidating_drops_only_the_current_callers_token()
    {
        var idp = new FakeIdp();
        idp.OnToken = request => FakeIdp.Reply(new { access_token = "t-" + Form(request, "subject_token"), expires_in = 300 });
        var subject = new FakeSubject();
        using var provider = Exchange(idp, subject, new ManualTimeProvider(DateTimeOffset.UtcNow));

        subject.Subject = "alice";
        await provider.GetTokenAsync(Ct);
        subject.Subject = "bob";
        await provider.GetTokenAsync(Ct);
        Assert.Equal(2, idp.TokenRequests.Count());

        provider.Invalidate(); // bob's exchanged token was rejected

        await provider.GetTokenAsync(Ct); // bob: exchanged again
        subject.Subject = "alice";
        await provider.GetTokenAsync(Ct); // alice: still cached
        Assert.Equal(3, idp.TokenRequests.Count());
    }

    [Fact]
    public async Task Without_a_caller_token_there_is_nothing_to_exchange()
    {
        var idp = new FakeIdp();
        using var provider = Exchange(idp, new FakeSubject { Subject = null }, new ManualTimeProvider(DateTimeOffset.UtcNow));

        var exception = await Assert.ThrowsAsync<TokenAcquisitionException>(async () => await provider.GetTokenAsync(Ct));

        Assert.Contains("not authenticated", exception.Message, StringComparison.Ordinal);
        Assert.Empty(idp.Http.Requests);
    }

    [Fact]
    public async Task A_refused_exchange_explains_the_likely_cause_without_leaking_secrets()
    {
        var idp = new FakeIdp { OnToken = _ => FakeIdp.Reply(new { error = "access_denied", error_description = "Client not allowed to exchange" }, HttpStatusCode.Forbidden) };
        using var provider = Exchange(idp, new FakeSubject { Subject = "SECRET-CALLER-TOKEN" }, new ManualTimeProvider(DateTimeOffset.UtcNow));

        var exception = await Assert.ThrowsAsync<TokenAcquisitionException>(async () => await provider.GetTokenAsync(Ct));

        Assert.Contains("issued for this server", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-CALLER-TOKEN", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("server-secret", exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AuthMode.TokenExchange, "Auth:ClientSecret")]
    public void Token_exchange_needs_a_confidential_client(AuthMode mode, string expected) =>
        Assert.Contains(expected, ServiceCollectionExtensions.AuthProblem(new AuthOptions { Mode = mode, ClientId = "x" }), StringComparison.Ordinal);

    [Fact]
    public void On_behalf_of_needs_a_scope_and_token_exchange_is_never_chosen_automatically()
    {
        var obo = new AuthOptions { Mode = AuthMode.TokenExchange, ClientId = "x", ClientSecret = "y", ExchangeStyle = ExchangeStyle.OnBehalfOf };
        Assert.Contains("Auth:Scope", ServiceCollectionExtensions.AuthProblem(obo), StringComparison.Ordinal);

        Assert.NotEqual(AuthMode.TokenExchange, ServiceCollectionExtensions.ResolveMode(new AuthOptions { ClientId = "x", ClientSecret = "y" }));
    }

    // ---- configuration ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, null, null, null, null, AuthMode.None)]
    [InlineData("tok", null, null, null, null, AuthMode.StaticToken)]
    [InlineData(null, "cmd", null, null, null, AuthMode.Command)]
    [InlineData(null, null, "id", "secret", null, AuthMode.ClientCredentials)]
    [InlineData(null, null, "id", null, null, AuthMode.DeviceCode)]
    [InlineData("tok", "cmd", "id", "secret", null, AuthMode.StaticToken)]
    public void Auto_mode_picks_the_most_specific_configured_credential(string? token, string? command, string? id, string? secret, string? unused, AuthMode expected)
    {
        Assert.Null(unused);
        var auth = new AuthOptions { Token = token, TokenCommand = command, ClientId = id, ClientSecret = secret };

        Assert.Equal(expected, ServiceCollectionExtensions.ResolveMode(auth));
    }

    [Theory]
    [InlineData(AuthMode.StaticToken, "Auth:Token")]
    [InlineData(AuthMode.Command, "Auth:TokenCommand")]
    [InlineData(AuthMode.ClientCredentials, "Auth:ClientSecret")]
    [InlineData(AuthMode.DeviceCode, "Auth:ClientId")]
    public void An_explicit_mode_with_missing_settings_names_what_is_missing(AuthMode mode, string expected)
    {
        var problem = ServiceCollectionExtensions.AuthProblem(new AuthOptions { Mode = mode });

        Assert.NotNull(problem);
        Assert.Contains(expected, problem, StringComparison.Ordinal);
        Assert.Null(ServiceCollectionExtensions.AuthProblem(new AuthOptions()));
    }
}
