using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TranslucentTodo.Interop;
using TranslucentTodo.Models;
using TranslucentTodo.Services;

namespace TranslucentTodo
{
    public partial class MainWindow : Window
    {
        private static readonly TimeSpan CheckHold = TimeSpan.FromMilliseconds(400);
        private static readonly TimeSpan RemoveDuration = TimeSpan.FromMilliseconds(260);
        private const double SlideDistance = 36;
        private static readonly Thickness CardMargin = new(0, 0, 0, 6);
        private static readonly IEasingFunction Spring = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 5 };
        private static readonly IEasingFunction Accelerate = new CubicEase { EasingMode = EasingMode.EaseIn };

        private readonly AppSettings _settings;
        private readonly ObservableCollection<TaskItem> _active = new();
        private readonly ObservableCollection<TaskItem> _upcoming = new();
        private readonly UndoBuffer _undo = new();
        private readonly DispatcherTimer _boundsTimer;
        private readonly DispatcherTimer _toastTimer;
        private readonly DispatcherTimer _trimTimer;
        private readonly RecurrenceScheduler _scheduler;
        private TaskItem? _editing;
        private bool _exiting;
        private bool _hiding;
        private readonly System.Collections.Generic.HashSet<FrameworkElement> _collapsing = new();
        // Hosts with a height animation in flight, and whether they are opening (true) or closing.
        private readonly System.Collections.Generic.Dictionary<FrameworkElement, bool> _revealing = new();
        private readonly DispatcherTimer _urgencyTimer;
        private long _nextSeq;
        private bool _initialLoad = true;   // cards cascade in only on first appearance
        private double? _scrollTarget;      // where an in-flight smooth scroll is heading
        private bool _rubberBanding;

        /// <summary>Animatable stand-in for the read-only ScrollViewer offset (drives smooth wheel scrolling).</summary>
        public static readonly DependencyProperty ScrollOffsetProperty = DependencyProperty.Register(
            nameof(ScrollOffset), typeof(double), typeof(MainWindow),
            new PropertyMetadata(0.0, (d, e) => ((MainWindow)d).Scroller.ScrollToVerticalOffset((double)e.NewValue)));

        public double ScrollOffset
        {
            get => (double)GetValue(ScrollOffsetProperty);
            set => SetValue(ScrollOffsetProperty, value);
        }

        private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

        public MainWindow(AppSettings settings)
        {
            _settings = settings;
            InitializeComponent();

            ActiveList.ItemsSource = _active;
            UpcomingList.ItemsSource = _upcoming;
            ApplySavedBounds();
            LoadTasks();

            _boundsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _boundsTimer.Tick += (_, _) => { _boundsTimer.Stop(); SaveBounds(); };

            _toastTimer = new DispatcherTimer();
            _toastTimer.Tick += (_, _) => RefreshToast();

            _trimTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _trimTimer.Tick += (_, _) => { _trimTimer.Stop(); DesktopPinning.TrimWorkingSet(); };

            _scheduler = new RecurrenceScheduler(Dispatcher);
            _scheduler.DayChanged += OnDayChanged;

            // Deadline tints move with the clock; a quarter-hour refresh is smooth enough and costs nothing.
            _urgencyTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(15) };
            _urgencyTimer.Tick += (_, _) => RefreshDeadlines();
            _urgencyTimer.Start();

            SourceInitialized += (_, _) => DesktopPinning.Attach(this);
            Deactivated += (_, _) => DesktopPinning.Lower(this);
            Loaded += (_, _) =>
            {
                _trimTimer.Start();
                // First appearance: the widget fades up into place and the cards cascade in.
                var scale = (ScaleTransform)Shell.RenderTransform;
                Shell.BeginAnimation(OpacityProperty, Anim(0, 1, 300));
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(0.97, 1, 420));
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(0.97, 1, 420));
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => _initialLoad = false);
            };
            LocationChanged += (_, _) => RestartBoundsTimer();
            SizeChanged += (_, _) => RestartBoundsTimer();
            PreviewKeyDown += OnPreviewKeyDown;

            UpcomingToggle.IsChecked = _settings.UpcomingExpanded;
            UpdateHeader();
            UpdateSubmitState();
        }

        // ---------------------------------------------------------------- scrolling

        /// <summary>
        /// Mouse-wheel scrolling glides to its target instead of jumping a fixed step; successive
        /// notches extend the same glide. At either end the list gives a little and springs back
        /// (a soft boundary) rather than stopping dead.
        /// </summary>
        private void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;
            double current = Scroller.VerticalOffset;
            double from = _scrollTarget ?? current;
            double target = Math.Clamp(from - e.Delta * 0.5, 0, Scroller.ScrollableHeight);
            if (Math.Abs(target - from) < 0.5)
            {
                RubberBand(e.Delta > 0 ? 1 : -1);
                return;
            }

            _scrollTarget = target;
            var glide = new DoubleAnimation(current, target, Dur(300)) { EasingFunction = Spring };
            glide.Completed += (_, _) =>
            {
                if (_scrollTarget == target) _scrollTarget = null;
            };
            BeginAnimation(ScrollOffsetProperty, glide);
        }

        private void RubberBand(int direction)
        {
            if (_rubberBanding || !MotionEnabled) return;
            _rubberBanding = true;
            var stretch = new DoubleAnimationUsingKeyFrames();
            stretch.KeyFrames.Add(new EasingDoubleKeyFrame(9 * direction, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(90)),
                new CubicEase { EasingMode = EasingMode.EaseOut }));
            stretch.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(460)), Spring));
            stretch.Completed += (_, _) => _rubberBanding = false;
            ((TranslateTransform)ListStack.RenderTransform).BeginAnimation(TranslateTransform.YProperty, stretch);
        }

        /// <summary>A quick decaying side-to-side shake: "nothing to add" (like a rejected password field).</summary>
        private static void Shake(FrameworkElement element)
        {
            if (!MotionEnabled || element.RenderTransform is not TranslateTransform shift) return;
            var shake = new DoubleAnimationUsingKeyFrames();
            double[] offsets = { -7, 6, -4, 3, -1.5, 0 };
            for (int i = 0; i < offsets.Length; i++)
                shake.KeyFrames.Add(new EasingDoubleKeyFrame(offsets[i],
                    KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(55 * (i + 1))), new SineEase()));
            shift.BeginAnimation(TranslateTransform.XProperty, shake);
        }

        /// <summary>A value that holds at <paramref name="from"/> for <paramref name="delay"/>, then eases to <paramref name="to"/>.</summary>
        private static DoubleAnimationUsingKeyFrames Delayed(double from, double to, TimeSpan delay, int ms)
        {
            var a = new DoubleAnimationUsingKeyFrames();
            a.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            a.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(delay)));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(delay + TimeSpan.FromMilliseconds(ms)), Spring));
            return a;
        }

        // ---------------------------------------------------------------- material & motion

        /// <summary>Honors Windows' "Animation effects" setting: when it is off, transitions are instant.</summary>
        private static bool MotionEnabled => SystemParameters.ClientAreaAnimation;

        private static Duration Dur(int ms) => new(TimeSpan.FromMilliseconds(MotionEnabled ? ms : 0));

        private static DoubleAnimation Anim(double? from, double to, int ms, IEasingFunction? ease = null) =>
            new() { From = from, To = to, Duration = Dur(ms), EasingFunction = ease ?? Spring };

        private static ColorAnimation ColorTo(Color to, int ms) =>
            new() { To = to, Duration = Dur(ms), EasingFunction = Spring };

        /// <summary>
        /// The shell is invisible at rest; hovering reveals a faint outline (so the edges can be
        /// grabbed for resizing) and the window buttons.
        /// </summary>
        private void Window_MouseEnter(object sender, MouseEventArgs e)
        {
            ShellEdge.BeginAnimation(SolidColorBrush.ColorProperty, ColorTo(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF), 180));
            ShellFill.BeginAnimation(SolidColorBrush.ColorProperty, ColorTo(Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF), 180));
            WindowButtons.BeginAnimation(OpacityProperty, Anim(null, 1, 180));
        }

        private void Window_MouseLeave(object sender, MouseEventArgs e)
        {
            ShellEdge.BeginAnimation(SolidColorBrush.ColorProperty, ColorTo(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 320));
            ShellFill.BeginAnimation(SolidColorBrush.ColorProperty, ColorTo(Color.FromArgb(0x03, 0xFF, 0xFF, 0xFF), 320));
            WindowButtons.BeginAnimation(OpacityProperty, Anim(null, 0, 320));
        }

        // ---------------------------------------------------------------- window state

        private void ApplySavedBounds()
        {
            double w = Math.Max(MinWidth, _settings.Width);
            double h = Math.Max(MinHeight, _settings.Height);
            var work = SystemParameters.WorkArea;
            double left = _settings.Left ?? work.Right - w - 24;
            double top = _settings.Top ?? work.Top + 24;

            // If the saved spot is off every monitor (e.g. a display was unplugged), fall back to the default.
            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            bool headerVisible = left + w - 60 > screen.Left && left + 60 < screen.Right &&
                                 top >= screen.Top - 10 && top + 40 < screen.Bottom;
            if (!headerVisible)
            {
                left = work.Right - w - 24;
                top = work.Top + 24;
            }

            Left = left;
            Top = top;
            Width = w;
            Height = h;
        }

        private void RestartBoundsTimer()
        {
            if (!IsLoaded) return;
            _boundsTimer.Stop();
            _boundsTimer.Start();
        }

        private void SaveBounds()
        {
            if (!IsLoaded || WindowState != WindowState.Normal) return;
            _settings.Left = Math.Round(Left);
            _settings.Top = Math.Round(Top);
            _settings.Width = Math.Round(ActualWidth);
            _settings.Height = Math.Round(ActualHeight);
            _settings.UpcomingExpanded = UpcomingToggle.IsChecked == true;
            SettingsStore.Save(_settings);
        }

        public void ShowWidget()
        {
            bool wasHidden = !IsVisible || _hiding;
            _hiding = false;
            Show();
            WindowState = WindowState.Normal;
            Activate();
            DesktopPinning.Raise(this);
            if (wasHidden)
            {
                // Materialize: the widget grows into place instead of popping in.
                var scale = (ScaleTransform)Shell.RenderTransform;
                Shell.BeginAnimation(OpacityProperty, Anim(0, 1, 200));
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(0.96, 1, 320));
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(0.96, 1, 320));
            }
        }

        private void HideWidget()
        {
            if (!IsVisible || _hiding) return;
            _hiding = true;
            var scale = (ScaleTransform)Shell.RenderTransform;
            var fade = Anim(null, 0, 140, Accelerate);
            fade.Completed += (_, _) =>
            {
                if (!_hiding) return; // shown again mid-fade
                _hiding = false;
                Hide();
                _trimTimer.Stop();
                _trimTimer.Start();
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(null, 0.96, 140, Accelerate));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(null, 0.96, 140, Accelerate));
            Shell.BeginAnimation(OpacityProperty, fade);
        }

        /// <summary>Finishes any pending removals and saves everything before the app quits.</summary>
        public void PrepareForExit()
        {
            _exiting = true;
            foreach (var entry in _undo.Pending().ToList())
                Commit(entry);
            _boundsTimer.Stop();
            _urgencyTimer.Stop();
            SaveBounds();
            _scheduler.Dispose();
            Save();
            Close();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_exiting)
            {
                e.Cancel = true;
                HideWidget();
            }
            base.OnClosing(e);
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => HideWidget();

        private void Close_Click(object sender, RoutedEventArgs e) => HideWidget();

        // ---------------------------------------------------------------- data

        private void LoadTasks()
        {
            var today = Today;
            foreach (var task in TaskStore.Load())
            {
                if (string.IsNullOrWhiteSpace(task.Title)) continue;
                task.RefreshUrgency(); // deserialization order can leave it stale
                if (task.IsRecurring && !task.IsActiveOn(today))
                {
                    _upcoming.Add(task);
                }
                else
                {
                    task.NextDue = null;
                    AppendActive(task);
                }
            }
            SortUpcoming();
        }

        /// <summary>Adds a task at the end of the active list with a fresh position key.</summary>
        private void AppendActive(TaskItem task)
        {
            task.Seq = ++_nextSeq;
            _active.Add(task);
        }

        /// <summary>Puts a task back into the active list at its original position (by position key).</summary>
        private void RestoreActive(TaskItem task)
        {
            int index = _active.Count(t => t.Seq < task.Seq);
            _active.Insert(index, task);
        }

        private void Save() => TaskStore.Save(_active.Concat(_upcoming).ToList());

        private void SortUpcoming()
        {
            var sorted = _upcoming.OrderBy(t => t.NextDue).ToList();
            for (int i = 0; i < sorted.Count; i++)
            {
                int current = _upcoming.IndexOf(sorted[i]);
                if (current != i) _upcoming.Move(current, i);
            }
        }

        private void InsertUpcoming(TaskItem task)
        {
            _upcoming.Add(task);
            SortUpcoming();
        }

        private void OnDayChanged()
        {
            var today = Today;
            var due = _upcoming.Where(t => t.IsActiveOn(today)).ToList();
            foreach (var task in due)
            {
                _upcoming.Remove(task);
                task.NextDue = null;
                AppendActive(task);
            }
            foreach (var task in _upcoming)
                task.RefreshDateText();
            RefreshDeadlines();
            UpdateDeadlineIndicator(); // "Due tomorrow" in the picker becomes "Due today"
            if (due.Count > 0) Save();
            UpdateHeader();
        }

        private void RefreshDeadlines()
        {
            foreach (var task in _active)
                if (task.HasDeadline) task.RefreshUrgency();
            foreach (var task in _upcoming)
                if (task.HasDeadline) task.RefreshUrgency();
        }

        private void UpdateHeader()
        {
            DateRun.Text = DateTime.Today.ToString("dddd, MMMM d");
            int open = _active.Count(t => !t.IsChecked);
            CountRun.Text = open == 0 ? "" : open == 1 ? "  ·  1 open" : $"  ·  {open} open";
            UpcomingSection.Visibility = _upcoming.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            UpcomingHeader.Text = $"Upcoming  ({_upcoming.Count})";
            // "All clear" unrolls once the last task has left (and rolls away when one is added).
            Reveal(EmptyHost, _active.Count == 0);
        }

        // ---------------------------------------------------------------- check off, delete, undo

        /// <summary>Entrance: new cards rise into place; undone cards come back from the right, the way they left.</summary>
        private void TaskRoot_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Panel root || root.DataContext is not TaskItem task) return;

            // Each card gets its own animatable transforms (Loaded can fire again if the card is re-parented).
            if (root.RenderTransform is not TranslateTransform shift || shift.IsFrozen)
            {
                shift = new TranslateTransform();
                root.RenderTransform = shift;
                root.LayoutTransform = new ScaleTransform();
                if (root.Children.Count > 0 && root.Children[0] is FrameworkElement card)
                    card.RenderTransform = new ScaleTransform();
            }

            if (task.EnterFromRight)
            {
                task.EnterFromRight = false;
                var height = (ScaleTransform)root.LayoutTransform;
                height.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(0, 1, 260));
                root.BeginAnimation(MarginProperty, new ThicknessAnimation(new Thickness(0), CardMargin, Dur(260)) { EasingFunction = Spring });
                shift.BeginAnimation(TranslateTransform.XProperty, Anim(SlideDistance, 0, 380));
                root.BeginAnimation(OpacityProperty, Anim(0, 1, 260));
            }
            else if (_initialLoad && MotionEnabled)
            {
                // First appearance: a short top-to-bottom cascade (capped so long lists don't drag).
                var delay = TimeSpan.FromMilliseconds(45 * Math.Min(_active.IndexOf(task), 10));
                shift.BeginAnimation(TranslateTransform.YProperty, Delayed(10, 0, delay, 420));
                root.BeginAnimation(OpacityProperty, Delayed(0, 1, delay, 300));
            }
            else
            {
                shift.BeginAnimation(TranslateTransform.YProperty, Anim(8, 0, 320));
                root.BeginAnimation(OpacityProperty, Anim(0, 1, 220));
            }
        }

        private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement card) return;

            // Respond on press, not release: the card gives slightly under the pointer.
            if (card.RenderTransform is ScaleTransform { IsFrozen: false } press)
            {
                press.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(null, 0.985, 90));
                press.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(null, 0.985, 90));
            }

            if (e.ClickCount == 2 && card.DataContext is TaskItem task && card.Parent is FrameworkElement root)
            {
                Remove(task, root, permanentDelete: false);
                e.Handled = true;
            }
        }

        private void Card_Release(object sender, MouseEventArgs e)
        {
            if (sender is not FrameworkElement card || card.RenderTransform is not ScaleTransform { IsFrozen: false } press) return;
            // Animate from wherever the press got to, so a quick tap never jumps.
            press.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(null, 1, 280));
            press.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(null, 1, 280));
        }

        private void Check_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (e.ClickCount != 1) return;
            if (sender is FrameworkElement check && check.DataContext is TaskItem task)
            {
                var root = FindRoot(task);
                if (root != null) Remove(task, root, permanentDelete: false);
            }
        }

        private FrameworkElement? FindRoot(TaskItem task)
        {
            if (ActiveList.ItemContainerGenerator.ContainerFromItem(task) is not DependencyObject container) return null;
            return VisualTreeHelper.GetChildrenCount(container) > 0
                ? VisualTreeHelper.GetChild(container, 0) as FrameworkElement
                : null;
        }

        private bool IsBeingRemoved(TaskItem task) => _undo.Pending().Any(e => e.Task == task);

        /// <summary>
        /// Checks off (or deletes) an active task: plays the check animation, then slides the card out.
        /// The task stays undoable for five seconds.
        /// </summary>
        private void Remove(TaskItem task, FrameworkElement root, bool permanentDelete)
        {
            if (IsBeingRemoved(task)) return;
            if (_editing == task) EndEditing();

            // Make sure Ctrl+Z reaches this window right after a check.
            if (!IsKeyboardFocusWithin)
            {
                Activate();
                Focus();
            }

            if (!permanentDelete) task.IsChecked = true;

            var entry = new UndoEntry(task, permanentDelete);
            _undo.Push(entry);

            var sb = BuildRemoveStoryboard(permanentDelete ? TimeSpan.Zero : CheckHold);
            sb.Completed += (_, _) => Commit(entry);
            entry.Card = root;
            sb.Begin(root, HandoffBehavior.SnapshotAndReplace, isControllable: true);

            UpdateHeader();
            RefreshToast();
        }

        private static Storyboard BuildRemoveStoryboard(TimeSpan delay)
        {
            var sb = new Storyboard { BeginTime = MotionEnabled ? delay : TimeSpan.Zero };
            var duration = Dur((int)RemoveDuration.TotalMilliseconds);
            var stagger = TimeSpan.FromMilliseconds(MotionEnabled ? 90 : 0);

            // Exit to the right, accelerating away; the gap closes slightly after so the eye can follow.
            var slide = new DoubleAnimation(0, SlideDistance, duration) { EasingFunction = Accelerate };
            Storyboard.SetTargetProperty(slide, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.X)"));

            var fade = new DoubleAnimation(0, duration);
            Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));

            var collapse = new DoubleAnimation(0, duration)
            {
                BeginTime = stagger,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };
            Storyboard.SetTargetProperty(collapse, new PropertyPath("(FrameworkElement.LayoutTransform).(ScaleTransform.ScaleY)"));

            var margin = new ThicknessAnimation(new Thickness(0), duration) { BeginTime = stagger };
            Storyboard.SetTargetProperty(margin, new PropertyPath(MarginProperty));

            sb.Children.Add(slide);
            sb.Children.Add(fade);
            sb.Children.Add(collapse);
            sb.Children.Add(margin);
            return sb;
        }

        /// <summary>
        /// Interrupts a removal mid-flight: the card glides back from wherever it is on screen
        /// instead of snapping, retracing the path it was leaving by.
        /// </summary>
        private static void AnimateBack(FrameworkElement root)
        {
            var sb = new Storyboard();
            void Add(Timeline t, string path)
            {
                Storyboard.SetTargetProperty(t, new PropertyPath(path));
                sb.Children.Add(t);
            }
            Add(Anim(null, 0, 340), "(UIElement.RenderTransform).(TranslateTransform.X)");
            Add(Anim(null, 1, 220), "Opacity");
            Add(Anim(null, 1, 260), "(FrameworkElement.LayoutTransform).(ScaleTransform.ScaleY)");
            Add(new ThicknessAnimation { To = CardMargin, Duration = Dur(260), EasingFunction = Spring }, "Margin");
            sb.Begin(root, HandoffBehavior.SnapshotAndReplace);
        }

        /// <summary>Takes the task out of the active list once its animation has finished.</summary>
        private void Commit(UndoEntry entry)
        {
            if (entry.Undone || entry.Committed) return;
            entry.Committed = true;

            var task = entry.Task;
            _active.Remove(task);
            task.IsChecked = false;
            if (task.IsRecurring && !entry.PermanentDelete)
            {
                var next = task.Recurrence!.Next(Today);
                if (task.Deadline is DateOnly deadline)
                {
                    // The deadline travels with the task, keeping the same lead time; its countdown
                    // starts when the task comes back.
                    var setOn = DateOnly.FromDateTime(task.DeadlineSetAt ?? task.CreatedAt);
                    task.Deadline = DeadlineMath.Shift(deadline, setOn, next);
                    task.DeadlineSetAt = next.ToDateTime(TimeOnly.MinValue);
                }
                task.NextDue = next;
                InsertUpcoming(task);
                Save();
                UpdateHeader();
                // Show where it went: the Upcoming header gives a small pulse as the card arrives.
                PulseUpcomingHeader();
                return;
            }
            Save();
            UpdateHeader();
        }

        private void PulseUpcomingHeader()
        {
            if (!MotionEnabled || UpcomingToggle.RenderTransform is not ScaleTransform scale) return;
            var pulse = new DoubleAnimationUsingKeyFrames();
            pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1.1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120)),
                new CubicEase { EasingMode = EasingMode.EaseOut }));
            pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(480)), Spring));
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
        }

        private bool TryUndo()
        {
            var entry = _undo.PopLive();
            if (entry == null) return false;
            entry.Undone = true;

            var task = entry.Task;
            if (!entry.Committed)
            {
                // Still animating out: reverse from the current on-screen position.
                if (entry.Card != null) AnimateBack(entry.Card);
                task.IsChecked = false;
            }
            else
            {
                // The task may have moved since ("Show now", or midnight brought it back):
                // take it out of both lists so undo can never leave a duplicate.
                _active.Remove(task);
                _upcoming.Remove(task);
                task.NextDue = entry.PreviousNextDue;
                task.Deadline = entry.PreviousDeadline;
                task.DeadlineSetAt = entry.PreviousDeadlineSetAt;

                if (entry.FromUpcoming && !task.IsActiveOn(Today))
                {
                    InsertUpcoming(task);
                }
                else if (entry.FromUpcoming)
                {
                    // Its due day arrived while it was deleted.
                    task.NextDue = null;
                    AppendActive(task);
                }
                else
                {
                    task.EnterFromRight = true;
                    RestoreActive(task);
                }
            }

            Save();
            UpdateHeader();
            RefreshToast();
            return true;
        }

        private void Undo_Click(object sender, RoutedEventArgs e) => TryUndo();

        private void RefreshToast()
        {
            _toastTimer.Stop();
            var entry = _undo.PeekLive();
            var remaining = entry?.Remaining(DateTime.UtcNow) ?? TimeSpan.Zero;
            if (entry == null || remaining <= TimeSpan.Zero)
            {
                HideToast();
                return;
            }

            UndoText.Text = (entry.PermanentDelete ? "Deleted: " : "Done: ") + entry.Task.Title;

            var scale = (ScaleTransform)UndoProgress.RenderTransform;
            double fraction = remaining.TotalMilliseconds / UndoBuffer.Window.TotalMilliseconds;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(fraction, 0, remaining));

            // Unrolls from the add bar and later rolls back into it the same way.
            Reveal(ToastHost, true);

            _toastTimer.Interval = remaining;
            _toastTimer.Start();
        }

        private void HideToast() => Reveal(ToastHost, false);

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
            {
                // Only claim Ctrl+Z when there is a task to restore; otherwise text boxes keep their own undo.
                if (TryUndo()) e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                if (_editing != null) EndEditing();
                else
                {
                    RepeatToggle.IsChecked = false;
                    DeadlineToggle.IsChecked = false;
                }
                e.Handled = true;
            }
        }

        // ---------------------------------------------------------------- context menu

        private static TaskItem? MenuTask(object sender) => (sender as FrameworkElement)?.DataContext as TaskItem;

        private void Edit_Click(object sender, RoutedEventArgs e)
        {
            if (MenuTask(sender) is not TaskItem task || IsBeingRemoved(task)) return;
            if (_editing != null) _editing.IsEditing = false;
            _editing = task;
            task.IsEditing = true;
            ShowCancelButton(true);
            NewTaskBox.Text = task.Title;
            LoadDeadline(task.Deadline);
            LoadRecurrence(task.Recurrence);
            SubmitGlyph.Text = "";
            SubmitButton.ToolTip = "Save (Enter) · Esc to cancel";
            Placeholder.Text = "Edit task…";
            ShowWidget();
            NewTaskBox.Focus();
            NewTaskBox.SelectAll();
            AddBar.BringIntoView();
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (MenuTask(sender) is not TaskItem task) return;

            if (_upcoming.Contains(task))
            {
                if (_editing == task) EndEditing();
                var entry = new UndoEntry(task, permanentDelete: true)
                {
                    FromUpcoming = true,
                    Committed = true
                };
                _undo.Push(entry);
                _upcoming.Remove(task);
                Save();
                UpdateHeader();
                RefreshToast();
                return;
            }

            var root = FindRoot(task);
            if (root != null) Remove(task, root, permanentDelete: true);
        }

        private void ShowNow_Click(object sender, RoutedEventArgs e)
        {
            if (MenuTask(sender) is not TaskItem task || !_upcoming.Remove(task)) return;
            // DeadlineSetAt is deliberately left alone: it also defines the lead time carried to the
            // next cycle, so moving it here would make the deadline drift later on every "Show now".
            // Before that day the urgency simply stays at zero.
            task.NextDue = null;
            AppendActive(task);
            Save();
            UpdateHeader();
        }

        // ---------------------------------------------------------------- add / edit bar

        private void NewTaskBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                Submit();
                e.Handled = true;
            }
        }

        private void NewTaskBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // The placeholder fades rather than blinking off with the first keystroke.
            bool empty = NewTaskBox.Text.Length == 0;
            Placeholder.BeginAnimation(OpacityProperty, Anim(null, empty ? 1 : 0, empty ? 180 : 90));
            UpdateSubmitState();
        }

        private void Submit_Click(object sender, RoutedEventArgs e) => Submit();

        private void Submit()
        {
            var title = NewTaskBox.Text.Trim();
            if (title.Length == 0)
            {
                if (_editing != null) EndEditing();
                else Shake(AddBar);
                return;
            }

            var today = Today;
            var recurrence = ReadRecurrence(today);
            var deadline = ReadDeadline(today);

            if (_editing != null)
            {
                var task = _editing;
                bool ruleChanged = recurrence == null ? task.Recurrence != null : !recurrence.SameAs(task.Recurrence);
                task.Title = title;
                task.Recurrence = recurrence;
                if (_upcoming.Contains(task))
                {
                    if (recurrence == null)
                    {
                        _upcoming.Remove(task);
                        task.NextDue = null;
                        AppendActive(task);
                    }
                    else if (ruleChanged)
                    {
                        // Only a new rule moves the due date; renaming must not shift it.
                        task.NextDue = recurrence.Next(today);
                        SortUpcoming();
                    }
                }
                if (task.Deadline != deadline)
                {
                    // A new deadline restarts the countdown; an unchanged one keeps its progress.
                    // (Set after NextDue is final, since the countdown start depends on it.)
                    task.DeadlineSetAt = deadline == null ? null : CountdownStart(task);
                    task.Deadline = deadline;
                }
                EndEditing();
            }
            else
            {
                var task = new TaskItem { Title = title, Recurrence = recurrence };
                if (recurrence != null && !recurrence.OccursOn(today))
                    task.NextDue = recurrence.Next(today); // e.g. a "Mondays" task added on a Wednesday
                task.DeadlineSetAt = deadline == null ? null : CountdownStart(task);
                task.Deadline = deadline;

                if (task.NextDue != null)
                {
                    InsertUpcoming(task);
                    UpcomingToggle.IsChecked = true;
                }
                else
                {
                    AppendActive(task);
                    // The add bar follows the newest task; keep it in view so typing can continue.
                    Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => AddBar.BringIntoView());
                }
                NewTaskBox.Clear();
                ResetRepeat();
                ResetDeadline();
            }

            Save();
            UpdateHeader();
        }

        private void CancelEdit_Click(object sender, RoutedEventArgs e) => EndEditing();

        /// <summary>The cancel (×) button slides open beside Save while editing and folds away after.</summary>
        private void ShowCancelButton(bool show)
        {
            var button = CancelEditButton;
            if (show) button.Visibility = Visibility.Visible;
            var width = new DoubleAnimation(show ? 30 : 0, Dur(260)) { EasingFunction = Spring };
            if (!show)
                width.Completed += (_, _) =>
                {
                    if (_editing == null) button.Visibility = Visibility.Collapsed;
                };
            button.BeginAnimation(WidthProperty, width);
            button.BeginAnimation(OpacityProperty, Anim(null, show ? 1 : 0, show ? 220 : 140));
        }

        /// <summary>
        /// The + button is dimmed and inert while there's nothing to add, and turns green once
        /// there is (while editing it is always live: Save, or end editing if cleared).
        /// </summary>
        private void UpdateSubmitState()
        {
            bool hasText = NewTaskBox.Text.Trim().Length > 0;
            SubmitButton.IsEnabled = hasText || _editing != null;
            if (hasText)
                SubmitButton.Foreground = (Brush)FindResource("AccentBrush");
            else
                SubmitButton.ClearValue(ForegroundProperty);
        }

        /// <summary>
        /// Where a deadline's countdown starts: now for a task on the list, or the day a waiting
        /// recurring task appears. That keeps the first cycle consistent with later ones, whose
        /// countdown also starts on the day they come back.
        /// </summary>
        private static DateTime CountdownStart(TaskItem task) =>
            task.NextDue is DateOnly next && next > Today ? next.ToDateTime(TimeOnly.MinValue) : DateTime.Now;

        private void EndEditing()
        {
            if (_editing != null) _editing.IsEditing = false;
            _editing = null;
            ShowCancelButton(false);
            NewTaskBox.Clear();
            ResetRepeat();
            ResetDeadline();
            UpdateSubmitState();
            SubmitGlyph.Text = "";
            SubmitButton.ToolTip = "Add task (Enter)";
            Placeholder.Text = "Add a task…";
        }

        // ---------------------------------------------------------------- repeat picker

        private void RepeatToggle_Changed(object sender, RoutedEventArgs e)
        {
            bool open = RepeatToggle.IsChecked == true;
            if (open) DeadlineToggle.IsChecked = false; // one picker at a time
            Reveal(RepeatHost, open, scrollIntoView: true);
        }

        private void DeadlineToggle_Changed(object sender, RoutedEventArgs e)
        {
            bool open = DeadlineToggle.IsChecked == true;
            if (open) RepeatToggle.IsChecked = false;
            Reveal(DeadlineHost, open, scrollIntoView: true);
        }

        /// <summary>
        /// Shows or hides a clipping host by animating its height, so it unrolls from (and rolls back
        /// into) the element above it and the content below slides instead of jumping. Each animation
        /// starts from the height currently on screen, so reversing mid-way is seamless.
        /// </summary>
        private void Reveal(FrameworkElement host, bool show, bool scrollIntoView = false)
        {
            // Inside a closed picker there is nothing to see: just set the end state.
            if (host.Parent is UIElement parent && !parent.IsVisible)
            {
                SnapToEnd(host, show);
                return;
            }

            if (show)
            {
                bool wasShown = host.Visibility == Visibility.Visible && !_collapsing.Contains(host);
                if (wasShown) return;
                double from = host.Visibility == Visibility.Visible ? host.ActualHeight : 0;
                _collapsing.Remove(host);

                // Anything still unrolling inside this host (e.g. the weekday chips after Edit) would
                // be measured at its in-between height, and the host would snap when it finished.
                // Settle those first so the measurement below is the true final height.
                foreach (var (inner, innerShow) in _revealing.ToList())
                    if (inner != host && inner.IsDescendantOf(host))
                        SnapToEnd(inner, innerShow);

                // Measure the natural height with the current width.
                host.BeginAnimation(HeightProperty, null);
                host.Height = double.NaN;
                host.Visibility = Visibility.Visible;
                double width = (host.Parent as FrameworkElement)?.ActualWidth ?? host.ActualWidth;
                host.Measure(new Size(width, double.PositiveInfinity));
                double to = host.DesiredSize.Height - host.Margin.Top - host.Margin.Bottom;

                var grow = new DoubleAnimation(from, to, Dur(320)) { EasingFunction = Spring };
                grow.Completed += (_, _) =>
                {
                    if (_collapsing.Contains(host)) return;
                    if (_revealing.TryGetValue(host, out bool showing) && showing) _revealing.Remove(host);
                    // Hand back to auto height so later content changes (e.g. the calendar) can resize it.
                    host.BeginAnimation(HeightProperty, null);
                    host.Height = double.NaN;
                    if (scrollIntoView) host.BringIntoView();
                };
                _revealing[host] = true;
                host.BeginAnimation(HeightProperty, grow);
                host.BeginAnimation(OpacityProperty, Anim(from == 0 ? 0 : null, 1, 220));
                return;
            }

            if (host.Visibility != Visibility.Visible || _collapsing.Contains(host)) return;
            _collapsing.Add(host);
            _revealing[host] = false;
            var shrink = new DoubleAnimation(host.ActualHeight, 0, Dur(240))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };
            shrink.Completed += (_, _) =>
            {
                if (!_collapsing.Remove(host)) return; // reopened mid-way
                _revealing.Remove(host);
                host.Visibility = Visibility.Collapsed;
                host.BeginAnimation(HeightProperty, null);
                host.Height = double.NaN;
            };
            host.BeginAnimation(HeightProperty, shrink);
            host.BeginAnimation(OpacityProperty, Anim(null, 0, 160, Accelerate));
        }

        /// <summary>Jumps a host straight to its shown/hidden end state, cancelling any animation.</summary>
        private void SnapToEnd(FrameworkElement host, bool show)
        {
            _collapsing.Remove(host);
            _revealing.Remove(host);
            host.BeginAnimation(HeightProperty, null);
            host.BeginAnimation(OpacityProperty, null);
            host.Height = double.NaN;
            host.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---------------------------------------------------------------- calendar motion

        /// <summary>Paging months slides the grid in the direction of travel (next comes from the right).</summary>
        private void DeadlineCalendar_DisplayDateChanged(object sender, CalendarDateChangedEventArgs e)
        {
            if (e.AddedDate is not DateTime added || e.RemovedDate is not DateTime removed) return;
            bool samePage = DeadlineCalendar.DisplayMode switch
            {
                CalendarMode.Month => added.Year == removed.Year && added.Month == removed.Month,
                CalendarMode.Year => added.Year == removed.Year,
                _ => added.Year / 10 == removed.Year / 10
            };
            if (samePage) return;
            DimPastCalendarCells();

            var grid = CalendarPart(DeadlineCalendar.DisplayMode == CalendarMode.Month ? "PART_MonthView" : "PART_YearView");
            if (grid == null) return;
            var shift = new TranslateTransform();
            grid.RenderTransform = shift;
            shift.BeginAnimation(TranslateTransform.XProperty, Anim(added > removed ? 28 : -28, 0, 340));
            grid.BeginAnimation(OpacityProperty, Anim(0.2, 1, 240));
        }

        /// <summary>
        /// Switching between days, months and years zooms: going up to a broader view pulls back
        /// (content settles from slightly larger), drilling into a narrower one pushes in.
        /// </summary>
        private void DeadlineCalendar_DisplayModeChanged(object sender, CalendarModeChangedEventArgs e)
        {
            DimPastCalendarCells();
            var grid = CalendarPart(e.NewMode == CalendarMode.Month ? "PART_MonthView" : "PART_YearView");
            if (grid == null) return;
            var zoom = new ScaleTransform();
            grid.RenderTransformOrigin = new Point(0.5, 0.5);
            grid.RenderTransform = zoom;
            double from = e.NewMode > e.OldMode ? 1.08 : 0.92;
            zoom.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(from, 1, 320));
            zoom.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(from, 1, 320));
            grid.BeginAnimation(OpacityProperty, Anim(0, 1, 220));
        }

        /// <summary>
        /// In the month/year views, months or years that are entirely in the past are dimmed and
        /// can't be clicked (days have WPF's blackout for this; month and year cells don't).
        /// Runs after the calendar has rebuilt its cells.
        /// </summary>
        private void DimPastCalendarCells()
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (CalendarPart("PART_YearView") is not Panel cells) return;
                var today = DateTime.Today;
                var mode = DeadlineCalendar.DisplayMode;
                foreach (var cell in cells.Children.OfType<CalendarButton>())
                {
                    bool past = cell.DataContext is DateTime d && mode != CalendarMode.Month &&
                                (mode == CalendarMode.Year
                                    ? d.Year < today.Year || (d.Year == today.Year && d.Month < today.Month)
                                    : d.Year < today.Year);
                    cell.Opacity = past ? 0.3 : 1;
                    cell.IsHitTestVisible = !past;
                }
            });
        }

        private FrameworkElement? CalendarPart(string name)
        {
            if (DeadlineCalendar.Template?.FindName("PART_CalendarItem", DeadlineCalendar) is not CalendarItem item) return null;
            return item.Template?.FindName(name, item) as FrameworkElement;
        }

        // ---------------------------------------------------------------- deadline picker

        private void DeadlineKind_Checked(object sender, RoutedEventArgs e)
        {
            if (DeadlineCalendar == null) return; // fires during InitializeComponent

            bool pick = DuePick.IsChecked == true;
            if (pick && DeadlineCalendar.SelectedDate == null)
            {
                var today = DateTime.Today;
                RestrictCalendarToFuture(keep: null);
                DeadlineCalendar.SelectedDate = today.AddDays(1);
                DeadlineCalendar.DisplayDate = today.AddDays(1);
            }
            // The calendar unrolls under the chips; with a long list, scroll so all of it is visible.
            Reveal(CalendarHost, pick, scrollIntoView: true);
            UpdateDeadlineIndicator();
        }

        private void DeadlineCalendar_SelectedDatesChanged(object sender, SelectionChangedEventArgs e) =>
            UpdateDeadlineIndicator();

        private void DeadlineCalendar_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            // WPF's Calendar keeps mouse capture after a click, which would swallow the next click
            // elsewhere. Selection happens on mouse-down, so the capture can only be released here.
            if (Mouse.Captured is CalendarItem) Mouse.Capture(null);
        }

        private DateOnly? ReadDeadline(DateOnly today)
        {
            if (DueToday.IsChecked == true) return today;
            if (DueTomorrow.IsChecked == true) return today.AddDays(1);
            if (DueWeek.IsChecked == true) return today.AddDays(7);
            if (DuePick.IsChecked == true)
                return DeadlineCalendar.SelectedDate is DateTime d ? DateOnly.FromDateTime(d) : today.AddDays(1);
            return null;
        }

        private void LoadDeadline(DateOnly? deadline)
        {
            // As with the repeat picker: an open deadline panel stays open and updates in place.
            ResetDeadline(closePanel: deadline == null);
            if (deadline is not DateOnly d) return;

            var today = Today;
            int days = d.DayNumber - today.DayNumber;
            if (days == 0) DueToday.IsChecked = true;
            else if (days == 1) DueTomorrow.IsChecked = true;
            else if (days == 7) DueWeek.IsChecked = true;
            else
            {
                var date = d.ToDateTime(TimeOnly.MinValue);
                // Allow an existing (possibly overdue) date to stay selected.
                RestrictCalendarToFuture(keep: date);
                DeadlineCalendar.SelectedDate = date;
                DeadlineCalendar.DisplayDate = date;
                DuePick.IsChecked = true;
            }
        }

        private void ResetDeadline(bool closePanel = true)
        {
            DeadlineCalendar.SelectedDate = null;
            RestrictCalendarToFuture(keep: null);
            DueNone.IsChecked = true;
            if (closePanel) DeadlineToggle.IsChecked = false;
            UpdateDeadlineIndicator();
        }

        /// <summary>
        /// Past days stay visible but dimmed and unselectable, so the month grid has no empty rows.
        /// Navigation starts at this month (or at <paramref name="keep"/>'s month, an existing overdue
        /// deadline, which itself stays selectable). Call before setting SelectedDate: a blacked-out
        /// range may not contain the selection.
        /// </summary>
        private void RestrictCalendarToFuture(DateTime? keep)
        {
            var today = DateTime.Today;
            var earliest = keep is DateTime k && k < today ? k : today;
            // Start at the beginning of the decade so the month and year views have no hidden gaps;
            // the past is shown dimmed and can't be chosen (as in Apple's date pickers).
            var start = new DateTime(earliest.Year - earliest.Year % 10, 1, 1);
            var yesterday = today.AddDays(-1);

            DeadlineCalendar.BlackoutDates.Clear();
            DeadlineCalendar.DisplayDateStart = start;

            void Blackout(DateTime from, DateTime to)
            {
                if (from <= to) DeadlineCalendar.BlackoutDates.Add(new CalendarDateRange(from, to));
            }

            if (keep is DateTime past && past < today)
            {
                Blackout(start, past.AddDays(-1));
                Blackout(past.AddDays(1), yesterday);
            }
            else
            {
                Blackout(start, yesterday);
            }
        }

        /// <summary>Tints the deadline button while a deadline is chosen and summarizes it in the panel.</summary>
        private void UpdateDeadlineIndicator()
        {
            var deadline = ReadDeadline(Today);
            DeadlineSummary.Text = deadline is DateOnly d ? DeadlineMath.Describe(d, Today) : "No deadline";
            if (deadline == null)
            {
                DeadlineToggle.ClearValue(ForegroundProperty);
                // Not ClearValue: the grey is a local XAML value, so clearing would fall back to black text.
                DeadlineSummary.Foreground = (Brush)FindResource("TextSecondary");
            }
            else
            {
                var red = (Brush)FindResource("DeadlineBrush");
                DeadlineToggle.Foreground = red;
                DeadlineSummary.Foreground = red;
            }
        }

        private void RepeatKind_Checked(object sender, RoutedEventArgs e)
        {
            if (IntervalPanel == null || RepeatSummary == null) return; // fires during InitializeComponent

            var today = DateTime.Today;
            if (RepWeekly.IsChecked == true && !DayChips().Any(c => c.IsChecked == true))
            {
                var chip = DayChips().FirstOrDefault(c => (string)c.Tag == ((int)today.DayOfWeek).ToString());
                if (chip != null) chip.IsChecked = true;
            }
            if (RepMonthly.IsChecked == true && MonthDayBox.Text.Length == 0)
                MonthDayBox.Text = today.Day.ToString();
            if (RepEvery.IsChecked == true && IntervalBox.Text.Length == 0)
                IntervalBox.Text = "2";

            // The options for the chosen kind unroll beneath the chips; the others roll away.
            Reveal(WeekdayPanel, RepWeekly.IsChecked == true);
            Reveal(MonthlyPanel, RepMonthly.IsChecked == true);
            Reveal(IntervalPanel, RepEvery.IsChecked == true);
            UpdateRepeatIndicator();
        }

        private void RepeatDetail_Changed(object sender, RoutedEventArgs e)
        {
            if (RepeatSummary == null) return;

            // A weekly rule needs at least one day: unticking the last one is undone, so the chips
            // always show exactly what will be saved (instead of silently falling back to today).
            if (e.RoutedEvent == ToggleButton.UncheckedEvent && RepWeekly.IsChecked == true &&
                e.OriginalSource is ToggleButton chip && !DayChips().Any(c => c.IsChecked == true))
            {
                chip.IsChecked = true;
                return; // re-checking raises Checked, which refreshes the summary
            }
            UpdateRepeatIndicator();
        }

        /// <summary>
        /// Keeps ↻ tinted while a repeat is chosen (so it stays visible after the panel closes) and
        /// spells the rule out in the panel header, e.g. "Mon Wed" or "Every 3 days".
        /// </summary>
        private void UpdateRepeatIndicator()
        {
            var rule = ReadRecurrence(Today);
            RepeatSummary.Text = rule?.Describe() ?? "Never";
            if (rule == null)
            {
                RepeatToggle.ClearValue(ForegroundProperty);
                RepeatSummary.Foreground = (Brush)FindResource("TextSecondary");
            }
            else
            {
                var green = (Brush)FindResource("AccentBrush");
                RepeatToggle.Foreground = green;
                RepeatSummary.Foreground = green;
            }
        }

        private System.Collections.Generic.IEnumerable<ToggleButton> DayChips() =>
            WeekdayChips.Children.OfType<ToggleButton>();

        private Recurrence? ReadRecurrence(DateOnly today)
        {
            // The panel's open/closed state is only visibility; the chosen chip is what counts.
            if (RepNone.IsChecked == true) return null;

            if (RepDaily.IsChecked == true)
                return new Recurrence { Kind = RecurrenceKind.Daily };

            if (RepWeekly.IsChecked == true)
            {
                var days = DayChips().Where(c => c.IsChecked == true)
                    .Select(c => (DayOfWeek)int.Parse((string)c.Tag)).ToList();
                if (days.Count == 0) days.Add(today.DayOfWeek);
                return new Recurrence { Kind = RecurrenceKind.Weekdays, Days = days };
            }

            if (RepMonthly.IsChecked == true)
            {
                int day = int.TryParse(MonthDayBox.Text, out var d) ? Math.Clamp(d, 1, 31) : today.Day;
                return new Recurrence { Kind = RecurrenceKind.Monthly, DayOfMonth = day };
            }

            int interval = int.TryParse(IntervalBox.Text, out var n) ? Math.Clamp(n, 1, 365) : 2;
            return new Recurrence { Kind = RecurrenceKind.EveryNDays, Interval = interval };
        }

        private void LoadRecurrence(Recurrence? r)
        {
            // Keep an open panel open while its fields change: closing and immediately reopening it
            // would remeasure it mid-animation. The options simply morph in place instead.
            ResetRepeat(closePanel: r == null);
            if (r == null) return;

            switch (r.Kind)
            {
                case RecurrenceKind.Weekdays:
                    foreach (var chip in DayChips())
                        chip.IsChecked = r.Days.Contains((DayOfWeek)int.Parse((string)chip.Tag));
                    RepWeekly.IsChecked = true;
                    break;
                case RecurrenceKind.Monthly:
                    MonthDayBox.Text = r.DayOfMonth.ToString();
                    RepMonthly.IsChecked = true;
                    break;
                case RecurrenceKind.EveryNDays:
                    IntervalBox.Text = r.Interval.ToString();
                    RepEvery.IsChecked = true;
                    break;
                default:
                    RepDaily.IsChecked = true;
                    break;
            }
            RepeatToggle.IsChecked = true;
        }

        private void ResetRepeat(bool closePanel = true)
        {
            RepNone.IsChecked = true;
            foreach (var chip in DayChips()) chip.IsChecked = false;
            MonthDayBox.Text = "";
            IntervalBox.Text = "";
            if (closePanel) RepeatToggle.IsChecked = false;
        }

        // ---------------------------------------------------------------- upcoming section

        private void UpcomingToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (UpcomingList == null) return;
            bool open = UpcomingToggle.IsChecked == true;
            // The list unrolls beneath its header (and rolls back up) instead of popping.
            Reveal(UpcomingList, open);
            var rotate = (RotateTransform)UpcomingChevron.RenderTransform;
            rotate.BeginAnimation(RotateTransform.AngleProperty, Anim(null, open ? 90 : 0, IsLoaded ? 260 : 0));
            if (IsLoaded) RestartBoundsTimer();
        }
    }
}
