using System.Net;
using System.Text;

namespace Cjoergensen.ServiceControl.Mcp.Tests.Support;

/// <summary>
/// An in-memory ServiceControl: answers requests from registered routes and records every request it saw, so tests can assert
/// on exactly what was sent. Payloads mirror real ServiceControl JSON (snake_case for primary/audit, camelCase for monitoring).
/// </summary>
public sealed class FakeServiceControl : HttpMessageHandler
{
    readonly List<(Func<HttpRequestMessage, bool> Match, Func<HttpRequestMessage, HttpResponseMessage> Respond)> routes = [];

    public List<RecordedRequest> Requests { get; } = [];

    public FakeServiceControl Route(string method, string pathAndQueryPrefix, string json, HttpStatusCode status = HttpStatusCode.OK, params (string Name, string Value)[] headers) =>
        Route(method, pathAndQueryPrefix, _ => Json(json, status, headers));

    public FakeServiceControl Route(string method, string pathAndQueryPrefix, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        routes.Add((
            request => request.Method.Method == method &&
                       (request.RequestUri!.PathAndQuery.StartsWith(pathAndQueryPrefix, StringComparison.Ordinal)),
            respond));
        return this;
    }

    /// <summary>Like <see cref="Route(string, string, string, HttpStatusCode, (string, string)[])"/> but takes precedence over routes registered earlier.</summary>
    public FakeServiceControl Override(string method, string pathAndQueryPrefix, string json, HttpStatusCode status = HttpStatusCode.OK, params (string Name, string Value)[] headers)
    {
        routes.Insert(0, (
            request => request.Method.Method == method && request.RequestUri!.PathAndQuery.StartsWith(pathAndQueryPrefix, StringComparison.Ordinal),
            _ => Json(json, status, headers)));
        return this;
    }

    public FakeServiceControl Fail(Exception exception)
    {
        routes.Insert(0, (_ => true, _ => throw exception));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(new RecordedRequest(
            request.Method.Method,
            request.RequestUri!,
            request.Headers.Authorization?.ToString(),
            request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));

        foreach (var (match, respond) in routes)
        {
            if (match(request))
            {
                return respond(request);
            }
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("no route in fake", Encoding.UTF8) };
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        foreach (var (name, value) in headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

        return response;
    }
}

public sealed record RecordedRequest(string Method, Uri Uri, string? Authorization, string? Body)
{
    public string PathAndQuery => Uri.PathAndQuery;
}
