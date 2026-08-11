using System.Collections.Immutable;

namespace Scheduler.Application;

/// <summary>
/// Transport-neutral result of solving the deterministic personal-selection CNF.
/// Model literals are complete assignments over every CNF variable.
/// </summary>
public sealed record PersonalSelectionSatResult(
    PersonalSelectionSatStatus Status,
    ImmutableArray<ImmutableArray<int>> Models,
    TimeSpan Elapsed,
    int SolveCalls);

public enum PersonalSelectionSatStatus
{
    Feasible,
    Infeasible,
    TimedOut,
}

/// <summary>
/// Isolates the application layer from a particular SAT library or process protocol.
/// </summary>
public interface IPersonalSelectionSatSolver
{
    Task<PersonalSelectionSatResult> SolveAsync(
        PersonalSelectionCnf cnf,
        int maxSolutions,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}
