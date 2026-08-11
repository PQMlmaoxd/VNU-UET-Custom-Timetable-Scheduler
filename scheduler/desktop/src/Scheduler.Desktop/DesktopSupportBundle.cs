using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scheduler.Desktop;

/// <summary>
/// Creates a user-selected support archive from the explicitly sanitized activity log.
/// Workbook content, selections, paths, correlation IDs, and exception details are never copied.
/// </summary>
public static class DesktopSupportBundle
{
    private const int RetainedLogFiles = 5;
    private const int MaximumActivities = 10_000;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private static readonly JsonSerializerOptions CompactSerializerOptions = new(JsonSerializerDefaults.Web);

    public static Task<DesktopSupportBundleResult> CreateAsync(
        string destinationPath,
        DesktopDiagnostics diagnostics,
        string logDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);

        return Task.Run(
            () => Create(destinationPath, diagnostics, logDirectory, cancellationToken),
            cancellationToken);
    }

    private static DesktopSupportBundleResult Create(
        string destinationPath,
        DesktopDiagnostics diagnostics,
        string logDirectory,
        CancellationToken cancellationToken)
    {
        var fullDestinationPath = Path.GetFullPath(destinationPath);
        var destinationDirectory = Path.GetDirectoryName(fullDestinationPath)
            ?? throw new ArgumentException("Support archive destination must have a directory.", nameof(destinationPath));
        Directory.CreateDirectory(destinationDirectory);

        var temporaryPath = Path.Combine(
            destinationDirectory,
            string.Concat(Path.GetFileNameWithoutExtension(fullDestinationPath), ".", Guid.NewGuid().ToString("N"), ".tmp"));

        try
        {
            int activityCount;
            using (var archive = ZipFile.Open(temporaryPath, ZipArchiveMode.Create))
            {
                WriteDiagnostics(archive, diagnostics, cancellationToken);
                activityCount = WriteActivities(archive, logDirectory, cancellationToken);
                WriteReadme(archive, activityCount, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullDestinationPath, overwrite: true);
            return new DesktopSupportBundleResult(fullDestinationPath, activityCount);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void WriteDiagnostics(
        ZipArchive archive,
        DesktopDiagnostics diagnostics,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry("diagnostics.json", CompressionLevel.Optimal);
        using var stream = entry.Open();
        JsonSerializer.Serialize(stream, new SupportDiagnostics(
            diagnostics.ApplicationVersion,
            diagnostics.SolverVersion,
            diagnostics.CadicalCommit,
            diagnostics.BridgeProtocolVersion,
            diagnostics.WebView2RuntimeVersion), SerializerOptions);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static int WriteActivities(ZipArchive archive, string logDirectory, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry("activity.jsonl", CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var count = 0;

        foreach (var logPath in GetLogPaths(logDirectory))
        {
            if (!File.Exists(logPath))
            {
                continue;
            }

            foreach (var line in File.ReadLines(logPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (count >= MaximumActivities)
                {
                    return count;
                }

                if (!TryReadActivity(line, out var activity))
                {
                    continue;
                }

                writer.WriteLine(JsonSerializer.Serialize(activity, CompactSerializerOptions));
                count++;
            }
        }

        return count;
    }

    private static IEnumerable<string> GetLogPaths(string logDirectory)
    {
        for (var index = RetainedLogFiles; index >= 1; index--)
        {
            yield return Path.Combine(logDirectory, string.Concat("desktop.", index.ToString(CultureInfo.InvariantCulture), ".jsonl"));
        }

        yield return Path.Combine(logDirectory, "desktop.jsonl");
    }

    private static bool TryReadActivity(string line, out SupportActivity activity)
    {
        activity = default!;
        try
        {
            using var document = JsonDocument.Parse(line);
            var entry = document.RootElement;
            if (entry.ValueKind != JsonValueKind.Object
                || !TryGetDateTimeOffset(entry, "timestamp_utc", out var timestamp)
                || !TryGetString(entry, "command", out var command)
                || !TryGetString(entry, "outcome", out var outcome)
                || !TryGetInt64(entry, "elapsed_milliseconds", out var elapsed)
                || !TryGetInt32(entry, "bridge_protocol_version", out var protocolVersion)
                || !IsKnownCommand(command)
                || !IsKnownOutcome(outcome)
                || elapsed < 0
                || protocolVersion < 1)
            {
                return false;
            }

            activity = new SupportActivity(timestamp, command, outcome, elapsed, protocolVersion);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void WriteReadme(ZipArchive archive, int activityCount, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry("README.txt", CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.WriteLine("VNU-UET Custom Timetable Scheduler support bundle");
        writer.WriteLine();
        writer.WriteLine("Included: release diagnostics and sanitized command metadata.");
        writer.WriteLine("Excluded: workbook contents, workbook names, filesystem paths, selections,");
        writer.WriteLine("correlation IDs, request payloads, and exception details.");
        writer.WriteLine(string.Concat("Sanitized activity records: ", activityCount.ToString(CultureInfo.InvariantCulture)));
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static bool TryGetDateTimeOffset(JsonElement entry, string name, out DateTimeOffset value)
    {
        value = default;
        return entry.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(
                property.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out value);
    }

    private static bool TryGetString(JsonElement entry, string name, out string value)
    {
        value = string.Empty;
        if (!entry.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return value.Length is > 0 and <= 64;
    }

    private static bool TryGetInt64(JsonElement entry, string name, out long value)
    {
        value = default;
        return entry.TryGetProperty(name, out var property) && property.TryGetInt64(out value);
    }

    private static bool TryGetInt32(JsonElement entry, string name, out int value)
    {
        value = default;
        return entry.TryGetProperty(name, out var property) && property.TryGetInt32(out value);
    }

    private static bool IsKnownCommand(string command) => command is
        "validate_workbook" or "solve_workbook" or "export_unsat_artifact" or "cancel_command" or "invalid" or "unsupported";

    private static bool IsKnownOutcome(string outcome) => outcome is
        "completed" or "cancelled" or "rejected" or "failed";

    private sealed record SupportDiagnostics(
        [property: JsonPropertyName("application_version")] string ApplicationVersion,
        [property: JsonPropertyName("solver_version")] string SolverVersion,
        [property: JsonPropertyName("cadical_commit")] string CadicalCommit,
        [property: JsonPropertyName("bridge_protocol_version")] int BridgeProtocolVersion,
        [property: JsonPropertyName("webview2_runtime_version")] string WebView2RuntimeVersion);

    private sealed record SupportActivity(
        [property: JsonPropertyName("timestamp_utc")] DateTimeOffset TimestampUtc,
        [property: JsonPropertyName("command")] string Command,
        [property: JsonPropertyName("outcome")] string Outcome,
        [property: JsonPropertyName("elapsed_milliseconds")] long ElapsedMilliseconds,
        [property: JsonPropertyName("bridge_protocol_version")] int BridgeProtocolVersion);
}

public sealed record DesktopSupportBundleResult(string ArchivePath, int ActivityCount);
