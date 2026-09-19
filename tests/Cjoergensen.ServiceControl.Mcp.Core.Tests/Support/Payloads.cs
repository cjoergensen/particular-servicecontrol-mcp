namespace Cjoergensen.ServiceControl.Mcp.Tests.Support;

/// <summary>Sample payloads shaped like real ServiceControl responses (field names taken from the ServiceControl source).</summary>
public static class Payloads
{
    public const string FailedMessages = """
        [
          {
            "id": "b4f1a3d2-0000-0000-0000-000000000001",
            "message_type": "Billing.Messages.ChargeCustomer, Billing.Messages",
            "time_sent": "2026-09-19T08:00:00Z",
            "is_system_message": false,
            "exception": {
              "exception_type": "System.InvalidOperationException",
              "message": "Card gateway timed out",
              "source": "Billing",
              "stack_trace": "at Billing.Handler.Handle()"
            },
            "message_id": "11111111-1111-1111-1111-111111111111",
            "number_of_processing_attempts": 3,
            "status": "unresolved",
            "sending_endpoint": { "name": "Sales", "host_id": "22222222-2222-2222-2222-222222222222", "host": "sales-1" },
            "receiving_endpoint": { "name": "Billing", "host_id": "33333333-3333-3333-3333-333333333333", "host": "billing-1" },
            "queue_address": "Billing@machine",
            "time_of_failure": "2026-09-19T08:05:00Z",
            "last_modified": "2026-09-19T08:05:01Z",
            "edited": false
          }
        ]
        """;

    public const string FailedMessageDetail = """
        {
          "id": "FailedMessages/b4f1a3d2",
          "unique_message_id": "b4f1a3d2-0000-0000-0000-000000000001",
          "status": "unresolved",
          "processing_attempts": [
            {
              "message_id": "11111111-1111-1111-1111-111111111111",
              "attempted_at": "2026-09-19T08:05:00Z",
              "body": "SECRET-BODY-MUST-NOT-LEAK",
              "headers": { "NServiceBus.MessageId": "11111111-1111-1111-1111-111111111111" },
              "message_metadata": { "MessageId": "11111111-1111-1111-1111-111111111111" },
              "failure_details": {
                "address_of_failing_endpoint": "Billing@machine",
                "time_of_failure": "2026-09-19T08:05:00Z",
                "exception": {
                  "exception_type": "System.InvalidOperationException",
                  "message": "Card gateway timed out",
                  "source": "Billing",
                  "stack_trace": "at Billing.Handler.Handle()\nat NServiceBus.Pipeline()"
                }
              }
            }
          ],
          "failure_groups": [ { "id": "group-1", "title": "System.InvalidOperationException", "type": "Exception Type and Stack Trace" } ]
        }
        """;

    public const string Endpoints = """
        [
          { "id": "44444444-4444-4444-4444-444444444444", "name": "Sales", "host_display_name": "sales-1", "monitored": true, "monitor_heartbeat": true,
            "heartbeat_information": { "last_report_at": "2026-09-19T08:10:00Z", "reported_status": "beating" }, "is_sending_heartbeats": true },
          { "id": "55555555-5555-5555-5555-555555555555", "name": "Billing", "host_display_name": "billing-1", "monitored": true, "monitor_heartbeat": true,
            "heartbeat_information": { "last_report_at": "2026-09-19T07:00:00Z", "reported_status": "dead" }, "is_sending_heartbeats": false },
          { "id": "66666666-6666-6666-6666-666666666666", "name": "Shipping", "host_display_name": "shipping-1", "monitored": false, "monitor_heartbeat": false,
            "is_sending_heartbeats": false }
        ]
        """;

    public const string CustomChecks = """
        [
          { "id": "cc-1", "custom_check_id": "Database connectivity", "category": "Infrastructure", "status": "fail",
            "reported_at": "2026-09-19T08:00:00Z", "failure_reason": "Cannot open connection",
            "originating_endpoint": { "name": "Billing", "host_id": "33333333-3333-3333-3333-333333333333", "host": "billing-1" }, "internal": false }
        ]
        """;

    public const string FailureGroups = """
        [
          { "id": "small", "title": "System.TimeoutException", "type": "Exception Type and Stack Trace", "count": 2,
            "first": "2026-09-19T08:00:00Z", "last": "2026-09-19T08:30:00Z", "operation_progress": 0, "need_user_acknowledgement": false },
          { "id": "big", "title": "System.InvalidOperationException", "type": "Exception Type and Stack Trace", "count": 40,
            "first": "2026-09-19T07:00:00Z", "last": "2026-09-19T08:30:00Z", "operation_status": "RetryInProgress", "operation_progress": 0.5,
            "operation_remaining_count": 20, "operation_failed": false, "need_user_acknowledgement": false }
        ]
        """;

    public const string MonitoredEndpoints = """
        [
          { "name": "Billing", "isStale": false, "endpointInstanceIds": ["billing-1"],
            "metrics": { "queueLength": { "average": 12.5, "points": [10, 15] }, "processingTime": { "average": 40, "points": [35, 45] } },
            "disconnectedCount": 1, "connectedCount": 2 },
          { "name": "Sales", "isStale": true, "endpointInstanceIds": [], "metrics": {}, "disconnectedCount": 0, "connectedCount": 0 }
        ]
        """;

    public const string AuthDisabled = """{ "enabled": false, "role_based_authorization_enabled": false }""";
}
