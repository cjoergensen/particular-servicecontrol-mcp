using System.Security.Claims;
using System.Text.Json;

namespace Cjoergensen.ServiceControl.Mcp.Tools;

/// <summary>
/// Reads the caller's roles from their token, using the same convention as ServiceControl's own <c>RolesClaim</c> setting: a claim name, or a dotted
/// path into a nested claim (Keycloak's <c>realm_access.roles</c>).
/// </summary>
public static class CallerRoles
{
    public static IReadOnlyList<string> Read(ClaimsPrincipal? user, string claimPath)
    {
        if (user is null || string.IsNullOrWhiteSpace(claimPath))
        {
            return [];
        }

        var segments = claimPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var roles = new List<string>();

        foreach (var claim in user.FindAll(segments[0]))
        {
            if (segments.Length == 1)
            {
                AddValues(claim.Value, roles);
                continue;
            }

            // A nested claim is carried as a JSON object: walk down to the value the path names.
            try
            {
                using var document = JsonDocument.Parse(claim.Value);
                var current = document.RootElement;
                var found = true;
                foreach (var segment in segments.Skip(1))
                {
                    if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(segment, out var next))
                    {
                        current = next;
                    }
                    else
                    {
                        found = false;
                        break;
                    }
                }

                if (found)
                {
                    AddElement(current, roles);
                }
            }
            catch (JsonException)
            {
                // Not a JSON claim: it cannot hold the nested path.
            }
        }

        return [.. roles.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    static void AddValues(string value, List<string> roles)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith('['))
        {
            try
            {
                using var document = JsonDocument.Parse(trimmed);
                AddElement(document.RootElement, roles);
                return;
            }
            catch (JsonException)
            {
                // Treat it as plain text below.
            }
        }

        roles.Add(trimmed);
    }

    static void AddElement(JsonElement element, List<string> roles)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String when element.GetString() is { Length: > 0 } text:
                roles.Add(text);
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    AddElement(item, roles);
                }

                break;
        }
    }
}

/// <summary>
/// Which of this server's tools a caller may use, decided from the roles in the caller's own token. This matters when the server reaches ServiceControl
/// with its own identity (a service principal): ServiceControl then sees only that identity, so the server itself must refuse to let a reader trigger
/// what the service principal is allowed to do. Reading needs a recognised role; changing anything needs <c>writer</c> or <c>admin</c>. A caller with no
/// recognised role gets nothing.
/// </summary>
public static class CallerPolicy
{
    const string Reader = "reader";
    const string Writer = "writer";
    const string Admin = "admin";

    /// <summary>The tools that change state, derived from the routes they call rather than listed by hand, so a new write tool is covered automatically.</summary>
    public static IReadOnlySet<string> WriteTools { get; } = ToolRoutes.Required
        .Where(tool => tool.Value.Any(r => !r.StartsWith("GET ", StringComparison.Ordinal)))
        .Select(tool => tool.Key)
        .ToHashSet(StringComparer.Ordinal);

    public static bool IsAllowed(string tool, IReadOnlyList<string> roles)
    {
        var canWrite = roles.Any(r => r.Equals(Writer, StringComparison.OrdinalIgnoreCase) || r.Equals(Admin, StringComparison.OrdinalIgnoreCase));
        var canRead = canWrite || roles.Any(r => r.Equals(Reader, StringComparison.OrdinalIgnoreCase));

        return WriteTools.Contains(tool) ? canWrite : canRead;
    }
}
