using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Scheduler.Application;
using Scheduler.Domain;
using Scheduler.Infrastructure.FormalVerification;
using Xunit;

namespace Scheduler.Infrastructure.FormalVerification.Tests;

public sealed class FormalArtifactExporterTests
{
    [Fact]
    public void ExportWritesAuditableArtifactAndMatchingFormulaHash()
    {
        var outputDirectory = CreateTemporaryDirectory();

        var result = FormalArtifactExporter.Export(CreateRequest(outputDirectory));

        Assert.Equal(result.ArtifactDirectory, outputDirectory);
        Assert.Equal(2, result.VariableCount);
        Assert.Equal(2, result.ClauseCount);
        Assert.Equal(result.CnfSha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(result.FormulaPath))));
        Assert.EndsWith("0\n", File.ReadAllText(result.FormulaPath), StringComparison.Ordinal);
        Assert.Contains("p cnf 2 2\n", File.ReadAllText(result.FormulaPath), StringComparison.Ordinal);

        foreach (var fileName in new[]
        {
            "variables.json",
            "clauses.json",
            "manifest.json",
            "formula.sha256",
            "README.md",
            "generate-unsat-proof.ps1",
            "verify-unsat-proof.ps1",
            "verify-unsat.ps1",
        })
        {
            Assert.True(File.Exists(Path.Combine(outputDirectory, fileName)), fileName);
        }

        using var manifest = JsonDocument.Parse(File.ReadAllText(result.ManifestPath));
        Assert.Equal(2, manifest.RootElement.GetProperty("schema_version").GetInt32());
        Assert.Equal("personal-selection-cnf-v2", manifest.RootElement.GetProperty("encoder_version").GetString());
        Assert.Equal(result.CnfSha256, manifest.RootElement.GetProperty("cnf").GetProperty("sha256").GetString());
        Assert.Equal("example.xlsx", manifest.RootElement.GetProperty("source").GetProperty("file_name").GetString());
        Assert.Equal(
            "a36874a8b750b43fe4b385b8ddbf5b033e46a3fa",
            manifest.RootElement.GetProperty("tools").GetProperty("cake_lpr").GetProperty("source_commit").GetString());
        Assert.DoesNotContain(outputDirectory, File.ReadAllText(result.ManifestPath), StringComparison.Ordinal);
        Assert.Equal(
            $"{result.CnfSha256}  formula.cnf\n",
            File.ReadAllText(Path.Combine(outputDirectory, "formula.sha256")));
        using var variables = JsonDocument.Parse(File.ReadAllText(Path.Combine(outputDirectory, "variables.json")));
        Assert.Equal("1", variables.RootElement.GetProperty("variables")[0].GetProperty("session_timeslots")[0].GetProperty("period").GetString());
        var verifyScript = File.ReadAllText(Path.Combine(outputDirectory, "verify-unsat-proof.ps1"));
        Assert.Contains("s VERIFIED UNSAT", verifyScript, StringComparison.Ordinal);
        Assert.DoesNotContain("utf8NoBOM", verifyScript, StringComparison.Ordinal);
        Assert.Contains("& $CakeLprPath $formula $proof", verifyScript, StringComparison.Ordinal);
        Assert.Contains("ExpectedFormulaSha256", verifyScript, StringComparison.Ordinal);
        Assert.Contains("ExpectedProofSha256", verifyScript, StringComparison.Ordinal);
        Assert.Contains("proof.lrat.sha256", verifyScript, StringComparison.Ordinal);

        var generateScript = File.ReadAllText(Path.Combine(outputDirectory, "generate-unsat-proof.ps1"));
        Assert.Contains("$manifest.proof.status = 'generated'", generateScript, StringComparison.Ordinal);
        Assert.Contains("$manifest.tools.cadical.binary_sha256", generateScript, StringComparison.Ordinal);
        Assert.Contains("ExpectedFormulaSha256", generateScript, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -LiteralPath $verificationPath -Force", generateScript, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportProducesTheSameFormulaForTheSameCnf()
    {
        var firstDirectory = CreateTemporaryDirectory();
        var secondDirectory = CreateTemporaryDirectory();

        var first = FormalArtifactExporter.Export(CreateRequest(firstDirectory));
        var second = FormalArtifactExporter.Export(CreateRequest(secondDirectory));

        Assert.Equal(File.ReadAllBytes(first.FormulaPath), File.ReadAllBytes(second.FormulaPath));
        Assert.Equal(first.CnfSha256, second.CnfSha256);
    }

    [Fact]
    public void ExportRejectsInvalidSourceHash()
    {
        var request = CreateRequest(CreateTemporaryDirectory()) with { SourceSha256 = "bad" };

        var exception = Assert.Throws<ArgumentException>(() => FormalArtifactExporter.Export(request));

        Assert.Contains("SHA-256", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportDoesNotOverwriteAnExistingArtifact()
    {
        var outputDirectory = CreateTemporaryDirectory();
        File.WriteAllText(Path.Combine(outputDirectory, "formula.cnf"), "user data");

        var exception = Assert.Throws<IOException>(() => FormalArtifactExporter.Export(CreateRequest(outputDirectory)));

        Assert.Contains("formula.cnf", exception.Message, StringComparison.Ordinal);
        Assert.Equal("user data", File.ReadAllText(Path.Combine(outputDirectory, "formula.cnf")));
    }

    [Fact]
    public void ExportRejectsMismatchedSelectionAndCnfPairCounts()
    {
        var request = CreateRequest(CreateTemporaryDirectory()) with
        {
            SelectionSpec = new PersonalSelectionSpec([]),
        };

        var exception = Assert.Throws<ArgumentException>(() => FormalArtifactExporter.Export(request));

        Assert.Contains("pair counts", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportWritesSharedSessionConflictWitness()
    {
        var baseRequest = CreateRequest(CreateTemporaryDirectory());
        var firstSession = baseRequest.Problem.Sessions[0];
        var first = new DesiredAnchorAssignment("INT0001", "Lecturer", "Course");
        var second = new DesiredAnchorAssignment("INT0001", "Other lecturer", "Course");
        var specification = new PersonalSelectionSpec(ImmutableArray.Create(
            new SelectionPairSpec(first, ImmutableArray.Create(new SelectionCandidate("LHP-A", ImmutableArray.Create(firstSession.SessionId)))),
            new SelectionPairSpec(second, ImmutableArray.Create(new SelectionCandidate("LHP-A", ImmutableArray.Create(firstSession.SessionId))))));
        var request = baseRequest with
        {
            SelectionSpec = specification,
            Cnf = PersonalSelectionCnfEncoder.Encode(baseRequest.Problem, specification),
        };

        FormalArtifactExporter.Export(request);

        using var clauses = JsonDocument.Parse(File.ReadAllText(Path.Combine(request.OutputDirectory, "clauses.json")));
        var conflict = clauses.RootElement.GetProperty("clauses")
            .EnumerateArray()
            .Single(clause => clause.GetProperty("kind").GetString() == "Conflict");
        Assert.Equal("first", conflict.GetProperty("witness").GetProperty("shared_session_ids")[0].GetString());
    }

    private static FormalArtifactExportRequest CreateRequest(string outputDirectory)
    {
        var course = new Course("INT0001", "Course", 3);
        var lecturer = new Lecturer("Lecturer");
        var cohort = new StudentCohort("K69I-IT1");
        var sessions = ImmutableArray.Create(
            Session("first", "LHP-A", course, lecturer, cohort, Period.Ca1),
            Session("second", "LHP-B", course, lecturer, cohort, Period.Ca2));
        var problem = new SchedulingProblem(
            "problem-1",
            "ALL",
            "semester-1",
            sessions,
            ImmutableArray.Create(
                new TimeSlot(Day.Monday, Period.Ca1),
                new TimeSlot(Day.Monday, Period.Ca2)),
            ImmutableArray.Create(new Room("101-A")),
            ImmutableArray<LecturerConstraint>.Empty);
        var desired = new DesiredAnchorAssignment(course.Code, lecturer.Name, course.Name);
        var specification = new PersonalSelectionSpec(ImmutableArray.Create(
            new SelectionPairSpec(
                desired,
                ImmutableArray.Create(
                    new SelectionCandidate("LHP-A", ImmutableArray.Create("first")),
                    new SelectionCandidate("LHP-B", ImmutableArray.Create("second"))))));
        var cnf = PersonalSelectionCnfEncoder.Encode(problem, specification);

        return new FormalArtifactExportRequest(
            outputDirectory,
            "example.xlsx",
            "xlsx",
            new string('A', 64),
            problem,
            specification,
            cnf);
    }

    private static Session Session(
        string id,
        string lhpCode,
        Course course,
        Lecturer lecturer,
        StudentCohort cohort,
        Period period) =>
        new(
            id,
            lhpCode,
            course,
            SessionType.Lt,
            "CL",
            30,
            ImmutableArray.Create(lecturer),
            ImmutableArray.Create(cohort),
            new TimeSlot(Day.Monday, period),
            new Room("101-A"));

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "formal-verification-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
