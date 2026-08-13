using System.Collections.Immutable;
using Scheduler.Application;
using Scheduler.Domain;
using Scheduler.Infrastructure.NativeSolver;

namespace Scheduler.Diagnostics;

internal static class SelfTestDiagnostics
{
    public static DiagnosticReport Run(DiagnosticsOptions options)
    {
        var checks = new List<DiagnosticCheck>();
        try
        {
            var satCnf = CreateCnf(ImmutableArray.Create(1));
            var satJson = NativeSolverProtocol.SerializeRequest(
                "diagnostics-self-test",
                satCnf,
                maxSolutions: 1,
                TimeSpan.FromSeconds(1));
            var satResult = NativeSolverProtocol.ParseResponse(
                "diagnostics-self-test",
                "{\"protocol_version\":2,\"request_id\":\"diagnostics-self-test\",\"status\":\"feasible\",\"solutions\":[[1]],\"metrics\":{\"elapsed_milliseconds\":0,\"solve_calls\":1},\"message\":\"\"}",
                satCnf,
                maxSolutions: 1);
            var unsatCnf = CreateCnf(ImmutableArray.Create(1), ImmutableArray.Create(-1));
            var unsatResult = NativeSolverProtocol.ParseResponse(
                "diagnostics-self-test-unsat",
                "{\"protocol_version\":2,\"request_id\":\"diagnostics-self-test-unsat\",\"status\":\"infeasible\",\"solutions\":[],\"metrics\":{\"elapsed_milliseconds\":0,\"solve_calls\":1},\"message\":\"\"}",
                unsatCnf,
                maxSolutions: 1);

            var passed = satJson.Contains("\"protocol_version\":2", StringComparison.Ordinal) &&
                satResult.Status == PersonalSelectionSatStatus.Feasible &&
                satResult.Models.Length == 1 &&
                unsatResult.Status == PersonalSelectionSatStatus.Infeasible &&
                unsatResult.Models.IsEmpty;
            checks.Add(DiagnosticsReportFactory.Check(
                options,
                "managed_protocol_contract",
                passed ? DiagnosticStatus.Passed : DiagnosticStatus.Failed,
                passed
                    ? "Managed protocol contract self-test passed."
                    : "Managed protocol contract self-test failed."));
        }
        catch (Exception exception)
        {
            checks.Add(DiagnosticsReportFactory.Check(
                options,
                "managed_protocol_contract",
                DiagnosticStatus.Internal,
                "Managed protocol contract self-test could not complete.",
                detail: DiagnosticsReportFactory.ExceptionDetail(exception)));
        }

        return DiagnosticsReportFactory.Create(options, "self-test", checks);
    }

    private static PersonalSelectionCnf CreateCnf(params ImmutableArray<int>[] clauses)
    {
        var variables = ImmutableArray.Create(
            new PersonalSelectionCnfVariable(
                1,
                0,
                0,
                new PersonalSelectionChoice(
                    new DesiredAnchorAssignment("DIAGNOSTIC", "DIAGNOSTIC"),
                    "DIAGNOSTIC",
                    ImmutableArray<string>.Empty,
                    ImmutableArray<KeyValuePair<string, TimeSlot>>.Empty)));
        var encodedClauses = clauses
            .Select(clause => new PersonalSelectionCnfClause(clause, PersonalSelectionClauseKind.Conflict))
            .ToImmutableArray();
        return new PersonalSelectionCnf(variables, encodedClauses, 0, 0, 0, encodedClauses.Length);
    }
}
