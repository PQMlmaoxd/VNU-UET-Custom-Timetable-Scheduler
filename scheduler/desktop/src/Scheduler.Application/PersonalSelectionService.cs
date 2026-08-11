using System.Collections.Immutable;
using Scheduler.Domain;

namespace Scheduler.Application;

public sealed record PersonalSelectionSolution(
    ImmutableArray<PersonalSelectionChoice> Choices,
    int MovementCost);

public sealed record PersonalSelectionSolveResult(
    PersonalSelectionSatResult SolverResult,
    ImmutableArray<PersonalSelectionSolution> Solutions);

/// <summary>
/// Keeps the candidate specification and CNF used by one solve attempt together.
/// </summary>
public sealed class PreparedPersonalSelection
{
    internal PreparedPersonalSelection(
        SchedulingProblem problem,
        PersonalSelectionSpec specification,
        PersonalSelectionCnf cnf)
    {
        Problem = problem;
        Specification = specification;
        Cnf = cnf;
    }

    internal SchedulingProblem Problem { get; }

    public PersonalSelectionSpec Specification { get; }

    public PersonalSelectionCnf Cnf { get; }
}

public static class PersonalSelectionPreparation
{
    public static PreparedPersonalSelection Create(
        SchedulingProblem problem,
        ImmutableArray<DesiredAnchorAssignment> desiredAssignments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(problem);
        var specification = PersonalSelectionBuilder.Build(problem, desiredAssignments, cancellationToken);
        return new PreparedPersonalSelection(
            problem,
            specification,
            PersonalSelectionCnfEncoder.Encode(problem, specification));
    }
}

/// <summary>
/// Application orchestration for the fixed-workbook personal selection flow.
/// It treats the native solver as untrusted and validates every materialized result.
/// </summary>
public sealed class PersonalSelectionService(IPersonalSelectionSatSolver solver)
{
    public async Task<PersonalSelectionSolveResult> SolveAsync(
        SchedulingProblem problem,
        ImmutableArray<DesiredAnchorAssignment> desiredAssignments,
        int maxSolutions,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var prepared = PersonalSelectionPreparation.Create(problem, desiredAssignments, cancellationToken);
        return await SolveAsync(problem, prepared, maxSolutions, timeout, cancellationToken);
    }

    public async Task<PersonalSelectionSolveResult> SolveAsync(
        SchedulingProblem problem,
        PreparedPersonalSelection prepared,
        int maxSolutions,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(prepared);
        if (!ReferenceEquals(problem, prepared.Problem))
        {
            throw new ArgumentException(
                "Prepared selection must be created from the scheduling problem passed to SolveAsync.",
                nameof(prepared));
        }
        if (maxSolutions is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSolutions), "maxSolutions must be between 1 and 100.");
        }
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "timeout must be positive.");
        }

        var selectionSpec = prepared.Specification;
        var cnf = prepared.Cnf;
        var solverResult = await solver.SolveAsync(cnf, maxSolutions, timeout, cancellationToken);
        ValidateSolverResult(solverResult, maxSolutions);
        var fixedSchedule = FixedWorkbookSchedule.Create(problem);
        var solutions = ImmutableArray.CreateBuilder<PersonalSelectionSolution>(solverResult.Models.Length);

        foreach (var model in solverResult.Models)
        {
            PersonalSelectionCnfModelValidator.Validate(cnf, model);
            var choices = MaterializeChoices(cnf, model);
            var validation = TimetableValidator.ValidatePersonalTimetable(
                fixedSchedule,
                problem,
                selectionSpec,
                choices,
                cancellationToken);
            if (!validation.IsValid)
            {
                throw new PersonalSelectionValidationException(validation);
            }

            solutions.Add(new PersonalSelectionSolution(
                choices,
                PersonalSelectionMovementCost.Calculate(problem, fixedSchedule, choices)));
        }

        return new PersonalSelectionSolveResult(
            solverResult,
            solutions
                .OrderBy(solution => solution.MovementCost)
                .ThenBy(SolutionSignature, StringComparer.Ordinal)
                .ToImmutableArray());
    }

    private static ImmutableArray<PersonalSelectionChoice> MaterializeChoices(
        PersonalSelectionCnf cnf,
        ImmutableArray<int> model) =>
        model
            .Where(literal => literal > 0)
            .Select(literal => cnf.Variables[literal - 1].Choice)
            .ToImmutableArray();

    private static void ValidateSolverResult(PersonalSelectionSatResult result, int maxSolutions)
    {
        if (result.Models.Length > maxSolutions)
        {
            throw new InvalidOperationException("Solver returned more models than requested.");
        }
        if (result.Status == PersonalSelectionSatStatus.Feasible && result.Models.IsEmpty)
        {
            throw new InvalidOperationException("Solver reported SAT without a model.");
        }
        if (result.Status == PersonalSelectionSatStatus.Infeasible && !result.Models.IsEmpty)
        {
            throw new InvalidOperationException("Solver reported UNSAT with one or more models.");
        }

        var distinctModels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in result.Models)
        {
            if (!distinctModels.Add(string.Join(',', model)))
            {
                throw new InvalidOperationException("Solver returned a duplicate model.");
            }
        }
    }

    private static string SolutionSignature(PersonalSelectionSolution solution) => string.Join(
        "|",
        solution.Choices.Select(choice => string.Join(
            "~",
            choice.DesiredAssignment.CourseCode,
            choice.DesiredAssignment.TeachingTeamKey,
            choice.LhpCode,
            choice.TeachingUnitKey,
            string.Join(',', choice.SessionTimeSlots.Select(pair => $"{pair.Key}:{pair.Value}")))));
}

public sealed class PersonalSelectionValidationException : Exception
{
    public PersonalSelectionValidationException(PersonalValidationResult validation)
        : base($"Solver result violated personal-selection constraints: {validation.HardViolations.Length} violation(s).")
    {
        Validation = validation;
    }

    public PersonalValidationResult Validation { get; }
}
