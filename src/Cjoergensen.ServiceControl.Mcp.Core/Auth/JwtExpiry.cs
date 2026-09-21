using System.Text.Json;

namespace Cjoergensen.ServiceControl.Mcp.Auth;

/// <summary>Reads the <c>exp</c> claim of a JWT without validating it. Used only to decide when to refresh a cached token.</summary>
internal static class JwtExpiry
{
    public static DateTimeOffset? TryRead(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            return null;
        }

        try
        {
            var payload = Convert.FromBase64String(PadBase64(parts[1].Replace('-', '+').Replace('_', '/')));
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("exp", out var exp) &&
                exp.TryGetInt64(out var seconds))
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds);
            }
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentOutOfRangeException)
        {
            // Not a JWT we can read; the caller falls back to a fixed cache lifetime.
        }

        return null;
    }

    static string PadBase64(string value) => (value.Length % 4) switch
    {
        2 => value + "==",
        3 => value + "=",
        _ => value
    };
}
