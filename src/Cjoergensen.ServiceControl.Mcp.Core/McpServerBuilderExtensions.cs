using System.Security.Claims;
using Cjoergensen.ServiceControl.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Cjoergensen.ServiceControl.Mcp;

/// <summary>Cross-cutting behaviour for hosts that serve several callers.</summary>
public static class McpServerBuilderExtensions
{
    /// <summary>
    /// Limits each caller to the tools their own token's roles allow (see <see cref="CallerPolicy"/>), and refuses calls to the rest. Use it when the
    /// server reaches ServiceControl with its own identity, so ServiceControl cannot tell callers apart.
    /// </summary>
    /// <param name="rolesClaim">Where the caller's roles are in the token: a claim name or a dotted path such as <c>realm_access.roles</c>.</param>
    public static IMcpServerBuilder WithCallerRoleEnforcement(this IMcpServerBuilder builder, string rolesClaim)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithRequestFilters(filters =>
        {
            filters.AddListToolsFilter(next => async (context, cancellationToken) =>
            {
                var result = await next(context, cancellationToken).ConfigureAwait(false);
                var roles = CallerRoles.Read(context.User, rolesClaim);
                result.Tools = [.. result.Tools.Where(tool => CallerPolicy.IsAllowed(tool.Name, roles))];
                return result;
            });

            filters.AddCallToolFilter(next => async (context, cancellationToken) =>
            {
                var name = context.Params?.Name;
                var roles = CallerRoles.Read(context.User, rolesClaim);
                if (name is not null && !CallerPolicy.IsAllowed(name, roles))
                {
                    return new CallToolResult
                    {
                        IsError = true,
                        Content =
                        [
                            new TextContentBlock
                            {
                                Text = $"The tool '{name}' is not available to you" +
                                       (roles.Count > 0 ? $" (your roles: {string.Join(", ", roles)})" : " (your token carries no reader, writer or admin role)") +
                                       ". Reading needs the reader role; retry, archive and dismiss need the writer role."
                            }
                        ]
                    };
                }

                return await next(context, cancellationToken).ConfigureAwait(false);
            });
        });
    }

    /// <summary>
    /// Records who called which tool and how it ended. When the server reaches ServiceControl with its own identity, ServiceControl's audit log shows only
    /// that identity, so this is the record of which person actually asked. Changes are logged at Information, reads at Debug.
    /// </summary>
    public static IMcpServerBuilder WithCallAuditLogging(this IMcpServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
        {
            var logger = context.Services!.GetRequiredService<ILoggerFactory>().CreateLogger("Cjoergensen.ServiceControl.Mcp.Audit");
            var name = context.Params?.Name ?? "(unknown)";
            var caller = DescribeCaller(context.User);

            var result = await next(context, cancellationToken).ConfigureAwait(false);

            var outcome = result.IsError == true ? "error" : "ok";
            if (CallerPolicy.WriteTools.Contains(name))
            {
                AuditLog.Write(logger, caller, name, outcome);
            }
            else
            {
                AuditLog.Read(logger, caller, name, outcome);
            }

            return result;
        }));
    }

    static string DescribeCaller(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true)
        {
            return "anonymous";
        }

        var who = user.FindFirst("preferred_username")?.Value ?? user.FindFirst("email")?.Value ?? user.FindFirst("sub")?.Value ?? "unknown";
        var client = user.FindFirst("azp")?.Value ?? user.FindFirst("client_id")?.Value;
        return client is null ? who : $"{who} (via {client})";
    }
}

static partial class AuditLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "AUDIT change: caller {Caller} called {Tool} -> {Outcome}")]
    public static partial void Write(ILogger logger, string caller, string tool, string outcome);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "AUDIT read: caller {Caller} called {Tool} -> {Outcome}")]
    public static partial void Read(ILogger logger, string caller, string tool, string outcome);
}
