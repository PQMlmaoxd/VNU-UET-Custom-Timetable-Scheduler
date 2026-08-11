using System.Collections.Immutable;
using Scheduler.Domain;

namespace Scheduler.Application;

public sealed record ConstraintViolation(
    string ConstraintId,
    string ConstraintName,
    string Description,
    ImmutableArray<string> InvolvedSessionIds,
    string Witness)
{
    public override string ToString() =>
        $"[{ConstraintId}] {Description} (sessions: {string.Join(", ", InvolvedSessionIds)})";
}

public sealed record ValidationResult(
    bool IsComplete,
    ImmutableArray<ConstraintViolation> HardViolations,
    ImmutableArray<string> MissingSessionIds)
{
    public bool IsValid => IsComplete && HardViolations.IsEmpty;

    public int ViolationCount => HardViolations.Length;
}

public sealed record SelectionCandidate(
    string LhpCode,
    ImmutableArray<string> SessionIds,
    string TeachingUnitKey = "");

public sealed record SelectionPairSpec(
    DesiredAnchorAssignment DesiredAssignment,
    ImmutableArray<SelectionCandidate> Candidates);

public sealed record PersonalSelectionSpec(ImmutableArray<SelectionPairSpec> Pairs);

public sealed record PersonalSelectionChoice(
    DesiredAnchorAssignment DesiredAssignment,
    string LhpCode,
    ImmutableArray<string> SessionIds,
    ImmutableArray<KeyValuePair<string, TimeSlot>> SessionTimeSlots,
    string TeachingUnitKey = "");

public sealed record PersonalValidationResult(
    int ExpectedChoiceCount,
    int ActualChoiceCount,
    ImmutableArray<ConstraintViolation> HardViolations)
{
    public bool IsValid => ExpectedChoiceCount == ActualChoiceCount && HardViolations.IsEmpty;
}
