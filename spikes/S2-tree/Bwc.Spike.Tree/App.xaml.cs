using System;
using System.IO;
using Microsoft.UI.Xaml;

namespace Bwc.Spike.Tree
{
    public partial class App : Application
    {
        private Window? _window;

        public App()
        {
            InitializeComponent();
            // WinUI turns an exception that escapes a XAML callback into a native fail-fast (0xC000027B),
            // which hides the message. Record it next to the exe first.
            UnhandledException += (_, e) => LogError("UnhandledException", e.Exception);
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            try
            {
                LaunchOptions options = LaunchOptions.Parse(Environment.GetCommandLineArgs());
                _window = new MainWindow(options);
                _window.Activate();
            }
            catch (Exception ex)
            {
                LogError("OnLaunched", ex);
                Environment.Exit(3);
            }
        }

        private static void LogError(string where, Exception ex)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(AppContext.BaseDirectory, "bwc-s2-error.log"),
                    where + ": " + ex.ToString() + Environment.NewLine);
            }
            catch
            {
                // Logging must never throw.
            }
        }
    }
}
