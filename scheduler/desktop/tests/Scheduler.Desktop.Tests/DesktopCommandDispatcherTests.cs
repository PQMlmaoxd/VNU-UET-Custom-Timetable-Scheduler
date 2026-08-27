using System.Collections.Immutable;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Scheduler.Desktop;
using Scheduler.Application;
using Xunit;
using Xunit.Sdk;

namespace Scheduler.Desktop.Tests;

public sealed class DesktopCommandDispatcherTests : IDisposable
{
    private readonly string temporaryDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public DesktopCommandDispatcherTests() => Directory.CreateDirectory(temporaryDirectory);

    [Fact]
    public async Task ValidateWorkbookReturnsCurrentReactContractForImportedWorkbook()
    {
        var workbookPath = CreateWorkbook();
        var payload = JsonSerializer.SerializeToElement(new
        {
            workbook = new
            {
                file_name = "fixture.xlsx",
                bytes_base64 = Convert.ToBase64String(await File.ReadAllBytesAsync(workbookPath)),
            },
        });

        var result = await new DesktopCommandDispatcher().DispatchAsync(
            "validate_workbook",
            payload,
            CancellationToken.None);

        Assert.Equal("validate_existing", result.GetProperty("mode").GetString());
        Assert.Equal("fixture.xlsx", result.GetProperty("workbook_path").GetString());
        Assert.Equal(1, result.GetProperty("parse_summary").GetProperty("sessions").GetInt32());
        Assert.Equal(1, result.GetProperty("parse_summary").GetProperty("anchor_count").GetInt32());
        Assert.True(result.GetProperty("existing_schedule_validation").GetProperty("is_valid").GetBoolean());
        Assert.Equal("INT1000", result.GetProperty("prototype_catalog").GetProperty("anchors")[0]
            .GetProperty("course_code").GetString());
        Assert.Equal(JsonValueKind.Array, result.GetProperty("parse_summary").GetProperty("warnings").ValueKind);
        Assert.False(result.GetProperty("parse_summary").GetProperty("partial_import").GetBoolean());
        Assert.Equal(0, result.GetProperty("parse_summary").GetProperty("quarantined_lhp_count").GetInt32());
        Assert.Equal(JsonValueKind.Array, result.GetProperty("parse_summary")
            .GetProperty("quarantined_offerings").ValueKind);
        Assert.Equal(JsonValueKind.Array, result.GetProperty("existing_schedule_validation")
            .GetProperty("sample_violations").ValueKind);
        Assert.Equal(JsonValueKind.Number, result.GetProperty("prototype_catalog").GetProperty("room_cost_rules")[0]
            .GetProperty("cost").ValueKind);
    }

    [Fact]
    public async Task ValidateWorkbookReplacesMalformedDisplayName()
    {
        var workbookPath = CreateWorkbook();
        var payload = JsonSerializer.SerializeToElement(new
        {
            workbook = new
            {
                file_name = "fixture-\uFFFD.xlsx",
                bytes_base64 = Convert.ToBase64String(await File.ReadAllBytesAsync(workbookPath)),
            },
        });

        var result = await new DesktopCommandDispatcher().DispatchAsync(
            "validate_workbook",
            payload,
            CancellationToken.None);

        Assert.Equal("unknown", result.GetProperty("workbook_path").GetString());
    }

    [Fact]
    [Trait("Category", "Compatibility")]
    public async Task ValidatePdfReturnsCurrentReactContractWhenSignedPdfIsAvailable()
    {
        var pdfPath = FindSignedPdf();
        if (pdfPath is null)
        {
            throw SkipException.ForSkip("External signed PDF fixture is not available.");
        }

        var pdfName = Path.GetFileName(pdfPath);

        var payload = JsonSerializer.SerializeToElement(new
        {
            workbook = new
            {
                file_name = pdfName,
                bytes_base64 = Convert.ToBase64String(await File.ReadAllBytesAsync(pdfPath)),
            },
        });

        var result = await new DesktopCommandDispatcher().DispatchAsync(
            "validate_workbook",
            payload,
            CancellationToken.None);

        Assert.Equal("validate_existing", result.GetProperty("mode").GetString());
        Assert.Equal(pdfName, result.GetProperty("workbook_path").GetString());
        Assert.Equal(1661, result.GetProperty("parse_summary").GetProperty("sessions").GetInt32());
        Assert.Equal(50, result.GetProperty("parse_summary").GetProperty("online_sessions").GetInt32());
    }

    [Fact]
    public async Task SolveWorkbookReturnsCurrentReactContractForValidatedModel()
    {
        var workbookPath = CreateWorkbook();
        var solvePayload = JsonSerializer.SerializeToElement(new
        {
            workbook = new
            {
                file_name = "fixture.xlsx",
                bytes_base64 = Convert.ToBase64String(await File.ReadAllBytesAsync(workbookPath)),
            },
            desired_assignments = new[]
            {
                new
                {
                    course_code = "INT1000",
                    course_name = "Programming",
                    teaching_team_label = "Alice",
                },
            },
            timeout_seconds = 30,
        });

        var result = await new DesktopCommandDispatcher(static () => new SatisfyingSolver()).DispatchAsync(
            "solve_workbook",
            solvePayload,
            CancellationToken.None);

        Assert.Equal("reschedule", result.GetProperty("mode").GetString());
        Assert.Equal("cadical", result.GetProperty("solver").GetProperty("backend").GetString());
        Assert.Equal("SAT", result.GetProperty("solver").GetProperty("satisfiability").GetString());
        Assert.Equal(1, result.GetProperty("solver").GetProperty("assignment_count").GetInt32());
        Assert.Null(result.GetProperty("solver").GetProperty("formal_verification_token").GetString());
        Assert.Equal(1, result.GetProperty("solutions").GetArrayLength());
        Assert.Equal(0, result.GetProperty("solutions")[0].GetProperty("movement_cost").GetInt32());
        Assert.Equal("LHP-1", result.GetProperty("solutions")[0].GetProperty("desired_assignments")[0]
            .GetProperty("lhp_codes")[0].GetString());
    }

    [Theory]
    [InlineData("null_assignment")]
    [InlineData("course_name")]
    [InlineData("teaching_team_key")]
    [InlineData("teaching_team_label")]
    [InlineData("lecturer_name")]
    public async Task SolveWorkbookRejectsMalformedDesiredAssignments(string field)
    {
        var workbookPath = CreateWorkbook();
        JsonNode? assignment = field == "null_assignment"
            ? null
            : new JsonObject
            {
                ["course_code"] = "INT1000",
                ["course_name"] = "Programming",
                ["teaching_team_key"] = "team-alice",
                ["teaching_team_label"] = "Alice",
                ["lecturer_name"] = "Alice",
            };
        if (assignment is JsonObject assignmentObject)
        {
            assignmentObject[field] = "\uFFFD";
        }
        var payload = JsonSerializer.SerializeToElement(new
        {
            workbook = new
            {
                file_name = "fixture.xlsx",
                bytes_base64 = Convert.ToBase64String(await File.ReadAllBytesAsync(workbookPath)),
            },
            desired_assignments = new JsonArray(assignment),
            timeout_seconds = 30,
        });
        var solverCalled = false;
        var dispatcher = new DesktopCommandDispatcher(() =>
        {
            solverCalled = true;
            return new SatisfyingSolver();
        });

        await Assert.ThrowsAsync<DesktopBridgeException>(() => dispatcher.DispatchAsync(
            "solve_workbook",
            payload,
            CancellationToken.None));

        Assert.False(solverCalled);
    }

    [Fact]
    public async Task ValidateWorkbookReturnsAnchorCandidatesForEveryCourseTeamPair()
    {
        var workbookPath = CreateWorkbook();
        var payload = JsonSerializer.SerializeToElement(new
        {
            workbook = new
            {
                file_name = "fixture.xlsx",
                bytes_base64 = Convert.ToBase64String(await File.ReadAllBytesAsync(workbookPath)),
            },
        });

        var result = await new DesktopCommandDispatcher().DispatchAsync(
            "validate_workbook",
            payload,
            CancellationToken.None);

        Assert.Equal(JsonValueKind.Array, result.GetProperty("anchor_candidates").ValueKind);
        var group = result.GetProperty("anchor_candidates")[0];
        Assert.Equal("INT1000", group.GetProperty("course_code").GetString());
        Assert.StartsWith("team:", group.GetProperty("teaching_team_key").GetString());
        Assert.Equal("Alice", group.GetProperty("teaching_team_label").GetString());
        var candidate = group.GetProperty("candidates")[0];
        Assert.Equal("LHP-1", candidate.GetProperty("lhp_code").GetString());
        Assert.StartsWith("unit:", candidate.GetProperty("teaching_unit_key").GetString());
        Assert.Equal(1, candidate.GetProperty("session_count").GetInt32());
        Assert.Equal(1, candidate.GetProperty("slots").GetArrayLength());
        var slot = candidate.GetProperty("slots")[0];
        Assert.Equal(JsonValueKind.Number, slot.GetProperty("day").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(slot.GetProperty("period_code").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(slot.GetProperty("timeslot_label").GetString()));
    }

    [Fact]
    public async Task SolveWorkbookHonorsLockedClassSection()
    {
        var workbookPath = CreateWorkbook();
        var validatePayload = JsonSerializer.SerializeToElement(new
        {
            workbook = new
            {
                file_name = "fixture.xlsx",
                bytes_base64 = Convert.ToBase64String(await File.ReadAllBytesAsync(workbookPath)),
            },
        });
        var validated = await new DesktopCommandDispatcher().DispatchAsync(
            "validate_workbook",
            validatePayload,
            CancellationToken.None);
        var candidate = validated.GetProperty("anchor_candidates")[0].GetProperty("candidates")[0];

        var solvePayload = JsonSerializer.SerializeToElement(new
        {
            workbook = new
            {
                file_name = "fixture.xlsx",
                bytes_base64 = Convert.ToBase64String(await File.ReadAllBytesAsync(workbookPath)),
            },
            desired_assignments = new[]
            {
                new
                {
                    course_code = "INT1000",
                    course_name = "Programming",
                    teaching_team_label = "Alice",
                    locked_lhp_code = candidate.GetProperty("lhp_code").GetString(),
                    locked_teaching_unit_key = candidate.GetProperty("teaching_unit_key").GetString(),
                },
            },
            timeout_seconds = 30,
        });

        var result = await new DesktopCommandDispatcher(static () => new SatisfyingSolver()).DispatchAsync(
            "solve_workbook",
            solvePayload,
            CancellationToken.None);

        Assert.Equal("SAT", result.GetProperty("solver").GetProperty("satisfiability").GetString());
        Assert.Equal("LHP-1", result.GetProperty("solutions")[0].GetProperty("desired_assignments")[0]
            .GetProperty("lhp_codes")[0].GetString());
    }

    [Theory]
    [InlineData("LHP-999")]
    [InlineData(null)]
    public async Task SolveWorkbookRejectsUnknownLockedClassSection(string? lockedLhpCode)
    {
        var workbookPath = CreateWorkbook();
        var validatePayload = JsonSerializer.SerializeToElement(new
        {
            workbook = new
            {
                file_name = "fixture.xlsx",
                bytes_base64 = Convert.ToBase64String(await File.ReadAllBytesAsync(workbookPath)),
            },
        });
        var validated = await new DesktopCommandDispatcher().DispatchAsync(
            "validate_workbook",
            validatePayload,
            CancellationToken.None);
        var realUnitKey = validated.GetProperty("anchor_candidates")[0].GetProperty("candidates")[0]
            .GetProperty("teaching_unit_key").GetString();
        var solverCalled = false;
        var dispatcher = new DesktopCommandDispatcher(() =>
        {
            solverCalled = true;
            return new SatisfyingSolver();
        });

        var assignment = new JsonObject
        {
            ["course_code"] = "INT1000",
            ["course_name"] = "Programming",
            ["teaching_team_label"] = "Alice",
            ["locked_lhp_code"] = lockedLhpCode ?? "LHP-1",
            ["locked_teaching_unit_key"] = lockedLhpCode is null ? "unit:not-in-catalog" : realUnitKey,
        };
        var solvePayload = JsonSerializer.SerializeToElement(new
        {
            workbook = new
            {
                file_name = "fixture.xlsx",
                bytes_base64 = Convert.ToBase64String(await File.ReadAllBytesAsync(workbookPath)),
            },
            desired_assignments = new JsonArray(assignment),
            timeout_seconds = 30,
        });

        var exception = await Assert.ThrowsAsync<DesktopBridgeException>(() => dispatcher.DispatchAsync(
            "solve_workbook",
            solvePayload,
            CancellationToken.None));

        Assert.Contains("khóa", exception.Message, StringComparison.Ordinal);
        Assert.False(solverCalled);
    }

    [Theory]
    [InlineData("missing_unit_key", "Khóa lớp học phần không hợp lệ.")]
    [InlineData("missing_lhp_code", "Khóa lớp học phần không hợp lệ.")]
    [InlineData("malformed_characters", "Khóa lớp học phần chứa ký tự không được hỗ trợ.")]
    [InlineData("too_long", "Khóa lớp học phần vượt quá giới hạn cho phép.")]
    public async Task SolveWorkbookRejectsInvalidLockPayloads(string scenario, string expectedMessage)
    {
        var workbookPath = CreateWorkbook();
        var assignment = new JsonObject
        {
            ["course_code"] = "INT1000",
            ["course_name"] = "Programming",
            ["teaching_team_label"] = "Alice",
        };
        switch (scenario)
        {
            case "missing_unit_key":
                assignment["locked_lhp_code"] = "LHP-1";
                break;
            case "missing_lhp_code":
                assignment["locked_teaching_unit_key"] = "unit:abc";
                break;
            case "malformed_characters":
                assignment["locked_lhp_code"] = "\uFFFD";
                assignment["locked_teaching_unit_key"] = "unit:abc";
                break;
            case "too_long":
                assignment["locked_lhp_code"] = "LHP-1";
                assignment["locked_teaching_unit_key"] = $"unit:{new string('a', 520)}";
                break;
        }
        var payload = JsonSerializer.SerializeToElement(new
        {
            workbook = new
            {
                file_name = "fixture.xlsx",
                bytes_base64 = Convert.ToBase64String(await File.ReadAllBytesAsync(workbookPath)),
            },
            desired_assignments = new JsonArray(assignment),
            timeout_seconds = 30,
        });

        var exception = await Assert.ThrowsAsync<DesktopBridgeException>(() => new DesktopCommandDispatcher().DispatchAsync(
            "solve_workbook",
            payload,
            CancellationToken.None));

        Assert.Equal(expectedMessage, exception.Message);
    }

    [Fact]
    public async Task SaveTimetableWritesVersionedSatDocumentAtomically()
    {
        var destination = Path.Combine(temporaryDirectory, "saved-timetable.json");
        string? suggestedFileName = null;
        var dispatcher = new DesktopCommandDispatcher(
            static () => new SatisfyingSolver(),
            chooseTimetablePath: suggested =>
            {
                suggestedFileName = suggested;
                return destination;
            });
        var solveResult = await dispatcher.DispatchAsync(
            "solve_workbook",
            CreateSolvePayload(CreateWorkbook()),
            CancellationToken.None);

        var result = await dispatcher.DispatchAsync(
            "save_timetable",
            JsonSerializer.SerializeToElement(new
            {
                timetable = solveResult,
                source_file_name = @"C:\Downloads\source.xlsx",
            }),
            CancellationToken.None);

        Assert.True(result.GetProperty("saved").GetBoolean());
        Assert.Equal("saved-timetable.json", result.GetProperty("file_name").GetString());
        Assert.NotNull(suggestedFileName);
        Assert.StartsWith("TKB-", suggestedFileName, StringComparison.Ordinal);
        Assert.EndsWith(".json", suggestedFileName, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(destination));
        var root = document.RootElement;
        Assert.Equal("vnu-uet-timetable", root.GetProperty("schema").GetString());
        Assert.Equal(1, root.GetProperty("schema_version").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("saved_at_utc").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("app_version").GetString()));
        Assert.Equal("source.xlsx", root.GetProperty("source").GetProperty("file_name").GetString());
        Assert.Equal("reschedule", root.GetProperty("timetable").GetProperty("mode").GetString());
        Assert.Equal(
            solveResult.GetProperty("solutions").GetArrayLength(),
            root.GetProperty("timetable").GetProperty("solutions").GetArrayLength());
        Assert.DoesNotContain("\uFEFF", await File.ReadAllTextAsync(destination), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveTimetableNormalizesWorkbookPathToFileName()
    {
        var destination = Path.Combine(temporaryDirectory, "normalized.json");
        var dispatcher = new DesktopCommandDispatcher(
            static () => new SatisfyingSolver(),
            chooseTimetablePath: _ => destination);
        var solveResult = await dispatcher.DispatchAsync(
            "solve_workbook",
            CreateSolvePayload(CreateWorkbook()),
            CancellationToken.None);
        var timetable = JsonNode.Parse(solveResult.GetRawText())!.AsObject();
        timetable["workbook_path"] = @"C:\Users\Alice\Private\fixture.xlsx";

        await dispatcher.DispatchAsync(
            "save_timetable",
            JsonSerializer.SerializeToElement(new { timetable }),
            CancellationToken.None);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(destination));
        Assert.Equal(
            "fixture.xlsx",
            document.RootElement.GetProperty("timetable").GetProperty("workbook_path").GetString());
    }

    [Fact]
    public async Task SaveTimetableTruncatesSourceFileNameWithoutSplittingSurrogatePair()
    {
        var destination = Path.Combine(temporaryDirectory, "source-name.json");
        var dispatcher = new DesktopCommandDispatcher(
            static () => new SatisfyingSolver(),
            chooseTimetablePath: _ => destination);
        var solveResult = await dispatcher.DispatchAsync(
            "solve_workbook",
            CreateSolvePayload(CreateWorkbook()),
            CancellationToken.None);
        var sourceFileName = new string('s', 259) + "\U0001F600";

        await dispatcher.DispatchAsync(
            "save_timetable",
            JsonSerializer.SerializeToElement(new
            {
                timetable = solveResult,
                source_file_name = sourceFileName,
            }),
            CancellationToken.None);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(destination));
        Assert.Equal(new string('s', 259), document.RootElement.GetProperty("source").GetProperty("file_name").GetString());
    }

    [Fact]
    public async Task SaveTimetableReplacesMalformedSourceFileName()
    {
        var destination = Path.Combine(temporaryDirectory, "source-name.json");
        var dispatcher = new DesktopCommandDispatcher(
            static () => new SatisfyingSolver(),
            chooseTimetablePath: _ => destination);
        var solveResult = await dispatcher.DispatchAsync(
            "solve_workbook",
            CreateSolvePayload(CreateWorkbook()),
            CancellationToken.None);

        await dispatcher.DispatchAsync(
            "save_timetable",
            JsonSerializer.SerializeToElement(new
            {
                timetable = solveResult,
                source_file_name = "source-\uFFFD.xlsx",
            }),
            CancellationToken.None);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(destination));
        Assert.Equal("unknown", document.RootElement.GetProperty("source").GetProperty("file_name").GetString());
    }

    [Fact]
    public async Task SaveTimetableReturnsCancellationWithoutCreatingAFile()
    {
        var destination = Path.Combine(temporaryDirectory, "cancelled.json");
        var dispatcher = new DesktopCommandDispatcher(
            static () => new SatisfyingSolver(),
            chooseTimetablePath: _ => null);
        var solveResult = await dispatcher.DispatchAsync(
            "solve_workbook",
            CreateSolvePayload(CreateWorkbook()),
            CancellationToken.None);

        var result = await dispatcher.DispatchAsync(
            "save_timetable",
            JsonSerializer.SerializeToElement(new { timetable = solveResult, source_file_name = "source.xlsx" }),
            CancellationToken.None);

        Assert.False(result.GetProperty("saved").GetBoolean());
        Assert.Null(result.GetProperty("file_name").GetString());
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task SaveTimetableRejectsOversizedDocumentBeforeOpeningDialog()
    {
        var destination = Path.Combine(temporaryDirectory, "oversized.json");
        var dialogOpened = false;
        var dispatcher = new DesktopCommandDispatcher(
            static () => new SatisfyingSolver(),
            chooseTimetablePath: _ =>
            {
                dialogOpened = true;
                return destination;
            });
        var response = await dispatcher.DispatchAsync(
            "solve_workbook",
            CreateSolvePayload(CreateWorkbook()),
            CancellationToken.None);
        var timetable = JsonNode.Parse(response.GetRawText())!.AsObject();
        timetable["solver"]!.AsObject()["explanation"] = new JsonArray(
            JsonValue.Create(new string('x', 25 * 1024 * 1024)));

        await Assert.ThrowsAsync<DesktopBridgeException>(() => dispatcher.DispatchAsync(
            "save_timetable",
            JsonSerializer.SerializeToElement(new { timetable }),
            CancellationToken.None));

        Assert.False(dialogOpened);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task SaveTimetableRejectsSessionsOutsideRenderableDomain()
    {
        var dispatcher = new DesktopCommandDispatcher(
            static () => new SatisfyingSolver(),
            chooseTimetablePath: _ => Path.Combine(temporaryDirectory, "invalid.json"));
        var response = await dispatcher.DispatchAsync(
            "solve_workbook",
            CreateSolvePayload(CreateWorkbook()),
            CancellationToken.None);

        foreach (var invalidField in new[] { "session_type", "day", "period_code", "period_atomic" })
        {
            var timetable = JsonNode.Parse(response.GetRawText())!.AsObject();
            foreach (var desiredAssignments in new[]
            {
                timetable["desired_assignments"]!.AsArray(),
                timetable["solutions"]!.AsArray()[0]!["desired_assignments"]!.AsArray(),
            })
            {
                var desiredAssignment = desiredAssignments[0]!.AsObject();
                SetInvalidSessionField(
                    desiredAssignment["matched_sessions"]!.AsArray()[0]!.AsObject(),
                    invalidField);
                SetInvalidSessionField(
                    desiredAssignment["lhp_schedules"]!.AsArray()[0]!["matched_sessions"]!.AsArray()[0]!.AsObject(),
                    invalidField);
            }

            await Assert.ThrowsAsync<DesktopBridgeException>(() => dispatcher.DispatchAsync(
                "save_timetable",
                JsonSerializer.SerializeToElement(new { timetable }),
                CancellationToken.None));
        }

        static void SetInvalidSessionField(JsonObject session, string field)
        {
            session[field] = field switch
            {
                "session_type" => JsonValue.Create("INVALID"),
                "day" => JsonValue.Create(99),
                "period_code" => JsonValue.Create("5"),
                "period_atomic" => new JsonArray(JsonValue.Create("5")),
                _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
            };
        }
    }

    [Fact]
    public async Task SaveTimetableRejectsMismatchedAtomicPeriods()
    {
        var dispatcher = new DesktopCommandDispatcher(
            static () => new SatisfyingSolver(),
            chooseTimetablePath: _ => Path.Combine(temporaryDirectory, "invalid.json"));
        var response = await dispatcher.DispatchAsync(
            "solve_workbook",
            CreateSolvePayload(CreateWorkbook()),
            CancellationToken.None);
        var timetable = JsonNode.Parse(response.GetRawText())!.AsObject();
        foreach (var desiredAssignments in new[]
        {
            timetable["desired_assignments"]!.AsArray(),
            timetable["solutions"]!.AsArray()[0]!["desired_assignments"]!.AsArray(),
        })
        {
            var desiredAssignment = desiredAssignments[0]!.AsObject();
            foreach (var session in new[]
            {
                desiredAssignment["matched_sessions"]!.AsArray()[0]!.AsObject(),
                desiredAssignment["lhp_schedules"]!.AsArray()[0]!["matched_sessions"]!.AsArray()[0]!.AsObject(),
            })
            {
                session["period_code"] = "1-2";
                session["period_atomic"] = new JsonArray(JsonValue.Create("1"), JsonValue.Create("3"));
            }
        }

        await Assert.ThrowsAsync<DesktopBridgeException>(() => dispatcher.DispatchAsync(
            "save_timetable",
            JsonSerializer.SerializeToElement(new { timetable }),
            CancellationToken.None));
    }

    [Fact]
    public async Task SaveTimetableRejectsNegativeMovementCost()
    {
        var dispatcher = new DesktopCommandDispatcher(
            static () => new SatisfyingSolver(),
            chooseTimetablePath: _ => Path.Combine(temporaryDirectory, "invalid.json"));
        var response = await dispatcher.DispatchAsync(
            "solve_workbook",
            CreateSolvePayload(CreateWorkbook()),
            CancellationToken.None);
        var timetable = JsonNode.Parse(response.GetRawText())!.AsObject();
        timetable["solutions"]!.AsArray()[0]!["movement_cost"] = -1;

        await Assert.ThrowsAsync<DesktopBridgeException>(() => dispatcher.DispatchAsync(
            "save_timetable",
            JsonSerializer.SerializeToElement(new { timetable }),
            CancellationToken.None));
    }

    [Fact]
    public async Task SaveTimetableRejectsDecreasingMovementCostOrder()
    {
        var dispatcher = new DesktopCommandDispatcher(
            static () => new SatisfyingSolver(),
            chooseTimetablePath: _ => Path.Combine(temporaryDirectory, "invalid.json"));
        var response = await dispatcher.DispatchAsync(
            "solve_workbook",
            CreateSolvePayload(CreateWorkbook()),
            CancellationToken.None);
        var timetable = JsonNode.Parse(response.GetRawText())!.AsObject();

        foreach (var desiredAssignments in new[]
        {
            timetable["desired_assignments"]!.AsArray(),
            timetable["solutions"]!.AsArray()[0]!["desired_assignments"]!.AsArray(),
        })
        {
            var desiredAssignment = desiredAssignments[0]!.AsObject();
            desiredAssignment["session_count"] = 2;

            var flatSession = JsonNode.Parse(
                desiredAssignment["matched_sessions"]!.AsArray()[0]!.ToJsonString())!.AsObject();
            flatSession["session_id"] = "row_2";
            flatSession["source_row"] = 2;
            flatSession["timeslot_label"] = "Thứ 2 Ca 2";
            flatSession["period_code"] = "2";
            flatSession["period_atomic"] = new JsonArray(JsonValue.Create("2"));
            flatSession["room_code"] = "999-Z";
            desiredAssignment["matched_sessions"]!.AsArray().Add(flatSession);

            var schedule = desiredAssignment["lhp_schedules"]!.AsArray()[0]!.AsObject();
            schedule["session_count"] = 2;
            var nestedSession = JsonNode.Parse(
                schedule["matched_sessions"]!.AsArray()[0]!.ToJsonString())!.AsObject();
            nestedSession["session_id"] = "row_2";
            nestedSession["source_row"] = 2;
            nestedSession["timeslot_label"] = "Thứ 2 Ca 2";
            nestedSession["period_code"] = "2";
            nestedSession["period_atomic"] = new JsonArray(JsonValue.Create("2"));
            nestedSession["room_code"] = "999-Z";
            schedule["matched_sessions"]!.AsArray().Add(nestedSession);
        }

        var solutions = timetable["solutions"]!.AsArray();
        solutions[0]!["movement_cost"] = 3;
        var secondSolution = JsonNode.Parse(solutions[0]!.ToJsonString())!.AsObject();
        secondSolution["solution_index"] = 2;
        secondSolution["movement_cost"] = 0;
        var secondAssignment = secondSolution["desired_assignments"]!.AsArray()[0]!.AsObject();
        secondAssignment["matched_sessions"]!.AsArray()[1]!["room_code"] = "105-B";
        secondAssignment["lhp_schedules"]!.AsArray()[0]!["matched_sessions"]!
            .AsArray()[1]!["room_code"] = "105-B";
        solutions.Add(secondSolution);
        timetable["solver"]!.AsObject()["solution_count"] = 2;

        await Assert.ThrowsAsync<DesktopBridgeException>(() => dispatcher.DispatchAsync(
            "save_timetable",
            JsonSerializer.SerializeToElement(new { timetable }),
            CancellationToken.None));
    }

    [Fact]
    public async Task SaveTimetableRejectsNonJsonDestination()
    {
        var destination = Path.Combine(temporaryDirectory, "saved-timetable.txt");
        var dispatcher = new DesktopCommandDispatcher(
            static () => new SatisfyingSolver(),
            chooseTimetablePath: _ => destination);
        var solveResult = await dispatcher.DispatchAsync(
            "solve_workbook",
            CreateSolvePayload(CreateWorkbook()),
            CancellationToken.None);

        await Assert.ThrowsAsync<DesktopBridgeException>(() => dispatcher.DispatchAsync(
            "save_timetable",
            JsonSerializer.SerializeToElement(new { timetable = solveResult }),
            CancellationToken.None));

        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task SaveTimetableRejectsMalformedDestinationFileName()
    {
        var destination = Path.Combine(temporaryDirectory, "saved-\uFFFD.json");
        var dispatcher = new DesktopCommandDispatcher(
            static () => new SatisfyingSolver(),
            chooseTimetablePath: _ => destination);
        var solveResult = await dispatcher.DispatchAsync(
            "solve_workbook",
            CreateSolvePayload(CreateWorkbook()),
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<DesktopBridgeException>(() => dispatcher.DispatchAsync(
            "save_timetable",
            JsonSerializer.SerializeToElement(new { timetable = solveResult }),
            CancellationToken.None));

        Assert.Contains("Tên tệp", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task SaveTimetableUsesInvariantGregorianFilenameDigits()
    {
        var destination = Path.Combine(temporaryDirectory, "culture.json");
        string? suggestedFileName = null;
        var dispatcher = new DesktopCommandDispatcher(
            static () => new SatisfyingSolver(),
            chooseTimetablePath: suggested =>
            {
                suggestedFileName = suggested;
                return destination;
            });
        var solveResult = await dispatcher.DispatchAsync(
            "solve_workbook",
            CreateSolvePayload(CreateWorkbook()),
            CancellationToken.None);
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("th-TH");
            await dispatcher.DispatchAsync(
                "save_timetable",
                JsonSerializer.SerializeToElement(new { timetable = solveResult }),
                CancellationToken.None);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }

        Assert.NotNull(suggestedFileName);
        Assert.Matches("^TKB-.+-[0-9]{8}-[0-9]{4}\\.json$", suggestedFileName);
    }

    [Fact]
    public async Task SaveTimetableBoundsDepartmentInSuggestedFileName()
    {
        var destination = Path.Combine(temporaryDirectory, "long-department.json");
        string? suggestedFileName = null;
        var dispatcher = new DesktopCommandDispatcher(
            static () => new SatisfyingSolver(),
            chooseTimetablePath: suggested =>
            {
                suggestedFileName = suggested;
                return destination;
            });
        var response = await dispatcher.DispatchAsync(
            "solve_workbook",
            CreateSolvePayload(CreateWorkbook()),
            CancellationToken.None);
        var timetable = JsonNode.Parse(response.GetRawText())!.AsObject();
        timetable["department"] = new string('D', 1_000);

        await dispatcher.DispatchAsync(
            "save_timetable",
            JsonSerializer.SerializeToElement(new { timetable }),
            CancellationToken.None);

        Assert.NotNull(suggestedFileName);
        Assert.True(suggestedFileName!.Length <= 255);
        Assert.StartsWith($"TKB-{new string('D', 200)}-", suggestedFileName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveTimetableTruncatesDepartmentWithoutSplittingSurrogatePair()
    {
        var destination = Path.Combine(temporaryDirectory, "surrogate-department.json");
        string? suggestedFileName = null;
        var dispatcher = new DesktopCommandDispatcher(
            static () => new SatisfyingSolver(),
            chooseTimetablePath: suggested =>
            {
                suggestedFileName = suggested;
                return destination;
            });
        var response = await dispatcher.DispatchAsync(
            "solve_workbook",
            CreateSolvePayload(CreateWorkbook()),
            CancellationToken.None);
        var timetable = JsonNode.Parse(response.GetRawText())!.AsObject();
        timetable["department"] = new string('D', 199) + "\U0001F600";

        await dispatcher.DispatchAsync(
            "save_timetable",
            JsonSerializer.SerializeToElement(new { timetable }),
            CancellationToken.None);

        Assert.NotNull(suggestedFileName);
        Assert.StartsWith($"TKB-{new string('D', 199)}-", suggestedFileName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveTimetableRejectsMalformedSolutionEntries()
    {
        var dispatcher = new DesktopCommandDispatcher(
            static () => new SatisfyingSolver(),
            chooseTimetablePath: _ => Path.Combine(temporaryDirectory, "invalid.json"));
        var invalidTimetable = new
        {
            mode = "reschedule",
            workbook_path = "fixture.xlsx",
            department = "CNTT",
            solver = new { satisfiability = "SAT", solution_count = 1 },
            solutions = new[] { new { } },
        };

        await Assert.ThrowsAsync<DesktopBridgeException>(() => dispatcher.DispatchAsync(
            "save_timetable",
            JsonSerializer.SerializeToElement(new { timetable = invalidTimetable }),
            CancellationToken.None));
    }

    [Fact]
    public async Task SaveTimetableRejectsMoreThanFiveSolutions()
    {
        var dispatcher = new DesktopCommandDispatcher(
            static () => new SatisfyingSolver(),
            chooseTimetablePath: _ => Path.Combine(temporaryDirectory, "invalid.json"));
        var invalidTimetable = new
        {
            mode = "reschedule",
            department = "CNTT",
            solver = new { satisfiability = "SAT", solution_count = 6 },
            solutions = Enumerable.Range(1, 6).Select(_ => new object()).ToArray(),
        };

        await Assert.ThrowsAsync<DesktopBridgeException>(() => dispatcher.DispatchAsync(
            "save_timetable",
            JsonSerializer.SerializeToElement(new { timetable = invalidTimetable }),
            CancellationToken.None));
    }

    [Fact]
    public async Task SaveTimetableRejectsMissingRenderableResponseArrays()
    {
        var dispatcher = new DesktopCommandDispatcher(
            static () => new SatisfyingSolver(),
            chooseTimetablePath: _ => Path.Combine(temporaryDirectory, "invalid.json"));
        var response = await dispatcher.DispatchAsync(
            "solve_workbook",
            CreateSolvePayload(CreateWorkbook()),
            CancellationToken.None);
        var timetable = JsonNode.Parse(response.GetRawText())!.AsObject();
        timetable.Remove("desired_assignments");

        await Assert.ThrowsAsync<DesktopBridgeException>(() => dispatcher.DispatchAsync(
            "save_timetable",
            JsonSerializer.SerializeToElement(new { timetable }),
            CancellationToken.None));
    }

    [Fact]
    public async Task SaveTimetableRejectsMissingNullableResponseProperty()
    {
        var dispatcher = new DesktopCommandDispatcher(
            static () => new SatisfyingSolver(),
            chooseTimetablePath: _ => Path.Combine(temporaryDirectory, "invalid.json"));
        var response = await dispatcher.DispatchAsync(
            "solve_workbook",
            CreateSolvePayload(CreateWorkbook()),
            CancellationToken.None);
        var timetable = JsonNode.Parse(response.GetRawText())!.AsObject();
        timetable["solver"]!.AsObject().Remove("objective_value");

        await Assert.ThrowsAsync<DesktopBridgeException>(() => dispatcher.DispatchAsync(
            "save_timetable",
            JsonSerializer.SerializeToElement(new { timetable }),
            CancellationToken.None));
    }

    [Fact]
    public async Task SaveTimetableRejectsEmptyRenderableAssignments()
    {
        var dispatcher = new DesktopCommandDispatcher(
            static () => new SatisfyingSolver(),
            chooseTimetablePath: _ => Path.Combine(temporaryDirectory, "invalid.json"));
        var response = await dispatcher.DispatchAsync(
            "solve_workbook",
            CreateSolvePayload(CreateWorkbook()),
            CancellationToken.None);
        var timetable = JsonNode.Parse(response.GetRawText())!.AsObject();
        timetable["desired_assignments"] = new JsonArray();
        timetable["solutions"]!.AsArray()[0]!["desired_assignments"] = new JsonArray();

        await Assert.ThrowsAsync<DesktopBridgeException>(() => dispatcher.DispatchAsync(
            "save_timetable",
            JsonSerializer.SerializeToElement(new { timetable }),
            CancellationToken.None));
    }

    [Fact]
    public async Task SaveTimetableRejectsDesiredAssignmentsWithoutSessions()
    {
        var dispatcher = new DesktopCommandDispatcher(
            static () => new SatisfyingSolver(),
            chooseTimetablePath: _ => Path.Combine(temporaryDirectory, "invalid.json"));
        var response = await dispatcher.DispatchAsync(
            "solve_workbook",
            CreateSolvePayload(CreateWorkbook()),
            CancellationToken.None);
        var timetable = JsonNode.Parse(response.GetRawText())!.AsObject();
        foreach (var desiredAssignments in new[]
        {
            timetable["desired_assignments"]!.AsArray(),
            timetable["solutions"]!.AsArray()[0]!["desired_assignments"]!.AsArray(),
        })
        {
            desiredAssignments[0]!["matched_sessions"] = new JsonArray();
            desiredAssignments[0]!["lhp_schedules"]!.AsArray()[0]!["matched_sessions"] = new JsonArray();
        }

        await Assert.ThrowsAsync<DesktopBridgeException>(() => dispatcher.DispatchAsync(
            "save_timetable",
            JsonSerializer.SerializeToElement(new { timetable }),
            CancellationToken.None));
    }

    [Fact]
    public async Task SaveTimetableRejectsMismatchedLhpScheduleSessionCount()
    {
        var dispatcher = new DesktopCommandDispatcher(
            static () => new SatisfyingSolver(),
            chooseTimetablePath: _ => Path.Combine(temporaryDirectory, "invalid.json"));
        var response = await dispatcher.DispatchAsync(
            "solve_workbook",
            CreateSolvePayload(CreateWorkbook()),
            CancellationToken.None);
        var timetable = JsonNode.Parse(response.GetRawText())!.AsObject();
        foreach (var desiredAssignments in new[]
        {
            timetable["desired_assignments"]!.AsArray(),
            timetable["solutions"]!.AsArray()[0]!["desired_assignments"]!.AsArray(),
        })
        {
            desiredAssignments[0]!["lhp_schedules"]!.AsArray()[0]!["session_count"] = 2;
        }

        await Assert.ThrowsAsync<DesktopBridgeException>(() => dispatcher.DispatchAsync(
            "save_timetable",
            JsonSerializer.SerializeToElement(new { timetable }),
            CancellationToken.None));
    }

    [Fact]
    public async Task SaveTimetableRejectsDesiredAssignmentsWhoseSessionCountDisagrees()
    {
        var dispatcher = new DesktopCommandDispatcher(
            static () => new SatisfyingSolver(),
            chooseTimetablePath: _ => Path.Combine(temporaryDirectory, "invalid.json"));
        var response = await dispatcher.DispatchAsync(
            "solve_workbook",
            CreateSolvePayload(CreateWorkbook()),
            CancellationToken.None);
        var timetable = JsonNode.Parse(response.GetRawText())!.AsObject();
        foreach (var desiredAssignments in new[]
        {
            timetable["desired_assignments"]!.AsArray(),
            timetable["solutions"]!.AsArray()[0]!["desired_assignments"]!.AsArray(),
        })
        {
            desiredAssignments[0]!["session_count"] = 2;
        }

        await Assert.ThrowsAsync<DesktopBridgeException>(() => dispatcher.DispatchAsync(
            "save_timetable",
            JsonSerializer.SerializeToElement(new { timetable }),
            CancellationToken.None));
    }

    [Fact]
    public async Task SaveTimetableRejectsFlatSessionsThatDisagreeWithNestedSessions()
    {
        var dispatcher = new DesktopCommandDispatcher(
            static () => new SatisfyingSolver(),
            chooseTimetablePath: _ => Path.Combine(temporaryDirectory, "invalid.json"));
        var response = await dispatcher.DispatchAsync(
            "solve_workbook",
            CreateSolvePayload(CreateWorkbook()),
            CancellationToken.None);
        var timetable = JsonNode.Parse(response.GetRawText())!.AsObject();
        foreach (var desiredAssignments in new[]
        {
            timetable["desired_assignments"]!.AsArray(),
            timetable["solutions"]!.AsArray()[0]!["desired_assignments"]!.AsArray(),
        })
        {
            desiredAssignments[0]!["matched_sessions"]!.AsArray()[0]!["room_code"] = "999-Z";
        }

        await Assert.ThrowsAsync<DesktopBridgeException>(() => dispatcher.DispatchAsync(
            "save_timetable",
            JsonSerializer.SerializeToElement(new { timetable }),
            CancellationToken.None));
    }

    [Fact]
    public async Task SaveTimetableRejectsSessionsWhoseLhpCodeDisagreesWithSchedule()
    {
        var dispatcher = new DesktopCommandDispatcher(
            static () => new SatisfyingSolver(),
            chooseTimetablePath: _ => Path.Combine(temporaryDirectory, "invalid.json"));
        var response = await dispatcher.DispatchAsync(
            "solve_workbook",
            CreateSolvePayload(CreateWorkbook()),
            CancellationToken.None);
        var timetable = JsonNode.Parse(response.GetRawText())!.AsObject();
        foreach (var desiredAssignments in new[]
        {
            timetable["desired_assignments"]!.AsArray(),
            timetable["solutions"]!.AsArray()[0]!["desired_assignments"]!.AsArray(),
        })
        {
            var desiredAssignment = desiredAssignments[0]!.AsObject();
            desiredAssignment["lhp_codes"] = new JsonArray(JsonValue.Create("INT2213-02"));
            desiredAssignment["lhp_schedules"]!.AsArray()[0]!["lhp_code"] = "INT2213-02";
        }

        await Assert.ThrowsAsync<DesktopBridgeException>(() => dispatcher.DispatchAsync(
            "save_timetable",
            JsonSerializer.SerializeToElement(new { timetable }),
            CancellationToken.None));
    }

    [Fact]
    public async Task SaveTimetableRejectsRootAssignmentsThatDifferFromFirstSolution()
    {
        var dispatcher = new DesktopCommandDispatcher(
            static () => new SatisfyingSolver(),
            chooseTimetablePath: _ => Path.Combine(temporaryDirectory, "invalid.json"));
        var response = await dispatcher.DispatchAsync(
            "solve_workbook",
            CreateSolvePayload(CreateWorkbook()),
            CancellationToken.None);
        var timetable = JsonNode.Parse(response.GetRawText())!.AsObject();
        timetable["desired_assignments"]!.AsArray()[0]!["course_name"] = "Khác";

        await Assert.ThrowsAsync<DesktopBridgeException>(() => dispatcher.DispatchAsync(
            "save_timetable",
            JsonSerializer.SerializeToElement(new { timetable }),
            CancellationToken.None));
    }

    [Fact]
    public async Task SaveTimetableRejectsLaterSolutionWithDifferentDesiredAssignments()
    {
        var dispatcher = new DesktopCommandDispatcher(
            static () => new SatisfyingSolver(),
            chooseTimetablePath: _ => Path.Combine(temporaryDirectory, "invalid.json"));
        var response = await dispatcher.DispatchAsync(
            "solve_workbook",
            CreateSolvePayload(CreateWorkbook()),
            CancellationToken.None);
        var timetable = JsonNode.Parse(response.GetRawText())!.AsObject();
        var solutions = timetable["solutions"]!.AsArray();
        var secondSolution = JsonNode.Parse(solutions[0]!.ToJsonString())!.AsObject();
        secondSolution["solution_index"] = 2;
        secondSolution["desired_assignments"]!.AsArray()[0]!["course_name"] = "Khác";
        solutions.Add(secondSolution);
        timetable["solver"]!.AsObject()["solution_count"] = 2;

        await Assert.ThrowsAsync<DesktopBridgeException>(() => dispatcher.DispatchAsync(
            "save_timetable",
            JsonSerializer.SerializeToElement(new { timetable }),
            CancellationToken.None));
    }

    [Fact]
    public async Task SaveTimetableAcceptsValidAlternateSolutionPlacement()
    {
        var destination = Path.Combine(temporaryDirectory, "alternate.json");
        var dispatcher = new DesktopCommandDispatcher(
            static () => new SatisfyingSolver(),
            chooseTimetablePath: _ => destination);
        var response = await dispatcher.DispatchAsync(
            "solve_workbook",
            CreateSolvePayload(CreateWorkbook()),
            CancellationToken.None);
        var timetable = JsonNode.Parse(response.GetRawText())!.AsObject();
        var solutions = timetable["solutions"]!.AsArray();
        var secondSolution = JsonNode.Parse(solutions[0]!.ToJsonString())!.AsObject();
        secondSolution["solution_index"] = 2;
        var secondDesiredAssignment = secondSolution["desired_assignments"]!.AsArray()[0]!.AsObject();
        secondDesiredAssignment["matched_sessions"]!.AsArray()[0]!["room_code"] = "106-B";
        secondDesiredAssignment["lhp_schedules"]!.AsArray()[0]!["matched_sessions"]!.AsArray()[0]!["room_code"] = "106-B";
        solutions.Add(secondSolution);
        timetable["solver"]!.AsObject()["solution_count"] = 2;

        var result = await dispatcher.DispatchAsync(
            "save_timetable",
            JsonSerializer.SerializeToElement(new { timetable }),
            CancellationToken.None);

        Assert.True(result.GetProperty("saved").GetBoolean());
        Assert.True(File.Exists(destination));
    }

    [Fact]
    public async Task SaveTimetableRejectsPhysicalSessionsWithoutTiming()
    {
        var dispatcher = new DesktopCommandDispatcher(
            static () => new SatisfyingSolver(),
            chooseTimetablePath: _ => Path.Combine(temporaryDirectory, "invalid.json"));
        var response = await dispatcher.DispatchAsync(
            "solve_workbook",
            CreateSolvePayload(CreateWorkbook()),
            CancellationToken.None);
        var timetable = JsonNode.Parse(response.GetRawText())!.AsObject();
        foreach (var desiredAssignments in new[]
        {
            timetable["desired_assignments"]!.AsArray(),
            timetable["solutions"]!.AsArray()[0]!["desired_assignments"]!.AsArray(),
        })
        {
            var desiredAssignment = desiredAssignments[0]!.AsObject();
            foreach (var sessionNode in new JsonNode?[]
            {
                desiredAssignment["matched_sessions"]!.AsArray()[0],
                desiredAssignment["lhp_schedules"]!.AsArray()[0]!["matched_sessions"]!.AsArray()[0],
            })
            {
                sessionNode!["day"] = null;
                sessionNode["period_code"] = null;
                sessionNode["period_atomic"] = null;
            }
        }

        await Assert.ThrowsAsync<DesktopBridgeException>(() => dispatcher.DispatchAsync(
            "save_timetable",
            JsonSerializer.SerializeToElement(new { timetable }),
            CancellationToken.None));
    }

    [Fact]
    public async Task SaveTimetableRejectsBlankPhysicalRooms()
    {
        var dispatcher = new DesktopCommandDispatcher(
            static () => new SatisfyingSolver(),
            chooseTimetablePath: _ => Path.Combine(temporaryDirectory, "invalid.json"));
        var response = await dispatcher.DispatchAsync(
            "solve_workbook",
            CreateSolvePayload(CreateWorkbook()),
            CancellationToken.None);
        var timetable = JsonNode.Parse(response.GetRawText())!.AsObject();
        foreach (var desiredAssignments in new[]
        {
            timetable["desired_assignments"]!.AsArray(),
            timetable["solutions"]!.AsArray()[0]!["desired_assignments"]!.AsArray(),
        })
        {
            var desiredAssignment = desiredAssignments[0]!.AsObject();
            desiredAssignment["matched_sessions"]!.AsArray()[0]!["room_code"] = " ";
            desiredAssignment["lhp_schedules"]!.AsArray()[0]!["matched_sessions"]!.AsArray()[0]!["room_code"] = " ";
        }

        await Assert.ThrowsAsync<DesktopBridgeException>(() => dispatcher.DispatchAsync(
            "save_timetable",
            JsonSerializer.SerializeToElement(new { timetable }),
            CancellationToken.None));
    }

    [Fact]
    public async Task SaveTimetableAcceptsOnlineSessionsWithoutTiming()
    {
        var destination = Path.Combine(temporaryDirectory, "online.json");
        var dispatcher = new DesktopCommandDispatcher(
            static () => new SatisfyingSolver(),
            chooseTimetablePath: _ => destination);
        var response = await dispatcher.DispatchAsync(
            "solve_workbook",
            CreateSolvePayload(CreateWorkbook()),
            CancellationToken.None);
        var timetable = JsonNode.Parse(response.GetRawText())!.AsObject();
        foreach (var desiredAssignments in new[]
        {
            timetable["desired_assignments"]!.AsArray(),
            timetable["solutions"]!.AsArray()[0]!["desired_assignments"]!.AsArray(),
        })
        {
            var desiredAssignment = desiredAssignments[0]!.AsObject();
            foreach (var sessionNode in new JsonNode?[]
            {
                desiredAssignment["matched_sessions"]!.AsArray()[0],
                desiredAssignment["lhp_schedules"]!.AsArray()[0]!["matched_sessions"]!.AsArray()[0],
            })
            {
                sessionNode!["session_type"] = "ONL";
                sessionNode["room_code"] = "ONL";
                sessionNode["day"] = null;
                sessionNode["period_code"] = null;
                sessionNode["period_atomic"] = null;
            }
        }

        var result = await dispatcher.DispatchAsync(
            "save_timetable",
            JsonSerializer.SerializeToElement(new { timetable }),
            CancellationToken.None);

        Assert.True(result.GetProperty("saved").GetBoolean());
    }

    [Fact]
    public async Task SaveTimetableRejectsDuplicateAnchorIdentities()
    {
        var dispatcher = new DesktopCommandDispatcher(
            static () => new SatisfyingSolver(),
            chooseTimetablePath: _ => Path.Combine(temporaryDirectory, "invalid.json"));
        var response = await dispatcher.DispatchAsync(
            "solve_workbook",
            CreateSolvePayload(CreateWorkbook()),
            CancellationToken.None);
        var timetable = JsonNode.Parse(response.GetRawText())!.AsObject();
        var duplicate = JsonNode.Parse(timetable["desired_assignments"]!.AsArray()[0]!.ToJsonString())!.AsObject();
        duplicate["matched_sessions"]!.AsArray()[0]!["session_id"] = "row_2";
        duplicate["lhp_schedules"]!.AsArray()[0]!["matched_sessions"]!.AsArray()[0]!["session_id"] = "row_2";
        timetable["desired_assignments"]!.AsArray().Add(duplicate);
        timetable["solutions"]!.AsArray()[0]!["desired_assignments"]!.AsArray().Add(
            JsonNode.Parse(duplicate.ToJsonString()));

        await Assert.ThrowsAsync<DesktopBridgeException>(() => dispatcher.DispatchAsync(
            "save_timetable",
            JsonSerializer.SerializeToElement(new { timetable }),
            CancellationToken.None));
    }

    [Fact]
    public async Task SaveTimetableRejectsDuplicateSolutionPlacements()
    {
        var dispatcher = new DesktopCommandDispatcher(
            static () => new SatisfyingSolver(),
            chooseTimetablePath: _ => Path.Combine(temporaryDirectory, "invalid.json"));
        var response = await dispatcher.DispatchAsync(
            "solve_workbook",
            CreateSolvePayload(CreateWorkbook()),
            CancellationToken.None);
        var timetable = JsonNode.Parse(response.GetRawText())!.AsObject();
        var solutions = timetable["solutions"]!.AsArray();
        var duplicate = JsonNode.Parse(solutions[0]!.ToJsonString())!.AsObject();
        duplicate["solution_index"] = 2;
        duplicate["desired_assignments"]!.AsArray()[0]!["matched_sessions"]!.AsArray()[0]!["source_row"] = 99;
        duplicate["desired_assignments"]!.AsArray()[0]!["matched_sessions"]!.AsArray()[0]!["timeslot_label"] = "Metadata only";
        duplicate["desired_assignments"]!.AsArray()[0]!["lhp_schedules"]!.AsArray()[0]!["matched_sessions"]!.AsArray()[0]!["source_row"] = 99;
        duplicate["desired_assignments"]!.AsArray()[0]!["lhp_schedules"]!.AsArray()[0]!["matched_sessions"]!.AsArray()[0]!["timeslot_label"] = "Metadata only";
        solutions.Add(duplicate);
        timetable["solver"]!.AsObject()["solution_count"] = 2;

        await Assert.ThrowsAsync<DesktopBridgeException>(() => dispatcher.DispatchAsync(
            "save_timetable",
            JsonSerializer.SerializeToElement(new { timetable }),
            CancellationToken.None));
    }

    [Fact]
    public async Task SaveTimetableRejectsTamperedMovementCost()
    {
        var dispatcher = new DesktopCommandDispatcher(
            static () => new SatisfyingSolver(),
            chooseTimetablePath: _ => Path.Combine(temporaryDirectory, "invalid.json"));
        var response = await dispatcher.DispatchAsync(
            "solve_workbook",
            CreateSolvePayload(CreateWorkbook()),
            CancellationToken.None);
        var timetable = JsonNode.Parse(response.GetRawText())!.AsObject();
        timetable["solutions"]!.AsArray()[0]!["movement_cost"] = 1;

        await Assert.ThrowsAsync<DesktopBridgeException>(() => dispatcher.DispatchAsync(
            "save_timetable",
            JsonSerializer.SerializeToElement(new { timetable }),
            CancellationToken.None));
    }

    [Fact]
    public async Task SaveTimetableRejectsBlankSessionIds()
    {
        var dispatcher = new DesktopCommandDispatcher(
            static () => new SatisfyingSolver(),
            chooseTimetablePath: _ => Path.Combine(temporaryDirectory, "invalid.json"));
        var response = await dispatcher.DispatchAsync(
            "solve_workbook",
            CreateSolvePayload(CreateWorkbook()),
            CancellationToken.None);
        var timetable = JsonNode.Parse(response.GetRawText())!.AsObject();
        foreach (var desiredAssignments in new[]
        {
            timetable["desired_assignments"]!.AsArray(),
            timetable["solutions"]!.AsArray()[0]!["desired_assignments"]!.AsArray(),
        })
        {
            desiredAssignments[0]!["matched_sessions"]!.AsArray()[0]!["session_id"] = " ";
            desiredAssignments[0]!["lhp_schedules"]!.AsArray()[0]!["matched_sessions"]!.AsArray()[0]!["session_id"] =
                " ";
        }

        await Assert.ThrowsAsync<DesktopBridgeException>(() => dispatcher.DispatchAsync(
            "save_timetable",
            JsonSerializer.SerializeToElement(new { timetable }),
            CancellationToken.None));
    }

    [Fact]
    public async Task SaveTimetableRejectsMalformedUtf16SessionIds()
    {
        var dispatcher = new DesktopCommandDispatcher(
            static () => new SatisfyingSolver(),
            chooseTimetablePath: _ => Path.Combine(temporaryDirectory, "invalid.json"));
        var response = await dispatcher.DispatchAsync(
            "solve_workbook",
            CreateSolvePayload(CreateWorkbook()),
            CancellationToken.None);
        using var responseDocument = JsonDocument.Parse(response.GetRawText());
        var originalSessionId = responseDocument.RootElement
            .GetProperty("desired_assignments")[0]
            .GetProperty("matched_sessions")[0]
            .GetProperty("session_id")
            .GetString();
        var serializedOriginalSessionId = JsonSerializer.Serialize(originalSessionId);
        foreach (var malformedToken in new[] { "\"\\uD800\"", "\"\\uDC00\"", "\"\\uFFFD\"" })
        {
            var invalidTimetableJson = response.GetRawText().Replace(
                serializedOriginalSessionId,
                malformedToken,
                StringComparison.Ordinal);
            using var invalidPayload = JsonDocument.Parse(
                "{\"timetable\":" + invalidTimetableJson + ",\"source_file_name\":\"source.xlsx\"}");

            await Assert.ThrowsAsync<DesktopBridgeException>(() => dispatcher.DispatchAsync(
                "save_timetable",
                invalidPayload.RootElement,
                CancellationToken.None));
        }
    }

    [Fact]
    public async Task SaveTimetableRejectsDuplicateRenderedSessionIds()
    {
        var dispatcher = new DesktopCommandDispatcher(
            static () => new SatisfyingSolver(),
            chooseTimetablePath: _ => Path.Combine(temporaryDirectory, "invalid.json"));
        var response = await dispatcher.DispatchAsync(
            "solve_workbook",
            CreateSolvePayload(CreateWorkbook()),
            CancellationToken.None);
        var timetable = JsonNode.Parse(response.GetRawText())!.AsObject();
        foreach (var desiredAssignments in new[]
        {
            timetable["desired_assignments"]!.AsArray(),
            timetable["solutions"]!.AsArray()[0]!["desired_assignments"]!.AsArray(),
        })
        {
            var desiredAssignment = desiredAssignments[0]!.AsObject();
            desiredAssignment["session_count"] = 2;
            var matchedSessions = desiredAssignment["matched_sessions"]!.AsArray();
            matchedSessions.Add(JsonNode.Parse(matchedSessions[0]!.ToJsonString()));
            var lhpSchedule = desiredAssignment["lhp_schedules"]!.AsArray()[0]!.AsObject();
            lhpSchedule["session_count"] = 2;
            lhpSchedule["matched_sessions"]!.AsArray().Add(
                JsonNode.Parse(lhpSchedule["matched_sessions"]!.AsArray()[0]!.ToJsonString()));
        }

        await Assert.ThrowsAsync<DesktopBridgeException>(() => dispatcher.DispatchAsync(
            "save_timetable",
            JsonSerializer.SerializeToElement(new { timetable }),
            CancellationToken.None));
    }

    [Theory]
    [InlineData("UNSAT", 0)]
    [InlineData("UNKNOWN", 0)]
    public async Task SaveTimetableRejectsNonSatResults(string satisfiability, int solutionCount)
    {
        var dispatcher = new DesktopCommandDispatcher(
            static () => new SatisfyingSolver(),
            chooseTimetablePath: _ => Path.Combine(temporaryDirectory, "invalid.json"));
        var invalidTimetable = new
        {
            mode = "reschedule",
            department = "CNTT",
            solver = new { satisfiability, solution_count = solutionCount },
            solutions = Array.Empty<object>(),
        };

        var exception = await Assert.ThrowsAsync<DesktopBridgeException>(() => dispatcher.DispatchAsync(
            "save_timetable",
            JsonSerializer.SerializeToElement(new { timetable = invalidTimetable }),
            CancellationToken.None));

        Assert.Contains("lưu", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveTimetableRejectsMissingTimetablePayload()
    {
        var dispatcher = new DesktopCommandDispatcher(
            static () => new SatisfyingSolver(),
            chooseTimetablePath: _ => Path.Combine(temporaryDirectory, "invalid.json"));

        await Assert.ThrowsAsync<DesktopBridgeException>(() => dispatcher.DispatchAsync(
            "save_timetable",
            JsonSerializer.SerializeToElement(new { source_file_name = "source.xlsx" }),
            CancellationToken.None));
    }

    [Theory]
    [InlineData("department")]
    [InlineData("workbook_path")]
    [InlineData("course_code")]
    public async Task SaveTimetableRejectsMalformedResponseIdentifiers(string identifier)
    {
        var dispatcher = new DesktopCommandDispatcher(
            static () => new SatisfyingSolver(),
            chooseTimetablePath: _ => Path.Combine(temporaryDirectory, "invalid.json"));
        var response = await dispatcher.DispatchAsync(
            "solve_workbook",
            CreateSolvePayload(CreateWorkbook()),
            CancellationToken.None);
        var timetable = JsonNode.Parse(response.GetRawText())!.AsObject();
        if (identifier is "department" or "workbook_path")
        {
            timetable[identifier] = "\uFFFD";
        }
        else
        {
            timetable["desired_assignments"]!.AsArray()[0]!["course_code"] = "\uFFFD";
            timetable["solutions"]!.AsArray()[0]!["desired_assignments"]!.AsArray()[0]!["course_code"] = "\uFFFD";
        }

        await Assert.ThrowsAsync<DesktopBridgeException>(() => dispatcher.DispatchAsync(
            "save_timetable",
            JsonSerializer.SerializeToElement(new { timetable }),
            CancellationToken.None));
    }

    [Theory]
    [InlineData("parse_warning")]
    [InlineData("prototype_anchor")]
    [InlineData("validation_violation")]
    [InlineData("solver_info")]
    [InlineData("solver_explanation")]
    public async Task SaveTimetableRejectsMalformedMetadataStrings(string field)
    {
        var dispatcher = new DesktopCommandDispatcher(
            static () => new SatisfyingSolver(),
            chooseTimetablePath: _ => Path.Combine(temporaryDirectory, "invalid-metadata.json"));
        var response = await dispatcher.DispatchAsync(
            "solve_workbook",
            CreateSolvePayload(CreateWorkbook()),
            CancellationToken.None);
        var timetable = JsonNode.Parse(response.GetRawText())!.AsObject();

        switch (field)
        {
            case "parse_warning":
                timetable["parse_summary"]!.AsObject()["warnings"] = new JsonArray(JsonValue.Create("\uFFFD"));
                break;
            case "prototype_anchor":
                timetable["prototype_catalog"]!.AsObject()["anchors"]!.AsArray()[0]!.AsObject()["course_name"] = "\uFFFD";
                break;
            case "validation_violation":
                timetable["existing_schedule_validation"]!.AsObject()["sample_violations"] =
                    new JsonArray(JsonValue.Create("\uFFFD"));
                break;
            case "solver_info":
                timetable["solver"]!.AsObject()["solver_info"] = "\uFFFD";
                break;
            case "solver_explanation":
                timetable["solver"]!.AsObject()["explanation"] = new JsonArray(JsonValue.Create("\uFFFD"));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(field), field, null);
        }

        await Assert.ThrowsAsync<DesktopBridgeException>(() => dispatcher.DispatchAsync(
            "save_timetable",
            JsonSerializer.SerializeToElement(new { timetable }),
            CancellationToken.None));
    }

    [Fact]
    public async Task SolveWorkbookRejectsFatalScheduleGapBeforeStartingSolver()
    {
        var workbookPath = CreateWorkbook(includeRoom: false, includeValidSession: true);
        var solvePayload = JsonSerializer.SerializeToElement(new
        {
            workbook = new
            {
                file_name = "fixture.xlsx",
                bytes_base64 = Convert.ToBase64String(await File.ReadAllBytesAsync(workbookPath)),
            },
            desired_assignments = new[]
            {
                new
                {
                    course_code = "INT1000",
                    course_name = "Programming",
                    teaching_team_label = "Alice",
                },
            },
            timeout_seconds = 30,
        });
        var solverCalled = false;
        var dispatcher = new DesktopCommandDispatcher(() =>
        {
            solverCalled = true;
            return new SatisfyingSolver();
        });

        var exception = await Assert.ThrowsAsync<DesktopBridgeException>(() => dispatcher.DispatchAsync(
            "solve_workbook",
            solvePayload,
            CancellationToken.None));

        Assert.False(solverCalled);
        Assert.Contains("thiếu hoặc sai thông tin", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportUnsatArtifactWritesACompleteCnfPackageWithoutChangingSolverFlow()
    {
        var workbookPath = CreateWorkbook();
        var destination = Path.Combine(temporaryDirectory, "unsat-verification.zip");
        var solvePayload = JsonSerializer.SerializeToElement(new
        {
            workbook = new
            {
                file_name = "fixture.xlsx",
                bytes_base64 = Convert.ToBase64String(await File.ReadAllBytesAsync(workbookPath)),
            },
            desired_assignments = new[]
            {
                new { course_code = "INT1000", course_name = "Programming", teaching_team_label = "Alice" },
            },
            timeout_seconds = 30,
        });

        var dispatcher = new DesktopCommandDispatcher(
            static () => new InfeasibleSolver(),
            () => destination);
        var result = await dispatcher.DispatchAsync(
            "solve_workbook",
            solvePayload,
            CancellationToken.None);

        var token = result.GetProperty("solver").GetProperty("formal_verification_token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));

        var exportPayload = JsonSerializer.SerializeToElement(new
        {
            workbook = new
            {
                file_name = "fixture.xlsx",
                bytes_base64 = Convert.ToBase64String(await File.ReadAllBytesAsync(workbookPath)),
            },
            desired_assignments = new[]
            {
                new { course_code = "INT1000", course_name = "Programming", teaching_team_label = "Alice" },
            },
            verification_token = token,
        });

        result = await dispatcher.DispatchAsync(
            "export_unsat_artifact",
            exportPayload,
            CancellationToken.None);

        Assert.True(result.GetProperty("exported").GetBoolean());
        Assert.Equal("unsat-verification.zip", result.GetProperty("file_name").GetString());
        Assert.Equal(1, result.GetProperty("variable_count").GetInt32());
        Assert.Equal(1, result.GetProperty("clause_count").GetInt32());
        Assert.True(File.Exists(destination));

        using var archive = ZipFile.OpenRead(destination);
        Assert.Contains(archive.Entries, entry => entry.FullName == "formula.cnf");
        Assert.Contains(archive.Entries, entry => entry.FullName == "manifest.json");
        Assert.Contains(archive.Entries, entry => entry.FullName == "verify-unsat.ps1");
        var manifest = archive.GetEntry("manifest.json");
        Assert.NotNull(manifest);
        using var manifestReader = new StreamReader(manifest!.Open());
        using var document = JsonDocument.Parse(manifestReader.ReadToEnd());
        Assert.Equal("INT1000", document.RootElement.GetProperty("selection").GetProperty("desired_assignments")[0]
            .GetProperty("course_code").GetString());
    }

    [Fact]
    public async Task ExportUnsatArtifactReturnsCancellationWithoutCreatingAFile()
    {
        var workbookPath = CreateWorkbook();
        var workbook = new
        {
            file_name = "fixture.xlsx",
            bytes_base64 = Convert.ToBase64String(await File.ReadAllBytesAsync(workbookPath)),
        };
        var desiredAssignments = new[]
        {
            new { course_code = "INT1000", course_name = "Programming", teaching_team_label = "Alice" },
        };
        var dispatcher = new DesktopCommandDispatcher(
            static () => new InfeasibleSolver(),
            () => null);
        var solveResult = await dispatcher.DispatchAsync(
            "solve_workbook",
            JsonSerializer.SerializeToElement(new
            {
                workbook,
                desired_assignments = desiredAssignments,
                timeout_seconds = 30,
            }),
            CancellationToken.None);

        var result = await dispatcher.DispatchAsync(
            "export_unsat_artifact",
            JsonSerializer.SerializeToElement(new
            {
                workbook,
                desired_assignments = desiredAssignments,
                verification_token = solveResult.GetProperty("solver").GetProperty("formal_verification_token").GetString(),
            }),
            CancellationToken.None);

        Assert.False(result.GetProperty("exported").GetBoolean());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("file_name").ValueKind);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("cnf_sha256").ValueKind);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("variable_count").ValueKind);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("clause_count").ValueKind);
        Assert.Empty(Directory.EnumerateFiles(temporaryDirectory, "*.zip"));
    }

    [Fact]
    public async Task ExportUnsatArtifactRejectsMalformedDestinationFileName()
    {
        var workbookPath = CreateWorkbook();
        var destination = Path.Combine(temporaryDirectory, "unsat-\uFFFD.zip");
        var workbook = new
        {
            file_name = "fixture.xlsx",
            bytes_base64 = Convert.ToBase64String(await File.ReadAllBytesAsync(workbookPath)),
        };
        var desiredAssignments = new[]
        {
            new { course_code = "INT1000", course_name = "Programming", teaching_team_label = "Alice" },
        };
        var dispatcher = new DesktopCommandDispatcher(
            static () => new InfeasibleSolver(),
            () => destination);
        var solveResult = await dispatcher.DispatchAsync(
            "solve_workbook",
            JsonSerializer.SerializeToElement(new
            {
                workbook,
                desired_assignments = desiredAssignments,
                timeout_seconds = 30,
            }),
            CancellationToken.None);
        var token = solveResult.GetProperty("solver").GetProperty("formal_verification_token").GetString();

        var exception = await Assert.ThrowsAsync<DesktopBridgeException>(() => dispatcher.DispatchAsync(
            "export_unsat_artifact",
            JsonSerializer.SerializeToElement(new
            {
                workbook,
                desired_assignments = desiredAssignments,
                verification_token = token,
            }),
            CancellationToken.None));

        Assert.Contains("Tên tệp", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task PartialImportKeepsCompleteOfferingsButDoesNotIssueFormalUnsatToken()
    {
        var workbookPath = CreateWorkbook(includeUnresolvedSession: true);
        var payload = JsonSerializer.SerializeToElement(new
        {
            workbook = new
            {
                file_name = "fixture.xlsx",
                bytes_base64 = Convert.ToBase64String(await File.ReadAllBytesAsync(workbookPath)),
            },
            desired_assignments = new[]
            {
                new { course_code = "INT1000", course_name = "Programming", teaching_team_label = "Alice" },
            },
            timeout_seconds = 30,
        });
        var destination = Path.Combine(temporaryDirectory, "partial-unsat.zip");
        var dispatcher = new DesktopCommandDispatcher(
            static () => new InfeasibleSolver(),
            () => destination);

        var result = await dispatcher.DispatchAsync("solve_workbook", payload, CancellationToken.None);

        var parseSummary = result.GetProperty("parse_summary");
        Assert.True(parseSummary.GetProperty("partial_import").GetBoolean());
        Assert.Equal(1, parseSummary.GetProperty("quarantined_lhp_count").GetInt32());
        Assert.Equal("INT1000", parseSummary.GetProperty("quarantined_offerings")[0]
            .GetProperty("course_code").GetString());
        Assert.Equal("LHP-2", parseSummary.GetProperty("quarantined_offerings")[0]
            .GetProperty("lhp_code").GetString());
        Assert.Null(result.GetProperty("solver").GetProperty("formal_verification_token").GetString());

        var exportPayload = JsonSerializer.SerializeToElement(new
        {
            workbook = new
            {
                file_name = "fixture.xlsx",
                bytes_base64 = Convert.ToBase64String(await File.ReadAllBytesAsync(workbookPath)),
            },
            desired_assignments = new[]
            {
                new { course_code = "INT1000", course_name = "Programming", teaching_team_label = "Alice" },
            },
            verification_token = "stale-token",
        });

        var exception = await Assert.ThrowsAsync<DesktopBridgeException>(() => dispatcher.DispatchAsync(
            "export_unsat_artifact",
            exportPayload,
            CancellationToken.None));

        Assert.Contains("chưa công bố lịch", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SolveWorkbookForwardsCancellationToTheSolver()
    {
        var workbookPath = CreateWorkbook();
        var payload = JsonSerializer.SerializeToElement(new
        {
            workbook = new
            {
                file_name = "fixture.xlsx",
                bytes_base64 = Convert.ToBase64String(await File.ReadAllBytesAsync(workbookPath)),
            },
            desired_assignments = new[]
            {
                new { course_code = "INT1000", course_name = "Programming", teaching_team_label = "Alice" },
            },
            timeout_seconds = 30,
        });
        var solver = new BlockingSolver();
        using var cancellation = new CancellationTokenSource();

        var solve = new DesktopCommandDispatcher(() => solver).DispatchAsync(
            "solve_workbook",
            payload,
            cancellation.Token);
        await solver.Started.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => solve);
    }

    public void Dispose() => Directory.Delete(temporaryDirectory, true);

    private static JsonElement CreateSolvePayload(string workbookPath) => JsonSerializer.SerializeToElement(new
    {
        workbook = new
        {
            file_name = "fixture.xlsx",
            bytes_base64 = Convert.ToBase64String(File.ReadAllBytes(workbookPath)),
        },
        desired_assignments = new[]
        {
            new
            {
                course_code = "INT1000",
                course_name = "Programming",
                teaching_team_label = "Alice",
            },
        },
        timeout_seconds = 30,
    });

    private string CreateWorkbook(
        bool includeRoom = true,
        bool includeValidSession = false,
        bool includeUnresolvedSession = false)
    {
        var path = Path.Combine(temporaryDirectory, "fixture.xlsx");
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        var row = new Row { RowIndex = 5 };
        AppendCell(row, "A5", "K69I-IT1");
        AppendCell(row, "B5", "INT1000");
        AppendCell(row, "C5", "Programming");
        AppendCell(row, "D5", "3");
        AppendCell(row, "I5", "30");
        AppendCell(row, "J5", "LHP-1");
        AppendCell(row, "K5", "CL");
        AppendCell(row, "L5", "LT");
        AppendCell(row, "M5", "2");
        AppendCell(row, "N5", "1");
        if (includeRoom)
        {
            AppendCell(row, "O5", "101-A");
        }
        AppendCell(row, "P5", "Alice");
        var sheetData = new SheetData(row);
        if (includeValidSession)
        {
            var validRow = new Row { RowIndex = 6 };
            AppendCell(validRow, "A6", "K69I-IT1");
            AppendCell(validRow, "B6", "INT1000");
            AppendCell(validRow, "C6", "Programming");
            AppendCell(validRow, "D6", "3");
            AppendCell(validRow, "I6", "30");
            AppendCell(validRow, "J6", "LHP-2");
            AppendCell(validRow, "K6", "CL");
            AppendCell(validRow, "L6", "LT");
            AppendCell(validRow, "M6", "2");
            AppendCell(validRow, "N6", "2");
            AppendCell(validRow, "O6", "101-B");
            AppendCell(validRow, "P6", "Alice");
            sheetData.Append(validRow);
        }
        if (includeUnresolvedSession)
        {
            var unresolvedRow = new Row { RowIndex = 6 };
            AppendCell(unresolvedRow, "A6", "K69I-IT1");
            AppendCell(unresolvedRow, "B6", "INT1000");
            AppendCell(unresolvedRow, "C6", "Programming");
            AppendCell(unresolvedRow, "D6", "3");
            AppendCell(unresolvedRow, "I6", "30");
            AppendCell(unresolvedRow, "J6", "LHP-2");
            AppendCell(unresolvedRow, "K6", "CL");
            AppendCell(unresolvedRow, "L6", "TH");
            AppendCell(unresolvedRow, "M6", "Thông báo sau");
            AppendCell(unresolvedRow, "P6", "Bob");
            sheetData.Append(unresolvedRow);
        }

        worksheetPart.Worksheet = new Worksheet(sheetData);

        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = 1,
            Name = "Sheet3",
        });
        workbookPart.Workbook.Save();
        return path;
    }

    private static string? FindSignedPdf()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = directory.GetFiles("*.pdf")
                .FirstOrDefault(file => !file.Name.Equals("constraint.pdf", StringComparison.OrdinalIgnoreCase));
            if (candidate is not null)
            {
                return candidate.FullName;
            }
        }

        return null;
    }

    private static void AppendCell(Row row, string reference, string value)
    {
        row.Append(new Cell
        {
            CellReference = reference,
            DataType = CellValues.InlineString,
            InlineString = new InlineString(new Text(value)),
        });
    }

    private sealed class SatisfyingSolver : IPersonalSelectionSatSolver
    {
        public Task<PersonalSelectionSatResult> SolveAsync(
            PersonalSelectionCnf cnf,
            int maxSolutions,
            TimeSpan timeout,
            CancellationToken cancellationToken) => Task.FromResult(new PersonalSelectionSatResult(
            PersonalSelectionSatStatus.Feasible,
            ImmutableArray.Create(Enumerable.Range(1, cnf.VariableCount).ToImmutableArray()),
            TimeSpan.FromMilliseconds(12),
                1));
    }

    private sealed class InfeasibleSolver : IPersonalSelectionSatSolver
    {
        public Task<PersonalSelectionSatResult> SolveAsync(
            PersonalSelectionCnf cnf,
            int maxSolutions,
            TimeSpan timeout,
            CancellationToken cancellationToken) => Task.FromResult(new PersonalSelectionSatResult(
            PersonalSelectionSatStatus.Infeasible,
            [],
            TimeSpan.FromMilliseconds(12),
            1));
    }

    private sealed class BlockingSolver : IPersonalSelectionSatSolver
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<PersonalSelectionSatResult> SolveAsync(
            PersonalSelectionCnf cnf,
            int maxSolutions,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Cancellation did not stop the solver.");
        }
    }
}
