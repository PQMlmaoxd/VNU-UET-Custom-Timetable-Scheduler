using System.Collections.Immutable;
using Scheduler.Application;
using Scheduler.Domain;
using Xunit;

namespace Scheduler.Application.Tests;

public sealed class PersonalSelectionServiceTests
{
    [Fact]
    public async Task SolveAsyncMaterializesAndValidatesEverySolverModel()
    {
        var solver = new FakeSolver(new PersonalSelectionSatResult(
            PersonalSelectionSatStatus.Feasible,
            ImmutableArray.Create(ImmutableArray.Create(1, -2)),
            TimeSpan.FromMilliseconds(3),
            SolveCalls: 1));
        var service = new PersonalSelectionService(solver);

        var result = await service.SolveAsync(
            CreateProblem(),
            ImmutableArray.Create(new DesiredAnchorAssignment("INT2213", "Lecturer A")),
            maxSolutions: 5,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        var solution = Assert.Single(result.Solutions);
        var choice = Assert.Single(solution.Choices);
        Assert.Equal("LHP-A", choice.LhpCode);
        Assert.Equal("session-a", Assert.Single(choice.SessionIds));
        Assert.Equal(RoomMovement.SameRoomCost, solution.MovementCost);
        Assert.NotNull(solver.LastCnf);
    }

    [Fact]
    public async Task SolveAsyncRejectsUnsatisfyingModelFromSolverAdapter()
    {
        var solver = new FakeSolver(new PersonalSelectionSatResult(
            PersonalSelectionSatStatus.Feasible,
            ImmutableArray.Create(ImmutableArray.Create(-1, -2)),
            TimeSpan.Zero,
            SolveCalls: 1));
        var service = new PersonalSelectionService(solver);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SolveAsync(
            CreateProblem(),
            ImmutableArray.Create(new DesiredAnchorAssignment("INT2213", "Lecturer A")),
            maxSolutions: 5,
            TimeSpan.FromSeconds(1),
            CancellationToken.None));
    }

    [Fact]
    public async Task SolveAsyncRanksReturnedSolutionsByMovementCost()
    {
        var solver = new FakeSolver(new PersonalSelectionSatResult(
            PersonalSelectionSatStatus.Feasible,
            ImmutableArray.Create(
                ImmutableArray.Create(1, -2, 3, -4),
                ImmutableArray.Create(-1, 2, -3, 4)),
            TimeSpan.Zero,
            SolveCalls: 3));
        var service = new PersonalSelectionService(solver);

        var result = await service.SolveAsync(
            CreateRankingProblem(),
            ImmutableArray.Create(
                new DesiredAnchorAssignment("COURSE-A", "Lecturer A"),
                new DesiredAnchorAssignment("COURSE-B", "Lecturer B")),
            maxSolutions: 5,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal(2, result.Solutions.Length);
        Assert.Equal(0, result.Solutions[0].MovementCost);
        Assert.Equal(RoomMovement.CrossZoneCost, result.Solutions[1].MovementCost);
    }

    [Fact]
    public async Task SolveAsyncUsesThePreparedCnfInstance()
    {
        var problem = CreateProblem();
        var prepared = PersonalSelectionPreparation.Create(
            problem,
            ImmutableArray.Create(new DesiredAnchorAssignment("INT2213", "Lecturer A")));
        var solver = new FakeSolver(new PersonalSelectionSatResult(
            PersonalSelectionSatStatus.Feasible,
            ImmutableArray.Create(ImmutableArray.Create(1, -2)),
            TimeSpan.Zero,
            SolveCalls: 1));

        await new PersonalSelectionService(solver).SolveAsync(
            problem,
            prepared,
            maxSolutions: 5,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Same(prepared.Cnf, solver.LastCnf);
    }

    [Fact]
    public async Task SolveAsyncRejectsPreparedSelectionFromAnotherProblem()
    {
        var problem = CreateProblem();
        var prepared = PersonalSelectionPreparation.Create(
            CreateProblem(),
            ImmutableArray.Create(new DesiredAnchorAssignment("INT2213", "Lecturer A")));
        var service = new PersonalSelectionService(new FakeSolver(new PersonalSelectionSatResult(
            PersonalSelectionSatStatus.Infeasible,
            [],
            TimeSpan.Zero,
            SolveCalls: 1)));

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.SolveAsync(
            problem,
            prepared,
            maxSolutions: 5,
            TimeSpan.FromSeconds(1),
            CancellationToken.None));

        Assert.Contains("scheduling problem", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PersonalSelectionSatStatus.Feasible, true)]
    [InlineData(PersonalSelectionSatStatus.Infeasible, false)]
    public async Task SolveAsyncRejectsInconsistentSolverStatus(
        PersonalSelectionSatStatus status,
        bool includeModel)
    {
        var models = includeModel
            ? ImmutableArray<ImmutableArray<int>>.Empty
            : ImmutableArray.Create(ImmutableArray.Create(1, -2));
        var service = new PersonalSelectionService(new FakeSolver(new PersonalSelectionSatResult(
            status,
            models,
            TimeSpan.Zero,
            SolveCalls: 1)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SolveAsync(
            CreateProblem(),
            ImmutableArray.Create(new DesiredAnchorAssignment("INT2213", "Lecturer A")),
            maxSolutions: 5,
            TimeSpan.FromSeconds(1),
            CancellationToken.None));
    }

    private static SchedulingProblem CreateProblem()
    {
        var course = new Course("INT2213", "Networks", 4);
        var lecturer = new Lecturer("Lecturer A");
        var cohort = new StudentCohort("K69I-IT1");
        var room = new Room("105-B");
        var firstSlot = new TimeSlot(Day.Monday, Period.Ca1);
        var secondSlot = new TimeSlot(Day.Monday, Period.Ca2);
        return new SchedulingProblem(
            "test",
            "CNTT",
            "test",
            ImmutableArray.Create(
                new Session(
                    "session-a",
                    "LHP-A",
                    course,
                    SessionType.Lt,
                    "CL",
                    60,
                    ImmutableArray.Create(lecturer),
                    ImmutableArray.Create(cohort),
                    firstSlot,
                    room,
                    sourceRow: 5),
                new Session(
                    "session-b",
                    "LHP-B",
                    course,
                    SessionType.Lt,
                    "CL",
                    60,
                    ImmutableArray.Create(lecturer),
                    ImmutableArray.Create(cohort),
                    secondSlot,
                    room,
                    sourceRow: 6)),
            ImmutableArray.Create(firstSlot, secondSlot),
            ImmutableArray.Create(room),
            ImmutableArray<LecturerConstraint>.Empty);
    }

    private static SchedulingProblem CreateRankingProblem()
    {
        var courseA = new Course("COURSE-A", "Course A", 3);
        var courseB = new Course("COURSE-B", "Course B", 3);
        var lecturerA = new Lecturer("Lecturer A");
        var lecturerB = new Lecturer("Lecturer B");
        var cohortA = new StudentCohort("K69A");
        var cohortB = new StudentCohort("K69B");
        var sharedRoom = new Room("101-A");

        return new SchedulingProblem(
            "ranking-test",
            "CNTT",
            "test",
            ImmutableArray.Create(
                SelectionSession("a1", "A-1", courseA, lecturerA, cohortA, Period.Ca1, sharedRoom, 1),
                SelectionSession("a2", "A-2", courseA, lecturerA, cohortA, Period.Ca1, sharedRoom, 2),
                SelectionSession("b1", "B-1", courseB, lecturerB, cohortB, Period.Ca2, sharedRoom, 3),
                SelectionSession("b2", "B-2", courseB, lecturerB, cohortB, Period.Ca2, new Room("101-T"), 4)),
            ImmutableArray<TimeSlot>.Empty,
            ImmutableArray.Create(sharedRoom, new Room("101-T")),
            ImmutableArray<LecturerConstraint>.Empty);
    }

    private static Session SelectionSession(
        string sessionId,
        string lhpCode,
        Course course,
        Lecturer lecturer,
        StudentCohort cohort,
        Period period,
        Room room,
        int sourceRow) => new(
        sessionId,
        lhpCode,
        course,
        SessionType.Lt,
        "CL",
        60,
        ImmutableArray.Create(lecturer),
        ImmutableArray.Create(cohort),
        new TimeSlot(Day.Monday, period),
        room,
        sourceRow: sourceRow);

    private sealed class FakeSolver(PersonalSelectionSatResult result) : IPersonalSelectionSatSolver
    {
        public PersonalSelectionCnf? LastCnf { get; private set; }

        public Task<PersonalSelectionSatResult> SolveAsync(
            PersonalSelectionCnf cnf,
            int maxSolutions,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            LastCnf = cnf;
            return Task.FromResult(result);
        }
    }
}
