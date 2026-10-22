using TranslucentTodo.Models;
using Xunit;

namespace RecurrenceTests;

public class DeadlineTests
{
    private static readonly DateTime SetAt = new(2026, 10, 21, 12, 0, 0);   // Wednesday noon
    private static readonly DateOnly Deadline = new(2026, 10, 25);          // Sunday, which starts 84 h later

    [Fact]
    public void Urgency_IsShareOfTimeElapsedTowardsDeadlineDay()
    {
        Assert.Equal(0, DeadlineMath.Urgency(SetAt, Deadline, SetAt), 3);
        Assert.Equal(0.5, DeadlineMath.Urgency(SetAt, Deadline, SetAt.AddHours(42)), 3);   // half of 84 h
        Assert.Equal(0.75, DeadlineMath.Urgency(SetAt, Deadline, SetAt.AddHours(63)), 3);
    }

    [Fact]
    public void Urgency_IsFullOnDeadlineDayAndWhenOverdue()
    {
        Assert.Equal(1, DeadlineMath.Urgency(SetAt, Deadline, new DateTime(2026, 10, 25, 0, 0, 0)));
        Assert.Equal(1, DeadlineMath.Urgency(SetAt, Deadline, new DateTime(2026, 10, 25, 18, 0, 0)));
        Assert.Equal(1, DeadlineMath.Urgency(SetAt, Deadline, new DateTime(2026, 10, 30, 9, 0, 0)));
    }

    [Fact]
    public void Urgency_DeadlineSetForToday_IsImmediatelyFull()
    {
        Assert.Equal(1, DeadlineMath.Urgency(SetAt, new DateOnly(2026, 10, 21), SetAt));
    }

    [Fact]
    public void Urgency_BeforeCountdownStarts_IsZero()
    {
        Assert.Equal(0, DeadlineMath.Urgency(SetAt, Deadline, SetAt.AddHours(-5)));
    }

    [Fact]
    public void Urgency_CountdownStartingOnDeadlineDay_IsZeroBeforeThatDay()
    {
        // A same-day-deadline recurring task brought back early ("Show now") must not turn red yet.
        var start = new DateTime(2026, 10, 22);
        Assert.Equal(0, DeadlineMath.Urgency(start, new DateOnly(2026, 10, 22), new DateTime(2026, 10, 21, 15, 0, 0)));
        Assert.Equal(1, DeadlineMath.Urgency(start, new DateOnly(2026, 10, 22), new DateTime(2026, 10, 22, 0, 0, 0)));
    }

    [Fact]
    public void Shift_FirstAndLaterCyclesAgree_WhenCountdownStartsOnAppearanceDay()
    {
        // "Mondays" task added Wed Oct 21, deadline Wed Oct 28; it first appears Mon Oct 26.
        var appears = new DateOnly(2026, 10, 26);
        var deadline = new DateOnly(2026, 10, 28);
        // Lead is measured from the appearance day (2 days), so the next Monday's deadline is Wednesday.
        Assert.Equal(new DateOnly(2026, 11, 4), DeadlineMath.Shift(deadline, appears, new DateOnly(2026, 11, 2)));
    }

    [Fact]
    public void Shift_KeepsLeadTime()
    {
        // Deadline 4 days after it was set; the next occurrence on Nov 1 is due Nov 5.
        Assert.Equal(new DateOnly(2026, 11, 5), DeadlineMath.Shift(Deadline, new DateOnly(2026, 10, 21), new DateOnly(2026, 11, 1)));
        // Same-day deadline stays same-day.
        Assert.Equal(new DateOnly(2026, 10, 22), DeadlineMath.Shift(new DateOnly(2026, 10, 21), new DateOnly(2026, 10, 21), new DateOnly(2026, 10, 22)));
    }

    [Fact]
    public void Describe_IsReadable()
    {
        var today = new DateOnly(2026, 10, 21);
        Assert.Equal("Due today", DeadlineMath.Describe(today, today));
        Assert.Equal("Due tomorrow", DeadlineMath.Describe(today.AddDays(1), today));
        Assert.Equal("Due Sunday", DeadlineMath.Describe(today.AddDays(4), today));
        Assert.Equal("Due Fri, Nov 6", DeadlineMath.Describe(new DateOnly(2026, 11, 6), today));
        Assert.Equal("Overdue by 1 day", DeadlineMath.Describe(today.AddDays(-1), today));
        Assert.Equal("Overdue by 3 days", DeadlineMath.Describe(today.AddDays(-3), today));
    }
}
