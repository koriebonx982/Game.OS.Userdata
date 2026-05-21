using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;

namespace PS5_OS
{
    public partial class App : Application
    {
        private const int HOTKEY_ID = 9001;
        private HwndSource? _source;
        private GuideMenu? _guideMenuWindow;
        private ControllerService? _controllerService; // Add this field


        protected override void OnStartup(StartupEventArgs e)
        {
            // Global exception handlers to capture startup crashes before UI appears.
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            this.DispatcherUnhandledException += App_DispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

            base.OnStartup(e);
            _controllerService = new ControllerService();

            // Start SteamDb update in background (fire-and-forget).
            _ = Task.Run(async () =>
            {
                try
                {
                    await SteeamDB.InitializeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogException(ex, "SteeamDB.InitializeAsync");
                }
            });

            // --- First Run logic ---
            var firstRunMarker = Path.Combine(AppContext.BaseDirectory, "Data", "FirstRunComplete.txt");
            if (!File.Exists(firstRunMarker))
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var firstRun = new FirstRunWindow();
                firstRun.Closed += (s, ev) =>
                {
                    // Mark first run as complete
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(firstRunMarker)!);
                        File.WriteAllText(firstRunMarker, DateTime.UtcNow.ToString("u"));
                    }
                    catch { }
                    ShowIntroOrMain();
                };
                firstRun.Show();
                return;
            }

            ShowIntroOrMain();
        }

        private void ShowIntroOrMain()
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            var introPath = Path.Combine(AppContext.BaseDirectory, "Data", "Intro", "Intro.mp4");
            if (File.Exists(introPath))
            {
                var main = new MainWindow
                {
                    WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize,
                    WindowState = WindowState.Maximized,
                    Topmost = true
                };
                var introControl = new IntroPlayerControl();
                introControl.IntroFinished += (s, e) =>
                {
                    main.Topmost = false;
                    main.Content = new LoginPage();
                };
                main.Content = introControl;
                MainWindow = main;
                main.Show();
                return;
            }

            ShowMainWindow();
            // RegisterHotkeyForWindow(MainWindow);

            // Only use the message-only window for hotkey registration:
            var parameters = new HwndSourceParameters("HotkeyMessageWindow")
            {
                WindowStyle = 0,
                Width = 0,
                Height = 0,
                PositionX = 0,
                PositionY = 0,
                ParentWindow = IntPtr.Zero
            };
            _source = new HwndSource(parameters);
            _source.AddHook(HwndHook);
            RegisterHotKey(_source.Handle, HOTKEY_ID, MOD_CONTROL | MOD_SHIFT, 0); // 0 = no extra key
        }


        // Public so IntroWindow can call it after playback finishes.
        // This now shows the LoginPage first; LoginPage will navigate to Dashboard after successful login.
        public void ShowMainWindow()
        {
            var main = new MainWindow
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                WindowState = WindowState.Maximized,
                Topmost = false
            };

            // Show login first; LoginPage code-behind already switches to Dashboard on successful login.
            main.Content = new LoginPage();

            // Make this the application's main window (important for ShutdownMode)
            MainWindow = main;
            main.Show();
        }

        private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            LogException(e.Exception, "DispatcherUnhandledException");
            // Prevent default crash dialog so we can provide a friendly message
            MessageBox.Show($"An unexpected error occurred: {e.Exception.Message}\n\nSee crash.log in application folder for details.", "Application Error", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
            ShutdownIfNeeded();
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception;
            LogException(ex, "CurrentDomain_UnhandledException");
            MessageBox.Show($"A fatal error occurred: {ex?.Message ?? "unknown"}\n\nSee crash.log in application folder for details.", "Fatal Error", MessageBoxButton.OK, MessageBoxImage.Error);
            ShutdownIfNeeded();
        }

        // Match nullable signature of EventHandler<T> in modern .NET (sender may be null)
        private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            LogException(e.Exception, "TaskScheduler_UnobservedTaskException");
            e.SetObserved();
        }

        private static void LogException(Exception? ex, string source)
        {
            try
            {
                var logFile = Path.Combine(AppContext.BaseDirectory, "crash.log");
                using var sw = new StreamWriter(logFile, append: true);
                sw.WriteLine("-----");
                sw.WriteLine(DateTime.UtcNow.ToString("u") + "  Source: " + source);
                if (ex != null)
                {
                    sw.WriteLine(ex.ToString());
                }
                else
                {
                    sw.WriteLine("Exception object was null.");
                }
                sw.Flush();
            }
            catch
            {
                // swallow logging errors to avoid recursive failures
            }
        }

        private static void ShutdownIfNeeded()
        {
            try
            {
                // Give user a chance to read message then exit cleanly
                Application.Current?.Dispatcher?.Invoke(() =>
                {
                    try { Application.Current?.Shutdown(); } catch { }
                });
            }
            catch { }
        }

        // Global hotkey functionality

        protected override void OnExit(ExitEventArgs e)
        {
            if (_source != null)
                UnregisterHotKey(_source.Handle, HOTKEY_ID);
            base.OnExit(e);
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_HOTKEY = 0x0312;
            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
            {
                if (_guideMenuWindow == null)
                {
                    _guideMenuWindow = new GuideMenu
                    {
                        Topmost = true,
                        WindowState = WindowState.Normal // or Maximized if you want full screen
                    };
                    _guideMenuWindow.RequestClose += GuideMenuWindow_RequestClose;
                    _guideMenuWindow.Closed += GuideMenuWindow_Closed;
                    _guideMenuWindow.Show();
                    _guideMenuWindow.Activate();
                    _guideMenuWindow.Focus();
                }
                else
                {
                    _guideMenuWindow.Close();
                }
                handled = true;
            }
            return IntPtr.Zero;
        }

        private void GuideMenuWindow_RequestClose(object? sender, EventArgs e)
        {
            _guideMenuWindow?.Close();
        }

        private void GuideMenuWindow_Closed(object? sender, EventArgs e)
        {
            _guideMenuWindow = null;
        }

        // P/Invoke for global hotkey
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, int vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_WIN = 0x0008;
    }
}