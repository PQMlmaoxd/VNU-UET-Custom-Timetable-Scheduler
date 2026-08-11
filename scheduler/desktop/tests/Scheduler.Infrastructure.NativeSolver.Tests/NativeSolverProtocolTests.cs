using System.Collections.Immutable;
using Scheduler.Application;
using Scheduler.Domain;
using Scheduler.Infrastructure.NativeSolver;
using Xunit;

namespace Scheduler.Infrastructure.NativeSolver.Tests;

public sealed class NativeSolverProtocolTests
{
    [Fact]
    public void SerializeRequestUsesWorkerContract()
    {
        var json = NativeSolverProtocol.SerializeRequest("request-1", CreateCnf(), 5, TimeSpan.FromSeconds(30));

        Assert.Equal(
            "{\"protocol_version\":2,\"request_id\":\"request-1\",\"variable_count\":2,\"clauses\":[[1,2],[-1,-2]],\"exactly_one_groups\":[[1,2]],\"max_solutions\":5,\"timeout_milliseconds\":30000}",
            json);
    }

    [Fact]
    public async Task WriteRequestAsyncStreamsTheSameWorkerContract()
    {
        await using var output = new MemoryStream();

        await NativeSolverProtocol.WriteRequestAsync(
            output,
            "request-1",
            CreateCnf(),
            5,
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        output.Position = 0;
        using var reader = new StreamReader(output);
        Assert.Equal(
            "{\"protocol_version\":2,\"request_id\":\"request-1\",\"variable_count\":2,\"clauses\":[[1,2],[-1,-2]],\"exactly_one_groups\":[[1,2]],\"max_solutions\":5,\"timeout_milliseconds\":30000}\n",
            await reader.ReadToEndAsync());
    }

    [Fact]
    public void ParseResponseAcceptsCompleteSatisfyingModels()
    {
        var result = NativeSolverProtocol.ParseResponse(
            "request-1",
            "{\"protocol_version\":2,\"request_id\":\"request-1\",\"status\":\"feasible\",\"solutions\":[[1,-2],[-1,2]],\"metrics\":{\"elapsed_milliseconds\":8,\"solve_calls\":2},\"message\":\"\"}",
            CreateCnf(),
            5);

        Assert.Equal(PersonalSelectionSatStatus.Feasible, result.Status);
        Assert.Equal(2, result.Models.Length);
        Assert.Equal(TimeSpan.FromMilliseconds(8), result.Elapsed);
        Assert.Equal(2, result.SolveCalls);
    }

    [Theory]
    [InlineData("{\"protocol_version\":2,\"request_id\":\"request-1\",\"status\":\"feasible\",\"solutions\":[[1,2]],\"metrics\":{\"elapsed_milliseconds\":0,\"solve_calls\":1},\"message\":\"\"}")]
    [InlineData("{\"protocol_version\":2,\"request_id\":\"request-1\",\"status\":\"infeasible\",\"solutions\":[[1,-2]],\"metrics\":{\"elapsed_milliseconds\":0,\"solve_calls\":1},\"message\":\"\"}")]
    [InlineData("{\"protocol_version\":2,\"request_id\":\"request-1\",\"status\":\"feasible\",\"solutions\":[[1,-2],[1,-2]],\"metrics\":{\"elapsed_milliseconds\":0,\"solve_calls\":2},\"message\":\"\"}")]
    public void ParseResponseRejectsInvalidWorkerModels(string responseJson)
    {
        Assert.Throws<NativeSolverProtocolException>(() =>
            NativeSolverProtocol.ParseResponse("request-1", responseJson, CreateCnf(), 5));
    }

    [Fact]
    public void ParseResponseRejectsMismatchedRequestId()
    {
        const string responseJson = "{\"protocol_version\":2,\"request_id\":\"other\",\"status\":\"infeasible\",\"solutions\":[],\"metrics\":{\"elapsed_milliseconds\":0,\"solve_calls\":1},\"message\":\"\"}";

        Assert.Throws<NativeSolverProtocolException>(() =>
            NativeSolverProtocol.ParseResponse("request-1", responseJson, CreateCnf(), 5));
    }

    [Fact]
    public void SerializeRequestRejectsRequestIdsAboveWorkerLimit()
    {
        var requestId = new string('x', NativeSolverProtocol.MaximumRequestIdBytes + 1);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NativeSolverProtocol.SerializeRequest(requestId, CreateCnf(), 5, TimeSpan.FromSeconds(30)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ValidateRequestRejectsTimeoutBeforeProcessStartup(int milliseconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeSolverProtocol.ValidateRequest(
            "request-1",
            CreateCnf(),
            5,
            TimeSpan.FromMilliseconds(milliseconds)));
    }

    private static PersonalSelectionCnf CreateCnf() => new(
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
