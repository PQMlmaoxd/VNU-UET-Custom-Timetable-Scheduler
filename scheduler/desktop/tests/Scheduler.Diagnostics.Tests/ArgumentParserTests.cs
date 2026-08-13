using Xunit;

namespace Scheduler.Diagnostics.Tests;

public sealed class ArgumentParserTests
{
    [Fact]
    public void ParsesWorkerCommandAndPrivateOutputOptions()
    {
        var options = ArgumentParser.Parse(
        [
            "worker",
            "--worker",
            "worker.exe",
            "--format=json",
            "--output",
            "report.json",
            "--include-paths",
            "--include-file-hashes",
            "--verbose-private",
        ]);

        Assert.Equal(DiagnosticsCommand.Worker, options.Command);
        Assert.Equal("worker.exe", options.WorkerPath);
        Assert.Equal(DiagnosticsOutputFormat.Json, options.Format);
        Assert.Equal("report.json", options.OutputPath);
        Assert.True(options.IncludePaths);
        Assert.True(options.IncludeFileHashes);
        Assert.True(options.VerbosePrivate);
    }

    [Fact]
    public void RejectsIncompleteWorkerUsage()
    {
        Assert.Throws<DiagnosticsUsageException>(() => ArgumentParser.Parse(["worker"]));
    }

    [Fact]
    public void RejectsIncompleteWorkbookUsage()
    {
        Assert.Throws<DiagnosticsUsageException>(() => ArgumentParser.Parse(["workbook", "--workbook"]));
    }

    [Fact]
    public void RejectsUnsupportedFormat()
    {
        Assert.Throws<DiagnosticsUsageException>(() => ArgumentParser.Parse(["doctor", "--format", "xml"]));
    }

    [Fact]
    public void RejectsUnknownCommand()
    {
        Assert.Throws<DiagnosticsUsageException>(() => ArgumentParser.Parse(["unknown"]));
    }
}
