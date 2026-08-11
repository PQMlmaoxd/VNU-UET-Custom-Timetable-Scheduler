using System.Collections.Frozen;

namespace Scheduler.Domain;

public enum SessionType
{
    Lt,
    Th,
    LtTh,
    TlTh,
    Bt,
    LtBt,
    Onl,
}

public enum Day
{
    Monday = 2,
    Tuesday = 3,
    Wednesday = 4,
    Thursday = 5,
    Friday = 6,
    Saturday = 7,
}

public enum Period
{
    Ca1,
    Ca2,
    Ca3,
    Ca4,
    Ca1To2,
    Ca2To3,
    Ca3To4,
    Morning,
    Afternoon,
}

public enum LecturerType
{
    Individual,
    Organization,
}

public static class SessionTypeExtensions
{
    public static bool RequiresPhysicalRoom(this SessionType sessionType) => sessionType != SessionType.Onl;

    public static bool IsLab(this SessionType sessionType) =>
        sessionType is SessionType.Th or SessionType.TlTh;

    public static string ToWorkbookValue(this SessionType sessionType) => sessionType switch
    {
        SessionType.Lt => "LT",
        SessionType.Th => "TH",
        SessionType.LtTh => "LT+TH",
        SessionType.TlTh => "TL+TH",
        SessionType.Bt => "BT",
        SessionType.LtBt => "LT+BT",
        SessionType.Onl => "ONL",
        _ => throw new ArgumentOutOfRangeException(nameof(sessionType), sessionType, "Unsupported session type."),
    };
}

public static class PeriodExtensions
{
    private static readonly FrozenSet<int> Period1 = new[] { 1 }.ToFrozenSet();
    private static readonly FrozenSet<int> Period2 = new[] { 2 }.ToFrozenSet();
    private static readonly FrozenSet<int> Period3 = new[] { 3 }.ToFrozenSet();
    private static readonly FrozenSet<int> Period4 = new[] { 4 }.ToFrozenSet();
    private static readonly FrozenSet<int> Period1To2 = new[] { 1, 2 }.ToFrozenSet();
    private static readonly FrozenSet<int> Period2To3 = new[] { 2, 3 }.ToFrozenSet();
    private static readonly FrozenSet<int> Period3To4 = new[] { 3, 4 }.ToFrozenSet();

    public static FrozenSet<int> ToAtomicPeriods(this Period period) => period switch
    {
        Period.Ca1 => Period1,
        Period.Ca2 => Period2,
        Period.Ca3 => Period3,
        Period.Ca4 => Period4,
        Period.Ca1To2 or Period.Morning => Period1To2,
        Period.Ca2To3 => Period2To3,
        Period.Ca3To4 or Period.Afternoon => Period3To4,
        _ => throw new ArgumentOutOfRangeException(nameof(period), period, "Unsupported period."),
    };

    public static bool OverlapsWith(this Period left, Period right) =>
        left.ToAtomicPeriods().Overlaps(right.ToAtomicPeriods());

    public static string ToWorkbookValue(this Period period) => period switch
    {
        Period.Ca1 => "1",
        Period.Ca2 => "2",
        Period.Ca3 => "3",
        Period.Ca4 => "4",
        Period.Ca1To2 => "1-2",
        Period.Ca2To3 => "2-3",
        Period.Ca3To4 => "3-4",
        Period.Morning => "Sáng",
        Period.Afternoon => "Chiều",
        _ => throw new ArgumentOutOfRangeException(nameof(period), period, "Unsupported period."),
    };
}
