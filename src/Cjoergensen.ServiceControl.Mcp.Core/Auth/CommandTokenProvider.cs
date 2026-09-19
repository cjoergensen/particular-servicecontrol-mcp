using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Cjoergensen.ServiceControl.Mcp.Auth;

/// <summary>
/// Obtains a token by running a configured command, for example the Azure CLI. This reuses whatever login the user
/// already has, so it needs no client registration at the identity provider. The command is started directly (never
/// through a shell) and its output is treated as a secret.
/// </summary>
public sealed partial class CommandTokenProvider : ITokenProvider, IDisposable
{
    static readonly TimeSpan RefreshSkew = TimeSpan.FromSeconds(60);
    static readonly TimeSpan FallbackLifetime = TimeSpan.FromMinutes(5);
    static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    readonly string command;
    readonly string[] arguments;
    readonly TimeProvider timeProvider;
    readonly ILogger<CommandTokenProvider> logger;
    readonly SemaphoreSlim gate = new(1, 1);

    string? cachedToken;
    DateTimeOffset cachedUntil;

    public CommandTokenProvider(string command, string[] arguments, TimeProvider timeProvider, ILogger<CommandTokenProvider> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        this.command = command;
        this.arguments = arguments;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public async ValueTask<string?> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (TryGetCached(out var token))
        {
            return token;
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetCached(out token))
            {
                return token;
            }

            var output = await RunAsync(cancellationToken).ConfigureAwait(false);
            var fresh = ExtractToken(output);
            var now = timeProvider.GetUtcNow();
            var expiry = JwtExpiry.TryRead(fresh) ?? now + FallbackLifetime;

            cachedToken = fresh;
            cachedUntil = expiry - RefreshSkew;
            return fresh;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose() => gate.Dispose();

    public void Invalidate()
    {
        gate.Wait();
        try
        {
            cachedToken = null;
        }
        finally
        {
            gate.Release();
        }
    }

    bool TryGetCached(out string? token)
    {
        token = cachedToken;
        return token is not null && timeProvider.GetUtcNow() < cachedUntil;
    }

    async Task<string> RunAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(command)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new TokenAcquisitionException($"The token command '{command}' could not be started: {ex.Message}", ex);
        }

        process.StandardInput.Close();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);

        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false);
            var error = await stderr.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                // stderr can help diagnose a failed login; stdout might contain a token, so it is never logged.
                LogCommandFailed(command, process.ExitCode, Truncate(error));
                throw new TokenAcquisitionException($"The token command '{command}' failed with exit code {process.ExitCode}. {Truncate(error)}".TrimEnd());
            }

            return output;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TokenAcquisitionException($"The token command '{command}' did not finish within {CommandTimeout.TotalSeconds:0} seconds.");
        }
    }

    internal static string ExtractToken(string output)
    {
        var trimmed = output.Trim();
        if (trimmed.Length == 0)
        {
            throw new TokenAcquisitionException("The token command produced no output.");
        }

        if (trimmed[0] != '{')
        {
            return trimmed;
        }

        // Some tools print a JSON document, for example `az account get-access-token` without --query.
        try
        {
            using var document = JsonDocument.Parse(trimmed);
            foreach (var name in new[] { "accessToken", "access_token", "token" })
            {
                if (document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
                    value.GetString() is { Length: > 0 } token)
                {
                    return token;
                }
            }
        }
        catch (JsonException)
        {
            // Fall through to the error below.
        }

        throw new TokenAcquisitionException("The token command printed JSON without an accessToken, access_token or token property.");
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Token command '{Command}' exited with code {ExitCode}: {Error}")]
    partial void LogCommandFailed(string command, int exitCode, string error);

    static string Truncate(string value) => value.Length <= 300 ? value.Trim() : value[..300].Trim() + "...";

    static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The process already exited.
        }
    }
}

/// <summary>A token could not be obtained. The message is safe to show to the user and the LLM.</summary>
public sealed class TokenAcquisitionException : Exception
{
    public TokenAcquisitionException(string message) : base(message)
    {
    }

    public TokenAcquisitionException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
