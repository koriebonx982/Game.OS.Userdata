using System;
using System.ComponentModel;
using System.IO;
using System.Windows;

namespace PS5_OS
{
    public partial class IntroWindow : Window
    {
        private readonly string _introPath;
        private bool _finished;

        public IntroWindow()
        {
            InitializeComponent();

            _introPath = Path.Combine(AppContext.BaseDirectory, "Data", "Intro", "Intro.mp4");
            Loaded += IntroWindow_Loaded;
            Closing += Window_Closing;
        }

        private void IntroWindow_Loaded(object? sender, RoutedEventArgs e)
        {
            try
            {
                if (File.Exists(_introPath))
                {
                    IntroMedia.Source = new Uri(_introPath, UriKind.Absolute);
                    IntroMedia.Play();
                }
                else
                {
                    // If file missing, fall back to main window so app doesn't hang.
                    FinishAndShowMain();
                }
            }
            catch
            {
                // On any playback initialization failure, proceed to main window.
                FinishAndShowMain();
            }
        }

        private void IntroMedia_MediaEnded(object? sender, RoutedEventArgs e)
        {
            FinishAndShowMain();
        }

        private void IntroMedia_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
        {
            // Log if needed, then proceed. User skipping is not allowed.
            FinishAndShowMain();
        }

        // Prevent user from closing the intro window while playback is running.
        private void Window_Closing(object? sender, CancelEventArgs e)
        {
            if (!_finished)
            {
                // Cancel closure attempts (Alt+F4, close button, etc.) while intro is playing.
                e.Cancel = true;
            }
        }

        private void FinishAndShowMain()
        {
            try
            {
                IntroMedia.Stop();
            }
            catch { /* ignore */ }

            _finished = true;

            if (Application.Current is App app)
            {
                app.ShowMainWindow();
            }

            // Now that _finished is true, closing is allowed.
            Close();
        }
    }
}