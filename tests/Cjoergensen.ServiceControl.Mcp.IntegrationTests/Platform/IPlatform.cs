namespace Cjoergensen.ServiceControl.Mcp.IntegrationTests.Platform;

/// <summary>What the MCP harness needs to know about the platform it is pointed at.</summary>
public interface IPlatform
{
    string PrimaryUrl { get; }

    string? MonitoringUrl { get; }
}
