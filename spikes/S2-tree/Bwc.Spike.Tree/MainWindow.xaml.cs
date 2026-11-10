using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Bwc.Spike.Tree
{
    public sealed partial class MainWindow : Window
    {
        private readonly LaunchOptions _options;
        private bool _started;
        private double _treeBuildMs;
        private double _startupMs;
        private double _privateMbAfterLoad;

        public MainWindow(LaunchOptions options)
        {
            _options = options;
            InitializeComponent();
            FrameClock.Start();
            Title = $"BWC spike S2 - {options.Rows} keys @ {options.RowHeight}px rows";
            AppWindow.Resize(new Windows.Graphics.SizeInt32(640, 760));
            Tree.Loaded += OnTreeLoaded;
        }

        private async void OnTreeLoaded(object sender, RoutedEventArgs e)
        {
            if (_started) return;
            _started = true;

            try
            {
                var sw = Stopwatch.StartNew();
                var sample = new SampleTree(_options.Rows);
                Tree.Load(sample.Root, sample.Hkcr);
                _treeBuildMs = sw.Elapsed.TotalMilliseconds;
                _privateMbAfterLoad = PrivateMB();

                CompositionTarget.Rendering += OnFirstFrame;
                Tree.Focus(FocusState.Programmatic);

                if (_options.BenchOut is null && _options.Screenshot is null)
                {
                    return; // interactive run: leave the window open
                }

                BenchResult? result = null;
                if (_options.BenchOut is not null)
                {
                    var bench = new TreeBench(Tree, sample, _options.Steps);
                    result = await bench.RunAsync();
                    FillEnvironment(result);
                    WriteJson(_options.BenchOut, result);
                }

                if (_options.Screenshot is not null)
                {
                    await PrepareScreenshotSceneAsync(sample);
                    await Screenshot.SaveAsync(Tree, _options.Screenshot, Tree.XamlRoot?.RasterizationScale ?? 1.0);
                }

                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                string message = ex.GetType().Name + ": " + ex.Message;
                if (_options.BenchOut is not null)
                {
                    WriteJson(_options.BenchOut, new { error = message, stack = ex.ToString() });
                }
                Console.Error.WriteLine(ex.ToString());
                Environment.Exit(2);
            }
        }

        private void OnFirstFrame(object? sender, object e)
        {
            if (_startupMs != 0) return;
            _startupMs = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
            CompositionTarget.Rendering -= OnFirstFrame;
        }

        private void FillEnvironment(BenchResult result)
        {
            double scale = Tree.XamlRoot?.RasterizationScale ?? 1.0;
            result.Rows = _options.Rows;
            result.RowHeight = TreeMetrics.RowHeight;
            result.FontSize = TreeMetrics.FontSize;
            result.DpiScale = scale;
            result.Dpi = (int)Math.Round(scale * 96);
            result.Arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
            result.StartupMs = _startupMs;
            result.TreeBuildMs = _treeBuildMs;
            result.PrivateMBAfterLoad = _privateMbAfterLoad;
            result.PrivateMB = PrivateMB();
            result.Notify = FlatRowList.Style switch
            {
                NotifyStyle.Reset => "reset",
                NotifyStyle.RangeCountOnly => "range-count-only",
                _ => "range",
            };
        }

        /// <summary>Expands two keys, selects one child, and scrolls to the top so the PNG shows nesting, glyphs and selection.</summary>
        private async Task PrepareScreenshotSceneAsync(SampleTree sample)
        {
            TreeRow first = sample.Hkcr.Children![0];
            Tree.Expand(first);
            Tree.Expand(sample.Hkcr.Children![4]);
            TreeRow selected = first.Children![1];
            Tree.SelectRow(selected);
            Tree.ScrollHost.ChangeView(null, 0, null, disableAnimation: true);
            await FrameClock.FramesAsync(3);
            Tree.RevealIndex(Tree.IndexOfRow(selected));
            await FrameClock.FramesAsync(3);
        }

        private static double PrivateMB()
        {
            return Process.GetCurrentProcess().PrivateMemorySize64 / 1048576.0;
        }

        private static void WriteJson(string path, object value)
        {
            string full = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
