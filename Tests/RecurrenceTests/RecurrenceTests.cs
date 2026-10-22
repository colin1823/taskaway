using TranslucentTodo.Models;
using Xunit;

namespace RecurrenceTests;

public class RecurrenceTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    [Fact]
    public void Daily_IsNextDay()
    {
        var r = new Recurrence { Kind = RecurrenceKind.Daily };
        Assert.Equal(D(2026, 10, 22), r.Next(D(2026, 10, 21)));
        Assert.Equal(D(2027, 1, 1), r.Next(D(2026, 12, 31)));
    }

    [Fact]
    public void Weekdays_WrapsToNextWeek()
    {
        // 2026-10-23 is a Friday.
        var r = new Recurrence { Kind = RecurrenceKind.Weekdays, Days = { DayOfWeek.Monday, DayOfWeek.Wednesday } };
        Assert.Equal(D(2026, 10, 26), r.Next(D(2026, 10, 23)));  // Fri -> Mon
        Assert.Equal(D(2026, 10, 28), r.Next(D(2026, 10, 26)));  // Mon -> Wed
        Assert.Equal(D(2026, 11, 2), r.Next(D(2026, 10, 28)));   // Wed -> next Mon
    }

    [Fact]
    public void Weekdays_SingleDay_IsOneWeekLater()
    {
        var r = new Recurrence { Kind = RecurrenceKind.Weekdays, Days = { DayOfWeek.Wednesday } };
        Assert.Equal(D(2026, 10, 28), r.Next(D(2026, 10, 21)));
    }

    [Fact]
    public void Monthly_31st_ClampsInShortMonths()
    {
        var r = new Recurrence { Kind = RecurrenceKind.Monthly, DayOfMonth = 31 };
        Assert.Equal(D(2027, 2, 28), r.Next(D(2027, 1, 31)));
        Assert.Equal(D(2028, 2, 29), r.Next(D(2028, 1, 31)));    // leap year
        Assert.Equal(D(2027, 3, 31), r.Next(D(2027, 2, 28)));
    }

    [Fact]
    public void Monthly_LaterThisMonth()
    {
        var r = new Recurrence { Kind = RecurrenceKind.Monthly, DayOfMonth = 15 };
        Assert.Equal(D(2026, 10, 15), r.Next(D(2026, 10, 3)));
        Assert.Equal(D(2026, 11, 15), r.Next(D(2026, 10, 15)));
    }

    [Fact]
    public void EveryThreeDays()
    {
        var r = new Recurrence { Kind = RecurrenceKind.EveryNDays, Interval = 3 };
        Assert.Equal(D(2026, 10, 24), r.Next(D(2026, 10, 21)));
        Assert.Equal(D(2026, 11, 1), r.Next(D(2026, 10, 29)));
    }

    [Fact]
    public void OccursOn_MatchesRule()
    {
        var weekly = new Recurrence { Kind = RecurrenceKind.Weekdays, Days = { DayOfWeek.Wednesday } };
        Assert.True(weekly.OccursOn(D(2026, 10, 21)));   // Wednesday
        Assert.False(weekly.OccursOn(D(2026, 10, 22)));

        var monthly = new Recurrence { Kind = RecurrenceKind.Monthly, DayOfMonth = 31 };
        Assert.True(monthly.OccursOn(D(2026, 11, 30)));  // clamped
    }

    [Fact]
    public void SameAs_ComparesOnlyFieldsTheKindUses()
    {
        var a = new Recurrence { Kind = RecurrenceKind.Weekdays, Days = { DayOfWeek.Monday, DayOfWeek.Friday } };
        var b = new Recurrence { Kind = RecurrenceKind.Weekdays, Days = { DayOfWeek.Friday, DayOfWeek.Monday }, Interval = 9 };
        Assert.True(a.SameAs(b));
        Assert.False(a.SameAs(new Recurrence { Kind = RecurrenceKind.Weekdays, Days = { DayOfWeek.Monday } }));
        Assert.False(a.SameAs(null));
        Assert.False(new Recurrence { Kind = RecurrenceKind.EveryNDays, Interval = 3 }
            .SameAs(new Recurrence { Kind = RecurrenceKind.EveryNDays, Interval = 4 }));
        Assert.True(new Recurrence { Kind = RecurrenceKind.Daily }.SameAs(new Recurrence { Kind = RecurrenceKind.Daily, DayOfMonth = 5 }));
    }

    [Fact]
    public void Describe_IsReadable()
    {
        Assert.Equal("Weekdays", new Recurrence
        {
            Kind = RecurrenceKind.Weekdays,
            Days = { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday }
        }.Describe());
        Assert.Equal("Monthly on the 22nd", new Recurrence { Kind = RecurrenceKind.Monthly, DayOfMonth = 22 }.Describe());
        Assert.Equal("Every 3 days", new Recurrence { Kind = RecurrenceKind.EveryNDays, Interval = 3 }.Describe());
    }
}
