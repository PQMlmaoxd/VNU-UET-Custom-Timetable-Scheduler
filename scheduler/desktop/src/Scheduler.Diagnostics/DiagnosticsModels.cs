using System.Text.Json.Serialization;

namespace Scheduler.Diagnostics;

internal static class DiagnosticStatus
{
    public const string Passed = "passed";
    public const string Failed = "failed";
    public const string Unsupported = "unsupported";
    public const string Missing = "missing";
    public const string Internal = "internal";
}

internal sealed record DiagnosticFileHash(
    [property: JsonPropertyName("relative_path")] string RelativePath,
    [property: JsonPropertyName("expected_sha256")] string? ExpectedSha256,
    [property: JsonPropertyName("actual_sha256")] string? ActualSha256,
    string Status);

internal sealed record DiagnosticCheck(
    string Name,
    string Status,
    string Message,
    IReadOnlyDictionary<string, long>? Metrics = null,
    string? Path = null,
    IReadOnlyList<DiagnosticFileHash>? FileHashes = null,
    string? Detail = null);

internal sealed record DiagnosticSummary(
    int Total,
    int Passed,
    int Failed,
    int Unsupported,
    int Missing,
    int Internal);

internal sealed record DiagnosticReport(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    string Tool,
    string Version,
    string Command,
    string Status,
    [property: JsonPropertyName("exit_code")] int ExitCode,
    IReadOnlyList<DiagnosticCheck> Checks,
    DiagnosticSummary Summary,
    string? Content = null);
