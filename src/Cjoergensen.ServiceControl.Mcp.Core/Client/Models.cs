using System.Text.Json;

namespace Cjoergensen.ServiceControl.Mcp.Client;

// Wire models for the ServiceControl primary/audit API (snake_case JSON). Kept deliberately tolerant: enum-like values
// are strings and unknown members are ignored, because ServiceControl adds fields over time.

public sealed record EndpointDetails(string? Name, Guid? HostId, string? Host);

public sealed record ExceptionDetails(string? ExceptionType, string? Message, string? Source, string? StackTrace);

public sealed record FailedMessageView(
    string Id,
    string? MessageType,
    DateTime? TimeSent,
    bool IsSystemMessage,
    ExceptionDetails? Exception,
    string? MessageId,
    int NumberOfProcessingAttempts,
    string? Status,
    EndpointDetails? SendingEndpoint,
    EndpointDetails? ReceivingEndpoint,
    string? QueueAddress,
    DateTime TimeOfFailure,
    DateTime LastModified,
    bool Edited,
    string? EditOf);

public sealed record FailureDetails(string? AddressOfFailingEndpoint, DateTime? TimeOfFailure, ExceptionDetails? Exception);

public sealed record ProcessingAttempt(
    string? MessageId,
    DateTime? AttemptedAt,
    FailureDetails? FailureDetails,
    Dictionary<string, JsonElement>? MessageMetadata,
    Dictionary<string, string>? Headers);

public sealed record FailureGroupReference(string? Id, string? Title, string? Type);

public sealed record FailedMessageDetail(
    string? Id,
    string? UniqueMessageId,
    string? Status,
    List<ProcessingAttempt>? ProcessingAttempts,
    List<FailureGroupReference>? FailureGroups);

public sealed record GroupOperation(
    string? Id,
    string? Title,
    string? Type,
    int Count,
    int? OperationMessagesCompletedCount,
    string? Comment,
    DateTime? First,
    DateTime? Last,
    string? OperationStatus,
    bool? OperationFailed,
    double OperationProgress,
    int? OperationRemainingCount,
    DateTime? OperationStartTime,
    DateTime? OperationCompletionTime,
    bool NeedUserAcknowledgement);

public sealed record HeartbeatInformation(DateTime? LastReportAt, string? ReportedStatus);

public sealed record EndpointView(
    Guid Id,
    string? Name,
    string? HostDisplayName,
    bool Monitored,
    bool MonitorHeartbeat,
    HeartbeatInformation? HeartbeatInformation,
    bool IsSendingHeartbeats);

public sealed record HeartbeatStats(int Active, int Failing);

public sealed record CustomCheckView(
    string? Id,
    string? CustomCheckId,
    string? Category,
    string? Status,
    DateTime? ReportedAt,
    string? FailureReason,
    EndpointDetails? OriginatingEndpoint,
    bool Internal);

public sealed record SagaHistory(Guid Id, Guid SagaId, string? SagaType, List<SagaStateChange>? Changes);

public sealed record SagaStateChange(
    DateTime? StartTime,
    DateTime? FinishTime,
    string? Status,
    string? StateAfterChange,
    JsonElement? InitiatingMessage,
    JsonElement? OutgoingMessages,
    string? Endpoint);

public sealed record AuditMessageView(
    string? Id,
    string? MessageId,
    string? MessageType,
    EndpointDetails? SendingEndpoint,
    EndpointDetails? ReceivingEndpoint,
    DateTime? TimeSent,
    DateTime? ProcessedAt,
    TimeSpan? CriticalTime,
    TimeSpan? ProcessingTime,
    TimeSpan? DeliveryTime,
    bool IsSystemMessage,
    string? ConversationId,
    string? Status,
    string? MessageIntent,
    int BodySize,
    string? InstanceId);

/// <summary>Anonymous discovery document of the primary instance: tells a client whether and how to authenticate.</summary>
public sealed record AuthConfiguration(
    bool Enabled,
    bool RoleBasedAuthorizationEnabled,
    string? ClientId,
    string? Authority,
    string? Audience,
    string? ApiScopes,
    string? Scopes);

/// <summary>One API route the caller's token may use, as reported by <c>GET /api/my/routes</c>: the HTTP method and the route template.</summary>
public sealed record RouteEntry(string Method, string UrlTemplate);

/// <summary>The caller's roles and the routes those roles allow on this instance. Only recognised roles (reader, writer, admin) grant routes.</summary>
public sealed record MyRoutes(string[]? Roles, RouteEntry[]? Routes);

// Wire models for the monitoring instance (camelCase JSON).

public sealed record MonitoredValues(double? Average, double[]? Points);

public sealed record MonitoredEndpoint(
    string? Name,
    bool IsStale,
    string[]? EndpointInstanceIds,
    Dictionary<string, MonitoredValues>? Metrics,
    int DisconnectedCount,
    int ConnectedCount);

public sealed record MonitoredMetricDigest(double? Latest, double? Average);

public sealed record MonitoredEndpointDigest(Dictionary<string, MonitoredMetricDigest>? Metrics);

public sealed record MonitoredEndpointInstance(string? Name, string? Id, bool IsStale, Dictionary<string, MonitoredValues>? Metrics);

public sealed record MonitoredEndpointMessageType(string? Id, string? TypeName, string? AssemblyName, Dictionary<string, MonitoredValues>? Metrics);

public sealed record MonitoredEndpointDetails(
    MonitoredEndpointDigest? Digest,
    MonitoredEndpointInstance[]? Instances,
    MonitoredEndpointMessageType[]? MessageTypes);
