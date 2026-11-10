using System;
using System.Diagnostics;
using System.IO;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Bwc.Spike.ListApp.Bench;
using Bwc.Spike.ListApp.Controls;
using Bwc.Spike.ListApp.Model;
using Windows.Graphics;

namespace Bwc.Spike.ListApp;

public sealed class MainWindow : Window
{
    private readonly SpikeOptions _options;
    private readonly DetailsList _list;
    private bool _started;

    public MainWindow(SpikeOptions options)
    {
        _options = options;
        Title = "BWC S3 list spike";

        _list = new DetailsList();
        _list.Configure(options.RowHeight, options.HeaderHeight, options.FontSize, options.IconSize, options.ExtendedSelection);
        Content = _list;
        _list.Loaded += OnListLoaded;
    }

    private async void OnListLoaded(object sender, RoutedEventArgs e)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        try
        {
            // The window is sized in DIPs (960x600) and converted to physical pixels with the monitor scale.
            double scale = _list.XamlRoot.RasterizationScale;
            AppWindow.Resize(new SizeInt32((int)Math.Round(960 * scale), (int)Math.Round(600 * scale)));

            _list.SetItems(SampleData.Generate(_options.Rows));
            var recorder = new Bench.FrameRecorder(_list);
            await recorder.NextFrame();
            double startupMs = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;

            if (_options.ShotPath != null)
            {
                await BenchRunner.SaveScreenshotAsync(_list, _options.ShotPath);
            }

            if (_options.BenchOut != null)
            {
                bool ok = await BenchRunner.RunAsync(_list, recorder, _options, startupMs);
                Environment.Exit(ok ? 0 : 3);
            }
        }
        catch (Exception ex)
        {
            if (_options.BenchOut != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_options.BenchOut));
                File.WriteAllText(_options.BenchOut,
                    System.Text.Json.JsonSerializer.Serialize(new { error = ex.GetType().Name + ": " + ex.Message }));
            }

            Environment.Exit(2);
        }
    }
}
