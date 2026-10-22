using System;
using System.Collections.Generic;
using System.Linq;

namespace TranslucentTodo.Models
{
    public enum RecurrenceKind
    {
        Daily,
        Weekdays,
        Monthly,
        EveryNDays
    }

    /// <summary>
    /// A repeat rule. Pure date math only, so it can be unit tested without WPF.
    /// </summary>
    public sealed class Recurrence
    {
        public RecurrenceKind Kind { get; set; } = RecurrenceKind.Daily;

        /// <summary>Used by <see cref="RecurrenceKind.Weekdays"/>.</summary>
        public List<DayOfWeek> Days { get; set; } = new();

        /// <summary>Used by <see cref="RecurrenceKind.Monthly"/> (1-31, clamped for short months).</summary>
        public int DayOfMonth { get; set; } = 1;

        /// <summary>Used by <see cref="RecurrenceKind.EveryNDays"/>.</summary>
        public int Interval { get; set; } = 1;

        /// <summary>True when the rule has an occurrence on <paramref name="date"/>.</summary>
        public bool OccursOn(DateOnly date) => Kind switch
        {
            RecurrenceKind.Daily => true,
            RecurrenceKind.EveryNDays => true,
            RecurrenceKind.Weekdays => Days.Count == 0 || Days.Contains(date.DayOfWeek),
            RecurrenceKind.Monthly => date.Day == ClampDay(date.Year, date.Month, DayOfMonth),
            _ => true
        };

        /// <summary>The first occurrence strictly after <paramref name="from"/>.</summary>
        public DateOnly Next(DateOnly from)
        {
            switch (Kind)
            {
                case RecurrenceKind.Weekdays:
                    if (Days.Count == 0)
                        return from.AddDays(1);
                    for (int i = 1; i <= 7; i++)
                    {
                        var d = from.AddDays(i);
                        if (Days.Contains(d.DayOfWeek))
                            return d;
                    }
                    return from.AddDays(7);

                case RecurrenceKind.Monthly:
                    var thisMonth = new DateOnly(from.Year, from.Month, ClampDay(from.Year, from.Month, DayOfMonth));
                    if (thisMonth > from)
                        return thisMonth;
                    var nm = new DateOnly(from.Year, from.Month, 1).AddMonths(1);
                    return new DateOnly(nm.Year, nm.Month, ClampDay(nm.Year, nm.Month, DayOfMonth));

                case RecurrenceKind.EveryNDays:
                    return from.AddDays(Math.Max(1, Interval));

                default:
                    return from.AddDays(1);
            }
        }

        public string Describe()
        {
            switch (Kind)
            {
                case RecurrenceKind.Weekdays:
                    if (Days.Count == 0 || Days.Count == 7)
                        return "Daily";
                    var set = new HashSet<DayOfWeek>(Days);
                    if (set.SetEquals(new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday }))
                        return "Weekdays";
                    if (set.SetEquals(new[] { DayOfWeek.Saturday, DayOfWeek.Sunday }))
                        return "Weekends";
                    return string.Join(" ", OrderedDays().Select(d => d.ToString().Substring(0, 3)));
                case RecurrenceKind.Monthly:
                    return "Monthly on the " + Ordinal(DayOfMonth);
                case RecurrenceKind.EveryNDays:
                    return Interval <= 1 ? "Daily" : $"Every {Interval} days";
                default:
                    return "Daily";
            }
        }

        /// <summary>True when both rules produce the same dates (fields the kind doesn't use are ignored).</summary>
        public bool SameAs(Recurrence? other)
        {
            if (other == null || other.Kind != Kind) return false;
            return Kind switch
            {
                RecurrenceKind.Weekdays => new HashSet<DayOfWeek>(Days).SetEquals(other.Days),
                RecurrenceKind.Monthly => DayOfMonth == other.DayOfMonth,
                RecurrenceKind.EveryNDays => Interval == other.Interval,
                _ => true
            };
        }

        private IEnumerable<DayOfWeek> OrderedDays() =>
            Days.Distinct().OrderBy(d => ((int)d + 6) % 7); // Monday first

        private static int ClampDay(int year, int month, int day) =>
            Math.Clamp(day, 1, DateTime.DaysInMonth(year, month));

        private static string Ordinal(int n)
        {
            int rem100 = n % 100;
            if (rem100 is >= 11 and <= 13) return n + "th";
            return (n % 10) switch { 1 => n + "st", 2 => n + "nd", 3 => n + "rd", _ => n + "th" };
        }
    }
}
