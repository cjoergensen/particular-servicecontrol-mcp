using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Cjoergensen.ServiceControl.Mcp.Client;

/// <summary>One page of a list response, with what the server reported about the whole result set.</summary>
/// <param name="Items">The items on this page.</param>
/// <param name="TotalCount">Total number of matching items across all pages, when the server reports it.</param>
/// <param name="IncompleteInstances">
/// Instances (for example audit instances) that could not answer, as <c>instanceId:reason</c>. Non-empty means the result
/// is missing data, which callers must not present as complete.
/// </param>
public sealed record ApiPage<T>(IReadOnlyList<T> Items, long? TotalCount, IReadOnlyList<string> IncompleteInstances);

/// <summary>Shared HTTP plumbing: JSON options, failure mapping and paging/incomplete-result header parsing.</summary>
public abstract class ApiClientBase(HttpClient http, JsonSerializerOptions jsonOptions)
{
    const string TotalCountHeader = "Total-Count";
    const string IncompleteResultsHeader = "X-Particular-Incomplete-Results";

    protected HttpClient Http { get; } = http;

    protected async Task<ApiPage<T>> GetPageAsync<T>(string relativeUri, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, relativeUri, content: null, cancellationToken).ConfigureAwait(false);
        var items = await ReadJsonAsync<List<T>>(response, cancellationToken).ConfigureAwait(false) ?? [];
        return new ApiPage<T>(items, ReadTotalCount(response), ReadIncomplete(response));
    }

    protected async Task<T> GetAsync<T>(string relativeUri, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, relativeUri, content: null, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync<T>(response, cancellationToken).ConfigureAwait(false)
            ?? throw new ServiceControlApiException(ServiceControlFailureKind.Other, "ServiceControl returned an empty response.");
    }

    /// <summary>Like <see cref="GetAsync{T}"/> but returns <c>null</c> when ServiceControl answers 204 No Content, which it does for an item that does not exist.</summary>
    protected async Task<T?> GetOrNullAsync<T>(string relativeUri, CancellationToken cancellationToken)
        where T : class
    {
        using var response = await SendAsync(HttpMethod.Get, relativeUri, content: null, cancellationToken).ConfigureAwait(false);
        return response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0
            ? null
            : await ReadJsonAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends a request whose successful answer carries nothing we need (ServiceControl typically replies 202 Accepted).</summary>
    protected async Task SendAcceptedAsync(HttpMethod method, string relativeUri, HttpContent? content, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(method, relativeUri, content, cancellationToken).ConfigureAwait(false);
    }

    protected async Task<HttpResponseMessage> SendAsync(HttpMethod method, string relativeUri, HttpContent? content, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, relativeUri) { Content = content };

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ServiceControlApiException(
                ServiceControlFailureKind.Unreachable,
                $"Could not reach ServiceControl at {Http.BaseAddress}: {ex.Message}. Check the configured URL, that the instance is running, and its TLS certificate.",
                ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ServiceControlApiException(
                ServiceControlFailureKind.Timeout,
                $"ServiceControl at {Http.BaseAddress} did not answer within {Http.Timeout.TotalSeconds:0} seconds.",
                ex);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            throw await MapFailureAsync(response, cancellationToken).ConfigureAwait(false);
        }
    }

    async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(jsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new ServiceControlApiException(
                ServiceControlFailureKind.Other,
                "ServiceControl returned a response this server could not understand. The instance may be a different version than this tool supports.",
                ex);
        }
    }

    static async Task<ServiceControlApiException> MapFailureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        switch (response.StatusCode)
        {
            case HttpStatusCode.Unauthorized:
                return new ServiceControlApiException(
                    ServiceControlFailureKind.Unauthorized,
                    "ServiceControl rejected the credentials (401 Unauthorized). Authentication is enabled on the instance: configure a token " +
                    "(Auth.Token), a token command (Auth.TokenCommand) or client credentials for the audience the instance expects.");
            case HttpStatusCode.Forbidden:
                return new ServiceControlApiException(
                    ServiceControlFailureKind.Forbidden,
                    "ServiceControl denied the request (403 Forbidden). The signed-in identity is authenticated but lacks the role required for this " +
                    "operation. Reading needs the reader role; retry, archive and dismiss need the writer role.");
            case HttpStatusCode.NotFound:
                return new ServiceControlApiException(ServiceControlFailureKind.NotFound, "ServiceControl reported that the requested item was not found (404).");
        }

        var detail = await ReadSnippetAsync(response, cancellationToken).ConfigureAwait(false);
        var kind = status is >= 400 and < 500 ? ServiceControlFailureKind.BadRequest : ServiceControlFailureKind.ServerError;
        return new ServiceControlApiException(kind, $"ServiceControl returned {status} {response.ReasonPhrase}.{(detail.Length > 0 ? " " + detail : string.Empty)}");
    }

    static async Task<string> ReadSnippetAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            text = text.Trim();
            return text.Length <= 300 ? text : text[..300] + "...";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return string.Empty;
        }
    }

    static long? ReadTotalCount(HttpResponseMessage response) =>
        response.Headers.TryGetValues(TotalCountHeader, out var values) &&
        long.TryParse(values.FirstOrDefault(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var total)
            ? total
            : null;

    static string[] ReadIncomplete(HttpResponseMessage response) =>
        response.Headers.TryGetValues(IncompleteResultsHeader, out var values)
            ? [.. values.SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Select(DescribeIncomplete)]
            : [];

    /// <summary>
    /// ServiceControl names an instance that did not answer as <c>id:reason</c>, where the id is the base64 of the instance URL.
    /// Decode it so a person (or an LLM) can tell which instance is meant; anything unrecognised is passed through unchanged.
    /// </summary>
    internal static string DescribeIncomplete(string entry)
    {
        var separator = entry.LastIndexOf(':');
        if (separator <= 0)
        {
            return entry;
        }

        try
        {
            var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(entry[..separator]));
            if (Uri.TryCreate(decoded, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                return $"{decoded} ({entry[(separator + 1)..]})";
            }
        }
        catch (FormatException)
        {
            // Not base64: the id is already readable.
        }

        return entry;
    }
}
