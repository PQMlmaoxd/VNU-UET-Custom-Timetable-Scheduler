using System.Security.Cryptography;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scheduler.Diagnostics;

internal static class DiagnosticsReportFactory
{
    public const int SchemaVersion = 1;
    public const string ToolName = "scheduler-diagnostics";
    public static string ToolVersion =>
        typeof(DiagnosticsReportFactory).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(DiagnosticsReportFactory).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static DiagnosticCheck Check(
        DiagnosticsOptions options,
        string name,
        string status,
        string message,
        IReadOnlyDictionary<string, long>? metrics = null,
        string? path = null,
        IReadOnlyList<DiagnosticFileHash>? fileHashes = null,
        string? detail = null) => new(
        name,
        status,
        message,
        metrics,
        options.IncludePaths ? path : null,
        options.IncludeFileHashes ? fileHashes : null,
        options.VerbosePrivate ? LimitDetail(detail) : null);

    public static DiagnosticReport Create(
        DiagnosticsOptions options,
        string command,
        IEnumerable<DiagnosticCheck> checks,
        string? content = null)
    {
        var checkList = checks.ToArray();
        var summary = new DiagnosticSummary(
            checkList.Length,
            checkList.Count(check => check.Status == DiagnosticStatus.Passed),
            checkList.Count(check => check.Status == DiagnosticStatus.Failed),
            checkList.Count(check => check.Status == DiagnosticStatus.Unsupported),
            checkList.Count(check => check.Status == DiagnosticStatus.Missing),
            checkList.Count(check => check.Status == DiagnosticStatus.Internal));
        var exitCode = summary.Internal > 0
            ? (int)DiagnosticsExitCode.Internal
            : summary.Missing > 0
                ? (int)DiagnosticsExitCode.MissingTarget
                : summary.Failed > 0 || summary.Unsupported > 0
                    ? (int)DiagnosticsExitCode.FailedChecks
                    : (int)DiagnosticsExitCode.Success;
        var status = exitCode switch
        {
            (int)DiagnosticsExitCode.Success => DiagnosticStatus.Passed,
            (int)DiagnosticsExitCode.MissingTarget => DiagnosticStatus.Missing,
            (int)DiagnosticsExitCode.Internal => DiagnosticStatus.Internal,
            _ => DiagnosticStatus.Failed,
        };

        return new DiagnosticReport(
            SchemaVersion,
            ToolName,
            ToolVersion,
            command,
            status,
            exitCode,
            checkList,
            summary,
            content);
    }

    public static string SerializeJson(DiagnosticReport report) => JsonSerializer.Serialize(report, JsonOptions);

    public static string ToText(DiagnosticReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Scheduler diagnostics");
        builder.Append("Schema: ")
            .Append(report.SchemaVersion.ToString(CultureInfo.InvariantCulture))
            .AppendLine();
        builder.Append("Command: ").Append(report.Command).AppendLine();
        builder.Append("Status: ").Append(report.Status).AppendLine();
        builder.AppendLine("Checks:");
        foreach (var check in report.Checks)
        {
            builder.Append("- ")
                .Append(check.Name)
                .Append(": ")
                .Append(check.Status)
                .Append(" - ")
                .AppendLine(check.Message);
            if (check.Metrics is not null)
            {
                foreach (var metric in check.Metrics.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    builder.Append("  ")
                        .Append(metric.Key)
                        .Append(": ")
                        .AppendLine(metric.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }

            if (check.Path is not null)
            {
                builder.Append("  path: ").AppendLine(check.Path);
            }

            if (check.FileHashes is not null)
            {
                foreach (var fileHash in check.FileHashes)
                {
                    builder.Append("  hash ")
                        .Append(fileHash.RelativePath)
                        .Append(": ")
                        .Append(fileHash.Status)
                        .Append(' ')
                        .AppendLine(fileHash.ActualSha256 ?? "unavailable");
                }
            }

            if (check.Detail is not null)
            {
                builder.Append("  detail: ").AppendLine(Flatten(check.Detail));
            }
        }

        builder.Append("Summary: ")
            .Append(report.Summary.Passed)
            .Append(" passed, ")
            .Append(report.Summary.Failed)
            .Append(" failed, ")
            .Append(report.Summary.Unsupported)
            .Append(" unsupported, ")
            .Append(report.Summary.Missing)
            .Append(" missing")
            .AppendLine();
        builder.Append("Exit code: ")
            .AppendLine(report.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return builder.ToString();
    }

    public static string? TryComputeSha256(string path, out string? error)
    {
        error = null;
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = exception.GetType().Name + ": " + exception.Message;
            return null;
        }
    }

    public static string ExceptionDetail(Exception exception) =>
        exception.GetType().Name + ": " + exception.Message;

    private static string? LimitDetail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        const int maximumCharacters = 4_000;
        var normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= maximumCharacters
            ? normalized
            : string.Concat(normalized.AsSpan(0, maximumCharacters), "...");
    }

    private static string Flatten(string value) => value.Replace('\r', ' ').Replace('\n', ' ');
}

internal enum DiagnosticsExitCode
{
    Success = 0,
    FailedChecks = 1,
    Usage = 2,
    MissingTarget = 4,
    Internal = 5,
}
