using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace Scheduler.Diagnostics.Tests;

public sealed class AppDiagnosticsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "scheduler-diagnostics-" + Guid.NewGuid().ToString("N"));

    public AppDiagnosticsTests()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "web"));
        File.WriteAllText(Path.Combine(_directory, "Scheduler.Desktop.exe"), "desktop");
        File.WriteAllText(Path.Combine(_directory, "Scheduler.Desktop.dll"), "desktop-dll");
        File.WriteAllText(Path.Combine(_directory, "SolverWorker.exe"), "worker");
        File.WriteAllText(Path.Combine(_directory, "app.ico"), "icon");
        File.WriteAllText(Path.Combine(_directory, "web", "index.html"), "<html></html>");
        WriteManifest();
    }

    [Fact]
    public void ValidatesPackageStructureManifestAndHashes()
    {
        var options = new DiagnosticsOptions(
            DiagnosticsCommand.App,
            null,
            _directory,
            null,
            DiagnosticsOutputFormat.Json,
            null,
            IncludePaths: false,
            IncludeFileHashes: true,
            VerbosePrivate: false);

        var report = AppDiagnostics.Run(options, _directory);

        Assert.Equal(DiagnosticStatus.Passed, report.Status);
        Assert.Equal(0, report.Summary.Failed);
        Assert.Contains(report.Checks, check => check.Name == "package_manifest" && check.Status == DiagnosticStatus.Passed);
        Assert.Contains(report.Checks, check => check.Name == "package_hashes" && check.Status == DiagnosticStatus.Passed);
    }

    [Fact]
    public void DetectsManifestHashMismatch()
    {
        var manifestPath = Path.Combine(_directory, "release-manifest.json");
        var manifest = new Dictionary<string, object?>
        {
            ["schema_version"] = 1,
            ["solver_worker"] = new Dictionary<string, object?>
            {
                ["file"] = "SolverWorker.exe",
                ["sha256"] = Sha256(Path.Combine(_directory, "SolverWorker.exe")),
            },
            ["publish_files"] = new Dictionary<string, string>
            {
                ["Scheduler.Desktop.exe"] = "0000000000000000000000000000000000000000000000000000000000000000",
            },
        };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));

        var options = new DiagnosticsOptions(
            DiagnosticsCommand.App,
            null,
            _directory,
            null,
            DiagnosticsOutputFormat.Text,
            null,
            false,
            true,
            false);
        var report = AppDiagnostics.Run(options, _directory);

        Assert.Equal(DiagnosticStatus.Failed, report.Status);
        Assert.Contains(report.Checks, check => check.Name == "package_hashes" && check.Status == DiagnosticStatus.Failed);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private void WriteManifest()
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Scheduler.Desktop.exe"] = Sha256(Path.Combine(_directory, "Scheduler.Desktop.exe")),
            ["Scheduler.Desktop.dll"] = Sha256(Path.Combine(_directory, "Scheduler.Desktop.dll")),
            ["SolverWorker.exe"] = Sha256(Path.Combine(_directory, "SolverWorker.exe")),
            ["app.ico"] = Sha256(Path.Combine(_directory, "app.ico")),
            ["web/index.html"] = Sha256(Path.Combine(_directory, "web", "index.html")),
        };
        var manifest = new Dictionary<string, object?>
        {
            ["schema_version"] = 1,
            ["solver_worker"] = new Dictionary<string, object?>
            {
                ["file"] = "SolverWorker.exe",
                ["sha256"] = files["SolverWorker.exe"],
            },
            ["publish_files"] = files,
        };
        File.WriteAllText(Path.Combine(_directory, "release-manifest.json"), JsonSerializer.Serialize(manifest));
    }

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
