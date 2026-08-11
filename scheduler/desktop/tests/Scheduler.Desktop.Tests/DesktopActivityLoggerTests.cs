using System.Text.Json;
using Scheduler.Desktop;
using Xunit;

namespace Scheduler.Desktop.Tests;

public sealed class DesktopActivityLoggerTests : IDisposable
{
    private readonly string temporaryDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RecordAsyncWritesSanitizedJsonLine()
    {
        var logger = new LocalDesktopActivityLogger(temporaryDirectory, "9.0.0-test");

        await logger.RecordAsync(new DesktopActivity(
            DateTimeOffset.Parse("2026-07-26T00:00:00+00:00", System.Globalization.CultureInfo.InvariantCulture),
            "request-1",
            "solve_workbook",
            "completed",
            123,
            1,
            "cadical-3.0.1"));

        var content = await File.ReadAllTextAsync(Path.Combine(temporaryDirectory, "desktop.jsonl"));
        using var document = JsonDocument.Parse(content);
        var entry = document.RootElement;

        Assert.Equal("request-1", entry.GetProperty("correlation_id").GetString());
        Assert.Equal("solve_workbook", entry.GetProperty("command").GetString());
        Assert.Equal("completed", entry.GetProperty("outcome").GetString());
        Assert.Equal(123, entry.GetProperty("elapsed_milliseconds").GetInt64());
        Assert.Equal("9.0.0-test", entry.GetProperty("application_version").GetString());
        Assert.Equal("cadical-3.0.1", entry.GetProperty("solver_version").GetString());
        Assert.False(entry.TryGetProperty("payload", out _));
        Assert.False(entry.TryGetProperty("workbook_path", out _));
        Assert.False(entry.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task RecordAsyncDoesNotPropagateLocalWriteFailures()
    {
        var blockedPath = Path.Combine(temporaryDirectory, "blocked");
        Directory.CreateDirectory(temporaryDirectory);
        await File.WriteAllTextAsync(blockedPath, "not a directory");
        var logger = new LocalDesktopActivityLogger(Path.Combine(blockedPath, "logs"));

        await logger.RecordAsync(new DesktopActivity(
            DateTimeOffset.UtcNow,
            "request-1",
            "validate_workbook",
            "completed",
            1,
            1,
            null));
    }

    public void Dispose()
    {
        if (Directory.Exists(temporaryDirectory))
        {
            Directory.Delete(temporaryDirectory, true);
        }
    }
}
