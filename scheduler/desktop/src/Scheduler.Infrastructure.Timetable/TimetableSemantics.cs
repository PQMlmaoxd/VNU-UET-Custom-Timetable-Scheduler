using System.Collections.Immutable;
using System.Globalization;
using Scheduler.Domain;

namespace Scheduler.Infrastructure.Timetable;

public static class TimetableSemantics
{
    public static ImmutableArray<TimeSlot> BuildAllTimeSlots() =>
        Enum.GetValues<Day>()
            .SelectMany(day => new[] { Period.Ca1, Period.Ca2, Period.Ca3, Period.Ca4 }
                .Select(period => new TimeSlot(day, period)))
            .ToImmutableArray();

    public static bool IsPhysicalEducationSection(string classCode) =>
        classCode is "Tất cả" or "Thời khóa biểu các học phần Giáo dục thể chất";

    public static bool IsCnttClass(string classCode) =>
        Split(classCode).Any(part => DepartmentLetterFromPart(part) == "I");

    public static ImmutableArray<string> Split(string value) =>
        value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToImmutableArray();

    public static bool TryParseSessionType(string value, out SessionType sessionType)
    {
        var normalized = value.Trim().ToUpperInvariant().Replace(" ", string.Empty);
        sessionType = normalized switch
        {
            "LT" => SessionType.Lt,
            "TH" => SessionType.Th,
            "LT+TH" => SessionType.LtTh,
            "TL+TH" => SessionType.TlTh,
            "BT" => SessionType.Bt,
            "LT+BT" => SessionType.LtBt,
            "ONL" => SessionType.Onl,
            _ => default,
        };
        return normalized is "LT" or "TH" or "LT+TH" or "TL+TH" or "BT" or "LT+BT" or "ONL";
    }

    public static bool TryParseDay(string value, out Day day)
    {
        var normalized = value.Trim();
        if (normalized.StartsWith("Thứ", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[3..].Trim();
        }

        if (int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericDay) &&
            Enum.IsDefined((Day)numericDay))
        {
            day = (Day)numericDay;
            return true;
        }

        day = default;
        return false;
    }

    public static bool TryParsePeriod(string value, out Period period)
    {
        var normalized = value.Trim()
            .Replace(" ", string.Empty)
            .Replace('–', '-')
            .Replace('—', '-');
        period = normalized switch
        {
            "1" => Period.Ca1,
            "2" => Period.Ca2,
            "3" => Period.Ca3,
            "4" => Period.Ca4,
            "1-2" => Period.Ca1To2,
            "2-3" => Period.Ca2To3,
            "3-4" => Period.Ca3To4,
            "Sáng" => Period.Morning,
            "Chiều" => Period.Afternoon,
            _ => default,
        };
        return normalized is "1" or "2" or "3" or "4" or "1-2" or "2-3" or "3-4" or "Sáng" or "Chiều";
    }

    public static ImmutableArray<LecturerConstraint> BuildLecturerBlocks(
        IEnumerable<Session> otherDepartmentSessions,
        IEnumerable<Session> departmentSessions)
    {
        var departmentLecturers = departmentSessions
            .SelectMany(session => session.IndividualLecturers)
            .Select(lecturer => lecturer.Name)
            .ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<(string Lecturer, Day Day, Period Period)>();
        var blocks = new List<LecturerConstraint>();

        foreach (var session in otherDepartmentSessions)
        {
            if (session.TimeSlot is null)
            {
                continue;
            }

            foreach (var lecturer in session.IndividualLecturers)
            {
                var key = (lecturer.Name, session.TimeSlot.Day, session.TimeSlot.Period);
                if (departmentLecturers.Contains(lecturer.Name) && seen.Add(key))
                {
                    blocks.Add(new LecturerConstraint(
                        lecturer,
                        session.TimeSlot,
                        $"Teaching {session.LhpCode} outside target department"));
                }
            }
        }

        return blocks.ToImmutableArray();
    }

    private static string? DepartmentLetterFromPart(string classCode)
    {
        if (!classCode.StartsWith('K'))
        {
            return null;
        }

        foreach (var character in classCode)
        {
            if (char.IsLetter(character) && character != 'K')
            {
                return character.ToString();
            }
        }

        return null;
    }
}
