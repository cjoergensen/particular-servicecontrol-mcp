using System.Net;
using Cjoergensen.ServiceControl.Mcp.Tests.Support;
using Cjoergensen.ServiceControl.Mcp.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using static Cjoergensen.ServiceControl.Mcp.Tests.Support.TestKit;

namespace Cjoergensen.ServiceControl.Mcp.Tests;

public class ToolAccessTests
{
    /// <summary>The routes a role allows, as ServiceControl's <c>/api/my/routes</c> reports them: readers get every GET, writers everything a tool needs.</summary>
    internal static string RoutesJson(string role, bool includeWrites)
    {
        var requirements = ToolRoutes.Required.Values.SelectMany(r => r).Distinct().Where(r => includeWrites || r.StartsWith("GET ", StringComparison.Ordinal));
        var routes = requirements.Select(r =>
        {
            var space = r.IndexOf(' ', StringComparison.Ordinal);
            return new { method = r[..space], url_template = r[(space + 1)..] };
        });
        return System.Text.Json.JsonSerializer.Serialize(new { roles = new[] { role }, routes });
    }

    [Theory]
    [InlineData("GET", "/api/errors/{failedMessageId}", "GET /api/errors/{}")]
    [InlineData("get", "/API/Errors/{id}/", "GET /api/errors/{}")]
    [InlineData("POST", "/api/errors/{endpointName}/retry/all", "POST /api/errors/{}/retry/all")]
    [InlineData("GET", "/api/errors?page=1", "GET /api/errors")]
    [InlineData("PATCH", "/api/errors/archive", "PATCH /api/errors/archive")]
    public void Routes_are_compared_without_regard_to_parameter_names_case_or_query(string method, string template, string expected) =>
        Assert.Equal(expected, ToolRoutes.Normalize(method, template));

    [Fact]
    public void Every_requirement_in_the_registry_is_well_formed()
    {
        foreach (var (tool, requirements) in ToolRoutes.Required)
        {
            Assert.NotEmpty(requirements);
            Assert.All(requirements, r => Assert.Matches(@"^(GET|POST|PATCH|DELETE) /api/", r));
            Assert.All(requirements, r => Assert.False(string.IsNullOrEmpty(ToolRoutes.NormalizeRequirement(r)), tool));
        }
    }

    static ToolAccess Access(FakeServiceControl fake, TimeProvider clock) => new(PrimaryClient(fake), clock, NullLogger<ToolAccess>.Instance);

    [Fact]
    public async Task A_reader_may_use_read_tools_and_not_write_tools()
    {
        var fake = new FakeServiceControl().Route("GET", "/api/my/routes", RoutesJson("reader", includeWrites: false));
        using var access = Access(fake, TimeProvider.System);

        var snapshot = await access.GetAsync(Ct);

        Assert.True(snapshot.Restricted);
        Assert.Equal(["reader"], snapshot.Roles);
        Assert.True(ToolAccess.IsAllowed("list_failed_messages", snapshot));
        Assert.True(ToolAccess.IsAllowed("get_health_overview", snapshot));
        Assert.False(ToolAccess.IsAllowed("retry_failed_message", snapshot));
        Assert.False(ToolAccess.IsAllowed("dismiss_custom_check", snapshot));
        Assert.Equal(["POST /api/errors/{id}/retry"], ToolAccess.MissingRoutes("retry_failed_message", snapshot));
    }

    [Fact]
    public async Task A_bulk_tool_needs_every_route_it_calls()
    {
        // Can archive by group, but cannot read a group's messages (which the count guard needs).
        var fake = new FakeServiceControl().Route(
            "GET", "/api/my/routes",
            """{ "roles": ["custom"], "routes": [ { "method": "POST", "url_template": "/api/recoverability/groups/{groupId}/errors/archive" } ] }""");
        using var access = Access(fake, TimeProvider.System);

        var snapshot = await access.GetAsync(Ct);

        Assert.False(ToolAccess.IsAllowed("archive_failure_group", snapshot));
        Assert.Equal(["GET /api/recoverability/groups/{id}/errors"], ToolAccess.MissingRoutes("archive_failure_group", snapshot));
    }

    [Fact]
    public async Task An_identity_with_no_recognised_role_sees_no_gated_tools_but_the_overview_stays()
    {
        var fake = new FakeServiceControl().Route("GET", "/api/my/routes", """{ "roles": [], "routes": [] }""");
        using var access = Access(fake, TimeProvider.System);

        var snapshot = await access.GetAsync(Ct);

        Assert.All(ToolRoutes.Required.Keys, tool => Assert.False(ToolAccess.IsAllowed(tool, snapshot), tool));
        Assert.True(ToolAccess.IsAllowed("get_health_overview", snapshot));
        Assert.True(ToolAccess.IsAllowed("list_monitored_endpoints", snapshot));
    }

    [Fact]
    public async Task Tools_the_registry_does_not_know_are_never_hidden()
    {
        var fake = new FakeServiceControl().Route("GET", "/api/my/routes", """{ "roles": [], "routes": [] }""");
        using var access = Access(fake, TimeProvider.System);

        Assert.True(ToolAccess.IsAllowed("some_future_tool", await access.GetAsync(Ct)));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task When_permissions_cannot_be_determined_nothing_is_hidden(HttpStatusCode status)
    {
        var fake = new FakeServiceControl().Route("GET", "/api/my/routes", "{}", status);
        using var access = Access(fake, TimeProvider.System);

        var snapshot = await access.GetAsync(Ct);

        Assert.False(snapshot.Restricted);
        Assert.All(ToolRoutes.Required.Keys, tool => Assert.True(ToolAccess.IsAllowed(tool, snapshot), tool));
    }

    [Fact]
    public async Task An_unreachable_servicecontrol_does_not_hide_tools_either()
    {
        var fake = new FakeServiceControl().Fail(new HttpRequestException("refused"));
        using var access = Access(fake, TimeProvider.System);

        Assert.False((await access.GetAsync(Ct)).Restricted);
    }

    [Fact]
    public async Task The_answer_is_cached_for_a_minute_and_a_failure_is_retried_sooner()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var fake = new FakeServiceControl().Route("GET", "/api/my/routes", RoutesJson("reader", includeWrites: false));
        using var access = Access(fake, clock);

        await access.GetAsync(Ct);
        await access.GetAsync(Ct);
        Assert.Single(fake.Requests);

        clock.Advance(TimeSpan.FromSeconds(61));
        await access.GetAsync(Ct);
        Assert.Equal(2, fake.Requests.Count);

        var failing = new FakeServiceControl().Route("GET", "/api/my/routes", "{}", HttpStatusCode.Unauthorized);
        using var failingAccess = Access(failing, clock);
        await failingAccess.GetAsync(Ct);
        await failingAccess.GetAsync(Ct);
        Assert.Single(failing.Requests);
        clock.Advance(TimeSpan.FromSeconds(16)); // a failure is remembered only briefly: credentials may just have been fixed
        await failingAccess.GetAsync(Ct);
        Assert.Equal(2, failing.Requests.Count);
    }

    [Fact]
    public async Task While_a_sign_in_is_pending_permissions_are_asked_for_again_within_seconds()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var fake = new FakeServiceControl().Route("GET", "/api/my/routes", RoutesJson("reader", includeWrites: false));

        // Model the real cause: the token provider cannot supply a token yet, which surfaces as CredentialsUnavailable.
        var provider = new PendingThenReady();
        var handler = new Client.BearerTokenHandler(provider, Wrap(Options())) { InnerHandler = fake };
        using var access = new ToolAccess(new Client.ServiceControlClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:33333/api/") }), clock, NullLogger<ToolAccess>.Instance);

        Assert.False((await access.GetAsync(Ct)).Restricted);

        clock.Advance(TimeSpan.FromSeconds(3));
        provider.Ready = true;
        var snapshot = await access.GetAsync(Ct);

        Assert.True(snapshot.Restricted);
        Assert.False(ToolAccess.IsAllowed("retry_failed_message", snapshot));
    }

    sealed class PendingThenReady : Auth.ITokenProvider
    {
        public bool Ready { get; set; }

        public ValueTask<string?> GetTokenAsync(CancellationToken cancellationToken) =>
            Ready ? ValueTask.FromResult<string?>("t") : throw new Auth.TokenAcquisitionException("Sign-in is still pending.");

        public void Invalidate()
        {
        }
    }
}

