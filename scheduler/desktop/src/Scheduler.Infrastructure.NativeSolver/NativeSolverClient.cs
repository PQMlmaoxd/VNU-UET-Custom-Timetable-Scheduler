using System.Diagnostics;
using System.Text;
using Scheduler.Application;

namespace Scheduler.Infrastructure.NativeSolver;

/// <summary>
/// Starts one isolated SolverWorker process per request. A native crash or a
/// non-cooperative timeout cannot take down the desktop host process.
/// </summary>
public sealed class NativeSolverClient : IPersonalSelectionSatSolver
{
    private static readonly TimeSpan WorkerShutdownGrace = TimeSpan.FromSeconds(2);
    // The desktop asks for at most five personal-selection models. A much larger
    // response is malformed or unsuitable for interactive rendering.
    private const int MaximumStandardOutputCharacters = 8 * 1024 * 1024;
    private const int MaximumStandardErrorCharacters = 64 * 1024;

    private readonly string workerExecutablePath;

    public NativeSolverClient(string workerExecutablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerExecutablePath);
        this.workerExecutablePath = Path.GetFullPath(workerExecutablePath);
    }

    public async Task<PersonalSelectionSatResult> SolveAsync(
        PersonalSelectionCnf cnf,
        int maxSolutions,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cnf);
        if (!File.Exists(workerExecutablePath))
        {
            throw new FileNotFoundException("Solver worker executable was not found.", workerExecutablePath);
        }

        var requestId = Guid.NewGuid().ToString("N");
        NativeSolverProtocol.ValidateRequest(requestId, cnf, maxSolutions, timeout);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = workerExecutablePath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };

        using var workerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        workerTimeout.CancelAfter(timeout + WorkerShutdownGrace);
        try
        {
            if (!process.Start())
            {
                throw new NativeSolverProcessException("Solver worker process did not start.");
            }

            await NativeSolverProtocol.WriteRequestAsync(
                process.StandardInput.BaseStream,
                requestId,
                cnf,
                maxSolutions,
                timeout,
                workerTimeout.Token);
            process.StandardInput.Close();

            var standardOutput = ReadBoundedAsync(
                process.StandardOutput,
                MaximumStandardOutputCharacters,
                "standard output",
                workerTimeout.Token);
            var standardError = ReadBoundedAsync(
                process.StandardError,
                MaximumStandardErrorCharacters,
                "standard error",
                workerTimeout.Token);
            await Task.WhenAll(standardOutput, standardError, process.WaitForExitAsync(workerTimeout.Token));
            var responseJson = await standardOutput;
            var errorText = await standardError;

            if (process.ExitCode != 0)
            {
                throw new NativeSolverProcessException(
                    $"Solver worker exited with code {process.ExitCode}: {Abbreviate(errorText)}");
            }

            return NativeSolverProtocol.ParseResponse(requestId, responseJson, cnf, maxSolutions);
        }
        catch (OperationCanceledException) when (workerTimeout.IsCancellationRequested)
        {
            await TerminateProcessAsync(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new TimeoutException("Solver worker did not stop before the timeout grace period.");
        }
        catch
        {
            await TerminateProcessAsync(process);
            throw;
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

            using var waitTimeout = new CancellationTokenSource(WorkerShutdownGrace);
            await process.WaitForExitAsync(waitTimeout.Token);
        }
        catch (InvalidOperationException)
        {
            // Process startup failed before Windows assigned a process handle.
        }
        catch (OperationCanceledException)
        {
            // A non-cooperative native process remains isolated; the caller gets
            // the original timeout/process error rather than hanging shutdown.
        }
    }

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        int maximumCharacters,
        string streamName,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(Math.Min(maximumCharacters, 8 * 1024));
        var buffer = new char[8 * 1024];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
            {
                return builder.ToString();
            }

            if (builder.Length > maximumCharacters - read)
            {
                throw new NativeSolverProcessException(
                    $"Solver worker {streamName} exceeded the {maximumCharacters} character limit.");
            }

            builder.Append(buffer, 0, read);
        }
    }

    private static string Abbreviate(string value) =>
        value.Length <= 500 ? value.Trim() : string.Concat(value.AsSpan(0, 500), "...");
}

public sealed class NativeSolverProcessException : Exception
{
    public NativeSolverProcessException(string message)
        : base(message)
    {
    }
}
