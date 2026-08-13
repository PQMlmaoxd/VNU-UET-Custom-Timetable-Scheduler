using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Scheduler.Application;
using Scheduler.Domain;
using Scheduler.Infrastructure.FormalVerification;
using Scheduler.Infrastructure.NativeSolver;
using Scheduler.Infrastructure.Pdf;
using Scheduler.Infrastructure.Timetable;
using Scheduler.Infrastructure.Xlsx;

namespace Scheduler.Desktop;

/// <summary>
/// Desktop boundary for the current UI commands. Timetable bytes are scoped to one
/// command and deleted before its bridge response is returned.
/// </summary>
public sealed class DesktopCommandDispatcher : IDesktopCommandDispatcher
{
    // WebView transfers base64 JSON, so a 25 MiB source file already has a much
    // larger transient memory footprint in both renderer and host processes.
    private const int MaxWorkbookBytes = 25 * 1024 * 1024;
    private const int MaxWorkbookBase64Characters = ((MaxWorkbookBytes + 2) / 3) * 4;
    private const int MaxDesiredAssignments = 100;
    private const int MaxAssignmentFieldCharacters = 512;
    private readonly Func<IPersonalSelectionSatSolver> createSolver;
    private readonly Func<string?> chooseFormalArtifactPath;
    private readonly ConcurrentDictionary<string, UnsatVerificationTicket> unsatTickets = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public DesktopCommandDispatcher()
        : this(CreateNativeSolver, ChooseFormalArtifactPath)
    {
    }

    public DesktopCommandDispatcher(
        Func<IPersonalSelectionSatSolver> createSolver,
        Func<string?>? chooseFormalArtifactPath = null)
    {
        ArgumentNullException.ThrowIfNull(createSolver);
        this.createSolver = createSolver;
        this.chooseFormalArtifactPath = chooseFormalArtifactPath ?? ChooseFormalArtifactPath;
    }

    public async Task<JsonElement> DispatchAsync(
        string method,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        return method switch
        {
            "validate_workbook" => await ValidateWorkbookAsync(payload, cancellationToken),
            "solve_workbook" => await SolveWorkbookAsync(payload, cancellationToken),
            "export_unsat_artifact" => await ExportUnsatArtifactAsync(payload, cancellationToken),
            _ => throw new DesktopBridgeException($"Unsupported desktop command '{method}'."),
        };
    }

    private static async Task<JsonElement> ValidateWorkbookAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var command = Deserialize<ValidateWorkbookCommand>(payload);
        await using var document = await TemporaryTimetableFile.CreateAsync(command.Workbook, cancellationToken);
        var parseResult = await ParseAsync(document, cancellationToken);
        var validation = TimetableValidator.Validate(
            FixedWorkbookSchedule.Create(parseResult.Problem),
            parseResult.Problem,
            cancellationToken);

        return DesktopResponseMapper.ToJson(DesktopResponseMapper.CreateValidateResponse(
            document.DisplayName,
            parseResult,
            validation));
    }

    private async Task<JsonElement> SolveWorkbookAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var command = Deserialize<SolveWorkbookCommand>(payload);
        if (command.TimeoutSeconds is < 1 or > 300)
        {
            throw new DesktopBridgeException("Thời gian tìm phải từ 1 đến 300 giây.");
        }

        var requestedAssignments = command.DesiredAssignments.ToImmutableArray();
        ValidateDesiredAssignments(requestedAssignments);
        await using var document = await TemporaryTimetableFile.CreateAsync(command.Workbook, cancellationToken);
        var parseResult = await ParseAsync(document, cancellationToken);
        if (!parseResult.FatalWarnings.IsEmpty)
        {
            throw new DesktopBridgeException(
                "Không thể tìm thời khóa biểu vì có dòng lịch bị thiếu hoặc sai thông tin bắt buộc. Hãy chọn file đã hoàn chỉnh.");
        }
        var desiredAssignments = requestedAssignments.Select(assignment => assignment.ToDomain()).ToImmutableArray();
        var preparedSelection = PersonalSelectionPreparation.Create(
            parseResult.Problem,
            desiredAssignments,
            cancellationToken);
        var sourceHash = await ComputeSha256Async(document.Path, cancellationToken);
        var cnfSha256 = ComputeCnfSha256(preparedSelection.Cnf);
        var desiredSignature = ComputeDesiredAssignmentSignature(desiredAssignments);
        var fixedSchedule = FixedWorkbookSchedule.Create(parseResult.Problem);
        var existingValidation = TimetableValidator.Validate(
            fixedSchedule,
            parseResult.Problem,
            cancellationToken);

        PersonalSelectionSolveResult solveResult;
        try
        {
            solveResult = await new PersonalSelectionService(createSolver()).SolveAsync(
                parseResult.Problem,
                preparedSelection,
                maxSolutions: 5,
                TimeSpan.FromSeconds(command.TimeoutSeconds),
                cancellationToken);
        }
        catch (TimeoutException)
        {
            solveResult = new PersonalSelectionSolveResult(
                new PersonalSelectionSatResult(
                    PersonalSelectionSatStatus.TimedOut,
                    [],
                    TimeSpan.FromSeconds(command.TimeoutSeconds),
                    0),
                []);
        }
        catch (FileNotFoundException)
        {
            throw new DesktopBridgeException(
                "Không thể tìm thời khóa biểu. Hãy kiểm tra cài đặt ứng dụng.");
        }
        catch (NativeSolverProcessException)
        {
            throw new DesktopBridgeException("Không thể tìm thời khóa biểu. Hãy thử lại.");
        }

        string? unsatVerificationToken = null;
        if (solveResult.SolverResult.Status == PersonalSelectionSatStatus.Infeasible &&
            parseResult.QuarantinedOfferings.IsEmpty)
        {
            unsatVerificationToken = CreateUnsatVerificationTicket(
                sourceHash,
                cnfSha256,
                desiredSignature,
                document.Format);
        }

        return DesktopResponseMapper.ToJson(DesktopResponseMapper.CreateSolveResponse(
            document.DisplayName,
            parseResult,
            desiredAssignments,
            existingValidation,
            solveResult,
            unsatVerificationToken));
    }

    private async Task<JsonElement> ExportUnsatArtifactAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var command = Deserialize<ExportUnsatArtifactCommand>(payload);
        if (string.IsNullOrWhiteSpace(command.VerificationToken))
        {
            throw new DesktopBridgeException("Gói kiểm chứng chỉ được xuất sau một kết quả UNSAT hợp lệ.");
        }

        var requestedAssignments = command.DesiredAssignments.ToImmutableArray();
        ValidateDesiredAssignments(requestedAssignments);
        await using var document = await TemporaryTimetableFile.CreateAsync(command.Workbook, cancellationToken);
        var outputPath = chooseFormalArtifactPath();
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return DesktopResponseMapper.ToJson(new ExportUnsatArtifactResponse(false, null, null, null, null));
        }

        var parseResult = await ParseAsync(document, cancellationToken);
        if (!parseResult.QuarantinedOfferings.IsEmpty)
        {
            throw new DesktopBridgeException(
                "Không thể xuất chứng nhận formal khi workbook còn LHP chưa công bố lịch.");
        }

        var desiredAssignments = requestedAssignments.Select(assignment => assignment.ToDomain()).ToImmutableArray();
        var preparedSelection = PersonalSelectionPreparation.Create(
            parseResult.Problem,
            desiredAssignments,
            cancellationToken);
        var sourceHash = await ComputeSha256Async(document.Path, cancellationToken);
        var cnfSha256 = ComputeCnfSha256(preparedSelection.Cnf);
        var desiredSignature = ComputeDesiredAssignmentSignature(desiredAssignments);
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryClaimUnsatTicket(
                command.VerificationToken,
                sourceHash,
                cnfSha256,
                desiredSignature,
                document.Format,
                out var ticket))
        {
            throw new DesktopBridgeException("Gói kiểm chứng không còn hợp lệ hoặc không khớp với kết quả UNSAT.");
        }

        var stagingDirectory = Path.Combine(Path.GetTempPath(), "SchedulerDesktop", $"formal-{Guid.NewGuid():N}");
        var exportCompleted = false;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            FormalArtifactExporter.Export(new FormalArtifactExportRequest(
                stagingDirectory,
                document.DisplayName,
                document.Format == TimetableFileFormat.Pdf ? "pdf" : "xlsx",
                sourceHash,
                parseResult.Problem,
                preparedSelection.Specification,
                preparedSelection.Cnf));

            var archivePath = Path.GetFullPath(outputPath);
            var archiveDirectory = Path.GetDirectoryName(archivePath)
                ?? throw new DesktopBridgeException("Không thể xác định thư mục xuất gói kiểm chứng.");
            Directory.CreateDirectory(archiveDirectory);
            var temporaryArchivePath = Path.Combine(
                archiveDirectory,
                $".{Path.GetFileName(archivePath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                ZipFile.CreateFromDirectory(
                    stagingDirectory,
                    temporaryArchivePath,
                    CompressionLevel.Optimal,
                    includeBaseDirectory: false);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryArchivePath, archivePath, overwrite: true);
                exportCompleted = true;
            }
            finally
            {
                if (File.Exists(temporaryArchivePath))
                {
                    File.Delete(temporaryArchivePath);
                }
            }

            return DesktopResponseMapper.ToJson(new ExportUnsatArtifactResponse(
                true,
                Path.GetFileName(archivePath),
                cnfSha256,
                preparedSelection.Cnf.VariableCount,
                preparedSelection.Cnf.ClauseCount));
        }
        finally
        {
            if (!exportCompleted && DateTimeOffset.UtcNow - ticket.CreatedUtc <= TimeSpan.FromMinutes(10))
            {
                unsatTickets.TryAdd(command.VerificationToken, ticket);
            }
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
    }

    private string CreateUnsatVerificationTicket(
        string sourceSha256,
        string cnfSha256,
        string desiredAssignmentSignature,
        TimetableFileFormat format)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in unsatTickets)
        {
            if (now - pair.Value.CreatedUtc > TimeSpan.FromMinutes(10))
            {
                unsatTickets.TryRemove(pair.Key, out _);
            }
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        unsatTickets[token] = new UnsatVerificationTicket(
            sourceSha256,
            cnfSha256,
            desiredAssignmentSignature,
            format,
            now);
        return token;
    }

    private bool TryClaimUnsatTicket(
        string token,
        string sourceSha256,
        string cnfSha256,
        string desiredAssignmentSignature,
        TimetableFileFormat format,
        out UnsatVerificationTicket ticket)
    {
        if (!unsatTickets.TryRemove(token, out var claimedTicket))
        {
            ticket = null!;
            return false;
        }
        ticket = claimedTicket;

        if (DateTimeOffset.UtcNow - ticket.CreatedUtc > TimeSpan.FromMinutes(10))
        {
            return false;
        }

        var matches = ticket.Format == format
            && CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(ticket.SourceSha256),
                Convert.FromHexString(sourceSha256))
            && CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(ticket.CnfSha256),
                Convert.FromHexString(cnfSha256))
            && CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(ticket.DesiredAssignmentSignature),
                Convert.FromHexString(desiredAssignmentSignature));
        if (!matches)
        {
            return false;
        }

        return true;
    }

    private static string ComputeCnfSha256(PersonalSelectionCnf cnf)
    {
        var dimacs = cnf.ToDimacs().ReplaceLineEndings("\n");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dimacs)));
    }

    private static string ComputeDesiredAssignmentSignature(
        ImmutableArray<DesiredAnchorAssignment> desiredAssignments)
    {
        var payload = JsonSerializer.Serialize(desiredAssignments);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private static T Deserialize<T>(JsonElement payload)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(payload.GetRawText(), SerializerOptions)
                ?? throw new DesktopBridgeException("Desktop command payload is empty.");
        }
        catch (JsonException exception)
        {
            throw new DesktopBridgeException($"Invalid desktop command payload: {exception.Message}");
        }
    }

    private static void ValidateDesiredAssignments(ImmutableArray<DesiredAssignmentPayload> assignments)
    {
        if (assignments.Length is < 1 or > MaxDesiredAssignments)
        {
            throw new DesktopBridgeException($"Chọn từ 1 đến {MaxDesiredAssignments} môn học.");
        }

        for (var index = 0; index < assignments.Length; index++)
        {
            var assignment = assignments[index];
            if (string.IsNullOrWhiteSpace(assignment.CourseCode) ||
                string.IsNullOrWhiteSpace(assignment.TeachingTeamLabel ?? assignment.LegacyLecturerName))
            {
                throw new DesktopBridgeException("Chọn đủ môn và nhóm giảng dạy trước khi tìm lịch.");
            }

            if ((assignment.CourseCode?.Length ?? 0) > MaxAssignmentFieldCharacters ||
                (assignment.CourseName?.Length ?? 0) > MaxAssignmentFieldCharacters ||
                (assignment.TeachingTeamKey?.Length ?? 0) > MaxAssignmentFieldCharacters ||
                (assignment.TeachingTeamLabel?.Length ?? assignment.LegacyLecturerName?.Length ?? 0) > MaxAssignmentFieldCharacters)
            {
                throw new DesktopBridgeException("Thông tin môn học hoặc nhóm giảng dạy vượt quá giới hạn cho phép.");
            }
        }
    }

    private sealed record ValidateWorkbookCommand(
        [property: JsonPropertyName("workbook")] WorkbookPayload Workbook);

    private sealed record SolveWorkbookCommand(
        [property: JsonPropertyName("workbook")] WorkbookPayload Workbook,
        [property: JsonPropertyName("desired_assignments")] DesiredAssignmentPayload[]? RequestedAssignments,
        [property: JsonPropertyName("timeout_seconds")] int TimeoutSeconds)
    {
        public IEnumerable<DesiredAssignmentPayload> DesiredAssignments => RequestedAssignments ?? [];
    }

    private sealed record ExportUnsatArtifactCommand(
        [property: JsonPropertyName("workbook")] WorkbookPayload Workbook,
        [property: JsonPropertyName("desired_assignments")] DesiredAssignmentPayload[]? RequestedAssignments,
        [property: JsonPropertyName("verification_token")] string? VerificationToken)
    {
        public IEnumerable<DesiredAssignmentPayload> DesiredAssignments => RequestedAssignments ?? [];
    }

    private sealed record ExportUnsatArtifactResponse(
        [property: JsonPropertyName("exported")] bool Exported,
        [property: JsonPropertyName("file_name")] string? FileName,
        [property: JsonPropertyName("cnf_sha256")] string? CnfSha256,
        [property: JsonPropertyName("variable_count")] int? VariableCount,
        [property: JsonPropertyName("clause_count")] int? ClauseCount);

    private sealed record UnsatVerificationTicket(
        string SourceSha256,
        string CnfSha256,
        string DesiredAssignmentSignature,
        TimetableFileFormat Format,
        DateTimeOffset CreatedUtc);

    private sealed record DesiredAssignmentPayload(
        [property: JsonPropertyName("course_code")] string? CourseCode,
        [property: JsonPropertyName("course_name")] string? CourseName,
        [property: JsonPropertyName("teaching_team_key")] string? TeachingTeamKey = null,
        [property: JsonPropertyName("teaching_team_label")] string? TeachingTeamLabel = null,
        [property: JsonPropertyName("lecturer_name")] string? LegacyLecturerName = null)
    {
        public Scheduler.Domain.DesiredAnchorAssignment ToDomain()
        {
            var teachingTeamLabel = TeachingTeamLabel ?? LegacyLecturerName;
            if (string.IsNullOrWhiteSpace(CourseCode) ||
                string.IsNullOrWhiteSpace(teachingTeamLabel))
            {
                throw new DesktopBridgeException("Chọn đủ môn và nhóm giảng dạy trước khi tìm lịch.");
            }

            return new Scheduler.Domain.DesiredAnchorAssignment(
                CourseCode!,
                teachingTeamLabel,
                CourseName ?? string.Empty,
                TeachingTeamKey ?? string.Empty,
                teachingTeamLabel);
        }
    }

    private sealed record WorkbookPayload(
        [property: JsonPropertyName("file_name")] string FileName,
        [property: JsonPropertyName("bytes_base64")] string BytesBase64);

    private static async Task<TimetableParseResult> ParseAsync(
        TemporaryTimetableFile document,
        CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(
                () => document.Format switch
                {
                    TimetableFileFormat.Xlsx => TimetableParser.Parse(
                        document.Path,
                        cancellationToken: cancellationToken),
                    TimetableFileFormat.Pdf => PdfTimetableParser.Parse(
                        document.Path,
                        cancellationToken: cancellationToken),
                    _ => throw new DesktopBridgeException("Định dạng thời khóa biểu chưa được hỗ trợ."),
                },
                cancellationToken);
        }
        catch (InvalidDataException)
        {
            throw new DesktopBridgeException(
                "Không thể đọc thời khóa biểu. Hãy kiểm tra định dạng và thử lại.");
        }
    }

    private sealed class TemporaryTimetableFile : IAsyncDisposable
    {
        private TemporaryTimetableFile(string path, string displayName, TimetableFileFormat format)
        {
            Path = path;
            DisplayName = displayName;
            Format = format;
        }

        public string Path { get; }

        public string DisplayName { get; }

        public TimetableFileFormat Format { get; }

        public static async Task<TemporaryTimetableFile> CreateAsync(
            WorkbookPayload payload,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(payload);
            if (string.IsNullOrWhiteSpace(payload.FileName))
            {
                throw new DesktopBridgeException("Chọn một file thời khóa biểu.");
            }

            var format = GetFormat(payload.FileName);
            if (format is null)
            {
                throw new DesktopBridgeException("Chỉ hỗ trợ file XLSX và PDF.");
            }

            if (string.IsNullOrEmpty(payload.BytesBase64) ||
                payload.BytesBase64.Length > MaxWorkbookBase64Characters ||
                payload.BytesBase64.Any(char.IsWhiteSpace))
            {
                throw new DesktopBridgeException("File thời khóa biểu phải có dung lượng từ 1 byte đến 25 MB.");
            }

            var content = new byte[MaxWorkbookBytes];
            if (!Convert.TryFromBase64String(payload.BytesBase64, content, out var contentLength) ||
                contentLength == 0)
            {
                throw new DesktopBridgeException("Không thể đọc nội dung file. Hãy chọn lại thời khóa biểu.");
            }

            var fileContents = content.AsMemory(0, contentLength);
            if (!HasExpectedSignature(fileContents.Span, format.Value))
            {
                throw new DesktopBridgeException("Nội dung file không khớp với định dạng đã chọn.");
            }

            var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SchedulerDesktop");
            Directory.CreateDirectory(directory);
            var path = System.IO.Path.Combine(directory, $"{Guid.NewGuid():N}{Extension(format.Value)}");
            try
            {
                await using var stream = new FileStream(
                    path,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await stream.WriteAsync(fileContents, cancellationToken);
                return new TemporaryTimetableFile(path, System.IO.Path.GetFileName(payload.FileName), format.Value);
            }
            catch
            {
                File.Delete(path);
                throw;
            }
        }

        private static TimetableFileFormat? GetFormat(string fileName) =>
            System.IO.Path.GetExtension(fileName).ToLowerInvariant() switch
            {
                ".xlsx" => TimetableFileFormat.Xlsx,
                ".pdf" => TimetableFileFormat.Pdf,
                _ => null,
            };

        private static bool HasExpectedSignature(ReadOnlySpan<byte> content, TimetableFileFormat format) => format switch
        {
            TimetableFileFormat.Xlsx => content.Length >= 2 && content[0] == (byte)'P' && content[1] == (byte)'K',
            TimetableFileFormat.Pdf => content.Length >= 5 && content[..5].SequenceEqual("%PDF-"u8),
            _ => false,
        };

        public ValueTask DisposeAsync()
        {
            File.Delete(Path);
            return ValueTask.CompletedTask;
        }
    }

    private enum TimetableFileFormat
    {
        Xlsx,
        Pdf,
    }

    private static string Extension(TimetableFileFormat format) => format switch
    {
        TimetableFileFormat.Xlsx => ".xlsx",
        TimetableFileFormat.Pdf => ".pdf",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    private static IPersonalSelectionSatSolver CreateNativeSolver() =>
        new NativeSolverClient(FindWorkerExecutable());

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static string? ChooseFormalArtifactPath()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            AddExtension = true,
            DefaultExt = ".zip",
            FileName = "unsat-verification.zip",
            Filter = "ZIP archive (*.zip)|*.zip",
            OverwritePrompt = true,
            Title = "Xuất gói kiểm chứng UNSAT",
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static string FindWorkerExecutable()
    {
        var configuredPath = Environment.GetEnvironmentVariable("SCHEDULER_SOLVER_WORKER");
        var allowExternalWorker =
#if DEBUG
            true;
#else
            string.Equals(
                Environment.GetEnvironmentVariable("SCHEDULER_ALLOW_EXTERNAL_SOLVER"),
                "1",
                StringComparison.Ordinal);
#endif
        var candidates = new[]
        {
            allowExternalWorker ? configuredPath : null,
            Path.Combine(AppContext.BaseDirectory, "SolverWorker.exe"),
        };

        foreach (var candidate in candidates.Where(static candidate => !string.IsNullOrWhiteSpace(candidate)))
        {
            var resolved = Path.GetFullPath(candidate!);
            if (File.Exists(resolved))
            {
                return resolved;
            }
        }

        throw new FileNotFoundException(
            "Không thể tìm thời khóa biểu. Hãy kiểm tra cài đặt ứng dụng.");
    }
}
