using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using Scheduler.Application;
using Scheduler.Domain;
using Scheduler.Infrastructure.NativeSolver;

namespace Scheduler.Diagnostics;

internal static class WorkerDiagnostics
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SolveTimeout = TimeSpan.FromSeconds(5);
    private const int MaximumOutputCharacters = 64 * 1024;

    public static async Task<DiagnosticReport> RunAsync(
        DiagnosticsOptions options,
        string workerPath,
        CancellationToken cancellationToken)
    {
        var checks = new List<DiagnosticCheck>
        {
            DiagnosticsReportFactory.Check(
                options,
                "worker_target",
                DiagnosticStatus.Passed,
                "Worker target is present.",
                path: workerPath),
        };

        if (options.IncludeFileHashes)
        {
            var hash = DiagnosticsReportFactory.TryComputeSha256(workerPath, out var hashError);
            checks.Add(DiagnosticsReportFactory.Check(
                options,
                "worker_file_hash",
                hash is null ? DiagnosticStatus.Failed : DiagnosticStatus.Passed,
                hash is null ? "Worker file hash could not be computed." : "Worker file hash was computed.",
                fileHashes: [new DiagnosticFileHash("worker", null, hash, hash is null ? "unavailable" : "computed")],
                detail: hashError));
        }

        checks.Add(await RunFlagCheckAsync(
            options,
            workerPath,
            "--version",
            "worker_version",
            "Worker version check passed.",
            "Worker version check failed.",
            output => output.Contains("solver-worker protocol=2", StringComparison.Ordinal) &&
                output.Contains("solver=cadical version=", StringComparison.Ordinal),
            cancellationToken));
        checks.Add(await RunFlagCheckAsync(
            options,
            workerPath,
            "--self-test",
            "worker_self_test",
            "Worker self-test passed.",
            "Worker self-test failed.",
            output => output.Contains("solver-worker self-test=ok protocol=2", StringComparison.Ordinal),
            cancellationToken));
        checks.Add(await RunFlagCheckAsync(
            options,
            workerPath,
            "--protocol-self-test",
            "worker_protocol_self_test",
            "Worker protocol self-test passed.",
            "Worker protocol self-test failed.",
            output => output.Contains("solver-worker protocol-self-test=ok protocol=2", StringComparison.Ordinal),
            cancellationToken));

        checks.Add(await RunKnownSolveCheckAsync(
            options,
            workerPath,
            "known_sat",
            expectedStatus: PersonalSelectionSatStatus.Feasible,
            expectedModelCount: 1,
            "Known SAT protocol check passed.",
            "Known SAT protocol check failed.",
            cancellationToken));
        checks.Add(await RunKnownSolveCheckAsync(
            options,
            workerPath,
            "known_unsat",
            expectedStatus: PersonalSelectionSatStatus.Infeasible,
            expectedModelCount: 0,
            "Known UNSAT protocol check passed.",
            "Known UNSAT protocol check failed.",
            cancellationToken));

        return DiagnosticsReportFactory.Create(options, "worker", checks);
    }

    private static async Task<DiagnosticCheck> RunFlagCheckAsync(
        DiagnosticsOptions options,
        string workerPath,
        string argument,
        string checkName,
        string successMessage,
        string failureMessage,
        Func<string, bool> outputPredicate,
        CancellationToken cancellationToken)
    {
        var outcome = await RunProcessAsync(workerPath, argument, CommandTimeout, cancellationToken);
        var passed = outcome.ExitCode == 0 && !outcome.TimedOut && outputPredicate(outcome.StandardOutput);
        var detail = outcome.Detail;
        if (options.VerbosePrivate && (!string.IsNullOrWhiteSpace(outcome.StandardOutput) ||
            !string.IsNullOrWhiteSpace(outcome.StandardError)))
        {
            detail = string.Join(
                " | ",
                new[] { outcome.StandardOutput.Trim(), outcome.StandardError.Trim() }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
        }

        return DiagnosticsReportFactory.Check(
            options,
            checkName,
            passed ? DiagnosticStatus.Passed : DiagnosticStatus.Failed,
            passed ? successMessage : failureMessage,
            metrics: outcome.ExitCode is { } exitCode
                ? new Dictionary<string, long> { ["process_exit_code"] = exitCode }
                : null,
            detail: detail);
    }

    private static async Task<DiagnosticCheck> RunKnownSolveCheckAsync(
        DiagnosticsOptions options,
        string workerPath,
        string checkName,
        PersonalSelectionSatStatus expectedStatus,
        int expectedModelCount,
        string successMessage,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            var cnf = expectedStatus == PersonalSelectionSatStatus.Feasible
                ? CreateCnf(ImmutableArray.Create(1))
                : CreateCnf(ImmutableArray.Create(1), ImmutableArray.Create(-1));
            var result = await new NativeSolverClient(workerPath).SolveAsync(
                cnf,
                maxSolutions: 1,
                timeout: SolveTimeout,
                cancellationToken: cancellationToken);
            var passed = result.Status == expectedStatus && result.Models.Length == expectedModelCount;
            return DiagnosticsReportFactory.Check(
                options,
                checkName,
                passed ? DiagnosticStatus.Passed : DiagnosticStatus.Failed,
                passed ? successMessage : failureMessage,
                metrics: new Dictionary<string, long>
                {
                    ["model_count"] = result.Models.Length,
                    ["solve_calls"] = result.SolveCalls,
                });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return DiagnosticsReportFactory.Check(
                options,
                checkName,
                DiagnosticStatus.Failed,
                failureMessage,
                detail: "Worker solve timed out.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return DiagnosticsReportFactory.Check(
                options,
                checkName,
                DiagnosticStatus.Failed,
                failureMessage,
                detail: DiagnosticsReportFactory.ExceptionDetail(exception));
        }
    }

    private static async Task<WorkerProcessOutcome> RunProcessAsync(
        string executablePath,
        string argument,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory,
            },
        };
        process.StartInfo.ArgumentList.Add(argument);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            if (!process.Start())
            {
                return WorkerProcessOutcome.Failed("Worker process did not start.");
            }

            process.StandardInput.Close();
            var standardOutput = ReadBoundedAsync(process.StandardOutput, timeoutSource.Token);
            var standardError = ReadBoundedAsync(process.StandardError, timeoutSource.Token);
            await Task.WhenAll(
                standardOutput,
                standardError,
                process.WaitForExitAsync(timeoutSource.Token));
            return new WorkerProcessOutcome(
                process.ExitCode,
                false,
                await standardOutput,
                await standardError,
                process.ExitCode == 0 ? null : "Worker process returned a non-zero exit code.");
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            await TerminateProcessAsync(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return WorkerProcessOutcome.Timeout();
        }
        catch (Exception exception)
        {
            await TerminateProcessAsync(process);
            return WorkerProcessOutcome.Failed(DiagnosticsReportFactory.ExceptionDetail(exception));
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var buffer = new char[8 * 1024];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
            {
                return builder.ToString();
            }

            if (builder.Length > MaximumOutputCharacters - read)
            {
                throw new InvalidOperationException("Worker diagnostic output exceeded its bounded limit.");
            }

            builder.Append(buffer, 0, read);
        }
    }

    private static async Task TerminateProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            using var waitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await process.WaitForExitAsync(waitTimeout.Token);
        }
        catch (InvalidOperationException)
        {
        }
        catch (OperationCanceledException)
        {
        }
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

    private sealed record WorkerProcessOutcome(
        int? ExitCode,
        bool TimedOut,
        string StandardOutput,
        string StandardError,
        string? Detail)
    {
        public static WorkerProcessOutcome Failed(string detail) => new(null, false, "", "", detail);

        public static WorkerProcessOutcome Timeout() => new(null, true, "", "", "Worker process timed out.");
    }
}
