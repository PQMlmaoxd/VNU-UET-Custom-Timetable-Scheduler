using Scheduler.Domain;
using Xunit;

namespace Scheduler.Domain.Tests;

public sealed class PeriodTests
{
    [Theory]
    [InlineData(Period.Ca1, Period.Ca1, true)]
    [InlineData(Period.Ca1, Period.Ca2, false)]
    [InlineData(Period.Ca1To2, Period.Ca1, true)]
    [InlineData(Period.Ca1To2, Period.Ca2, true)]
    [InlineData(Period.Ca1To2, Period.Ca3, false)]
    [InlineData(Period.Ca1To2, Period.Ca2To3, true)]
    [InlineData(Period.Ca1To2, Period.Ca3To4, false)]
    [InlineData(Period.Morning, Period.Ca2, true)]
    [InlineData(Period.Morning, Period.Ca3, false)]
    [InlineData(Period.Afternoon, Period.Ca3, true)]
    [InlineData(Period.Afternoon, Period.Ca2, false)]
    public void OverlapsWithMatchesAtomicPeriodSemantics(Period left, Period right, bool expected)
    {
        Assert.Equal(expected, left.OverlapsWith(right));
        Assert.Equal(expected, right.OverlapsWith(left));
    }

    [Fact]
    public void TimeSlotOverlapRequiresMatchingDay()
    {
        var monday = new TimeSlot(Day.Monday, Period.Ca1To2);
        var sameDay = new TimeSlot(Day.Monday, Period.Ca2);
        var otherDay = new TimeSlot(Day.Tuesday, Period.Ca2);

        Assert.True(monday.OverlapsWith(sameDay));
        Assert.False(monday.OverlapsWith(otherDay));
    }
}
