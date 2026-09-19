using System.Globalization;

namespace Cjoergensen.ServiceControl.Mcp.Client;

/// <summary>Builds a query string, skipping parameters that have no value.</summary>
internal sealed class QueryBuilder
{
    readonly List<string> parts = [];

    public QueryBuilder Add(string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parts.Add($"{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value)}");
        }

        return this;
    }

    public QueryBuilder Add(string name, int? value) =>
        value is null ? this : Add(name, value.Value.ToString(CultureInfo.InvariantCulture));

    public QueryBuilder Add(string name, DateTime? value) =>
        value is null ? this : Add(name, value.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));

    public override string ToString() => parts.Count == 0 ? string.Empty : "?" + string.Join('&', parts);
}
