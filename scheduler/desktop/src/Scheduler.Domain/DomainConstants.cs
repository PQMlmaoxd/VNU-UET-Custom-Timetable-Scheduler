using System.Collections.Frozen;

namespace Scheduler.Domain;

public static class DomainConstants
{
    public static readonly FrozenSet<string> OrganizationalUnits = new[]
    {
        "Viện Khảo thí",
        "Viện Cơ",
        "TT GDTC",
        "Khoa CNTT",
        "PTN Hòa Lạc",
        "PTN K.CHKT",
        "PTN K.VLKT",
        "PTN Khoa",
        "Viện Di truyền",
        "Xưởng Cơ khí",
    }.ToFrozenSet(StringComparer.Ordinal);

    public static readonly FrozenSet<string> VirtualRooms = new[]
    {
        "ONL",
        "Khoa CNTT",
        "PTN Hòa Lạc",
        "PTN K.CHKT",
        "PTN K.VLKT",
        "PTN Khoa",
        "Viện Di truyền",
        "Xưởng Cơ khí",
        "Tiết 1-2",
        "Tiết 3-4",
        "Tiết 7-8",
        "Tiết 9-10",
    }.ToFrozenSet(StringComparer.Ordinal);
}
