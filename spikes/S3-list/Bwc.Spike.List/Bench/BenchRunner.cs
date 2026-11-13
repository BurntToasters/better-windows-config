using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;
using Bwc.Spike.ListApp.Controls;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Bwc.Spike.ListApp.Bench;

/// <summary>
/// Scripted run: idle baseline, warm-up, scroll 300 steps down and 300 back, jump to middle, sort Name desc then asc,
/// resize Data 20x by 10 px, auto-size Data, select-all. Writes one JSON report.
/// Operations are timed as: operation + UpdateLayout (synchronous CPU cost), then one composition frame is awaited.
/// Frame intervals come from CompositionTarget.Rendering RenderingTime deltas.
/// </summary>
internal static class BenchRunner
{
    private const int ScrollSteps = 300;
    private const int IdleFrames = 60;
    private const int WarmupSteps = 3;
    private const int ResizeCount = 20;
    private const int ResizeStep = 10;
    private const int DataColumn = 2;

    /// <summary>Runs the scripted measurement, writes the JSON report, and returns true when no error was recorded.</summary>
    public static async Task<bool> RunAsync(DetailsList list, FrameRecorder rec, SpikeOptions options, double startupMs)
    {
        var report = new BenchReport
        {
            Rows = list.Count,
            RowHeight = options.RowHeight,
            HeaderHeight = options.HeaderHeight,
            FontSize = options.FontSize,
            Arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            StartupMs = Round(startupMs),
        };

        var checks = new Dictionary<string, object>();
        report.Checks = checks;
        try
        {
            double scale = list.XamlRoot.RasterizationScale;
            report.Dpi = (int)Math.Round(scale * 96);
            checks["scale"] = scale;

            // Idle baseline: what a frame costs when nothing is happening on this display.
            rec.Start();
            for (int i = 0; i < IdleFrames; i++)
            {
                await rec.NextFrame();
            }

            rec.Stop();
            List<double> idle = rec.Deltas.OrderBy(d => d).ToList();
            checks["idleFrameCount"] = idle.Count;
            report.IdleP95FrameMs = Round(Percentile(idle, 0.95));
            report.IdleMaxFrameMs = Round(idle.Count == 0 ? 0 : idle[idle.Count - 1]);

            double max = list.ScrollableHeight;
            checks["scrollableHeightStart"] = Round(max);

            // Untimed, unrecorded: move to a Data order so the timed sorts start from a non-sorted permutation
            // (sorting an already-sorted array would flatter every sort number).
            list.Sort(DataColumn, false);

            // Warm-up (not recorded): first-use JIT and text caches for the scroll path.
            for (int i = 1; i <= WarmupSteps; i++)
            {
                list.ScrollVerticalTo(max * i / ScrollSteps);
                await rec.NextFrame();
            }

            // Settle the heap before recording so a collection triggered by setup does not land in a measured frame.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await rec.NextFrame();

            int gen2Before = GC.CollectionCount(2);
            int gen0Before = GC.CollectionCount(0);
            TimeSpan pauseBefore = GC.GetTotalPauseDuration();
            long prepareBefore = list.PrepareCount;
            long clearBefore = list.ClearCount;

            rec.Start();
            var scrollLayout = new List<double>(2 * ScrollSteps + 1);
            for (int i = 1; i <= ScrollSteps; i++)
            {
                scrollLayout.Add(TimeSync(() => list.ScrollVerticalTo(max * i / ScrollSteps), list));
                await rec.NextFrame();
            }

            for (int i = ScrollSteps - 1; i >= 0; i--)
            {
                scrollLayout.Add(TimeSync(() => list.ScrollVerticalTo(max * i / ScrollSteps), list));
                await rec.NextFrame();
            }

            list.ScrollVerticalTo(max / 2);
            await rec.NextFrame();

            checks["scrollPhasePrepares"] = list.PrepareCount - prepareBefore;
            checks["scrollPhaseClears"] = list.ClearCount - clearBefore;
            List<double> scrollSorted = scrollLayout.OrderBy(d => d).ToList();
            report.ScrollLayoutMsMedian = Round(Percentile(scrollSorted, 0.5));
            report.ScrollLayoutMsP95 = Round(Percentile(scrollSorted, 0.95));
            report.ScrollLayoutMsMax = Round(scrollSorted[scrollSorted.Count - 1]);

            (double sortDesc, double bgDesc) = await TimeSortAsync(list, 0, true, rec);
            checks["firstNameAfterDescSort"] = list.ItemAt(0).Name;
            (double sortAsc, double bgAsc) = await TimeSortAsync(list, 0, false, rec);
            checks["firstNameAfterAscSort"] = list.ItemAt(0).Name;
            report.SortDescMs = Round(sortDesc);
            report.SortAscMs = Round(sortAsc);
            report.SortMs = Round(Math.Max(sortDesc, sortAsc));
            report.SortBackgroundMs = Round(Math.Max(bgDesc, bgAsc));

            double startWidth = list.Columns.Width(DataColumn);
            long preparesBeforeResize = list.PrepareCount;
            var resizeMs = new List<double>(ResizeCount);
            for (int k = 0; k < ResizeCount; k++)
            {
                double target = list.Columns.Width(DataColumn) + ResizeStep;
                resizeMs.Add(await TimeOpAsync(() => list.ResizeColumn(DataColumn, target), list, rec));
            }

            // A width change must re-arrange realized rows, not re-template them: expect 0 element prepares here.
            checks["resizePhasePrepares"] = list.PrepareCount - preparesBeforeResize;

            report.ResizeMsMax = Round(resizeMs.Max());
            report.ResizeMsAvg = Round(resizeMs.Average());
            checks["dataWidthBeforeResizes"] = startWidth;
            checks["dataWidthAfterResizes"] = list.Columns.Width(DataColumn);
            checks["dataWidthExpectedAfterResizes"] = startWidth + ResizeCount * ResizeStep;

            double autoWidth = 0;
            report.AutoSizeMs = Round(await TimeOpAsync(() => autoWidth = list.AutoSizeColumn(DataColumn), list, rec));
            checks["dataWidthAfterAutoSize"] = autoWidth;

            int selected = 0;
            report.SelectAllMs = Round(await TimeOpAsync(() => selected = list.SelectAll(), list, rec));
            checks["selectedCountAfterSelectAll"] = selected;
            checks["rowsExpected"] = list.Count;

            rec.Stop();
            checks["realizedRowsAtEnd"] = list.RealizedCount;
            checks["recordedWindowGen0"] = GC.CollectionCount(0) - gen0Before;
            checks["recordedWindowGen2"] = GC.CollectionCount(2) - gen2Before;
            checks["recordedWindowGcPauseMs"] = Round((GC.GetTotalPauseDuration() - pauseBefore).TotalMilliseconds);

            // Comparison only (not recorded as frames): the naive all-on-UI-thread sort, Name desc from a Data-order state,
            // i.e. the same operation and input state as sortDescMs / sortBackgroundMs.
            list.Sort(DataColumn, false);
            report.SortUiThreadSyncMs = Round(TimeSync(() => list.Sort(0, true), list));

            List<double> deltas = rec.Deltas.OrderBy(d => d).ToList();
            report.Frames = deltas.Count;
            report.MaxFrameMs = Round(deltas.Count == 0 ? 0 : deltas[deltas.Count - 1]);
            report.P99FrameMs = Round(Percentile(deltas, 0.99));
            report.P95FrameMs = Round(Percentile(deltas, 0.95));
            report.FramesOver100Ms = deltas.Count(d => d > 100.0);

            report.MeasuredRowHeights = rec.RowHeights.Select(h => Math.Round(h, 2)).OrderBy(h => h).ToArray();
            report.MeasuredHeaderHeight = Round(list.HeaderActualHeight);
            report.PrivateMB = Round(Process.GetCurrentProcess().PrivateMemorySize64 / 1048576.0);
        }
        catch (Exception ex)
        {
            report.Error = ex.GetType().Name + ": " + ex.Message;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(options.BenchOut));
        File.WriteAllText(options.BenchOut, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return report.Error == null;
    }

    /// <summary>Runs the operation and forces layout synchronously. Returns CPU ms.</summary>
    private static double TimeSync(Action operation, DetailsList list)
    {
        var sw = Stopwatch.StartNew();
        operation();
        list.UpdateLayout();
        return sw.Elapsed.TotalMilliseconds;
    }

    /// <summary>Runs the operation, forces layout, then waits for one composition frame. Returns CPU ms.</summary>
    private static async Task<double> TimeOpAsync(Action operation, DetailsList list, FrameRecorder rec)
    {
        double ms = TimeSync(operation, list);
        await rec.NextFrame();
        return ms;
    }

    /// <summary>Background sort + UI apply + layout, then one frame. Returns (total ms, background ms).</summary>
    private static async Task<(double Total, double Background)> TimeSortAsync(DetailsList list, int column, bool descending, FrameRecorder rec)
    {
        var sw = Stopwatch.StartNew();
        double background = await list.SortAsync(column, descending);
        list.UpdateLayout();
        double total = sw.Elapsed.TotalMilliseconds;
        await rec.NextFrame();
        return (total, background);
    }

    private static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0)
        {
            return 0;
        }

        int index = (int)Math.Ceiling(p * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }

    private static double Round(double value) => Math.Round(value, 3);

    /// <summary>Writes a PNG of the list at its current state, via RenderTargetBitmap of the list root.</summary>
    internal static async Task SaveScreenshotAsync(DetailsList list, string path)
    {
        var rtb = new RenderTargetBitmap();
        await rtb.RenderAsync(list);
        IBuffer buffer = await rtb.GetPixelsAsync();
        byte[] pixels = buffer.ToArray();

        using var ras = new InMemoryRandomAccessStream();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, ras);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)rtb.PixelWidth, (uint)rtb.PixelHeight, 96, 96, pixels);
        await encoder.FlushAsync();
        ras.Seek(0);

        Directory.CreateDirectory(Path.GetDirectoryName(path));
        using FileStream file = File.Create(path);
        await ras.AsStreamForRead().CopyToAsync(file);
    }
}

/// <summary>JSON report. Property names are the contract consumed by bench.ps1 and the README.</summary>
internal sealed class BenchReport
{
    [JsonPropertyName("rows")] public int Rows { get; set; }
    [JsonPropertyName("rowHeight")] public int RowHeight { get; set; }
    [JsonPropertyName("headerHeight")] public int HeaderHeight { get; set; }
    [JsonPropertyName("fontSize")] public double FontSize { get; set; }
    [JsonPropertyName("dpi")] public int Dpi { get; set; }
    [JsonPropertyName("arch")] public string Arch { get; set; }
    [JsonPropertyName("frames")] public int Frames { get; set; }
    [JsonPropertyName("maxFrameMs")] public double MaxFrameMs { get; set; }
    [JsonPropertyName("p99FrameMs")] public double P99FrameMs { get; set; }
    [JsonPropertyName("p95FrameMs")] public double P95FrameMs { get; set; }
    [JsonPropertyName("framesOver100ms")] public int FramesOver100Ms { get; set; }
    [JsonPropertyName("idleP95FrameMs")] public double IdleP95FrameMs { get; set; }
    [JsonPropertyName("idleMaxFrameMs")] public double IdleMaxFrameMs { get; set; }
    [JsonPropertyName("scrollLayoutMsMedian")] public double ScrollLayoutMsMedian { get; set; }
    [JsonPropertyName("scrollLayoutMsP95")] public double ScrollLayoutMsP95 { get; set; }
    [JsonPropertyName("scrollLayoutMsMax")] public double ScrollLayoutMsMax { get; set; }
    [JsonPropertyName("measuredRowHeights")] public double[] MeasuredRowHeights { get; set; }
    [JsonPropertyName("measuredHeaderHeight")] public double MeasuredHeaderHeight { get; set; }
    [JsonPropertyName("sortMs")] public double SortMs { get; set; }
    [JsonPropertyName("sortDescMs")] public double SortDescMs { get; set; }
    [JsonPropertyName("sortAscMs")] public double SortAscMs { get; set; }
    [JsonPropertyName("sortBackgroundMs")] public double SortBackgroundMs { get; set; }
    [JsonPropertyName("sortUiThreadSyncMs")] public double SortUiThreadSyncMs { get; set; }
    [JsonPropertyName("resizeMsMax")] public double ResizeMsMax { get; set; }
    [JsonPropertyName("resizeMsAvg")] public double ResizeMsAvg { get; set; }
    [JsonPropertyName("autoSizeMs")] public double AutoSizeMs { get; set; }
    [JsonPropertyName("selectAllMs")] public double SelectAllMs { get; set; }
    [JsonPropertyName("startupMs")] public double StartupMs { get; set; }
    [JsonPropertyName("privateMB")] public double PrivateMB { get; set; }
    [JsonPropertyName("checks")] public Dictionary<string, object> Checks { get; set; }
    [JsonPropertyName("error"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Error { get; set; }
}
