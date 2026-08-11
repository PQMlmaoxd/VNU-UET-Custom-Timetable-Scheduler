using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Scheduler.Application;
using Scheduler.Domain;

namespace Scheduler.Infrastructure.FormalVerification;

public sealed record FormalArtifactExportRequest(
    string OutputDirectory,
    string SourceFileName,
    string SourceFormat,
    string SourceSha256,
    SchedulingProblem Problem,
    PersonalSelectionSpec SelectionSpec,
    PersonalSelectionCnf Cnf);

public sealed record FormalArtifactExportResult(
    string ArtifactDirectory,
    string FormulaPath,
    string ManifestPath,
    string CnfSha256,
    int VariableCount,
    int ClauseCount);

/// <summary>
/// Writes an auditable personal-selection CNF package.
/// Export does not prove the encoding or timetable semantics; the external
/// checker workflow is the separate formal-verification boundary. Tool
/// executables are supplied and hash-checked by that workflow.
/// </summary>
public static class FormalArtifactExporter
{
    public const int SchemaVersion = 2;
    public const string EncoderVersion = "personal-selection-cnf-v2";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    public static FormalArtifactExportResult Export(FormalArtifactExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        var outputDirectory = Path.GetFullPath(request.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);
        EnsureOutputIsSafe(outputDirectory);

        var dimacs = request.Cnf.ToDimacs().ReplaceLineEndings("\n");
        var dimacsBytes = Utf8WithoutBom.GetBytes(dimacs);
        var cnfSha256 = Convert.ToHexString(SHA256.HashData(dimacsBytes));

        var variables = request.Cnf.Variables
            .Select(variable => new VariableArtifact(
                variable.VariableId,
                variable.PairIndex,
                variable.CandidateIndex,
                variable.Choice.DesiredAssignment.CourseCode,
                variable.Choice.DesiredAssignment.CourseName,
                variable.Choice.DesiredAssignment.TeachingTeamKey,
                variable.Choice.DesiredAssignment.DisplayTeachingTeam,
                variable.Choice.LhpCode,
                variable.Choice.SessionIds,
                variable.Choice.SessionTimeSlots
                    .Select(pair => ToTimeSlotArtifact(pair.Key, pair.Value))
                    .ToImmutableArray()))
            .ToImmutableArray();
        var variablesById = request.Cnf.Variables.ToDictionary(variable => variable.VariableId);
        var clauses = request.Cnf.Clauses
            .Select((clause, index) => new ClauseArtifact(
                index + 1,
                clause.Kind.ToString(),
                clause.Literals,
                DescribeClause(clause.Kind),
                CreateClauseWitness(clause, variablesById)))
            .ToImmutableArray();

        var manifest = new ManifestArtifact(
            SchemaVersion,
            EncoderVersion,
            DateTimeOffset.UtcNow,
            new SourceArtifact(
                Path.GetFileName(request.SourceFileName),
                request.SourceFormat,
                request.SourceSha256),
            new ProblemArtifact(request.Problem.ProblemId, request.Problem.Department, request.Problem.Semester),
            new SelectionArtifact(
                request.SelectionSpec.Pairs.Length,
                request.SelectionSpec.Pairs
                    .Select(pair => new DesiredAssignmentArtifact(
                        pair.DesiredAssignment.CourseCode,
                        pair.DesiredAssignment.CourseName,
                        pair.DesiredAssignment.TeachingTeamKey,
                        pair.DesiredAssignment.DisplayTeachingTeam))
                    .ToImmutableArray()),
            new CnfArtifact(
                request.Cnf.VariableCount,
                request.Cnf.ClauseCount,
                request.Cnf.AtLeastOneClauseCount,
                request.Cnf.AtMostOneClauseCount,
                request.Cnf.ConflictClauseCount,
                cnfSha256),
            new ProofArtifact("proof.lrat", null, "not_generated"),
            new ToolingArtifact(
                new ToolArtifact(
                    "CaDiCaL",
                    "3.0.1",
                    "c60730422e758ef1cebe7aeddf2dda31c996bf04",
                    null,
                    "https://github.com/arminbiere/cadical"),
                new ToolArtifact(
                    "cake_lpr",
                    null,
                    "a36874a8b750b43fe4b385b8ddbf5b033e46a3fa",
                    null,
                    "https://github.com/tanyongkiam/cake_lpr"),
                "--lrat --no-binary --quiet"));

        WriteText(Path.Combine(outputDirectory, "formula.cnf"), dimacs);
        WriteText(Path.Combine(outputDirectory, "formula.sha256"), $"{cnfSha256}  formula.cnf\n");
        WriteJson(
            Path.Combine(outputDirectory, "variables.json"),
            new VariablesArtifact(SchemaVersion, variables));
        WriteJson(
            Path.Combine(outputDirectory, "clauses.json"),
            new ClausesArtifact(SchemaVersion, clauses));
        WriteJson(Path.Combine(outputDirectory, "manifest.json"), manifest);
        WriteText(Path.Combine(outputDirectory, "README.md"), CreateReadme(manifest));
        WriteText(Path.Combine(outputDirectory, "generate-unsat-proof.ps1"), FormalArtifactScripts.Generate);
        WriteText(Path.Combine(outputDirectory, "verify-unsat-proof.ps1"), FormalArtifactScripts.Verify);
        WriteText(Path.Combine(outputDirectory, "verify-unsat.ps1"), FormalArtifactScripts.VerifyAll);

        return new FormalArtifactExportResult(
            outputDirectory,
            Path.Combine(outputDirectory, "formula.cnf"),
            Path.Combine(outputDirectory, "manifest.json"),
            cnfSha256,
            request.Cnf.VariableCount,
            request.Cnf.ClauseCount);
    }

    private static SessionTimeSlotArtifact ToTimeSlotArtifact(string sessionId, TimeSlot timeSlot) => new(
        sessionId,
        timeSlot.Day.ToString(),
        timeSlot.Period.ToWorkbookValue(),
        timeSlot.Period.ToAtomicPeriods().OrderBy(period => period).ToImmutableArray());

    private static string DescribeClause(PersonalSelectionClauseKind kind) => kind switch
    {
        PersonalSelectionClauseKind.AtLeastOne => "At least one LHP candidate must be selected for the requested pair.",
        PersonalSelectionClauseKind.AtMostOne => "At most one LHP candidate may be selected for the requested pair.",
        PersonalSelectionClauseKind.Conflict => "The two candidate LHPs cannot be selected together.",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported CNF clause kind."),
    };

    private static ClauseWitnessArtifact? CreateClauseWitness(
        PersonalSelectionCnfClause clause,
        Dictionary<int, PersonalSelectionCnfVariable> variablesById)
    {
        if (clause.Kind != PersonalSelectionClauseKind.Conflict || clause.Literals.Length != 2)
        {
            return null;
        }

        if (!variablesById.TryGetValue(Math.Abs(clause.Literals[0]), out var left) ||
            !variablesById.TryGetValue(Math.Abs(clause.Literals[1]), out var right))
        {
            throw new ArgumentException("Conflict clause references an unknown CNF variable.", nameof(clause));
        }

        var sharedSessionIds = left.Choice.SessionIds
            .Intersect(right.Choice.SessionIds, StringComparer.Ordinal)
            .OrderBy(sessionId => sessionId, StringComparer.Ordinal)
            .ToImmutableArray();
        var overlaps = ImmutableArray.CreateBuilder<TimeSlotOverlapArtifact>();
        foreach (var leftTimeSlot in left.Choice.SessionTimeSlots)
        {
            foreach (var rightTimeSlot in right.Choice.SessionTimeSlots)
            {
                if (!leftTimeSlot.Value.OverlapsWith(rightTimeSlot.Value))
                {
                    continue;
                }

                overlaps.Add(new TimeSlotOverlapArtifact(
                    leftTimeSlot.Key,
                    ToTimeSlotArtifact(leftTimeSlot.Key, leftTimeSlot.Value),
                    rightTimeSlot.Key,
                    ToTimeSlotArtifact(rightTimeSlot.Key, rightTimeSlot.Value)));
            }
        }

        return new ClauseWitnessArtifact(sharedSessionIds, overlaps.ToImmutable());
    }

    private static void ValidateRequest(FormalArtifactExportRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.OutputDirectory))
        {
            throw new ArgumentException("Artifact output directory is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.SourceFileName))
        {
            throw new ArgumentException("Source file name is required.", nameof(request));
        }

        if (request.SourceFormat is not ("xlsx" or "pdf"))
        {
            throw new ArgumentException("Source format must be xlsx or pdf.", nameof(request));
        }

        ValidateSha256(request.SourceSha256, nameof(request.SourceSha256));
        ArgumentNullException.ThrowIfNull(request.Problem);
        ArgumentNullException.ThrowIfNull(request.SelectionSpec);
        ArgumentNullException.ThrowIfNull(request.Cnf);

        if (request.SelectionSpec.Pairs.Length != request.Cnf.PairCount)
        {
            throw new ArgumentException("Selection specification and CNF pair counts do not match.", nameof(request));
        }

        for (var index = 0; index < request.Cnf.Variables.Length; index++)
        {
            if (request.Cnf.Variables[index].VariableId != index + 1)
            {
                throw new ArgumentException("CNF variable IDs must be sequential starting at one.", nameof(request));
            }
        }

        foreach (var clause in request.Cnf.Clauses)
        {
            foreach (var literal in clause.Literals)
            {
                if (literal == 0 || Math.Abs((long)literal) > request.Cnf.VariableCount)
                {
                    throw new ArgumentException("CNF contains a literal outside its variable range.", nameof(request));
                }
            }
        }
    }

    private static void ValidateSha256(string value, string parameterName)
    {
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("SHA-256 values must contain exactly 64 hexadecimal characters.", parameterName);
        }
    }

    private static void EnsureOutputIsSafe(string outputDirectory)
    {
        var protectedNames = new[]
        {
            "formula.cnf",
            "formula.sha256",
            "proof.lrat",
            "proof.lrat.sha256",
            "variables.json",
            "clauses.json",
            "manifest.json",
            "README.md",
            "generate-unsat-proof.ps1",
            "verify-unsat-proof.ps1",
            "verify-unsat.ps1",
        };

        var existing = protectedNames.FirstOrDefault(name => File.Exists(Path.Combine(outputDirectory, name)));
        if (existing is not null)
        {
            throw new IOException($"Formal artifact output already contains '{existing}'. Choose a new directory.");
        }
    }

    private static void WriteJson<T>(string path, T value) =>
        WriteText(path, JsonSerializer.Serialize(value, JsonOptions));

    private static void WriteText(string path, string content) =>
        File.WriteAllText(path, content, Utf8WithoutBom);

    private static string CreateReadme(ManifestArtifact manifest) => string.Join(
        "\n",
        "# UNSAT verification package",
        "",
        "This package contains the exact personal-selection CNF exported by the desktop application.",
        "It is intended for the UNSAT proof workflow only. A CNF file by itself is not a formal proof.",
        "",
        "## Contents",
        "",
        "- `formula.cnf`: DIMACS formula used by the solver.",
        "- `variables.json` and `clauses.json`: auditable variable and clause metadata.",
         "- `formula.sha256`: immutable formula hash for an external custody record.",
         "- `manifest.json`: source, selection and CNF metadata.",
        "- `generate-unsat-proof.ps1`: creates `proof.lrat` with a CaDiCaL CLI.",
        "- `verify-unsat-proof.ps1`: checks the proof with `cake_lpr`.",
        "- `verify-unsat.ps1`: runs generation and verification in one command.",
        "",
        "## Verify",
        "",
        "Run PowerShell from this directory:",
        "",
        "```powershell",
          ".\\verify-unsat.ps1 -CadicalPath 'C:\\tools\\cadical.exe' -CakeLprPath 'C:\\tools\\cake_lpr.exe' `",
          "  -ExpectedCadicalSha256 '<approved SHA-256>' -ExpectedCakeLprSha256 '<approved SHA-256>' `",
          "  -ExpectedFormulaSha256 '<exported formula SHA-256>'",
        "```",
        "",
         "Only the exact `s VERIFIED UNSAT` result from an approved, hash-matched `cake_lpr` executable counts as formal verification.",
         "The scripts require formula and proof hashes supplied from an external custody record. Do not edit `formula.cnf` or `proof.lrat`.",
         "",
        "## Export details",
        "",
        $"- Schema: {manifest.SchemaVersion}",
        $"- Encoder: {manifest.EncoderVersion}",
        $"- Variables: {manifest.Cnf.VariableCount}",
        $"- Clauses: {manifest.Cnf.ClauseCount}",
        $"- CNF SHA-256: `{manifest.Cnf.Sha256}`",
        "",
         "The package records the pinned CaDiCaL and cake_lpr source commits. The verification commands require approved executable SHA-256 values and record the observed hashes.",
         "The tools are not bundled in this package. Obtain them from their upstream projects and retain their licenses and notices when distributing them.",
        "",
        "The package can contain course and lecturer names. Store and share it according to your data policy.");

    private sealed record VariablesArtifact(
        [property: JsonPropertyName("schema_version")] int SchemaVersion,
        [property: JsonPropertyName("variables")] ImmutableArray<VariableArtifact> Variables);

    private sealed record VariableArtifact(
        [property: JsonPropertyName("variable_id")] int VariableId,
        [property: JsonPropertyName("pair_index")] int PairIndex,
        [property: JsonPropertyName("candidate_index")] int CandidateIndex,
        [property: JsonPropertyName("course_code")] string CourseCode,
        [property: JsonPropertyName("course_name")] string CourseName,
        [property: JsonPropertyName("teaching_team_key")] string TeachingTeamKey,
        [property: JsonPropertyName("teaching_team_label")] string TeachingTeamLabel,
        [property: JsonPropertyName("lhp_code")] string LhpCode,
        [property: JsonPropertyName("session_ids")] ImmutableArray<string> SessionIds,
        [property: JsonPropertyName("session_timeslots")] ImmutableArray<SessionTimeSlotArtifact> SessionTimeSlots);

    private sealed record SessionTimeSlotArtifact(
        [property: JsonPropertyName("session_id")] string SessionId,
        [property: JsonPropertyName("day")] string Day,
        [property: JsonPropertyName("period")] string Period,
        [property: JsonPropertyName("atomic_periods")] ImmutableArray<int> AtomicPeriods);

    private sealed record ClausesArtifact(
        [property: JsonPropertyName("schema_version")] int SchemaVersion,
        [property: JsonPropertyName("clauses")] ImmutableArray<ClauseArtifact> Clauses);

    private sealed record ClauseArtifact(
        [property: JsonPropertyName("clause_index")] int ClauseIndex,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("literals")] ImmutableArray<int> Literals,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("witness")] ClauseWitnessArtifact? Witness);

    private sealed record ClauseWitnessArtifact(
        [property: JsonPropertyName("shared_session_ids")] ImmutableArray<string> SharedSessionIds,
        [property: JsonPropertyName("timeslot_overlaps")] ImmutableArray<TimeSlotOverlapArtifact> TimeSlotOverlaps);

    private sealed record TimeSlotOverlapArtifact(
        [property: JsonPropertyName("left_session_id")] string LeftSessionId,
        [property: JsonPropertyName("left_timeslot")] SessionTimeSlotArtifact LeftTimeSlot,
        [property: JsonPropertyName("right_session_id")] string RightSessionId,
        [property: JsonPropertyName("right_timeslot")] SessionTimeSlotArtifact RightTimeSlot);

    private sealed record ManifestArtifact(
        [property: JsonPropertyName("schema_version")] int SchemaVersion,
        [property: JsonPropertyName("encoder_version")] string EncoderVersion,
        [property: JsonPropertyName("created_utc")] DateTimeOffset CreatedUtc,
        [property: JsonPropertyName("source")] SourceArtifact Source,
        [property: JsonPropertyName("problem")] ProblemArtifact Problem,
        [property: JsonPropertyName("selection")] SelectionArtifact Selection,
        [property: JsonPropertyName("cnf")] CnfArtifact Cnf,
        [property: JsonPropertyName("proof")] ProofArtifact Proof,
        [property: JsonPropertyName("tools")] ToolingArtifact Tools);

    private sealed record SourceArtifact(
        [property: JsonPropertyName("file_name")] string FileName,
        [property: JsonPropertyName("format")] string Format,
        [property: JsonPropertyName("sha256")] string Sha256);

    private sealed record ProblemArtifact(
        [property: JsonPropertyName("problem_id")] string ProblemId,
        [property: JsonPropertyName("department")] string Department,
        [property: JsonPropertyName("semester")] string Semester);

    private sealed record SelectionArtifact(
        [property: JsonPropertyName("pair_count")] int PairCount,
        [property: JsonPropertyName("desired_assignments")] ImmutableArray<DesiredAssignmentArtifact> DesiredAssignments);

    private sealed record DesiredAssignmentArtifact(
        [property: JsonPropertyName("course_code")] string CourseCode,
        [property: JsonPropertyName("course_name")] string CourseName,
        [property: JsonPropertyName("teaching_team_key")] string TeachingTeamKey,
        [property: JsonPropertyName("teaching_team_label")] string TeachingTeamLabel);

    private sealed record CnfArtifact(
        [property: JsonPropertyName("variable_count")] int VariableCount,
        [property: JsonPropertyName("clause_count")] int ClauseCount,
        [property: JsonPropertyName("at_least_one_clause_count")] int AtLeastOneClauseCount,
        [property: JsonPropertyName("at_most_one_clause_count")] int AtMostOneClauseCount,
        [property: JsonPropertyName("conflict_clause_count")] int ConflictClauseCount,
        [property: JsonPropertyName("sha256")] string Sha256);

    private sealed record ProofArtifact(
        [property: JsonPropertyName("file_name")] string FileName,
        [property: JsonPropertyName("sha256")] string? Sha256,
        [property: JsonPropertyName("status")] string Status);

    private sealed record ToolingArtifact(
        [property: JsonPropertyName("cadical")] ToolArtifact Cadical,
        [property: JsonPropertyName("cake_lpr")] ToolArtifact CakeLpr,
        [property: JsonPropertyName("proof_options")] string ProofOptions);

    private sealed record ToolArtifact(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("version")] string? Version,
        [property: JsonPropertyName("source_commit")] string SourceCommit,
        [property: JsonPropertyName("binary_sha256")] string? BinarySha256,
        [property: JsonPropertyName("source_url")] string SourceUrl);
}

internal static class FormalArtifactScripts
{
    public const string Generate = """
param(
    [Parameter(Mandatory = $true)][string]$CadicalPath,
    [Parameter(Mandatory = $true)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedCadicalSha256,
    [Parameter(Mandatory = $true)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedFormulaSha256,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$formula = Join-Path $PSScriptRoot 'formula.cnf'
$formulaHashFile = Join-Path $PSScriptRoot 'formula.sha256'
$proof = Join-Path $PSScriptRoot 'proof.lrat'
$proofHashFile = Join-Path $PSScriptRoot 'proof.lrat.sha256'
if (-not (Test-Path -LiteralPath $CadicalPath -PathType Leaf)) { throw "CaDiCaL executable was not found: $CadicalPath" }
if (-not (Test-Path -LiteralPath $formula -PathType Leaf)) { throw 'formula.cnf was not found.' }
if (-not (Test-Path -LiteralPath $formulaHashFile -PathType Leaf)) { throw 'formula.sha256 was not found.' }
if ((Test-Path -LiteralPath $proof) -and -not $Force) { throw 'proof.lrat already exists. Use -Force to replace it.' }
$actualCadicalHash = (Get-FileHash -LiteralPath $CadicalPath -Algorithm SHA256).Hash
if ($actualCadicalHash -ne $ExpectedCadicalSha256.ToUpperInvariant()) { throw 'CaDiCaL executable hash does not match the approved value.' }
$recordedFormulaHash = ((Get-Content -LiteralPath $formulaHashFile -Raw).Trim() -split '\s+')[0].ToUpperInvariant()
$actualFormulaHash = (Get-FileHash -LiteralPath $formula -Algorithm SHA256).Hash
if ($recordedFormulaHash -ne $ExpectedFormulaSha256.ToUpperInvariant() -or $actualFormulaHash -ne $recordedFormulaHash) { throw 'formula.cnf does not match the externally supplied export hash.' }

& $CadicalPath '--lrat' '--no-binary' '--quiet' $formula $proof
if ($LASTEXITCODE -ne 20) { throw "CaDiCaL did not report UNSAT. Exit code: $LASTEXITCODE" }
$verificationPath = Join-Path $PSScriptRoot 'verification.json'
if (Test-Path -LiteralPath $verificationPath) { Remove-Item -LiteralPath $verificationPath -Force }
$proofHash = (Get-FileHash -LiteralPath $proof -Algorithm SHA256).Hash
$utf8 = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText($proofHashFile, "$proofHash  proof.lrat`n", $utf8)
$manifestPath = Join-Path $PSScriptRoot 'manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$manifest.proof.sha256 = $proofHash
$manifest.proof.status = 'generated'
$manifest.tools.cadical.binary_sha256 = $actualCadicalHash
$manifestJson = $manifest | ConvertTo-Json -Depth 10
[System.IO.File]::WriteAllText($manifestPath, $manifestJson, $utf8)
Write-Host "Created $proof"
""";

    public const string Verify = """
param(
    [Parameter(Mandatory = $true)][string]$CakeLprPath,
    [Parameter(Mandatory = $true)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedCakeLprSha256,
    [Parameter(Mandatory = $true)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedFormulaSha256,
    [Parameter(Mandatory = $true)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedProofSha256
)

$ErrorActionPreference = 'Stop'
$formula = Join-Path $PSScriptRoot 'formula.cnf'
$formulaHashFile = Join-Path $PSScriptRoot 'formula.sha256'
$proof = Join-Path $PSScriptRoot 'proof.lrat'
$proofHashFile = Join-Path $PSScriptRoot 'proof.lrat.sha256'
if (-not (Test-Path -LiteralPath $CakeLprPath -PathType Leaf)) { throw "cake_lpr executable was not found: $CakeLprPath" }
if (-not (Test-Path -LiteralPath $formula -PathType Leaf)) { throw 'formula.cnf was not found.' }
if (-not (Test-Path -LiteralPath $proof -PathType Leaf)) { throw 'proof.lrat was not found. Generate it first.' }
if (-not (Test-Path -LiteralPath $formulaHashFile -PathType Leaf)) { throw 'formula.sha256 was not found.' }
if (-not (Test-Path -LiteralPath $proofHashFile -PathType Leaf)) { throw 'proof.lrat.sha256 was not found. Generate the proof again.' }
$actualCakeLprHash = (Get-FileHash -LiteralPath $CakeLprPath -Algorithm SHA256).Hash
if ($actualCakeLprHash -ne $ExpectedCakeLprSha256.ToUpperInvariant()) { throw 'cake_lpr executable hash does not match the approved value.' }

$actualFormulaHash = (Get-FileHash -LiteralPath $formula -Algorithm SHA256).Hash
$recordedFormulaHash = ((Get-Content -LiteralPath $formulaHashFile -Raw).Trim() -split '\s+')[0].ToUpperInvariant()
$actualProofHash = (Get-FileHash -LiteralPath $proof -Algorithm SHA256).Hash
$recordedProofHash = ((Get-Content -LiteralPath $proofHashFile -Raw).Trim() -split '\s+')[0].ToUpperInvariant()
if ($actualFormulaHash -ne $recordedFormulaHash -or $actualFormulaHash -ne $ExpectedFormulaSha256.ToUpperInvariant()) { throw 'formula.cnf does not match the externally supplied export hash.' }
if ($actualProofHash -ne $recordedProofHash -or $actualProofHash -ne $ExpectedProofSha256.ToUpperInvariant()) { throw 'proof.lrat does not match the externally supplied proof hash.' }

$output = @(& $CakeLprPath $formula $proof 2>&1)
if ($LASTEXITCODE -ne 0 -or $output -notcontains 's VERIFIED UNSAT') {
    throw "cake_lpr did not verify the UNSAT proof. Exit code: $LASTEXITCODE"
}

$verification = [ordered]@{
    status = 'verified_unsat'
    checked_utc = [DateTime]::UtcNow.ToString('O')
    formula_sha256 = $actualFormulaHash
    proof_sha256 = $actualProofHash
    checker = 'cake_lpr'
    checker_sha256 = $actualCakeLprHash
    output = @($output | ForEach-Object { $_.ToString() })
}
$utf8 = [System.Text.UTF8Encoding]::new($false)
$verificationPath = Join-Path $PSScriptRoot 'verification.json'
$verificationJson = $verification | ConvertTo-Json -Depth 4
[System.IO.File]::WriteAllText($verificationPath, $verificationJson, $utf8)
Write-Host 'Verified UNSAT: s VERIFIED UNSAT'
""";

    public const string VerifyAll = """
param(
    [Parameter(Mandatory = $true)][string]$CadicalPath,
    [Parameter(Mandatory = $true)][string]$CakeLprPath,
    [Parameter(Mandatory = $true)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedCadicalSha256,
    [Parameter(Mandatory = $true)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedCakeLprSha256,
    [Parameter(Mandatory = $true)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedFormulaSha256,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'generate-unsat-proof.ps1') -CadicalPath $CadicalPath -ExpectedCadicalSha256 $ExpectedCadicalSha256 -ExpectedFormulaSha256 $ExpectedFormulaSha256 -Force:$Force
$expectedProofSha256 = ((Get-Content -LiteralPath (Join-Path $PSScriptRoot 'proof.lrat.sha256') -Raw).Trim() -split '\s+')[0]
& (Join-Path $PSScriptRoot 'verify-unsat-proof.ps1') -CakeLprPath $CakeLprPath -ExpectedCakeLprSha256 $ExpectedCakeLprSha256 -ExpectedFormulaSha256 $ExpectedFormulaSha256 -ExpectedProofSha256 $expectedProofSha256
""";
}
