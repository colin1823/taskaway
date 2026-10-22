using System;
using System.Windows.Threading;
using Microsoft.Win32;

namespace TranslucentTodo.Services
{
    /// <summary>
    /// Raises <see cref="DayChanged"/> at local midnight, after waking from sleep and when the
    /// clock changes. Uses a single one-shot timer instead of polling, so it costs nothing while idle.
    /// </summary>
    public sealed class RecurrenceScheduler : IDisposable
    {
        private readonly DispatcherTimer _timer;
        private readonly Dispatcher _dispatcher;

        public event Action? DayChanged;

        public RecurrenceScheduler(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
            _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
            _timer.Tick += (_, _) => Fire();
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.TimeChanged += OnTimeChanged;
            Schedule();
        }

        private void Schedule()
        {
            _timer.Stop();
            var untilMidnight = DateTime.Today.AddDays(1) - DateTime.Now;
            _timer.Interval = untilMidnight + TimeSpan.FromSeconds(1);
            _timer.Start();
        }

        private void Fire()
        {
            Schedule();
            DayChanged?.Invoke();
        }

        private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume)
                _dispatcher.BeginInvoke(Fire);
        }

        private void OnTimeChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(Fire);

        public void Dispose()
        {
            _timer.Stop();
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.TimeChanged -= OnTimeChanged;
        }
    }
}
