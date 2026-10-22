using System;
using System.Collections.Generic;
using TranslucentTodo.Models;

namespace TranslucentTodo.Services
{
    public sealed class UndoEntry
    {
        public UndoEntry(TaskItem task, bool permanentDelete)
        {
            Task = task;
            PreviousNextDue = task.NextDue;
            PreviousDeadline = task.Deadline;
            PreviousDeadlineSetAt = task.DeadlineSetAt;
            PermanentDelete = permanentDelete;
            CreatedUtc = DateTime.UtcNow;
        }

        public TaskItem Task { get; }

        // Checking off a recurring task moves these forward; undo puts them back.
        public DateOnly? PreviousNextDue { get; }
        public DateOnly? PreviousDeadline { get; }
        public DateTime? PreviousDeadlineSetAt { get; }

        /// <summary>True for "Delete" from the menu: recurring tasks are removed instead of rescheduled.</summary>
        public bool PermanentDelete { get; }

        public DateTime CreatedUtc { get; }

        /// <summary>The task was deleted from the Upcoming list rather than the active list.</summary>
        public bool FromUpcoming { get; init; }

        /// <summary>The removal animation has finished and the task left the list.</summary>
        public bool Committed { get; set; }

        public bool Undone { get; set; }

        /// <summary>The card being animated out, so an undo can glide it back from where it is.</summary>
        public System.Windows.FrameworkElement? Card { get; set; }

        public TimeSpan Remaining(DateTime nowUtc) => UndoBuffer.Window - (nowUtc - CreatedUtc);
    }

    /// <summary>Holds recently checked tasks; each can be undone for <see cref="Window"/>.</summary>
    public sealed class UndoBuffer
    {
        public static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

        private readonly List<UndoEntry> _entries = new();

        public void Push(UndoEntry entry)
        {
            Prune();
            _entries.Add(entry);
        }

        /// <summary>Removes and returns the newest entry still inside the undo window.</summary>
        public UndoEntry? PopLive()
        {
            Prune();
            if (_entries.Count == 0) return null;
            var last = _entries[^1];
            _entries.RemoveAt(_entries.Count - 1);
            return last;
        }

        public UndoEntry? PeekLive()
        {
            Prune();
            return _entries.Count == 0 ? null : _entries[^1];
        }

        /// <summary>Entries whose removal animation is still running (used to flush on exit).</summary>
        public IEnumerable<UndoEntry> Pending()
        {
            foreach (var e in _entries)
                if (!e.Committed && !e.Undone)
                    yield return e;
        }

        private void Prune()
        {
            var now = DateTime.UtcNow;
            // Never drop an entry whose animation hasn't committed yet; the animation is far shorter than the window.
            _entries.RemoveAll(e => e.Committed && e.Remaining(now) <= TimeSpan.Zero);
        }
    }
}
