using Cjoergensen.ServiceControl.Mcp;

namespace Cjoergensen.ServiceControl.Mcp.Http;

/// <summary>Refuses configurations that would expose ServiceControl or callers' tokens unsafely, with a message saying how to fix each.</summary>
static class HostSafety
{
    public static IReadOnlyList<string> Problems(HttpHostOptions http, ServiceControlMcpOptions sc, IEnumerable<string> urls)
    {
        var problems = new List<string>();
        var listening = urls.Select(Describe).ToList();
        var nonLoopback = listening.Where(u => !u.Loopback).ToList();
        var mode = sc.Auth.EffectiveMode;

        if (http.AllowAnonymous)
        {
            if (nonLoopback.Count > 0)
            {
                problems.Add($"Http:AllowAnonymous is set but the server listens on {string.Join(", ", nonLoopback.Select(u => u.Url))}. Anonymous access is only allowed on loopback: bind to http://127.0.0.1:PORT or turn authentication on.");
            }

            if (mode == AuthMode.TokenExchange)
            {
                problems.Add("Auth:Mode TokenExchange exchanges each caller's token, so it cannot be used with Http:AllowAnonymous.");
            }
        }
        else if (string.IsNullOrWhiteSpace(http.Audience) && http.Audiences.Length == 0)
        {
            problems.Add("Http:Audience is not set. Set it to the audience callers' tokens are issued for (this server), so tokens meant for other services are refused.");
        }

        if (!http.AllowInsecureTransport && nonLoopback.Any(u => !u.Https))
        {
            problems.Add($"The server listens on plain HTTP at {string.Join(", ", nonLoopback.Where(u => !u.Https).Select(u => u.Url))}, which would expose callers' bearer tokens. Use HTTPS, or set Http:AllowInsecureTransport if TLS is terminated in front of this server.");
        }

        if (nonLoopback.Count > 0 && (http.AllowedHosts is null || http.AllowedHosts.Length == 0))
        {
            problems.Add("The server listens on a non-loopback address, so Http:AllowedHosts must list the host names it is reached by (this guards against DNS rebinding).");
        }

        return problems;
    }

    /// <summary>The addresses the host will listen on according to its configuration, using the same settings ASP.NET Core reads (the default applies when none is set).</summary>
    public static IEnumerable<string> ConfiguredUrls(IConfiguration hosting)
    {
        var urls = (hosting["urls"] ?? hosting["DOTNET_URLS"] ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        // A bare port means every interface.
        urls.AddRange((hosting["HTTP_PORTS"] ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(port => $"http://+:{port}"));
        urls.AddRange((hosting["HTTPS_PORTS"] ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(port => $"https://+:{port}"));

        return urls.Count > 0 ? urls : ["http://localhost:5000"];
    }

    static (string Url, bool Loopback, bool Https) Describe(string url)
    {
        // Kestrel wildcard forms such as http://+:5000 and http://*:5000 are not valid URIs but mean "every interface".
        var normalized = url.Replace("://+", "://0.0.0.0", StringComparison.Ordinal).Replace("://*", "://0.0.0.0", StringComparison.Ordinal);
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            return (url, Loopback: false, Https: url.StartsWith("https", StringComparison.OrdinalIgnoreCase));
        }

        var loopback = uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        return (url, loopback, uri.Scheme == Uri.UriSchemeHttps);
    }
}
