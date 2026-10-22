using System;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using TranslucentTodo.Services;
using Forms = System.Windows.Forms;

namespace TranslucentTodo
{
    public partial class App : Application
    {
        private const string MutexName = @"Local\TranslucentTodo.SingleInstance";
        private const string ShowEventName = @"Local\TranslucentTodo.Show";

        private Mutex? _mutex;
        private EventWaitHandle? _showEvent;
        private RegisteredWaitHandle? _showWait;
        private Forms.NotifyIcon? _tray;
        private Forms.ToolStripMenuItem? _autostartItem;
        private MainWindow? _window;
        private AppSettings _settings = new();
        private bool _exiting;

        protected override void OnStartup(StartupEventArgs e)
        {
            _mutex = new Mutex(true, MutexName, out bool isFirst);
            if (!isFirst)
            {
                // Already running: ask the existing instance to show itself, then quit.
                try
                {
                    using var ev = EventWaitHandle.OpenExisting(ShowEventName);
                    ev.Set();
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                }
                _mutex.Dispose();
                _mutex = null;
                Shutdown();
                return;
            }

            base.OnStartup(e);

            DispatcherUnhandledException += (_, args) =>
            {
                // A widget that lives in the background should log and keep going rather than vanish.
                LogError(args.Exception);
                args.Handled = true;
            };

            // A small widget doesn't need Direct3D; software rendering skips loading the GPU stack.
            RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

            _settings = SettingsStore.Load();
            StartupManager.Apply(_settings.StartWithWindows);

            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent,
                (_, _) => Dispatcher.BeginInvoke(ShowWidget), null, Timeout.Infinite, executeOnlyOnce: false);

            CreateTray();

            _window = new MainWindow(_settings);
            _window.Show();

            // Sign-out / shutdown: finish pending check-offs and remove the tray icon cleanly.
            SessionEnding += (_, _) => ExitApp();
        }

        private void CreateTray()
        {
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Show widget", null, (_, _) => ShowWidget());
            _autostartItem = new Forms.ToolStripMenuItem("Start with Windows")
            {
                Checked = _settings.StartWithWindows,
                CheckOnClick = true
            };
            _autostartItem.CheckedChanged += (_, _) =>
            {
                _settings.StartWithWindows = _autostartItem.Checked;
                StartupManager.Apply(_settings.StartWithWindows);
                SettingsStore.Save(_settings);
            };
            menu.Items.Add(_autostartItem);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Exit", null, (_, _) => ExitApp());

            _tray = new Forms.NotifyIcon
            {
                Text = "Translucent To-Do",
                Icon = LoadIcon(),
                ContextMenuStrip = menu,
                Visible = true
            };
            _tray.MouseClick += (_, args) =>
            {
                if (args.Button == Forms.MouseButtons.Left)
                    ShowWidget();
            };
        }

        private static System.Drawing.Icon LoadIcon()
        {
            var info = GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (info != null)
            {
                using var stream = info.Stream;
                return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
            }
            return System.Drawing.SystemIcons.Application;
        }

        private void ShowWidget() => _window?.ShowWidget();

        private static void LogError(Exception ex)
        {
            try
            {
                System.IO.Directory.CreateDirectory(JsonFile.Folder);
                System.IO.File.AppendAllText(System.IO.Path.Combine(JsonFile.Folder, "error.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{Environment.NewLine}");
            }
            catch (Exception)
            {
            }
        }

        private void ExitApp()
        {
            // Reachable from both the tray menu and sign-out; closing the window twice would throw.
            if (_exiting) return;
            _exiting = true;
            _window?.PrepareForExit();
            _showWait?.Unregister(null);
            _showEvent?.Dispose();
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }
            Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
            base.OnExit(e);
        }
    }
}
