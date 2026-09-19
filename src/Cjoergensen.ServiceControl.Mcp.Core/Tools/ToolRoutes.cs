namespace Cjoergensen.ServiceControl.Mcp.Tools;

/// <summary>
/// Which ServiceControl API routes each tool needs, so the server can offer only the tools the signed-in identity may actually use. The
/// source of truth for what is allowed stays ServiceControl (<c>GET /api/my/routes</c>); this only says which routes a tool calls, so the
/// server never has to know ServiceControl's permission vocabulary or role definitions.
/// </summary>
public static class ToolRoutes
{
    /// <summary>Tools that call the primary instance, with the routes each needs. Parameter names in templates do not matter.</summary>
    public static IReadOnlyDictionary<string, string[]> Required { get; } = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["list_failed_messages"] = ["GET /api/errors"],
        ["get_failed_message"] = ["GET /api/errors/{id}"],
        ["get_failed_message_counts"] = ["GET /api/errors"],
        ["list_failure_groups"] = ["GET /api/recoverability/groups/{classifier}"],
        ["list_failure_classifiers"] = ["GET /api/recoverability/classifiers"],
        ["list_failure_group_messages"] = ["GET /api/recoverability/groups/{id}/errors"],
        ["list_endpoints"] = ["GET /api/endpoints"],
        ["get_heartbeat_stats"] = ["GET /api/heartbeats/stats"],
        ["list_custom_checks"] = ["GET /api/customchecks"],
        ["get_saga_history"] = ["GET /api/sagas/{id}"],
        ["search_messages"] = ["GET /api/messages/search"],

        ["retry_failed_message"] = ["POST /api/errors/{id}/retry"],
        ["retry_failed_messages"] = ["POST /api/errors/retry"],
        ["retry_endpoint_failures"] = ["POST /api/errors/{endpoint}/retry/all", "GET /api/endpoints/{endpoint}/errors"],
        ["retry_failure_group"] = ["POST /api/recoverability/groups/{id}/errors/retry", "GET /api/recoverability/groups/{id}/errors"],
        ["archive_failed_messages"] = ["PATCH /api/errors/archive"],
        ["archive_failure_group"] = ["POST /api/recoverability/groups/{id}/errors/archive", "GET /api/recoverability/groups/{id}/errors"],
        ["unarchive_failed_messages"] = ["PATCH /api/errors/unarchive"],
        ["dismiss_custom_check"] = ["DELETE /api/customchecks/{id}"]
    };

    /// <summary>
    /// Tools deliberately not gated by routes: the overview reports whichever sections the caller can read, and the monitoring instance has no
    /// route listing (and every role may read it).
    /// </summary>
    public static IReadOnlySet<string> Ungated { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "get_health_overview",
        "list_monitored_endpoints",
        "get_monitored_endpoint"
    };

    /// <summary>Reduces a route to a comparable form: lower case, no query, and every <c>{parameter}</c> reduced to <c>{}</c>.</summary>
    public static string Normalize(string method, string template)
    {
        var path = template.Split('?')[0].Trim().TrimEnd('/').ToLowerInvariant();
        var segments = path.Split('/').Select(s => s.StartsWith('{') && s.EndsWith('}') ? "{}" : s);
        return method.Trim().ToUpperInvariant() + " " + string.Join('/', segments);
    }

    internal static string NormalizeRequirement(string requirement)
    {
        var space = requirement.IndexOf(' ', StringComparison.Ordinal);
        return Normalize(requirement[..space], requirement[(space + 1)..]);
    }
}
