using System.Collections.Immutable;
using Scheduler.Application;
using Scheduler.Domain;
using Scheduler.Infrastructure.NativeSolver;
using Xunit;
using Xunit.Sdk;

namespace Scheduler.Infrastructure.NativeSolver.Tests;

/// <summary>
/// Exercises the real Windows worker process when a release pipeline provides its path.
/// Unit tests cover protocol parsing without requiring a platform-specific executable.
/// </summary>
public sealed class NativeSolverClientIntegrationTests
{
    private const string WorkerEnvironmentVariable = "SCHEDULER_SOLVER_WORKER";

    [Fact]
    [Trait("Category", "NativeIntegration")]
    public async Task SolveAsyncEnumeratesDistinctSatisfyingModelsFromWorkerProcess()
    {
        var workerPath = GetWorkerPath();

        var result = await new NativeSolverClient(workerPath).SolveAsync(
            CreateExactlyOneCnf(),
            maxSolutions: 5,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal(PersonalSelectionSatStatus.Feasible, result.Status);
        Assert.Equal(2, result.Models.Length);
        Assert.All(result.Models, model => PersonalSelectionCnfModelValidator.Validate(CreateExactlyOneCnf(), model));
        Assert.False(result.Models[0].SequenceEqual(result.Models[1]));
        Assert.Equal(3, result.SolveCalls);
    }

    [Fact]
    [Trait("Category", "NativeIntegration")]
    public async Task SolveAsyncMatchesBruteForceOracleForDeterministicSmallCnfs()
    {
        var worker = new NativeSolverClient(GetWorkerPath());
        var random = new Random(20260726);

        for (var caseIndex = 0; caseIndex < 128; caseIndex++)
        {
            var cnf = CreateRandomCnf(random);
            var expectedModels = EnumerateSatisfyingModels(cnf);
            var result = await worker.SolveAsync(
                cnf,
                maxSolutions: 100,
                TimeSpan.FromSeconds(5),
                CancellationToken.None);

            Assert.Equal(
                expectedModels.Count == 0 ? PersonalSelectionSatStatus.Infeasible : PersonalSelectionSatStatus.Feasible,
                result.Status);
            var actualModels = result.Models.Select(model => ModelSignature(model)).ToHashSet(StringComparer.Ordinal);
            Assert.True(
                expectedModels.SetEquals(actualModels),
                $"Worker model set differed for deterministic generated CNF case {caseIndex}.");
        }
    }

    private static PersonalSelectionCnf CreateExactlyOneCnf() => new(
        ImmutableArray.Create(
            CreateVariable(1, "LHP-01"),
            CreateVariable(2, "LHP-02")),
        ImmutableArray.Create(
            new PersonalSelectionCnfClause(ImmutableArray.Create(1, 2), PersonalSelectionClauseKind.AtLeastOne),
            new PersonalSelectionCnfClause(ImmutableArray.Create(-1, -2), PersonalSelectionClauseKind.AtMostOne)),
        PairCount: 1,
        AtLeastOneClauseCount: 1,
        AtMostOneClauseCount: 1,
        ConflictClauseCount: 0);

    private static string GetWorkerPath()
    {
        var workerPath = Environment.GetEnvironmentVariable(WorkerEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(workerPath) || !File.Exists(workerPath))
        {
            throw SkipException.ForSkip(
                $"Set {WorkerEnvironmentVariable} to a built SolverWorker executable to run native integration tests.");
        }

        return workerPath;
    }

    private static PersonalSelectionCnf CreateRandomCnf(Random random)
    {
        var variableCount = random.Next(1, 6);
        var variables = Enumerable.Range(1, variableCount)
            .Select(variableId => CreateVariable(variableId, $"LHP-{variableId:D2}"))
            .ToImmutableArray();
        var clauses = ImmutableArray.CreateBuilder<PersonalSelectionCnfClause>();
        var clauseCount = random.Next(0, 9);
        for (var clauseIndex = 0; clauseIndex < clauseCount; clauseIndex++)
        {
            var literalCount = random.Next(0, 4);
            var literals = ImmutableArray.CreateBuilder<int>(literalCount);
            for (var literalIndex = 0; literalIndex < literalCount; literalIndex++)
            {
                var variableId = random.Next(1, variableCount + 1);
                literals.Add(random.Next(2) == 0 ? variableId : -variableId);
            }

            clauses.Add(new PersonalSelectionCnfClause(literals.ToImmutable(), PersonalSelectionClauseKind.Conflict));
        }

        return new PersonalSelectionCnf(
            variables,
            clauses.ToImmutable(),
            PairCount: 0,
            AtLeastOneClauseCount: 0,
            AtMostOneClauseCount: 0,
            ConflictClauseCount: clauseCount);
    }

    private static HashSet<string> EnumerateSatisfyingModels(PersonalSelectionCnf cnf)
    {
        var models = new HashSet<string>(StringComparer.Ordinal);
        for (var bits = 0; bits < 1 << cnf.VariableCount; bits++)
        {
            var model = Enumerable.Range(1, cnf.VariableCount)
                .Select(variableId => (bits & (1 << (variableId - 1))) == 0 ? -variableId : variableId)
                .ToImmutableArray();
            if (cnf.Clauses.All(clause => clause.Literals.Any(literal => model[Math.Abs(literal) - 1] == literal)))
            {
                models.Add(ModelSignature(model));
            }
        }

        return models;
    }

    private static string ModelSignature(IEnumerable<int> model) => string.Join(',', model);

    private static PersonalSelectionCnfVariable CreateVariable(int variableId, string lhpCode) => new(
        variableId,
        PairIndex: 0,
        CandidateIndex: variableId - 1,
        new PersonalSelectionChoice(
            new DesiredAnchorAssignment("COURSE", "Course", "Lecturer"),
            lhpCode,
            ImmutableArray<string>.Empty,
            ImmutableArray<KeyValuePair<string, TimeSlot>>.Empty));
}
