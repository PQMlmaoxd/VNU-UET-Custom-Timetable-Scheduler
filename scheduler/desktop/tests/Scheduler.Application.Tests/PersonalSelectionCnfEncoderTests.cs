using System.Collections.Immutable;
using Scheduler.Application;
using Scheduler.Domain;
using Xunit;

namespace Scheduler.Application.Tests;

public sealed class PersonalSelectionCnfEncoderTests
{
    [Fact]
    public void EncodeMatchesTheCompatibilityCnfForTwoPairs()
    {
        var problem = Problem();

        var cnf = PersonalSelectionCnfEncoder.Encode(problem, Specification());

        Assert.Equal(4, cnf.VariableCount);
        Assert.Equal(5, cnf.ClauseCount);
        Assert.Equal(2, cnf.AtLeastOneClauseCount);
        Assert.Equal(2, cnf.AtMostOneClauseCount);
        Assert.Equal(1, cnf.ConflictClauseCount);
        Assert.Equal(
            "1,2|-1,-2|3,4|-3,-4|-1,-3",
            string.Join('|', cnf.Clauses.Select(clause => string.Join(',', clause.Literals))));
        Assert.Equal(
            new[]
            {
                PersonalSelectionClauseKind.AtLeastOne,
                PersonalSelectionClauseKind.AtMostOne,
                PersonalSelectionClauseKind.AtLeastOne,
                PersonalSelectionClauseKind.AtMostOne,
                PersonalSelectionClauseKind.Conflict,
            },
            cnf.Clauses.Select(clause => clause.Kind));
        Assert.Equal("1,2|3,4", string.Join('|', cnf.ExactlyOneGroups.Select(group => string.Join(',', group))));

        var dimacs = cnf.ToDimacs();
        Assert.Contains("p cnf 4 5", dimacs, StringComparison.Ordinal);
        Assert.Contains("c var 1 pair=0 candidate=0 lhp=LHP-A1 sessions=a1", dimacs, StringComparison.Ordinal);
        Assert.Contains("c var 4 pair=1 candidate=1 lhp=LHP-B2 sessions=b2", dimacs, StringComparison.Ordinal);
    }

    [Fact]
    public void EncodeAddsConflictsForSharedSessionsAndOverlappingTimeslots()
    {
        var course = new Course("INT0001", "Course", 3);
        var lecturer = new Lecturer("Prof A");
        var cohort = new StudentCohort("K69I-IT1");
        var first = Session("first", "A", course, lecturer, cohort, new TimeSlot(Day.Monday, Period.Ca1));
        var second = Session("second", "B", course, lecturer, cohort, new TimeSlot(Day.Monday, Period.Ca1));
        var problem = CreateProblem(first, second);
        var desired = new DesiredAnchorAssignment(course.Code, lecturer.Name, course.Name);
        var spec = new PersonalSelectionSpec(ImmutableArray.Create(
            new SelectionPairSpec(desired, ImmutableArray.Create(new SelectionCandidate("A", ImmutableArray.Create("first")))),
            new SelectionPairSpec(desired, ImmutableArray.Create(
                new SelectionCandidate("Shared", ImmutableArray.Create("first")),
                new SelectionCandidate("Overlapping", ImmutableArray.Create("second"))))));

        var cnf = PersonalSelectionCnfEncoder.Encode(problem, spec);

        Assert.Equal(2, cnf.ConflictClauseCount);
        Assert.Equal("1|2,3|-2,-3|-1,-2|-1,-3", string.Join('|', cnf.Clauses.Select(clause => string.Join(',', clause.Literals))));
    }

    [Fact]
    public void EncodeDeduplicatesConflictsFoundThroughMultipleSessionAndTimeslotIndexes()
    {
        var courseA = new Course("INT0001", "Course A", 3);
        var courseB = new Course("INT0002", "Course B", 3);
        var lecturerA = new Lecturer("Prof A");
        var lecturerB = new Lecturer("Prof B");
        var cohort = new StudentCohort("K69I-IT1");
        var first = Session("a1", "A", courseA, lecturerA, cohort, new TimeSlot(Day.Monday, Period.Ca1));
        var second = Session("a2", "A", courseA, lecturerA, cohort, new TimeSlot(Day.Monday, Period.Ca2));
        var overlappingFirst = Session("b1", "B", courseB, lecturerB, cohort, new TimeSlot(Day.Monday, Period.Morning));
        var overlappingSecond = Session("b2", "B", courseB, lecturerB, cohort, new TimeSlot(Day.Monday, Period.Ca2));
        var problem = CreateProblem(first, second, overlappingFirst, overlappingSecond);
        var spec = new PersonalSelectionSpec(ImmutableArray.Create(
            new SelectionPairSpec(
                new DesiredAnchorAssignment(courseA.Code, lecturerA.Name, courseA.Name),
                ImmutableArray.Create(new SelectionCandidate("A", ImmutableArray.Create("a1", "a2")))),
            new SelectionPairSpec(
                new DesiredAnchorAssignment(courseB.Code, lecturerB.Name, courseB.Name),
                ImmutableArray.Create(new SelectionCandidate("B", ImmutableArray.Create("b1", "b2"))))));

        var cnf = PersonalSelectionCnfEncoder.Encode(problem, spec);

        Assert.Equal(1, cnf.ConflictClauseCount);
        Assert.Equal("1|2|-1,-2", string.Join('|', cnf.Clauses.Select(clause => string.Join(',', clause.Literals))));
    }

    [Fact]
    public void EncodeRetainsUnknownSessionIdsForTheIndependentValidator()
    {
        var problem = Problem();
        var desired = new DesiredAnchorAssignment("INT0001", "Prof A", "Course A");
        var spec = new PersonalSelectionSpec(ImmutableArray.Create(
            new SelectionPairSpec(desired, ImmutableArray.Create(
                new SelectionCandidate("Unknown", ImmutableArray.Create("missing"))))));

        var cnf = PersonalSelectionCnfEncoder.Encode(problem, spec);

        var variable = Assert.Single(cnf.Variables);
        Assert.Equal("missing", Assert.Single(variable.Choice.SessionIds));
        Assert.Empty(variable.Choice.SessionTimeSlots);
        Assert.Equal("1", string.Join(',', Assert.Single(cnf.Clauses).Literals));
    }

    private static SchedulingProblem Problem()
    {
        var courseA = new Course("INT0001", "Course A", 3, 30, 0);
        var courseB = new Course("INT0002", "Course B", 3, 30, 0);
        var lecturerA = new Lecturer("Prof A");
        var lecturerB = new Lecturer("Prof B");
        var cohort = new StudentCohort("K69I-IT1");
        return CreateProblem(
            Session("a1", "LHP-A1", courseA, lecturerA, cohort, new TimeSlot(Day.Monday, Period.Ca1)),
            Session("a2", "LHP-A2", courseA, lecturerA, cohort, new TimeSlot(Day.Monday, Period.Ca2)),
            Session("b1", "LHP-B1", courseB, lecturerB, cohort, new TimeSlot(Day.Monday, Period.Ca1)),
            Session("b2", "LHP-B2", courseB, lecturerB, cohort, new TimeSlot(Day.Monday, Period.Ca3)));
    }

    private static PersonalSelectionSpec Specification() =>
        new(ImmutableArray.Create(
            new SelectionPairSpec(
                new DesiredAnchorAssignment("INT0001", "Prof A", "Course A"),
                ImmutableArray.Create(
                    new SelectionCandidate("LHP-A1", ImmutableArray.Create("a1")),
                    new SelectionCandidate("LHP-A2", ImmutableArray.Create("a2")))),
            new SelectionPairSpec(
                new DesiredAnchorAssignment("INT0002", "Prof B", "Course B"),
                ImmutableArray.Create(
                    new SelectionCandidate("LHP-B1", ImmutableArray.Create("b1")),
                    new SelectionCandidate("LHP-B2", ImmutableArray.Create("b2"))))));

    private static SchedulingProblem CreateProblem(params Session[] sessions) =>
        new(
            "test",
            "ALL",
            "test",
            sessions.ToImmutableArray(),
            ImmutableArray.Create(
                new TimeSlot(Day.Monday, Period.Ca1),
                new TimeSlot(Day.Monday, Period.Ca2),
                new TimeSlot(Day.Monday, Period.Ca3)),
            ImmutableArray.Create(new Room("101-A")),
            ImmutableArray<LecturerConstraint>.Empty);

    private static Session Session(
        string id,
        string lhpCode,
        Course course,
        Lecturer lecturer,
        StudentCohort cohort,
        TimeSlot timeSlot) =>
        new(
            id,
            lhpCode,
            course,
            SessionType.Lt,
            "CL",
            30,
            ImmutableArray.Create(lecturer),
            ImmutableArray.Create(cohort),
            timeSlot,
            new Room("101-A"));
}
