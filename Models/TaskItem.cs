using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace TranslucentTodo.Models
{
    public sealed class TaskItem : INotifyPropertyChanged
    {
        private string _title = "";
        private Recurrence? _recurrence;
        private DateOnly? _nextDue;
        private DateOnly? _deadline;
        private DateTime? _deadlineSetAt;
        private double _urgency;
        private bool _isChecked;
        private bool _isEditing;

        public Guid Id { get; set; } = Guid.NewGuid();

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public string Title
        {
            get => _title;
            set => Set(ref _title, value);
        }

        public Recurrence? Recurrence
        {
            get => _recurrence;
            set
            {
                if (Set(ref _recurrence, value))
                {
                    OnPropertyChanged(nameof(IsRecurring));
                    OnPropertyChanged(nameof(RecurrenceText));
                }
            }
        }

        /// <summary>
        /// For recurring tasks: the day the task comes back. Null means it is active now.
        /// </summary>
        public DateOnly? NextDue
        {
            get => _nextDue;
            set
            {
                if (Set(ref _nextDue, value))
                    OnPropertyChanged(nameof(NextDueText));
            }
        }

        /// <summary>Optional deadline. Null (the default) means no deadline.</summary>
        public DateOnly? Deadline
        {
            get => _deadline;
            set
            {
                if (Set(ref _deadline, value))
                {
                    OnPropertyChanged(nameof(HasDeadline));
                    RefreshUrgency();
                }
            }
        }

        /// <summary>When the deadline was set: the starting point of the countdown towards it.</summary>
        public DateTime? DeadlineSetAt
        {
            get => _deadlineSetAt;
            set
            {
                if (Set(ref _deadlineSetAt, value))
                    RefreshUrgency();
            }
        }

        /// <summary>UI-only: 0..1 progress towards the deadline; drives the card's red tint.</summary>
        [JsonIgnore]
        public double Urgency
        {
            get => _urgency;
            private set => Set(ref _urgency, value);
        }

        [JsonIgnore]
        public bool HasDeadline => _deadline != null;

        [JsonIgnore]
        public string DeadlineText =>
            _deadline is DateOnly d ? DeadlineMath.Describe(d, DateOnly.FromDateTime(DateTime.Today)) : "";

        /// <summary>Recomputes the tint and label; called on a timer since both depend on the clock.</summary>
        public void RefreshUrgency()
        {
            Urgency = _deadline is DateOnly d
                ? DeadlineMath.Urgency(_deadlineSetAt ?? CreatedAt, d, DateTime.Now)
                : 0;
            OnPropertyChanged(nameof(DeadlineText));
        }

        /// <summary>UI-only: true while the check-off animation plays.</summary>
        [JsonIgnore]
        public bool IsChecked
        {
            get => _isChecked;
            set => Set(ref _isChecked, value);
        }

        /// <summary>UI-only: true while this task is loaded into the add bar for editing (draws a ring).</summary>
        [JsonIgnore]
        public bool IsEditing
        {
            get => _isEditing;
            set => Set(ref _isEditing, value);
        }

        /// <summary>
        /// UI-only position key for the active list (which is always sorted by it). An undone task
        /// keeps its old key, so it returns to its original slot no matter what else was removed meanwhile.
        /// </summary>
        [JsonIgnore]
        public long Seq { get; set; }

        /// <summary>UI-only: an undone task returns along the path it left by (from the right).</summary>
        [JsonIgnore]
        public bool EnterFromRight { get; set; }

        [JsonIgnore]
        public bool IsRecurring => _recurrence != null;

        [JsonIgnore]
        public string RecurrenceText => _recurrence?.Describe() ?? "";

        [JsonIgnore]
        public string NextDueText
        {
            get
            {
                if (_nextDue is not DateOnly d) return "";
                var today = DateOnly.FromDateTime(DateTime.Today);
                if (d == today.AddDays(1)) return "Tomorrow";
                if (d.DayNumber - today.DayNumber < 7) return d.ToString("dddd");
                return d.ToString("ddd, MMM d");
            }
        }

        public bool IsActiveOn(DateOnly today) => _nextDue is not DateOnly d || d <= today;

        /// <summary>Re-evaluates relative date text such as "Tomorrow" after midnight.</summary>
        public void RefreshDateText() => OnPropertyChanged(nameof(NextDueText));

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }
    }
}
