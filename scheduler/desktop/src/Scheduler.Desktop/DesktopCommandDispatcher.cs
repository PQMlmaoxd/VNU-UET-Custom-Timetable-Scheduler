using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Globalization;
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
    private const int MaxTimetableDocumentBytes = 25 * 1024 * 1024;
    private const int MaxTimetableSolutions = 5;
    private const int MaxDesiredAssignments = 100;
    private const int MaxAssignmentFieldCharacters = 512;
    private static readonly FrozenSet<string> ValidSessionTypes = new[]
    {
        "LT", "TH", "LT+TH", "TL+TH", "BT", "LT+BT", "ONL",
    }.ToFrozenSet(StringComparer.Ordinal);
    private static readonly FrozenSet<string> ValidPeriodCodes = new[]
    {
        "1", "2", "3", "4", "1-2", "2-3", "3-4", "Sáng", "Chiều",
    }.ToFrozenSet(StringComparer.Ordinal);
    private static readonly FrozenSet<string> ValidAtomicPeriods = new[]
    {
        "1", "2", "3", "4",
    }.ToFrozenSet(StringComparer.Ordinal);
    private readonly Func<IPersonalSelectionSatSolver> createSolver;
    private readonly Func<string?> chooseFormalArtifactPath;
    private readonly Func<string, string?> chooseTimetablePath;
    private readonly ConcurrentDictionary<string, UnsatVerificationTicket> unsatTickets = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public DesktopCommandDispatcher()
        : this(CreateNativeSolver, ChooseFormalArtifactPath, ChooseTimetablePath)
    {
    }

    public DesktopCommandDispatcher(
        Func<IPersonalSelectionSatSolver> createSolver,
        Func<string?>? chooseFormalArtifactPath = null,
        Func<string, string?>? chooseTimetablePath = null)
    {
        ArgumentNullException.ThrowIfNull(createSolver);
        this.createSolver = createSolver;
        this.chooseFormalArtifactPath = chooseFormalArtifactPath ?? ChooseFormalArtifactPath;
        this.chooseTimetablePath = chooseTimetablePath ?? ChooseTimetablePath;
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
            "save_timetable" => await SaveTimetableAsync(payload, cancellationToken),
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

    private async Task<JsonElement> SaveTimetableAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var command = Deserialize<SaveTimetableCommand>(payload);
        ValidateTimetableForSave(command.Timetable);

        var department = GetRequiredString(command.Timetable, "department");
        var sourceFileName = NormalizeSourceFileName(command.SourceFileName);
        var defaultFileName = CreateTimetableFileName(department, DateTimeOffset.Now);

        var response = JsonSerializer.Deserialize<DesktopResponseMapper.RescheduleResponse>(
            command.Timetable.GetRawText(),
            SerializerOptions)
            ?? throw new DesktopBridgeException("Kết quả thời khóa biểu có cấu trúc không hợp lệ.");
        response = response with
        {
            WorkbookPath = NormalizeSourceFileName(response.WorkbookPath),
        };
        var document = new DesktopResponseMapper.TimetableDocument(
            "vnu-uet-timetable",
            1,
            DateTimeOffset.UtcNow,
            DesktopReleaseMetadata.ApplicationVersion,
            new DesktopResponseMapper.TimetableDocumentSource(sourceFileName),
            DesktopResponseMapper.ToJson(response));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, TimetableDocumentSerializerOptions);
        if (bytes.Length > MaxTimetableDocumentBytes)
        {
            throw new DesktopBridgeException("Thời khóa biểu JSON vượt quá giới hạn 25 MB.");
        }

        var outputPath = chooseTimetablePath(defaultFileName);
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return DesktopResponseMapper.ToJson(new DesktopResponseMapper.SaveTimetableResponse(false, null));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = Path.GetFullPath(outputPath);
        if (!string.Equals(Path.GetExtension(fullPath), ".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new DesktopBridgeException("Hãy chọn tệp có phần mở rộng .json để lưu thời khóa biểu.");
        }

        var outputDirectory = Path.GetDirectoryName(fullPath)
            ?? throw new DesktopBridgeException("Không thể xác định thư mục lưu thời khóa biểu.");
        Directory.CreateDirectory(outputDirectory);
        var temporaryPath = Path.Combine(
            outputDirectory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullPath, overwrite: true);
            return DesktopResponseMapper.ToJson(new DesktopResponseMapper.SaveTimetableResponse(
                true,
                Path.GetFileName(fullPath)));
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
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

    private static void ValidateTimetableForSave(JsonElement timetable)
    {
        if (timetable.ValueKind != JsonValueKind.Object ||
            !string.Equals(GetOptionalString(timetable, "mode"), "reschedule", StringComparison.Ordinal))
        {
            throw new DesktopBridgeException("Chỉ có thể lưu kết quả thời khóa biểu hợp lệ.");
        }

        var solver = timetable.TryGetProperty("solver", out var solverElement) &&
            solverElement.ValueKind == JsonValueKind.Object
            ? solverElement
            : throw new DesktopBridgeException("Kết quả thời khóa biểu thiếu thông tin solver.");
        if (!string.Equals(GetOptionalString(solver, "satisfiability"), "SAT", StringComparison.Ordinal))
        {
            throw new DesktopBridgeException("Chỉ có thể lưu thời khóa biểu khi đã tìm thấy lời giải.");
        }

        if (!solver.TryGetProperty("solution_count", out var solutionCount) ||
            solutionCount.ValueKind != JsonValueKind.Number ||
            !solutionCount.TryGetInt32(out var count) ||
            count is < 1 or > MaxTimetableSolutions)
        {
            throw new DesktopBridgeException($"Kết quả thời khóa biểu phải có từ 1 đến {MaxTimetableSolutions} phương án.");
        }

        if (!timetable.TryGetProperty("solutions", out var solutions) ||
            solutions.ValueKind != JsonValueKind.Array ||
            solutions.GetArrayLength() != count)
        {
            throw new DesktopBridgeException("Danh sách phương án thời khóa biểu không hợp lệ.");
        }

        DesktopResponseMapper.RescheduleResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<DesktopResponseMapper.RescheduleResponse>(
                timetable.GetRawText(),
                SerializerOptions);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new DesktopBridgeException("Kết quả thời khóa biểu có cấu trúc không hợp lệ.");
        }

        if (response is null ||
            response.Mode != "reschedule" ||
            string.IsNullOrWhiteSpace(response.WorkbookPath) ||
            string.IsNullOrWhiteSpace(response.Department) ||
            response.ParseSummary is null ||
            response.PrototypeCatalog is null ||
            response.ExistingScheduleValidation is null ||
            response.Solver is null ||
            response.Solutions.IsDefaultOrEmpty ||
            response.Solver.Satisfiability != "SAT" ||
            response.Solver.SolutionCount != response.Solutions.Length ||
            !IsCompleteRescheduleResponse(response) ||
            !HasConsistentLhpScheduleCounts(timetable) ||
            !HasSameJsonShape(
                timetable,
                JsonSerializer.SerializeToElement(response, SerializerOptions)) ||
            response.Solutions
                .Select((solution, index) => (solution, index))
                .Any(item => item.solution is null || item.solution.SolutionIndex != item.index + 1))
        {
            throw new DesktopBridgeException("Kết quả thời khóa biểu có cấu trúc không hợp lệ.");
        }
    }

    private static bool IsCompleteRescheduleResponse(DesktopResponseMapper.RescheduleResponse response) =>
        !response.DesiredAssignments.IsDefaultOrEmpty &&
        response.DesiredAssignments.All(IsCompleteDesiredAnchor) &&
        HasUniqueRenderedSessionIds(response.DesiredAssignments) &&
        IsCompleteParseSummary(response.ParseSummary) &&
        IsCompletePrototypeCatalog(response.PrototypeCatalog) &&
        IsCompleteValidationSummary(response.ExistingScheduleValidation) &&
        IsCompleteSolverSummary(response.Solver) &&
        response.Solutions.All(solution =>
            solution is not null &&
            !solution.DesiredAssignments.IsDefaultOrEmpty &&
            solution.DesiredAssignments.All(IsCompleteDesiredAnchor) &&
            HasUniqueRenderedSessionIds(solution.DesiredAssignments)) &&
        HasRootAssignmentsMatchingFirstSolution(response) &&
        (response.SolvedScheduleValidation is null ||
         IsCompleteValidationSummary(response.SolvedScheduleValidation));

    private static bool HasSameJsonShape(JsonElement source, JsonElement expected)
    {
        if (source.ValueKind != expected.ValueKind)
        {
            return false;
        }

        if (source.ValueKind == JsonValueKind.Object)
        {
            var expectedProperties = expected.EnumerateObject().ToArray();
            if (source.EnumerateObject().Count() != expectedProperties.Length)
            {
                return false;
            }

            return expectedProperties.All(property =>
                source.TryGetProperty(property.Name, out var sourceValue) &&
                HasSameJsonShape(sourceValue, property.Value));
        }

        if (source.ValueKind == JsonValueKind.Array)
        {
            if (source.GetArrayLength() != expected.GetArrayLength())
            {
                return false;
            }

            for (var index = 0; index < source.GetArrayLength(); index++)
            {
                if (!HasSameJsonShape(source[index], expected[index]))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsCompleteParseSummary(DesktopResponseMapper.ParseSummary summary) =>
        summary.Sessions >= 0 &&
        summary.SchedulableSessions >= 0 &&
        summary.OnlineSessions >= 0 &&
        summary.OtherDepartmentSessions >= 0 &&
        summary.LecturerBlocks >= 0 &&
        summary.AnchorCount >= 0 &&
        summary.RequestedAssignments >= 0 &&
        summary.Rooms >= 0 &&
        summary.SkippedRows >= 0 &&
        summary.FatalWarningCount >= 0 &&
        summary.QuarantinedLhpCount >= 0 &&
        summary.QuarantinedSessionCount >= 0 &&
        !summary.Warnings.IsDefault &&
        summary.Warnings.All(item => item is not null) &&
        !summary.FatalWarnings.IsDefault &&
        summary.FatalWarnings.All(item => item is not null) &&
        !summary.QuarantinedOfferings.IsDefault &&
        summary.QuarantinedOfferings.All(item =>
            item is not null &&
            item.CourseCode is not null &&
            item.LhpCode is not null &&
            item.ReasonCode is not null &&
            item.QuarantinedRowCount >= 0 &&
            item.ExcludedSessionCount >= 0 &&
            !item.SourceLocations.IsDefault &&
            item.SourceLocations.All(location => location is not null));

    private static bool IsCompletePrototypeCatalog(DesktopResponseMapper.PrototypeCatalog catalog) =>
        !catalog.Anchors.IsDefault &&
        !catalog.RoomCostRules.IsDefault &&
        catalog.Anchors.All(anchor =>
            anchor is not null &&
            anchor.CourseCode is not null &&
            anchor.CourseName is not null &&
            anchor.TeachingTeamKey is not null &&
            anchor.TeachingTeamLabel is not null &&
            anchor.SessionCount >= 0) &&
        catalog.RoomCostRules.All(rule =>
            rule is not null &&
            rule.FromZone is not null &&
            rule.ToZone is not null &&
            rule.Description is not null);

    private static bool IsCompleteValidationSummary(DesktopResponseMapper.ValidationSummary summary) =>
        summary.ViolationCount >= 0 &&
        summary.MissingSessionCount >= 0 &&
        !summary.SampleViolations.IsDefault &&
        summary.SampleViolations.All(item => item is not null);

    private static bool IsCompleteSolverSummary(DesktopResponseMapper.SolverSummary solver) =>
        solver.Backend is not null &&
        solver.Status is "feasible" or "infeasible" or "timeout" &&
        solver.Satisfiability is "SAT" or "UNSAT" or "UNKNOWN" &&
        ((solver.Status == "feasible" && solver.Satisfiability == "SAT" && solver.SolutionCount > 0) ||
         (solver.Status == "infeasible" && solver.Satisfiability == "UNSAT" && solver.SolutionCount == 0) ||
         (solver.Status == "timeout" && solver.Satisfiability == "UNKNOWN" && solver.SolutionCount is >= 0 and <= MaxTimetableSolutions)) &&
        solver.SolveTimeMilliseconds >= 0 &&
        (solver.ObjectiveValue is null || double.IsFinite(solver.ObjectiveValue.Value)) &&
        solver.AssignmentCount >= 0 &&
        solver.SolutionCount >= 0 &&
        solver.SolverInfo is not null &&
        !solver.Explanation.IsDefault &&
        solver.Explanation.All(item => item is not null);

    private static bool IsCompleteDesiredAnchor(DesktopResponseMapper.DesiredAnchorSummary anchor) =>
        anchor is not null &&
        !string.IsNullOrWhiteSpace(anchor.CourseCode) &&
        !string.IsNullOrWhiteSpace(anchor.TeachingTeamKey) &&
        !string.IsNullOrWhiteSpace(anchor.TeachingTeamLabel) &&
        !string.IsNullOrWhiteSpace(anchor.CourseName) &&
        anchor.SessionCount >= 0 &&
        !anchor.LhpCodes.IsDefault &&
        anchor.LhpCodes.Length > 0 &&
        anchor.LhpCodes.All(code => !string.IsNullOrWhiteSpace(code)) &&
        !anchor.MatchedSessions.IsDefaultOrEmpty &&
        !anchor.LhpSchedules.IsDefaultOrEmpty &&
        anchor.MatchedSessions.All(IsCompleteSession) &&
        anchor.LhpSchedules.All(schedule =>
            schedule is not null &&
            !string.IsNullOrWhiteSpace(schedule.LhpCode) &&
            !schedule.MatchedSessions.IsDefaultOrEmpty &&
            schedule.MatchedSessions.All(IsCompleteSession)) &&
        IsSemanticallyConsistentDesiredAnchor(anchor);

    private static bool IsCompleteSession(DesktopResponseMapper.SessionScheduleItem session) =>
        session is not null &&
        !string.IsNullOrWhiteSpace(session.SessionId) &&
        !string.IsNullOrWhiteSpace(session.CourseCode) &&
        session.CourseName is not null &&
        !string.IsNullOrWhiteSpace(session.LhpCode) &&
        session.SessionType is not null &&
        session.TimeslotLabel is not null &&
        session.RoomCode is not null &&
        !session.LecturerNames.IsDefault &&
        !session.CohortCodes.IsDefault &&
        session.LecturerNames.All(name => name is not null) &&
        session.CohortCodes.All(cohort => cohort is not null) &&
        (session.PeriodAtomic is null ||
         (!session.PeriodAtomic.Value.IsDefault &&
           session.PeriodAtomic.Value.All(atomic => atomic is not null))) &&
        ValidSessionTypes.Contains(session.SessionType) &&
        IsValidSessionTiming(session);

    private static bool IsValidSessionTiming(DesktopResponseMapper.SessionScheduleItem session)
    {
        if (session.Day is null && session.PeriodCode is null && session.PeriodAtomic is null)
        {
            return true;
        }

        if (session.Day is null or < 2 or > 7 ||
            session.PeriodCode is null ||
            !ValidPeriodCodes.Contains(session.PeriodCode) ||
            session.PeriodAtomic is null)
        {
            return false;
        }

        var atomicPeriods = session.PeriodAtomic.Value;
        return !atomicPeriods.IsDefault &&
               !atomicPeriods.IsEmpty &&
               atomicPeriods.Distinct(StringComparer.Ordinal).Count() == atomicPeriods.Length &&
               atomicPeriods.All(ValidAtomicPeriods.Contains);
    }

    private static bool IsSemanticallyConsistentDesiredAnchor(
        DesktopResponseMapper.DesiredAnchorSummary anchor)
    {
        var renderedSessions = anchor.LhpSchedules
            .SelectMany(schedule => schedule.MatchedSessions)
            .ToArray();
        var scheduleCodes = anchor.LhpSchedules
            .Select(schedule => schedule.LhpCode)
            .ToArray();

        return anchor.LhpCodes.Distinct(StringComparer.Ordinal).Count() == anchor.LhpCodes.Length &&
               SameStringSet(anchor.LhpCodes, scheduleCodes) &&
               anchor.SessionCount == anchor.MatchedSessions.Length &&
               anchor.SessionCount == renderedSessions.Length &&
               anchor.MatchedSessions.All(session => session.CourseCode == anchor.CourseCode) &&
               anchor.LhpSchedules.All(schedule =>
                   schedule.MatchedSessions.All(session =>
                       session.CourseCode == anchor.CourseCode &&
                       session.LhpCode == schedule.LhpCode)) &&
               AreEquivalentSessionCollections(anchor.MatchedSessions, renderedSessions);
    }

    private static bool SameStringSet(
        IEnumerable<string> left,
        IEnumerable<string> right)
    {
        var leftValues = left.ToArray();
        var rightValues = right.ToArray();
        var leftSet = leftValues.ToFrozenSet(StringComparer.Ordinal);
        var rightSet = rightValues.ToFrozenSet(StringComparer.Ordinal);
        return leftSet.Count == leftValues.Length &&
               rightSet.Count == rightValues.Length &&
               leftSet.SetEquals(rightSet);
    }

    private static bool AreEquivalentSessionCollections(
        IEnumerable<DesktopResponseMapper.SessionScheduleItem> left,
        IEnumerable<DesktopResponseMapper.SessionScheduleItem> right)
    {
        var leftSessions = left.ToArray();
        var rightSessions = right.ToArray();
        if (leftSessions.Length != rightSessions.Length ||
            leftSessions.Select(session => session.SessionId).Distinct(StringComparer.Ordinal).Count() != leftSessions.Length ||
            rightSessions.Select(session => session.SessionId).Distinct(StringComparer.Ordinal).Count() != rightSessions.Length)
        {
            return false;
        }

        var rightById = rightSessions.ToDictionary(session => session.SessionId, StringComparer.Ordinal);
        return leftSessions.All(session =>
            rightById.TryGetValue(session.SessionId, out var counterpart) &&
            AreEquivalentSessions(session, counterpart));
    }

    private static bool AreEquivalentSessions(
        DesktopResponseMapper.SessionScheduleItem left,
        DesktopResponseMapper.SessionScheduleItem right) =>
        left.SessionId == right.SessionId &&
        left.CourseCode == right.CourseCode &&
        left.CourseName == right.CourseName &&
        left.LhpCode == right.LhpCode &&
        left.SessionType == right.SessionType &&
        left.LecturerNames.SequenceEqual(right.LecturerNames, StringComparer.Ordinal) &&
        left.CohortCodes.SequenceEqual(right.CohortCodes, StringComparer.Ordinal) &&
        left.TimeslotLabel == right.TimeslotLabel &&
        left.RoomCode == right.RoomCode &&
        left.SourceRow == right.SourceRow &&
        left.Day == right.Day &&
        left.PeriodCode == right.PeriodCode &&
        AreEquivalentOptionalArrays(left.PeriodAtomic, right.PeriodAtomic);

    private static bool AreEquivalentOptionalArrays(
        ImmutableArray<string>? left,
        ImmutableArray<string>? right)
    {
        if (left.HasValue != right.HasValue)
        {
            return false;
        }

        return !left.HasValue || left.Value.SequenceEqual(right!.Value, StringComparer.Ordinal);
    }

    private static bool HasRootAssignmentsMatchingFirstSolution(
        DesktopResponseMapper.RescheduleResponse response)
    {
        if (response.Solutions.IsDefaultOrEmpty)
        {
            return response.DesiredAssignments.IsDefaultOrEmpty;
        }

        return AreEquivalentDesiredAssignments(
            response.DesiredAssignments,
            response.Solutions[0].DesiredAssignments);
    }

    private static bool AreEquivalentDesiredAssignments(
        ImmutableArray<DesktopResponseMapper.DesiredAnchorSummary> left,
        ImmutableArray<DesktopResponseMapper.DesiredAnchorSummary> right)
    {
        return left.Length == right.Length &&
               left.Zip(right).All(pair => AreEquivalentDesiredAnchors(pair.First, pair.Second));
    }

    private static bool AreEquivalentDesiredAnchors(
        DesktopResponseMapper.DesiredAnchorSummary left,
        DesktopResponseMapper.DesiredAnchorSummary right)
    {
        return left.CourseCode == right.CourseCode &&
               left.TeachingTeamKey == right.TeachingTeamKey &&
               left.TeachingTeamLabel == right.TeachingTeamLabel &&
               left.CourseName == right.CourseName &&
               left.SessionCount == right.SessionCount &&
               left.LhpCodes.SequenceEqual(right.LhpCodes, StringComparer.Ordinal) &&
               AreEquivalentSessionCollections(left.MatchedSessions, right.MatchedSessions) &&
               left.LhpSchedules.Length == right.LhpSchedules.Length &&
               left.LhpSchedules.Zip(right.LhpSchedules).All(pair =>
                   pair.First.LhpCode == pair.Second.LhpCode &&
                   pair.First.SessionCount == pair.Second.SessionCount &&
                   AreEquivalentSessionCollections(pair.First.MatchedSessions, pair.Second.MatchedSessions));
    }

    private static bool HasUniqueRenderedSessionIds(
        IEnumerable<DesktopResponseMapper.DesiredAnchorSummary> desiredAssignments)
    {
        var sessionIds = desiredAssignments
            .SelectMany(anchor => anchor.LhpSchedules)
            .SelectMany(schedule => schedule.MatchedSessions)
            .Select(session => session.SessionId)
            .ToArray();
        return sessionIds.Length == sessionIds.Distinct(StringComparer.Ordinal).Count();
    }

    private static bool HasConsistentLhpScheduleCounts(JsonElement timetable) =>
        HasConsistentDesiredAssignmentCounts(timetable, "desired_assignments") &&
        timetable.TryGetProperty("solutions", out var solutions) &&
        solutions.ValueKind == JsonValueKind.Array &&
        solutions.EnumerateArray().All(solution =>
            HasConsistentDesiredAssignmentCounts(solution, "desired_assignments"));

    private static bool HasConsistentDesiredAssignmentCounts(JsonElement parent, string propertyName)
    {
        if (parent.ValueKind != JsonValueKind.Object ||
            !parent.TryGetProperty(propertyName, out var desiredAssignments) ||
            desiredAssignments.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return desiredAssignments.EnumerateArray().All(HasConsistentDesiredLhpScheduleCounts);
    }

    private static bool HasConsistentDesiredLhpScheduleCounts(JsonElement desiredAssignment)
    {
        if (desiredAssignment.ValueKind != JsonValueKind.Object ||
            !desiredAssignment.TryGetProperty("lhp_schedules", out var schedules) ||
            schedules.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return schedules.EnumerateArray().All(schedule =>
            schedule.ValueKind == JsonValueKind.Object &&
            schedule.TryGetProperty("session_count", out var sessionCount) &&
            sessionCount.TryGetInt32(out var count) &&
            schedule.TryGetProperty("matched_sessions", out var matchedSessions) &&
            matchedSessions.ValueKind == JsonValueKind.Array &&
            count == matchedSessions.GetArrayLength());
    }

    private static string GetRequiredString(JsonElement element, string propertyName)
    {
        var value = GetOptionalString(element, propertyName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DesktopBridgeException($"Kết quả thời khóa biểu thiếu trường {propertyName}.");
        }

        return value;
    }

    private static string? GetOptionalString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string NormalizeSourceFileName(string? sourceFileName)
    {
        var normalized = (sourceFileName ?? string.Empty).Replace('\\', '/');
        normalized = Path.GetFileName(normalized);
        return string.IsNullOrWhiteSpace(normalized) ? "unknown" : normalized[..Math.Min(normalized.Length, 260)];
    }

    private static string CreateTimetableFileName(string department, DateTimeOffset now)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var safeDepartment = new string(department
            .Select(character => invalidCharacters.Contains(character) || char.IsWhiteSpace(character)
                ? '-'
                : character)
            .ToArray())
            .Trim('-');
        if (string.IsNullOrWhiteSpace(safeDepartment))
        {
            safeDepartment = "unknown";
        }

        return $"TKB-{safeDepartment}-{now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture)}.json";
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

    private sealed record SaveTimetableCommand(
        [property: JsonPropertyName("timetable")] JsonElement Timetable,
        [property: JsonPropertyName("source_file_name")] string? SourceFileName);

    private sealed record ExportUnsatArtifactResponse(
        [property: JsonPropertyName("exported")] bool Exported,
        [property: JsonPropertyName("file_name")] string? FileName,
        [property: JsonPropertyName("cnf_sha256")] string? CnfSha256,
        [property: JsonPropertyName("variable_count")] int? VariableCount,
        [property: JsonPropertyName("clause_count")] int? ClauseCount);

    private static readonly JsonSerializerOptions TimetableDocumentSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

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

    private static string? ChooseTimetablePath(string defaultFileName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            AddExtension = true,
            DefaultExt = ".json",
            FileName = defaultFileName,
            Filter = "Timetable JSON (*.json)|*.json",
            OverwritePrompt = true,
            Title = "Lưu thời khóa biểu",
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
