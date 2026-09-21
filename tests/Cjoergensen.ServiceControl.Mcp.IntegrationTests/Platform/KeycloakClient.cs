using System.Net.Http.Json;
using System.Text.Json;
using Cjoergensen.ServiceControl.Mcp.Client;

namespace Cjoergensen.ServiceControl.Mcp.IntegrationTests.Platform;

/// <summary>Gets tokens from the test realm the way real clients do, so tests can hand them to the MCP server.</summary>
public sealed class KeycloakClient : IDisposable
{
    readonly HttpClient http;
    readonly string tokenEndpoint;
    readonly string caCertificatePath;

    public KeycloakClient(string authority, string caCertificatePath)
    {
        this.caCertificatePath = caCertificatePath;
        http = new HttpClient(TlsTrust.CreateHandler(caCertificatePath));
        Authority = authority;
        tokenEndpoint = $"{authority}/protocol/openid-connect/token";
    }

    public string Authority { get; }

    /// <summary>A token for a person, using the password grant. For tests only: it stands in for an interactive login.</summary>
    public Task<string> UserTokenAsync(string username, CancellationToken cancellationToken) =>
        RequestAsync(
            new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["client_id"] = KeycloakRealm.CliClient,
                ["username"] = username,
                ["password"] = KeycloakRealm.UserPassword
            },
            cancellationToken);

    /// <summary>A token for a person, issued for the MCP HTTP server (and not for ServiceControl), as a real MCP client would obtain it.</summary>
    public Task<string> UserTokenForMcpServerAsync(string username, CancellationToken cancellationToken) =>
        RequestAsync(
            new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["client_id"] = KeycloakRealm.HttpCallerClient,
                ["username"] = username,
                ["password"] = KeycloakRealm.UserPassword
            },
            cancellationToken);

    public Task<string> BotTokenAsync(string clientId, CancellationToken cancellationToken) =>
        RequestAsync(
            new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId,
                ["client_secret"] = KeycloakRealm.BotSecret
            },
            cancellationToken);

    /// <summary>
    /// Plays the part of the person who opens the verification page in a browser: signs in and approves the device. Keycloak's pages are HTML forms,
    /// so this submits each form it is shown (with its hidden fields) until the flow reports success.
    /// </summary>
    public async Task ApproveDeviceAsync(string verificationUri, string username, string password, CancellationToken cancellationToken)
    {
        var handler = TlsTrust.CreateHandler(caCertificatePath);
        handler.UseCookies = true;
        handler.CookieContainer = new System.Net.CookieContainer();
        using var browser = new HttpClient(handler);

        var page = await browser.GetAsync(verificationUri, cancellationToken);
        for (var step = 0; step < 6; step++)
        {
            var html = await page.Content.ReadAsStringAsync(cancellationToken);
            if (html.Contains("Device Login Successful", StringComparison.OrdinalIgnoreCase) || html.Contains("You may now close", StringComparison.OrdinalIgnoreCase) ||
                html.Contains("has been successfully", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var form = HtmlForm.Parse(html, page.RequestMessage!.RequestUri!);
            var fields = new Dictionary<string, string>(form.Fields);
            if (fields.ContainsKey("username"))
            {
                fields["username"] = username;
                fields["password"] = password;
            }
            else if (form.Buttons.TryGetValue("accept", out var accept))
            {
                fields["accept"] = accept;
            }

            page = await browser.PostAsync(form.Action, new FormUrlEncodedContent(fields), cancellationToken);
        }

        throw new InvalidOperationException("Keycloak did not report the device sign-in as successful. Last page: " +
            (await page.Content.ReadAsStringAsync(cancellationToken))[..Math.Min(400, (await page.Content.ReadAsStringAsync(cancellationToken)).Length)]);
    }

    async Task<string> RequestAsync(Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsync(tokenEndpoint, new FormUrlEncodedContent(form), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Keycloak refused the token request ({(int)response.StatusCode}): {body}");
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("access_token").GetString()!;
    }

    public void Dispose() => http.Dispose();
}

/// <summary>Just enough HTML form parsing to drive Keycloak's login and consent pages.</summary>
sealed record HtmlForm(Uri Action, Dictionary<string, string> Fields, Dictionary<string, string> Buttons)
{
    public static HtmlForm Parse(string html, Uri pageUri)
    {
        var form = System.Text.RegularExpressions.Regex.Match(html, "<form[^>]*action=\"([^\"]*)\"[^>]*>(.*?)</form>", System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!form.Success)
        {
            throw new InvalidOperationException("Expected a form on the Keycloak page but found none: " + html[..Math.Min(400, html.Length)]);
        }

        var action = new Uri(pageUri, System.Net.WebUtility.HtmlDecode(form.Groups[1].Value));
        var fields = new Dictionary<string, string>();
        var buttons = new Dictionary<string, string>();
        foreach (System.Text.RegularExpressions.Match input in System.Text.RegularExpressions.Regex.Matches(form.Groups[2].Value, "<input[^>]*>", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            var tag = input.Value;
            var name = Attribute(tag, "name");
            if (name is null)
            {
                continue;
            }

            var type = Attribute(tag, "type")?.ToLowerInvariant();
            var value = System.Net.WebUtility.HtmlDecode(Attribute(tag, "value") ?? string.Empty);
            if (type is "submit" or "button")
            {
                buttons[name] = value;
            }
            else
            {
                fields[name] = value;
            }
        }

        return new HtmlForm(action, fields, buttons);
    }

    static string? Attribute(string tag, string name)
    {
        var match = System.Text.RegularExpressions.Regex.Match(tag, name + "=\"([^\"]*)\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }
}
