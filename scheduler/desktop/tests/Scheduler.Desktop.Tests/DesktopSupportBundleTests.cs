using System.IO.Compression;
using System.Text.Json;
using Scheduler.Desktop;
using Xunit;

namespace Scheduler.Desktop.Tests;

public sealed class DesktopSupportBundleTests : IDisposable
{
    private static readonly string[] LogLines =
    {
        """
        {"timestamp_utc":"2026-07-26T12:00:00+00:00","correlation_id":"workbook-secret","command":"solve_workbook","outcome":"completed","elapsed_milliseconds":123,"application_version":"1.2.3","bridge_protocol_version":1,"solver_version":"cadical-3.0.1","workbook_path":"C:\\private.xlsx"}
        """,
        """
        {"timestamp_utc":"not-a-date","command":"solve_workbook","outcome":"completed","elapsed_milliseconds":123,"bridge_protocol_version":1}
        """,
    };

    private readonly string temporaryDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CreateAsyncWritesOnlySanitizedDiagnosticsAndActivities()
    {
        var logDirectory = Path.Combine(temporaryDirectory, "logs");
        Directory.CreateDirectory(logDirectory);
        await File.WriteAllLinesAsync(Path.Combine(logDirectory, "desktop.jsonl"), LogLines);
        var archivePath = Path.Combine(temporaryDirectory, "support.zip");

        var result = await DesktopSupportBundle.CreateAsync(
            archivePath,
            DesktopDiagnostics.Create("150.0.4078.99"),
            logDirectory);

        Assert.Equal(archivePath, result.ArchivePath);
        Assert.Equal(1, result.ActivityCount);
        using var archive = ZipFile.OpenRead(archivePath);
        Assert.Equal(
            "activity.jsonl,diagnostics.json,README.txt",
            string.Join(",", archive.Entries.Select(entry => entry.Name).OrderBy(name => name)));

        using var activityReader = new StreamReader(archive.GetEntry("activity.jsonl")!.Open());
        var activityContent = await activityReader.ReadToEndAsync();
        using var activity = JsonDocument.Parse(activityContent);
        Assert.Equal("solve_workbook", activity.RootElement.GetProperty("command").GetString());
        Assert.Equal("completed", activity.RootElement.GetProperty("outcome").GetString());
        Assert.False(activity.RootElement.TryGetProperty("correlation_id", out _));
        Assert.False(activity.RootElement.TryGetProperty("workbook_path", out _));
        Assert.DoesNotContain("private.xlsx", activityContent, StringComparison.Ordinal);

        using var diagnosticsReader = new StreamReader(archive.GetEntry("diagnostics.json")!.Open());
        var diagnosticsContent = await diagnosticsReader.ReadToEndAsync();
        using var diagnostics = JsonDocument.Parse(diagnosticsContent);
        Assert.Equal("cadical-3.0.1", diagnostics.RootElement.GetProperty("solver_version").GetString());
        Assert.False(diagnostics.RootElement.TryGetProperty("activity_log_directory", out _));
        Assert.DoesNotContain(logDirectory, diagnosticsContent, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(temporaryDirectory))
        {
            Directory.Delete(temporaryDirectory, true);
        }
    }
}
