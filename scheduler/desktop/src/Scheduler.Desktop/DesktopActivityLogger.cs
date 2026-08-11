using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scheduler.Desktop;

/// <summary>
/// Sanitized command metadata suitable for local diagnostics. Payloads, workbook
/// names, filesystem paths, and exception details are intentionally excluded.
/// </summary>
public sealed record DesktopActivity(
    DateTimeOffset TimestampUtc,
    string CorrelationId,
    string Command,
    string Outcome,
    long ElapsedMilliseconds,
    int BridgeProtocolVersion,
    string? SolverVersion);

public interface IDesktopActivityLogger
{
    Task RecordAsync(DesktopActivity activity);
}

public sealed class LocalDesktopActivityLogger : IDesktopActivityLogger
{
    private const long MaximumLogBytes = 1_000_000;
    private const int RetainedLogFiles = 5;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly string applicationVersion;
    private readonly string logDirectory;
    private readonly object writeLock = new();

    public LocalDesktopActivityLogger(string logDirectory, string? applicationVersion = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        this.logDirectory = Path.GetFullPath(logDirectory);
        this.applicationVersion = applicationVersion ?? DesktopReleaseMetadata.ApplicationVersion;
    }

    public static LocalDesktopActivityLogger CreateDefault()
    {
        return new LocalDesktopActivityLogger(DesktopReleaseMetadata.DefaultLogDirectory);
    }

    public Task RecordAsync(DesktopActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        return Task.Run(() => Write(activity));
    }

    private void Write(DesktopActivity activity)
    {
        try
        {
            var line = JsonSerializer.Serialize(new LogEntry(
                activity.TimestampUtc,
                activity.CorrelationId,
                activity.Command,
                activity.Outcome,
                activity.ElapsedMilliseconds,
                applicationVersion,
                activity.BridgeProtocolVersion,
                activity.SolverVersion), SerializerOptions);

            lock (writeLock)
            {
                Directory.CreateDirectory(logDirectory);
                RotateIfNeeded(Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length);
                File.AppendAllText(CurrentLogPath(), string.Concat(line, Environment.NewLine), Encoding.UTF8);
            }
        }
        catch (IOException)
        {
            // Diagnostics must never prevent a user command from completing.
        }
        catch (UnauthorizedAccessException)
        {
            // Diagnostics must never prevent a user command from completing.
        }
    }

    private void RotateIfNeeded(int nextLineBytes)
    {
        var currentPath = CurrentLogPath();
        if (!File.Exists(currentPath) || new FileInfo(currentPath).Length + nextLineBytes <= MaximumLogBytes)
        {
            return;
        }

        for (var index = RetainedLogFiles - 1; index >= 1; index--)
        {
            var source = RotatedLogPath(index);
            var destination = RotatedLogPath(index + 1);
            if (File.Exists(source))
            {
                File.Move(source, destination, overwrite: true);
            }
        }

        File.Move(currentPath, RotatedLogPath(1), overwrite: true);
    }

    private string CurrentLogPath() => Path.Combine(logDirectory, "desktop.jsonl");

    private string RotatedLogPath(int index) => Path.Combine(logDirectory, $"desktop.{index}.jsonl");

    private sealed record LogEntry(
        [property: JsonPropertyName("timestamp_utc")] DateTimeOffset TimestampUtc,
        [property: JsonPropertyName("correlation_id")] string CorrelationId,
        [property: JsonPropertyName("command")] string Command,
        [property: JsonPropertyName("outcome")] string Outcome,
        [property: JsonPropertyName("elapsed_milliseconds")] long ElapsedMilliseconds,
        [property: JsonPropertyName("application_version")] string ApplicationVersion,
        [property: JsonPropertyName("bridge_protocol_version")] int BridgeProtocolVersion,
        [property: JsonPropertyName("solver_version")] string? SolverVersion);
}

public sealed class NullDesktopActivityLogger : IDesktopActivityLogger
{
    public static NullDesktopActivityLogger Instance { get; } = new();

    private NullDesktopActivityLogger()
    {
    }

    public Task RecordAsync(DesktopActivity activity) => Task.CompletedTask;
}
