using System.Diagnostics;

namespace Cjoergensen.ServiceControl.Mcp.IntegrationTests.Platform;

/// <summary>
/// ServiceControl ingests messages asynchronously, so a test cannot assert right after sending. This polls until a condition holds
/// and, on timeout, fails with the last value it saw, so the failure says what the system actually looked like.
/// </summary>
public static class Eventually
{
    public static async Task<T> Async<T>(
        Func<Task<T>> probe, Func<T, bool> isDone, string description, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(60);
        var watch = Stopwatch.StartNew();
        T last = default!;
        Exception? lastError = null;

        while (watch.Elapsed < limit)
        {
            try
            {
                last = await probe();
                lastError = null;
                if (isDone(last))
                {
                    return last;
                }
            }
            catch (McpToolFailedException ex)
            {
                // The platform may still be settling; remember the reason and keep trying.
                lastError = ex;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken);
        }

        throw new TimeoutException(
            $"Timed out after {limit.TotalSeconds:0}s waiting for: {description}. " +
            (lastError is null ? $"Last observed: {last}" : $"Last error: {lastError.Message}"));
    }
}
