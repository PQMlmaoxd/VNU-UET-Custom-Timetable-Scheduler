using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Scheduler.Application;

namespace Scheduler.Infrastructure.NativeSolver;

/// <summary>
/// The versioned, one-request NDJSON contract shared with native/SolverWorker.
/// It validates every returned assignment before application code can use it.
/// </summary>
public static class NativeSolverProtocol
{
    public const int Version = 2;
    public const int MaximumVariables = 2_000_000;
    public const int MaximumClauses = 2_000_000;
    public const int MaximumLiterals = 10_000_000;
    public const int MaximumRequestBytes = 64 * 1024 * 1024;
    public const int MaximumResponseBytes = 8 * 1024 * 1024;
    public const int MaximumRequestIdBytes = 128;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static string SerializeRequest(
        string requestId,
        PersonalSelectionCnf cnf,
        int maxSolutions,
        TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentNullException.ThrowIfNull(cnf);
        ValidateRequest(requestId, cnf, maxSolutions, timeout);

        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        WriteRequest(writer, requestId, cnf, maxSolutions, timeout);
        writer.Flush();
        if (buffer.WrittenCount > MaximumRequestBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(cnf), "CNF request exceeds the native protocol size limit.");
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// Streams the request directly to the worker to avoid transient clause-array and
    /// JSON-string copies for large personal-selection CNFs.
    /// </summary>
    public static async Task WriteRequestAsync(
        Stream output,
        string requestId,
        PersonalSelectionCnf cnf,
        int maxSolutions,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentNullException.ThrowIfNull(cnf);
        ValidateRequest(requestId, cnf, maxSolutions, timeout);

        using var writer = new Utf8JsonWriter(output);
        WriteRequest(writer, requestId, cnf, maxSolutions, timeout);
        await writer.FlushAsync(cancellationToken);
        await output.WriteAsync("\n"u8.ToArray(), cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    public static PersonalSelectionSatResult ParseResponse(
        string requestId,
        string responseJson,
        PersonalSelectionCnf cnf,
        int maxSolutions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(responseJson);
        ArgumentNullException.ThrowIfNull(cnf);

        WorkerResponse response;
        try
        {
            response = JsonSerializer.Deserialize<WorkerResponse>(responseJson, SerializerOptions)
                ?? throw new NativeSolverProtocolException("Solver worker returned an empty JSON response.");
        }
        catch (JsonException exception)
        {
            throw new NativeSolverProtocolException("Solver worker returned malformed JSON.", exception);
        }

        if (response.ProtocolVersion != Version)
        {
            throw new NativeSolverProtocolException($"Unsupported solver protocol version {response.ProtocolVersion}.");
        }

        if (!string.Equals(response.RequestId, requestId, StringComparison.Ordinal))
        {
            throw new NativeSolverProtocolException("Solver worker response request_id does not match the request.");
        }

        if (response.Metrics is null || response.Metrics.ElapsedMilliseconds < 0 || response.Metrics.SolveCalls < 0)
        {
            throw new NativeSolverProtocolException("Solver worker returned invalid metrics.");
        }

        var status = response.Status switch
        {
            "feasible" => PersonalSelectionSatStatus.Feasible,
            "infeasible" => PersonalSelectionSatStatus.Infeasible,
            "timed_out" => PersonalSelectionSatStatus.TimedOut,
            "invalid_request" or "internal_error" => throw new NativeSolverProtocolException(
                $"Solver worker rejected a valid request: {response.Message}"),
            _ => throw new NativeSolverProtocolException($"Solver worker returned unknown status '{response.Status}'."),
        };

        if (response.Solutions is null || response.Solutions.Length > maxSolutions)
        {
            throw new NativeSolverProtocolException("Solver worker returned an invalid solution count.");
        }

        if (status == PersonalSelectionSatStatus.Feasible && response.Solutions.Length == 0)
        {
            throw new NativeSolverProtocolException("Solver worker reported feasible without a solution.");
        }

        if (status == PersonalSelectionSatStatus.Infeasible && response.Solutions.Length > 0)
        {
            throw new NativeSolverProtocolException("Solver worker reported infeasible with solutions.");
        }

        var models = ImmutableArray.CreateBuilder<ImmutableArray<int>>(response.Solutions.Length);
        var seenModels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var workerModel in response.Solutions)
        {
            if (workerModel is null)
            {
                throw new NativeSolverProtocolException("Solver worker returned a null model.");
            }

            var model = workerModel.ToImmutableArray();
            try
            {
                PersonalSelectionCnfModelValidator.Validate(cnf, model);
            }
            catch (InvalidOperationException exception)
            {
                throw new NativeSolverProtocolException("Solver worker returned an invalid model.", exception);
            }

            var canonicalModel = string.Join(',', model);
            if (!seenModels.Add(canonicalModel))
            {
                throw new NativeSolverProtocolException("Solver worker returned a duplicate model.");
            }

            models.Add(model);
        }

        return new PersonalSelectionSatResult(
            status,
            models.ToImmutable(),
            TimeSpan.FromMilliseconds(response.Metrics.ElapsedMilliseconds),
            response.Metrics.SolveCalls);
    }

    public static void ValidateRequest(
        string requestId,
        PersonalSelectionCnf cnf,
        int maxSolutions,
        TimeSpan timeout)
    {
        if (cnf.VariableCount is < 0 or > MaximumVariables)
        {
            throw new ArgumentOutOfRangeException(nameof(cnf), "CNF variable count exceeds the native protocol limit.");
        }

        var requestIdBytes = Encoding.UTF8.GetByteCount(requestId);
        if (requestIdBytes is < 1 or > MaximumRequestIdBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(requestId), "requestId must contain between 1 and 128 UTF-8 bytes.");
        }

        if (cnf.ClauseCount > MaximumClauses)
        {
            throw new ArgumentOutOfRangeException(nameof(cnf), "CNF clause count exceeds the native protocol limit.");
        }

        var clauseLiterals = 0L;
        foreach (var clause in cnf.Clauses)
        {
            clauseLiterals += clause.Literals.Length;
            if (clauseLiterals > MaximumLiterals)
            {
                throw new ArgumentOutOfRangeException(nameof(cnf), "CNF literal count exceeds the native protocol limit.");
            }
        }

        var groups = cnf.ExactlyOneGroups;
        if (groups.Length > MaximumClauses)
        {
            throw new ArgumentOutOfRangeException(nameof(cnf), "Exactly-one group count exceeds the native protocol limit.");
        }

        var groupLiterals = 0L;
        foreach (var group in groups)
        {
            groupLiterals += group.Length;
            if (groupLiterals > MaximumLiterals)
            {
                throw new ArgumentOutOfRangeException(nameof(cnf), "Exactly-one literal count exceeds the native protocol limit.");
            }
        }

        if (maxSolutions is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSolutions), "maxSolutions must be between 1 and 100.");
        }

        if (timeout < TimeSpan.FromMilliseconds(1) || timeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "timeout must be between 1 millisecond and 5 minutes.");
        }

        var estimatedBytes = EstimateRequestBytes(cnf, groups, maxSolutions, timeout, requestIdBytes);
        if (estimatedBytes > MaximumRequestBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(cnf), "CNF request exceeds the native protocol size limit.");
        }

        var estimatedResponseBytes = EstimateResponseBytes(cnf.VariableCount, maxSolutions);
        if (estimatedResponseBytes > MaximumResponseBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cnf),
                "The requested model set exceeds the native response size limit.");
        }
    }

    private static long EstimateRequestBytes(
        PersonalSelectionCnf cnf,
        ImmutableArray<ImmutableArray<int>> groups,
        int maxSolutions,
        TimeSpan timeout,
        int requestIdBytes)
    {
        var bytes = 512L + requestIdBytes * 6L;
        bytes += Digits(cnf.VariableCount) + Digits(maxSolutions)
            + Digits(checked((int)Math.Ceiling(timeout.TotalMilliseconds)));

        foreach (var clause in cnf.Clauses)
        {
            bytes += 4L + clause.Literals.Length * 14L;
        }

        foreach (var group in groups)
        {
            bytes += 4L + group.Length * 14L;
        }

        return bytes;
    }

    private static long EstimateResponseBytes(int variableCount, int maxSolutions)
    {
        // A signed literal, comma and JSON overhead are conservatively bounded
        // at twelve bytes. This keeps the client buffer and worker output bound
        // consistent before a native process is started.
        return checked(512L + (long)variableCount * 12L * maxSolutions);
    }

    private static int Digits(int value) =>
        value.ToString(CultureInfo.InvariantCulture).Length;

    private static void WriteRequest(
        Utf8JsonWriter writer,
        string requestId,
        PersonalSelectionCnf cnf,
        int maxSolutions,
        TimeSpan timeout)
    {
        writer.WriteStartObject();
        writer.WriteNumber("protocol_version", Version);
        writer.WriteString("request_id", requestId);
        writer.WriteNumber("variable_count", cnf.VariableCount);
        writer.WritePropertyName("clauses");
        writer.WriteStartArray();
        foreach (var clause in cnf.Clauses)
        {
            writer.WriteStartArray();
            foreach (var literal in clause.Literals)
            {
                writer.WriteNumberValue(literal);
            }

            writer.WriteEndArray();
        }

        writer.WriteEndArray();
        writer.WritePropertyName("exactly_one_groups");
        writer.WriteStartArray();
        foreach (var group in cnf.ExactlyOneGroups)
        {
            writer.WriteStartArray();
            foreach (var variableId in group)
            {
                writer.WriteNumberValue(variableId);
            }

            writer.WriteEndArray();
        }

        writer.WriteEndArray();
        writer.WriteNumber("max_solutions", maxSolutions);
        writer.WriteNumber("timeout_milliseconds", checked((int)Math.Ceiling(timeout.TotalMilliseconds)));
        writer.WriteEndObject();
    }

    private sealed record WorkerResponse(
        int ProtocolVersion,
        string RequestId,
        string Status,
        int[][] Solutions,
        WorkerMetrics? Metrics,
        string Message);

    private sealed record WorkerMetrics(long ElapsedMilliseconds, int SolveCalls);
}

public sealed class NativeSolverProtocolException : Exception
{
    public NativeSolverProtocolException(string message)
        : base(message)
    {
    }

    public NativeSolverProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
