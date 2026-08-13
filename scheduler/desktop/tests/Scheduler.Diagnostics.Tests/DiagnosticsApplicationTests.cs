using Xunit;

namespace Scheduler.Diagnostics.Tests;

public sealed class DiagnosticsApplicationTests
{
    [Fact]
    public async Task MissingTargetUsesStableExitCodeAndDoesNotLeakPathByDefault()
    {
        var privatePath = Path.Combine(Path.GetTempPath(), "private-workbook-course-lecturer.xlsx");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await DiagnosticsApplication.RunAsync(
            ["workbook", "--workbook", privatePath],
            output,
            error);

        Assert.Equal((int)DiagnosticsExitCode.MissingTarget, exitCode);
        Assert.DoesNotContain(privatePath, output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("course", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lecturer", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task JsonReportHasVersionedSchemaAndCanIncludePathsByOptIn()
    {
        var privatePath = Path.Combine(Path.GetTempPath(), "private-target.exe");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await DiagnosticsApplication.RunAsync(
            ["app", "--app", privatePath, "--format", "json", "--include-paths"],
            output,
            error);

        Assert.Equal((int)DiagnosticsExitCode.MissingTarget, exitCode);
        var json = System.Text.Json.JsonDocument.Parse(output.ToString()).RootElement;
        Assert.Equal(1, json.GetProperty("schema_version").GetInt32());
        Assert.Equal("missing", json.GetProperty("status").GetString());
        Assert.Contains("private-target.exe", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelfTestPassesWithoutPrivateEnvironmentDetails()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await DiagnosticsApplication.RunAsync(["self-test"], output, error);

        Assert.Equal((int)DiagnosticsExitCode.Success, exitCode);
        Assert.Contains("managed_protocol_contract: passed", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Environment.UserName, output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task VersionUsesAssemblyVersionInsteadOfDevelopmentConstant()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await DiagnosticsApplication.RunAsync(["version"], output, error);

        Assert.Equal((int)DiagnosticsExitCode.Success, exitCode);
        Assert.Equal(
            $"{DiagnosticsReportFactory.ToolName} {DiagnosticsReportFactory.ToolVersion}",
            output.ToString().Trim());
    }
}
