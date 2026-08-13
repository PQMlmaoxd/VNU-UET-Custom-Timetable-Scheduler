using System.Text.Json;

namespace Scheduler.Diagnostics;

internal static class AppDiagnostics
{
    private const long MaximumManifestBytes = 8L * 1024 * 1024;
    private const int MaximumManifestFiles = 10_000;
    private const int MaximumManifestPathCharacters = 512;

    private static readonly string[] RequiredPackageFiles =
    [
        "Scheduler.Desktop.dll",
        "SolverWorker.exe",
        "web/index.html",
    ];

    public static DiagnosticReport Run(DiagnosticsOptions options, string inputPath)
    {
        var rootPath = Directory.Exists(inputPath)
            ? inputPath
            : Path.GetDirectoryName(inputPath) ?? inputPath;
        var applicationPath = Directory.Exists(inputPath)
            ? Path.Combine(rootPath, "Scheduler.Desktop.exe")
            : inputPath;
        var checks = new List<DiagnosticCheck>
        {
            DiagnosticsReportFactory.Check(
                options,
                "app_target",
                DiagnosticStatus.Passed,
                "Application target is present.",
                path: inputPath),
        };

        var structureMissing = new List<string>();
        if (!File.Exists(applicationPath))
        {
            structureMissing.Add("application");
        }

        foreach (var requiredFile in RequiredPackageFiles)
        {
            if (!TryResolvePackageFile(rootPath, requiredFile, out var resolvedPath) || !File.Exists(resolvedPath))
            {
                structureMissing.Add(requiredFile);
            }
        }

        var structureStatus = structureMissing.Count == 0
            ? DiagnosticStatus.Passed
            : DiagnosticStatus.Failed;
        checks.Add(DiagnosticsReportFactory.Check(
            options,
            "package_structure",
            structureStatus,
            structureStatus == DiagnosticStatus.Passed
                ? "Required application package structure is present."
                : "Required application package structure is incomplete.",
            new Dictionary<string, long>
            {
                ["required_items"] = RequiredPackageFiles.Length + 1,
                ["missing_items"] = structureMissing.Count,
            },
            path: rootPath,
            detail: structureMissing.Count == 0 ? null : string.Join(", ", structureMissing)));

        var manifestPath = Path.Combine(rootPath, "release-manifest.json");
        var manifest = ReadManifest(manifestPath, rootPath, Path.GetFileName(applicationPath));
        checks.Add(DiagnosticsReportFactory.Check(
            options,
            "package_manifest",
            manifest.Status,
            manifest.Status == DiagnosticStatus.Passed
                ? "Release manifest is present and has the supported structure."
                : "Release manifest is missing or invalid.",
            new Dictionary<string, long> { ["manifest_schema_version"] = manifest.SchemaVersion },
            path: manifestPath,
            detail: manifest.Detail));

        var hashCheck = VerifyHashes(options, rootPath, manifest);
        checks.Add(hashCheck);
        return DiagnosticsReportFactory.Create(options, "app", checks);
    }

    private static DiagnosticCheck VerifyHashes(
        DiagnosticsOptions options,
        string rootPath,
        ManifestData manifest)
    {
        if (manifest.Status != DiagnosticStatus.Passed)
        {
            return DiagnosticsReportFactory.Check(
                options,
                "package_hashes",
                DiagnosticStatus.Failed,
                "Package file hashes could not be checked.",
                new Dictionary<string, long>
                {
                    ["total"] = 0,
                    ["verified"] = 0,
                    ["missing"] = 0,
                    ["mismatched"] = 0,
                    ["invalid"] = 0,
                },
                path: rootPath);
        }

        var entries = new List<DiagnosticFileHash>();
        var verified = 0;
        var missing = 0;
        var mismatched = 0;
        var invalid = 0;
        foreach (var entry in manifest.Files)
        {
            if (!IsSha256(entry.ExpectedSha256) ||
                !TryResolvePackageFile(rootPath, entry.RelativePath, out var filePath))
            {
                invalid++;
                entries.Add(new DiagnosticFileHash(entry.RelativePath, entry.ExpectedSha256, null, "invalid"));
                continue;
            }

            if (!File.Exists(filePath))
            {
                missing++;
                entries.Add(new DiagnosticFileHash(entry.RelativePath, entry.ExpectedSha256, null, "missing"));
                continue;
            }

            var actual = DiagnosticsReportFactory.TryComputeSha256(filePath, out _);
            if (actual is null)
            {
                missing++;
                entries.Add(new DiagnosticFileHash(entry.RelativePath, entry.ExpectedSha256, null, "unreadable"));
                continue;
            }

            if (string.Equals(actual, entry.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                verified++;
                entries.Add(new DiagnosticFileHash(entry.RelativePath, entry.ExpectedSha256, actual, "verified"));
            }
            else
            {
                mismatched++;
                entries.Add(new DiagnosticFileHash(entry.RelativePath, entry.ExpectedSha256, actual, "mismatched"));
            }
        }

        var passed = entries.Count > 0 && missing == 0 && mismatched == 0 && invalid == 0;
        return DiagnosticsReportFactory.Check(
            options,
            "package_hashes",
            passed ? DiagnosticStatus.Passed : DiagnosticStatus.Failed,
            passed ? "Package file hashes match the release manifest." : "Package file hashes do not match the release manifest.",
            new Dictionary<string, long>
            {
                ["total"] = entries.Count,
                ["verified"] = verified,
                ["missing"] = missing,
                ["mismatched"] = mismatched,
                ["invalid"] = invalid,
            },
            path: rootPath,
            fileHashes: entries);
    }

    private static ManifestData ReadManifest(
        string manifestPath,
        string rootPath,
        string applicationFileName)
    {
        if (!File.Exists(manifestPath))
        {
            return ManifestData.Failed("Manifest file was not found.");
        }

        try
        {
            var manifestBytes = new FileInfo(manifestPath).Length;
            if (manifestBytes <= 0 || manifestBytes > MaximumManifestBytes)
            {
                return ManifestData.Failed("Manifest size is outside the supported limit.");
            }

            using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
            var root = document.RootElement;
            var schemaVersion = 0;
            if (!root.TryGetProperty("schema_version", out var schemaVersionElement) ||
                !schemaVersionElement.TryGetInt32(out schemaVersion) || schemaVersion != 1 ||
                !root.TryGetProperty("solver_worker", out var solverWorker) ||
                !solverWorker.TryGetProperty("file", out var workerFile) ||
                !TryGetSafeString(workerFile, out var workerFileName) ||
                !TryResolvePackageFile(rootPath, workerFileName, out _) ||
                !solverWorker.TryGetProperty("sha256", out var workerHash) ||
                !TryGetSafeString(workerHash, out var workerHashValue) ||
                !IsSha256(workerHashValue) ||
                !root.TryGetProperty("publish_files", out var publishFiles) ||
                publishFiles.ValueKind != JsonValueKind.Object ||
                !string.Equals(workerFileName, "SolverWorker.exe", StringComparison.OrdinalIgnoreCase))
            {
                return ManifestData.Failed("Manifest does not contain the supported release fields.", schemaVersion);
            }

            var files = new List<ManifestFile>();
            foreach (var property in publishFiles.EnumerateObject())
            {
                if (files.Count >= MaximumManifestFiles || property.Name.Length > MaximumManifestPathCharacters)
                {
                    return ManifestData.Failed("Manifest contains too many or too-long file entries.", schemaVersion);
                }

                if (!TryGetSafeString(property.Value, out var hash))
                {
                    return ManifestData.Failed("Manifest contains a non-string file hash.", schemaVersion);
                }

                files.Add(new ManifestFile(property.Name, hash));
            }

            var workerEntry = files.FirstOrDefault(file =>
                string.Equals(file.RelativePath, workerFileName, StringComparison.OrdinalIgnoreCase));
            if (workerEntry is null ||
                !string.Equals(workerEntry.ExpectedSha256, workerHashValue, StringComparison.OrdinalIgnoreCase) ||
                !files.Any(file => string.Equals(
                    file.RelativePath,
                    applicationFileName,
                    StringComparison.OrdinalIgnoreCase)) ||
                RequiredPackageFiles.Any(required => !files.Any(file =>
                    string.Equals(file.RelativePath, required, StringComparison.OrdinalIgnoreCase))))
            {
                return ManifestData.Failed(
                    "Manifest solver worker metadata does not match publish_files.",
                    schemaVersion);
            }

            return new ManifestData(
                DiagnosticStatus.Passed,
                schemaVersion,
                files,
                null);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return ManifestData.Failed(
                DiagnosticsReportFactory.ExceptionDetail(exception));
        }
    }

    private static bool TryResolvePackageFile(string rootPath, string relativePath, out string resolvedPath)
    {
        resolvedPath = "";
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            return false;
        }

        try
        {
            var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var candidate = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            var prefix = root + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            resolvedPath = candidate;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsSha256(string? value) =>
        value is not null && value.Length == 64 && value.All(Uri.IsHexDigit);

    private static bool TryGetSafeString(JsonElement element, out string value)
    {
        value = "";
        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString() ?? "";
        return !string.IsNullOrWhiteSpace(value);
    }

    private sealed record ManifestData(
        string Status,
        int SchemaVersion,
        IReadOnlyList<ManifestFile> Files,
        string? Detail)
    {
        public static ManifestData Failed(string detail, int schemaVersion = 0) =>
            new(DiagnosticStatus.Failed, schemaVersion, [], detail);
    }

    private sealed record ManifestFile(string RelativePath, string ExpectedSha256);
}
