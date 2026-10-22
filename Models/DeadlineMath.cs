using System;

namespace TranslucentTodo.Models
{
    /// <summary>Pure deadline math, kept free of WPF so it can be unit tested.</summary>
    public static class DeadlineMath
    {
        /// <summary>
        /// How far along the task is towards its deadline, 0..1: the share of time elapsed between
        /// <paramref name="setAt"/> (when the deadline was set) and the start of the deadline day.
        /// It reaches 1 on the deadline day itself and stays there once overdue.
        /// </summary>
        public static double Urgency(DateTime setAt, DateOnly deadline, DateTime now)
        {
            var end = deadline.ToDateTime(TimeOnly.MinValue);
            if (now >= end) return 1;
            // The countdown hasn't started (e.g. a recurring task brought back before its day).
            if (now <= setAt) return 0;
            double total = (end - setAt).TotalMinutes;
            if (total <= 0) return 1;
            return Math.Clamp((now - setAt).TotalMinutes / total, 0, 1);
        }

        /// <summary>
        /// Moves a recurring task's deadline along with its next due date, keeping the same lead time
        /// (days between when the deadline was set and the deadline).
        /// </summary>
        public static DateOnly Shift(DateOnly deadline, DateOnly setOn, DateOnly nextDue) =>
            nextDue.AddDays(Math.Max(0, deadline.DayNumber - setOn.DayNumber));

        public static string Describe(DateOnly deadline, DateOnly today)
        {
            int days = deadline.DayNumber - today.DayNumber;
            if (days < 0) return days == -1 ? "Overdue by 1 day" : $"Overdue by {-days} days";
            if (days == 0) return "Due today";
            if (days == 1) return "Due tomorrow";
            if (days < 7) return "Due " + deadline.ToString("dddd");
            return "Due " + deadline.ToString("ddd, MMM d");
        }
    }
}
