using System.Security.Claims;
using System.Text.Json;
using Cjoergensen.ServiceControl.Mcp.Tests.Support;
using Cjoergensen.ServiceControl.Mcp.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using static Cjoergensen.ServiceControl.Mcp.Tests.Support.TestKit;

namespace Cjoergensen.ServiceControl.Mcp.Tests;

public class CallerPolicyTests
{
    static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), authenticationType: "test"));

    // ---- reading roles from a token -----------------------------------------------------------------------------

    [Fact]
    public void A_flat_roles_claim_repeated_per_role_is_read()
    {
        var user = Principal(("roles", "reader"), ("roles", "writer"), ("sub", "u1"));

        Assert.Equal(["reader", "writer"], CallerRoles.Read(user, "roles"));
    }

    [Fact]
    public void A_nested_claim_is_read_by_dotted_path_like_keycloaks_realm_access()
    {
        var user = Principal(("realm_access", """{ "roles": ["writer", "offline_access"] }"""));

        Assert.Equal(["writer", "offline_access"], CallerRoles.Read(user, "realm_access.roles"));
    }

    [Fact]
    public void A_roles_claim_holding_a_json_array_string_is_read_and_duplicates_collapse()
    {
        var user = Principal(("roles", """["Reader","reader"]"""));

        Assert.Equal(["Reader"], CallerRoles.Read(user, "roles"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("realm_access.nothing")]
    [InlineData("")]
    public void A_path_that_is_not_there_yields_no_roles(string path) =>
        Assert.Empty(CallerRoles.Read(Principal(("realm_access", """{ "roles": ["writer"] }""")), path));

    [Fact]
    public void A_malformed_nested_claim_and_a_missing_user_yield_no_roles()
    {
        Assert.Empty(CallerRoles.Read(Principal(("realm_access", "not json")), "realm_access.roles"));
        Assert.Empty(CallerRoles.Read(null, "roles"));
    }

    // ---- what a caller may do ------------------------------------------------------------------------------------

    [Fact]
    public void The_write_tools_are_derived_from_the_routes_they_call()
    {
        Assert.Contains("retry_failed_message", CallerPolicy.WriteTools);
        Assert.Contains("dismiss_custom_check", CallerPolicy.WriteTools);
        Assert.DoesNotContain("list_failed_messages", CallerPolicy.WriteTools);
        Assert.DoesNotContain("get_health_overview", CallerPolicy.WriteTools);
        Assert.Equal(8, CallerPolicy.WriteTools.Count);
    }

    [Theory]
    [InlineData("reader", "list_failed_messages", true)]
    [InlineData("reader", "get_health_overview", true)]
    [InlineData("reader", "list_monitored_endpoints", true)]
    [InlineData("reader", "retry_failed_message", false)]
    [InlineData("reader", "dismiss_custom_check", false)]
    [InlineData("writer", "retry_failed_message", true)]
    [InlineData("writer", "list_failed_messages", true)]
    [InlineData("admin", "archive_failure_group", true)]
    [InlineData("READER", "list_endpoints", true)]
    [InlineData("nonsense", "list_failed_messages", false)]
    [InlineData("nonsense", "get_health_overview", false)]
    public void A_callers_role_decides_which_tools_they_may_use(string role, string tool, bool expected) =>
        Assert.Equal(expected, CallerPolicy.IsAllowed(tool, [role]));

    [Fact]
    public void A_caller_with_no_role_may_use_nothing()
    {
        Assert.False(CallerPolicy.IsAllowed("list_failed_messages", []));
        Assert.False(CallerPolicy.IsAllowed("get_health_overview", []));
        Assert.False(CallerPolicy.IsAllowed("retry_failed_message", []));
    }

    [Fact]
    public void Combined_roles_grant_the_highest_capability()
    {
        Assert.True(CallerPolicy.IsAllowed("retry_failed_message", ["reader", "writer"]));
    }

    // ---- permissions are remembered per caller -------------------------------------------------------------------

    [Fact]
    public async Task One_callers_permissions_are_never_shown_to_another()
    {
        var responses = new Queue<string>([ToolAccessTests.RoutesJson("writer", includeWrites: true), ToolAccessTests.RoutesJson("reader", includeWrites: false)]);
        var fake = new FakeServiceControl().Route("GET", "/api/my/routes", _ => FakeServiceControl.Json(responses.Dequeue()));
        using var access = new ToolAccess(PrimaryClient(fake), TimeProvider.System, NullLogger<ToolAccess>.Instance);
        var alice = Principal(("sub", "alice-id"));
        var bob = Principal(("sub", "bob-id"));

        var forAlice = await access.GetAsync(alice, Ct);
        var forBob = await access.GetAsync(bob, Ct);

        Assert.True(ToolAccess.IsAllowed("retry_failed_message", forAlice));
        Assert.False(ToolAccess.IsAllowed("retry_failed_message", forBob));

        // Each is served from their own cached entry afterwards, without asking ServiceControl again.
        Assert.Same(forAlice, await access.GetAsync(alice, Ct));
        Assert.Same(forBob, await access.GetAsync(bob, Ct));
        Assert.Equal(2, fake.Requests.Count);
    }

    [Fact]
    public async Task Concurrent_callers_each_get_their_own_answer()
    {
        var fake = new FakeServiceControl().Route("GET", "/api/my/routes", ToolAccessTests.RoutesJson("reader", includeWrites: false));
        using var access = new ToolAccess(PrimaryClient(fake), TimeProvider.System, NullLogger<ToolAccess>.Instance);

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => access.GetAsync(Principal(("sub", $"user-{i % 5}")), Ct).AsTask()));

        Assert.All(results, snapshot => Assert.True(snapshot.Restricted));
        Assert.Equal(5, fake.Requests.Count); // five distinct callers, each asked exactly once
        _ = JsonSerializer.Serialize(results[0].Roles);
    }
}
