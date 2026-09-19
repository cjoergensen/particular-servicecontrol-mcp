using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cjoergensen.ServiceControl.Mcp.IntegrationTests.Platform;

/// <summary>
/// The Keycloak realm the secured platform uses, imported at startup. It follows Particular's Keycloak guidance: ServiceControl's audience is a
/// client, access tokens carry that audience through an audience mapper, and ServiceControl's built-in roles are ordinary realm roles that
/// Keycloak puts in <c>realm_access.roles</c>.
/// </summary>
public static class KeycloakRealm
{
    public const string Name = "mcp";

    /// <summary>The audience ServiceControl validates tokens against.</summary>
    public const string Audience = "servicecontrol";

    // Bots that use the client-credentials grant: no user involved. The role each one holds is in its name.
    public const string ReaderBot = "mcp-reader-bot";
    public const string WriterBot = "mcp-writer-bot";
    public const string BotSecret = "test-only-bot-secret";

    /// <summary>A public client for people: supports the device authorization grant and, for tests only, the password grant.</summary>
    public const string CliClient = "mcp-cli";

    /// <summary>
    /// The HTTP host's own client: the audience callers' tokens are issued for, and the confidential client that exchanges those tokens for ServiceControl
    /// tokens. Its id doubles as the audience because Keycloak's token exchange requires the requesting client to be the subject token's audience.
    /// </summary>
    public const string HttpServerClient = "mcp-http-server";
    public const string HttpServerSecret = "test-only-http-server-secret";

    /// <summary>A public client for people calling the HTTP host: its tokens are for the MCP server only, never for ServiceControl.</summary>
    public const string HttpCallerClient = "mcp-http-cli";

    public const string WriterUser = "alice";
    public const string ReaderUser = "bob";
    public const string UserPassword = "test-only-password";

    public static string Json()
    {
        JsonObject AudienceMapper() => new()
        {
            ["name"] = "servicecontrol-audience",
            ["protocol"] = "openid-connect",
            ["protocolMapper"] = "oidc-audience-mapper",
            ["config"] = new JsonObject
            {
                ["included.client.audience"] = Audience,
                ["access.token.claim"] = "true",
                ["id.token.claim"] = "false"
            }
        };

        JsonObject Bot(string id) => new()
        {
            ["clientId"] = id,
            ["enabled"] = true,
            ["publicClient"] = false,
            ["secret"] = BotSecret,
            ["serviceAccountsEnabled"] = true,
            ["standardFlowEnabled"] = false,
            ["directAccessGrantsEnabled"] = false,
            ["protocolMappers"] = new JsonArray(AudienceMapper())
        };

        // Alice may hold long-lived (offline) tokens, as ServiceControl's advertised scopes assume; Bob may not, to exercise the fallback.
        JsonObject User(string name, params string[] roles) => new()
        {
            ["username"] = name,
            ["enabled"] = true,
            ["emailVerified"] = true,
            ["firstName"] = name,
            ["lastName"] = "Tester",
            ["email"] = $"{name}@example.test",
            ["credentials"] = new JsonArray(new JsonObject { ["type"] = "password", ["value"] = UserPassword, ["temporary"] = false }),
            ["realmRoles"] = new JsonArray([.. roles.Select(r => JsonValue.Create(r))])
        };

        JsonObject ServiceAccount(string clientId, string role) => new()
        {
            ["username"] = $"service-account-{clientId}",
            ["enabled"] = true,
            ["serviceAccountClientId"] = clientId,
            ["realmRoles"] = new JsonArray(role)
        };

        var realm = new JsonObject
        {
            ["realm"] = Name,
            ["enabled"] = true,
            ["sslRequired"] = "external",
            ["accessTokenLifespan"] = 300,
            ["roles"] = new JsonObject
            {
                ["realm"] = new JsonArray(
                    new JsonObject { ["name"] = "reader" },
                    new JsonObject { ["name"] = "writer" },
                    new JsonObject { ["name"] = "admin" })
            },
            ["clients"] = new JsonArray(
                // The audience target: never used to log in, it only gives tokens an "aud" ServiceControl accepts.
                new JsonObject { ["clientId"] = Audience, ["enabled"] = true, ["bearerOnly"] = true },
                Bot(ReaderBot),
                Bot(WriterBot),
                new JsonObject
                {
                    ["clientId"] = HttpServerClient,
                    ["enabled"] = true,
                    ["publicClient"] = false,
                    ["secret"] = HttpServerSecret,
                    ["standardFlowEnabled"] = false,
                    ["directAccessGrantsEnabled"] = false,
                    ["attributes"] = new JsonObject { ["standard.token.exchange.enabled"] = "true" },

                    // Keycloak only lets a client ask an exchange for audiences it is allowed to obtain. This grants that for ServiceControl, and only for
                    // tokens issued to this client (that is, the result of an exchange): callers' own tokens are still for the MCP server alone.
                    ["protocolMappers"] = new JsonArray(AudienceMapper())
                },
                new JsonObject
                {
                    ["clientId"] = HttpCallerClient,
                    ["enabled"] = true,
                    ["publicClient"] = true,
                    ["standardFlowEnabled"] = true,
                    ["directAccessGrantsEnabled"] = true,
                    ["protocolMappers"] = new JsonArray(new JsonObject
                    {
                        ["name"] = "mcp-server-audience",
                        ["protocol"] = "openid-connect",
                        ["protocolMapper"] = "oidc-audience-mapper",
                        ["config"] = new JsonObject
                        {
                            ["included.client.audience"] = HttpServerClient,
                            ["access.token.claim"] = "true",
                            ["id.token.claim"] = "false"
                        }
                    })
                },
                new JsonObject
                {
                    ["clientId"] = CliClient,
                    ["enabled"] = true,
                    ["publicClient"] = true,
                    ["standardFlowEnabled"] = true,
                    ["directAccessGrantsEnabled"] = true,
                    ["attributes"] = new JsonObject { ["oauth2.device.authorization.grant.enabled"] = "true" },
                    ["protocolMappers"] = new JsonArray(AudienceMapper())
                }),
            ["users"] = new JsonArray(
                User(WriterUser, "writer", "offline_access"),
                User(ReaderUser, "reader"),
                ServiceAccount(ReaderBot, "reader"),
                ServiceAccount(WriterBot, "writer"))
        };

        return realm.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
