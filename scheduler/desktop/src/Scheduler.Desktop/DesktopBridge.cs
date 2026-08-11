using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scheduler.Desktop;

public interface IDesktopCommandDispatcher
{
    Task<JsonElement> DispatchAsync(string method, JsonElement payload, CancellationToken cancellationToken);
}

public sealed class DesktopBridgeException(string message) : Exception(message);

/// <summary>
/// Owns active bridge commands so a UI cancellation request can reach the
/// operation that owns the native solver process.
/// </summary>
public sealed class DesktopBridgeSession : IDisposable
{
    public const int ProtocolVersion = 1;
    public const int MaximumMessageCharacters = 40 * 1024 * 1024;

    private readonly ConcurrentDictionary<string, CancellationTokenSource> activeCommands = new(StringComparer.Ordinal);
    private readonly IDesktopActivityLogger activityLogger;
    private readonly IDesktopCommandDispatcher dispatcher;
    private readonly IDesktopShellController? shellController;
    private readonly SemaphoreSlim heavyCommandGate = new(1, 1);
    private int disposeRequested;
    private int gateDisposed;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public DesktopBridgeSession(
        IDesktopCommandDispatcher dispatcher,
        IDesktopActivityLogger? activityLogger = null,
        IDesktopShellController? shellController = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        this.dispatcher = dispatcher;
        this.activityLogger = activityLogger ?? NullDesktopActivityLogger.Instance;
        this.shellController = shellController;
    }

    public async Task<string> HandleAsync(string message)
    {
        var stopwatch = Stopwatch.StartNew();
        string requestId = string.Empty;
        var command = "invalid";
        var outcome = "failed";
        try
        {
            if (message.Length > MaximumMessageCharacters)
            {
                throw new DesktopBridgeException("Desktop bridge request is too large.");
            }

            var request = JsonSerializer.Deserialize<DesktopBridgeRequest>(message, SerializerOptions)
                ?? throw new DesktopBridgeException("Desktop bridge request is empty.");
            requestId = request.Id;
            ValidateRequest(request);
            command = NormalizeCommand(request.Method);

            if (string.Equals(request.Method, "cancel_command", StringComparison.Ordinal))
            {
                outcome = "completed";
                return CreateSuccessResponse(request.Id, CancelCommand(request.Payload));
            }

            if (string.Equals(request.Method, "set_theme", StringComparison.Ordinal))
            {
                outcome = "completed";
                return CreateSuccessResponse(request.Id, SetTheme(request.Payload));
            }

            if (string.Equals(request.Method, "desktop_ready", StringComparison.Ordinal))
            {
                outcome = "completed";
                shellController?.MarkFrontendReady();
                return CreateSuccessResponse(request.Id, JsonSerializer.SerializeToElement(new DesktopReadyResult(true), SerializerOptions));
            }

            if (!IsHeavyCommand(request.Method))
            {
                throw new DesktopBridgeException($"Unsupported desktop command '{request.Method}'.");
            }

            if (!heavyCommandGate.Wait(0))
            {
                throw new DesktopBridgeException("Một tác vụ xử lý thời khóa biểu đang chạy. Hãy chờ hoặc dừng tác vụ hiện tại.");
            }

            using var commandCancellation = new CancellationTokenSource();
            if (!activeCommands.TryAdd(request.Id, commandCancellation))
            {
                heavyCommandGate.Release();
                throw new DesktopBridgeException($"A desktop bridge command is already active for ID '{request.Id}'.");
            }

            JsonElement result;
            try
            {
                result = await dispatcher.DispatchAsync(request.Method, request.Payload, commandCancellation.Token);
            }
            finally
            {
                heavyCommandGate.Release();
                activeCommands.TryRemove(request.Id, out _);
                DisposeGateWhenIdle();
            }

            outcome = "completed";
            return JsonSerializer.Serialize(new DesktopBridgeResponse(ProtocolVersion, request.Id, true, result, null), SerializerOptions);
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            return CreateErrorResponse(requestId, "Desktop bridge command was cancelled.");
        }
        catch (Exception exception) when (exception is JsonException or DesktopBridgeException or ArgumentException)
        {
            outcome = "rejected";
            return CreateErrorResponse(requestId, exception.Message);
        }
        catch (Exception)
        {
            outcome = "failed";
            return CreateErrorResponse(requestId, "Desktop bridge command failed.");
        }
        finally
        {
            await RecordActivityAsync(requestId, command, outcome, stopwatch.Elapsed);
        }
    }

    public void CancelActiveCommands()
    {
        foreach (var cancellation in activeCommands.Values)
        {
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // A command completed while the window was closing.
            }
        }

    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposeRequested, 1) != 0)
        {
            return;
        }

        CancelActiveCommands();
        DisposeGateWhenIdle();
        GC.SuppressFinalize(this);
    }

    private void DisposeGateWhenIdle()
    {
        if (Volatile.Read(ref disposeRequested) != 0 &&
            activeCommands.IsEmpty &&
            Interlocked.Exchange(ref gateDisposed, 1) == 0)
        {
            heavyCommandGate.Dispose();
        }
    }

    private JsonElement CancelCommand(JsonElement payload)
    {
        var request = JsonSerializer.Deserialize<CancelCommandRequest>(payload.GetRawText(), SerializerOptions)
            ?? throw new DesktopBridgeException("Desktop cancellation payload is empty.");
        if (string.IsNullOrWhiteSpace(request.TargetId) || request.TargetId.Length > 128)
        {
            throw new DesktopBridgeException("Desktop cancellation target_id must contain 1 to 128 characters.");
        }

        if (!activeCommands.TryGetValue(request.TargetId, out var cancellation))
        {
            return JsonSerializer.SerializeToElement(new CancelCommandResult(false), SerializerOptions);
        }

        try
        {
            cancellation.Cancel();
            return JsonSerializer.SerializeToElement(new CancelCommandResult(true), SerializerOptions);
        }
        catch (ObjectDisposedException)
        {
            // The command completed after the lookup and before cancellation.
            return JsonSerializer.SerializeToElement(new CancelCommandResult(false), SerializerOptions);
        }
    }

    private JsonElement SetTheme(JsonElement payload)
    {
        var request = JsonSerializer.Deserialize<SetThemeRequest>(payload.GetRawText(), SerializerOptions)
            ?? throw new DesktopBridgeException("Desktop theme payload is empty.");
        var preference = request.Preference switch
        {
            "system" => DesktopThemePreference.System,
            "light" => DesktopThemePreference.Light,
            "dark" => DesktopThemePreference.Dark,
            _ => throw new DesktopBridgeException("Desktop theme preference must be system, light, or dark."),
        };

        shellController?.ApplyTheme(preference);
        return JsonSerializer.SerializeToElement(new SetThemeResult(DesktopTheme.ToWireValue(preference)), SerializerOptions);
    }

    private static string CreateSuccessResponse(string requestId, JsonElement result) =>
        JsonSerializer.Serialize(new DesktopBridgeResponse(ProtocolVersion, requestId, true, result, null), SerializerOptions);

    private static string CreateErrorResponse(string requestId, string error) =>
        JsonSerializer.Serialize(new DesktopBridgeResponse(ProtocolVersion, requestId, false, null, error), SerializerOptions);

    private static void ValidateRequest(DesktopBridgeRequest request)
    {
        if (request.ProtocolVersion != ProtocolVersion)
        {
            throw new DesktopBridgeException($"Unsupported desktop bridge protocol version: {request.ProtocolVersion}.");
        }

        if (string.IsNullOrWhiteSpace(request.Id) || request.Id.Length > 128)
        {
            throw new DesktopBridgeException("Desktop bridge request ID must contain 1 to 128 characters.");
        }

        if (string.IsNullOrWhiteSpace(request.Method) || request.Method.Length > 128)
        {
            throw new DesktopBridgeException("Desktop bridge method must contain 1 to 128 characters.");
        }
    }

    private static string NormalizeCommand(string method) => method switch
    {
        "validate_workbook" => "validate_workbook",
        "solve_workbook" => "solve_workbook",
        "export_unsat_artifact" => "export_unsat_artifact",
        "cancel_command" => "cancel_command",
        "set_theme" => "set_theme",
        "desktop_ready" => "desktop_ready",
        _ => "unsupported",
    };

    private static bool IsHeavyCommand(string method) => method is
        "validate_workbook" or "solve_workbook" or "export_unsat_artifact";

    private async Task RecordActivityAsync(string requestId, string command, string outcome, TimeSpan elapsed)
    {
        try
        {
            await activityLogger.RecordAsync(new DesktopActivity(
                DateTimeOffset.UtcNow,
                string.IsNullOrWhiteSpace(requestId) ? "invalid" : requestId,
                command,
                outcome,
                checked((long)Math.Ceiling(elapsed.TotalMilliseconds)),
                ProtocolVersion,
                command == "solve_workbook" ? DesktopReleaseMetadata.SolverVersion : null));
        }
        catch (Exception)
        {
            // A diagnostic sink is strictly best-effort.
        }
    }

    private sealed record DesktopBridgeRequest(
        [property: JsonPropertyName("protocol_version")] int ProtocolVersion,
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("payload")] JsonElement Payload);

    private sealed record CancelCommandRequest(
        [property: JsonPropertyName("target_id")] string TargetId);

    private sealed record CancelCommandResult(
        [property: JsonPropertyName("cancelled")] bool Cancelled);

    private sealed record SetThemeRequest(
        [property: JsonPropertyName("preference")] string Preference);

    private sealed record SetThemeResult(
        [property: JsonPropertyName("preference")] string Preference);

    private sealed record DesktopReadyResult(
        [property: JsonPropertyName("ready")] bool Ready);

    private sealed record DesktopBridgeResponse(
        [property: JsonPropertyName("protocol_version")] int ProtocolVersion,
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("result")] JsonElement? Result,
        [property: JsonPropertyName("error")] string? Error);
}
