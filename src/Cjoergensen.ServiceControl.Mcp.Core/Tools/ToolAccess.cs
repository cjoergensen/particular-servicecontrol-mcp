using System.Collections.Concurrent;
using System.Security.Claims;
using Cjoergensen.ServiceControl.Mcp.Client;
using Microsoft.Extensions.Logging;

namespace Cjoergensen.ServiceControl.Mcp.Tools;

/// <summary>What the current credentials may do. <see cref="Restricted"/> is false when that could not be determined, in which case nothing is hidden.</summary>
public sealed record AccessSnapshot(bool Restricted, IReadOnlySet<string> Routes, IReadOnlyList<string> Roles)
{
    public static AccessSnapshot Unrestricted { get; } = new(false, new HashSet<string>(), []);
}

/// <summary>
/// Decides which tools to offer by asking ServiceControl which routes the signed-in identity may call (<c>GET /api/my/routes</c>). ServiceControl
/// remains the authority and still enforces every request; this only avoids offering a tool that is certain to be refused. It fails open: if the
/// answer is unavailable (an older ServiceControl, bad credentials, an unreachable instance) nothing is hidden and the real error surfaces when a
/// tool is used.
/// <para>
/// Answers are remembered per caller. A host that serves one identity (stdio) has a single entry; the HTTP host, where each caller may reach
/// ServiceControl as a different person, must never show one caller another's permissions.
/// </para>
/// </summary>
public sealed partial class ToolAccess(ServiceControlClient client, TimeProvider timeProvider, ILogger<ToolAccess> logger) : IDisposable
{
    const int MaxCallers = 500;
    static readonly TimeSpan KnownFor = TimeSpan.FromSeconds(60);
    static readonly TimeSpan UnknownFor = TimeSpan.FromSeconds(15);
    static readonly TimeSpan PendingFor = TimeSpan.FromSeconds(2);
    static readonly TimeSpan NotSupportedFor = TimeSpan.FromMinutes(5);

    sealed record Entry(AccessSnapshot Snapshot, DateTimeOffset Until);

    readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);
    readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>Permissions of the single identity of a single-user host.</summary>
    public ValueTask<AccessSnapshot> GetAsync(CancellationToken cancellationToken) => GetAsync(null, cancellationToken);

    /// <summary>Permissions of the given caller, as ServiceControl would apply them to the credentials used on that caller's behalf.</summary>
    public async ValueTask<AccessSnapshot> GetAsync(ClaimsPrincipal? caller, CancellationToken cancellationToken)
    {
        var key = KeyOf(caller);
        if (TryGet(key, out var snapshot))
        {
            return snapshot;
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGet(key, out snapshot))
            {
                return snapshot;
            }

            var (loaded, lifetime) = await LoadAsync(cancellationToken).ConfigureAwait(false);
            var now = timeProvider.GetUtcNow();
            if (entries.Count >= MaxCallers)
            {
                foreach (var (candidate, entry) in entries)
                {
                    if (entry.Until <= now)
                    {
                        entries.TryRemove(candidate, out _);
                    }
                }

                if (entries.Count >= MaxCallers)
                {
                    entries.Clear();
                }
            }

            entries[key] = new Entry(loaded, now + lifetime);
            return loaded;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Whether the tool may be offered. Tools the registry does not know about are never hidden.</summary>
    public static bool IsAllowed(string tool, AccessSnapshot snapshot) => MissingRoutes(tool, snapshot).Count == 0;

    /// <summary>The routes a tool needs that the identity lacks; empty when the tool is allowed.</summary>
    public static IReadOnlyList<string> MissingRoutes(string tool, AccessSnapshot snapshot)
    {
        if (!snapshot.Restricted || !ToolRoutes.Required.TryGetValue(tool, out var required))
        {
            return [];
        }

        return [.. required.Where(r => !snapshot.Routes.Contains(ToolRoutes.NormalizeRequirement(r)))];
    }

    public void Dispose() => gate.Dispose();

    /// <summary>Identifies a caller by the subject of their token; a single-user host has no caller and shares one entry.</summary>
    static string KeyOf(ClaimsPrincipal? caller) =>
        caller?.FindFirst("sub")?.Value ?? caller?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? caller?.FindFirst("oid")?.Value ?? "-";

    bool TryGet(string key, out AccessSnapshot snapshot)
    {
        if (entries.TryGetValue(key, out var entry) && timeProvider.GetUtcNow() < entry.Until)
        {
            snapshot = entry.Snapshot;
            return true;
        }

        snapshot = AccessSnapshot.Unrestricted;
        return false;
    }

    async Task<(AccessSnapshot Snapshot, TimeSpan Lifetime)> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var mine = await client.GetMyRoutesAsync(cancellationToken).ConfigureAwait(false);
            var routes = (mine.Routes ?? []).Select(r => ToolRoutes.Normalize(r.Method, r.UrlTemplate)).ToHashSet(StringComparer.Ordinal);
            return (new AccessSnapshot(Restricted: true, routes, mine.Roles ?? []), KnownFor);
        }
        catch (ServiceControlApiException ex) when (ex.Kind == ServiceControlFailureKind.NotFound)
        {
            LogNotSupported();
            return (AccessSnapshot.Unrestricted, NotSupportedFor);
        }
        catch (ServiceControlApiException ex)
        {
            LogUnknown(ex.Kind.ToString(), ex.Message);

            // While a sign-in is pending, ask again soon: as soon as the person approves, their real permissions should apply.
            return (AccessSnapshot.Unrestricted, ex.Kind == ServiceControlFailureKind.CredentialsUnavailable ? PendingFor : UnknownFor);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "ServiceControl does not offer /api/my/routes (versions before 6.18); tools are not filtered by role. ServiceControl still enforces its own permissions.")]
    partial void LogNotSupported();

    [LoggerMessage(Level = LogLevel.Information, Message = "Could not determine which tools this identity may use ({Kind}); offering all tools. {Message}")]
    partial void LogUnknown(string kind, string message);
}
