using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Windows.System;

namespace Bwc.Spike.Tree
{
    /// <summary>Bench output. The first 14 fields are the agreed schema; the rest are extra diagnostics.</summary>
    public sealed class BenchResult
    {
        [JsonPropertyName("rows")] public int Rows { get; set; }
        [JsonPropertyName("rowHeight")] public double RowHeight { get; set; }
        [JsonPropertyName("fontSize")] public double FontSize { get; set; }
        [JsonPropertyName("dpi")] public int Dpi { get; set; }
        [JsonPropertyName("arch")] public string Arch { get; set; } = "";
        [JsonPropertyName("maxFrameMs")] public double MaxFrameMs { get; set; }
        [JsonPropertyName("p99FrameMs")] public double P99FrameMs { get; set; }
        [JsonPropertyName("p95FrameMs")] public double P95FrameMs { get; set; }
        [JsonPropertyName("framesOver100ms")] public int FramesOver100Ms { get; set; }
        [JsonPropertyName("measuredRowHeights")] public double[] MeasuredRowHeights { get; set; } = Array.Empty<double>();
        [JsonPropertyName("expandMs")] public double ExpandMs { get; set; }
        [JsonPropertyName("collapseMs")] public double CollapseMs { get; set; }
        [JsonPropertyName("startupMs")] public double StartupMs { get; set; }
        [JsonPropertyName("privateMB")] public double PrivateMB { get; set; }

        [JsonPropertyName("dpiScale")] public double DpiScale { get; set; }
        [JsonPropertyName("visibleRowsAtStart")] public int VisibleRowsAtStart { get; set; }
        [JsonPropertyName("treeBuildMs")] public double TreeBuildMs { get; set; }
        [JsonPropertyName("privateMBAfterLoad")] public double PrivateMBAfterLoad { get; set; }
        [JsonPropertyName("frameCount")] public int FrameCount { get; set; }
        [JsonPropertyName("frameTimeouts")] public int FrameTimeouts { get; set; }
        [JsonPropertyName("scrollSteps")] public int ScrollSteps { get; set; }
        [JsonPropertyName("scrollStepP99Ms")] public double ScrollStepP99Ms { get; set; }
        [JsonPropertyName("scrollStepMaxMs")] public double ScrollStepMaxMs { get; set; }
        [JsonPropertyName("jumpMs")] public double JumpMs { get; set; }
        [JsonPropertyName("expandModelMs")] public double ExpandModelMs { get; set; }
        [JsonPropertyName("collapseModelMs")] public double CollapseModelMs { get; set; }
        [JsonPropertyName("expandGcPauseMs")] public double ExpandGcPauseMs { get; set; }
        [JsonPropertyName("expandGen2Collections")] public int ExpandGen2Collections { get; set; }
        [JsonPropertyName("expandFrameMs")] public double ExpandFrameMs { get; set; }
        [JsonPropertyName("collapseFrameMs")] public double CollapseFrameMs { get; set; }
        [JsonPropertyName("maxRealizedRows")] public int MaxRealizedRows { get; set; }
        [JsonPropertyName("layoutMismatches")] public int LayoutMismatches { get; set; }
        [JsonPropertyName("maxPositionErrorPx")] public double MaxPositionErrorPx { get; set; }
        [JsonPropertyName("maxHeightErrorPx")] public double MaxHeightErrorPx { get; set; }
        [JsonPropertyName("keyboardOk")] public bool KeyboardOk { get; set; }
        [JsonPropertyName("notify")] public string Notify { get; set; } = "";
    }

    /// <summary>
    /// Scripted run: warm up, scroll top to bottom and back in steps, jump to the middle, expand and collapse
    /// the 10,000-child node, then check the keyboard model. Expand and collapse are timed with UpdateLayout so
    /// layout cost is included. Frame intervals are recorded only inside the scripted section.
    /// </summary>
    internal sealed class TreeBench
    {
        private readonly DenseTree _tree;
        private readonly SampleTree _sample;
        private readonly int _steps;

        public TreeBench(DenseTree tree, SampleTree sample, int steps)
        {
            _tree = tree;
            _sample = sample;
            _steps = steps;
        }

        public async Task<BenchResult> RunAsync()
        {
            double rh = TreeMetrics.RowHeight;
            await FrameClock.FramesAsync(30);
            _tree.UpdateLayout();
            int visibleAtStart = _tree.RowCount;

            var stepMs = new List<double>();
            var heights = new SortedSet<double>();
            int mismatches = 0;
            int maxRealized = 0;
            double maxPos = 0;
            double maxH = 0;

            void Check()
            {
                LayoutCheck c = _tree.CheckRealizedRows();
                mismatches += c.Mismatches;
                maxRealized = Math.Max(maxRealized, c.Realized);
                maxPos = Math.Max(maxPos, c.MaxPositionErrorPx);
                maxH = Math.Max(maxH, c.MaxHeightErrorPx);
                foreach (double h in c.Heights) heights.Add(h);
            }

            FrameClock.Timeouts = 0;
            FrameClock.Intervals.Clear();
            FrameClock.Recording = true;

            Check();
            _tree.UpdateLayout();
            double max = _tree.ScrollHost.ScrollableHeight;

            for (int s = 1; s <= _steps; s++)
            {
                stepMs.Add(await ScrollToAsync(max * s / _steps));
            }
            Check();
            for (int s = _steps - 1; s >= 0; s--)
            {
                stepMs.Add(await ScrollToAsync(max * s / _steps));
            }

            double jumpMs = await ScrollToAsync(max / 2);
            Check();

            TreeRow big = _sample.BigNode;
            int bigIndex = _tree.IndexOfRow(big);
            await ScrollToAsync(bigIndex * rh);

            TimeSpan gcPause0 = GC.GetTotalPauseDuration();
            int gen2Before = GC.CollectionCount(2);
            var sw = Stopwatch.StartNew();
            _tree.Expand(big);
            double expandModelMs = sw.Elapsed.TotalMilliseconds;
            _tree.UpdateLayout();
            double expandMs = sw.Elapsed.TotalMilliseconds;
            double expandGcPauseMs = (GC.GetTotalPauseDuration() - gcPause0).TotalMilliseconds;
            int expandGen2 = GC.CollectionCount(2) - gen2Before;
            double expandFrameMs = await TimeToFrameAsync(sw);
            Check();

            sw.Restart();
            _tree.Collapse(big);
            double collapseModelMs = sw.Elapsed.TotalMilliseconds;
            _tree.UpdateLayout();
            double collapseMs = sw.Elapsed.TotalMilliseconds;
            double collapseFrameMs = await TimeToFrameAsync(sw);
            Check();

            await ScrollToAsync(0);
            FrameClock.Recording = false;

            bool keyboardOk = KeyboardCheck();
            await ScrollToAsync(0);

            double[] intervals = FrameClock.Intervals.ToArray();
            Array.Sort(intervals);
            double[] steps = stepMs.ToArray();
            Array.Sort(steps);

            return new BenchResult
            {
                VisibleRowsAtStart = visibleAtStart,
                MeasuredRowHeights = heights.ToArray(),
                MaxFrameMs = intervals.Length > 0 ? intervals[^1] : 0,
                P99FrameMs = Percentile(intervals, 0.99),
                P95FrameMs = Percentile(intervals, 0.95),
                FramesOver100Ms = intervals.Count(x => x > 100),
                FrameCount = intervals.Length,
                FrameTimeouts = FrameClock.Timeouts,
                ExpandMs = expandMs,
                CollapseMs = collapseMs,
                ExpandModelMs = expandModelMs,
                CollapseModelMs = collapseModelMs,
                ExpandGcPauseMs = expandGcPauseMs,
                ExpandGen2Collections = expandGen2,
                ExpandFrameMs = expandFrameMs,
                CollapseFrameMs = collapseFrameMs,
                ScrollSteps = steps.Length,
                ScrollStepP99Ms = Percentile(steps, 0.99),
                ScrollStepMaxMs = steps.Length > 0 ? steps[^1] : 0,
                JumpMs = jumpMs,
                MaxRealizedRows = maxRealized,
                LayoutMismatches = mismatches,
                MaxPositionErrorPx = maxPos,
                MaxHeightErrorPx = maxH,
                KeyboardOk = keyboardOk,
            };
        }

        private async Task<double> ScrollToAsync(double offset)
        {
            var sw = Stopwatch.StartNew();
            _tree.ScrollHost.ChangeView(null, offset, null, disableAnimation: true);
            await FrameClock.NextFrameAsync();
            return sw.Elapsed.TotalMilliseconds;
        }

        private static async Task<double> TimeToFrameAsync(Stopwatch sw)
        {
            await FrameClock.NextFrameAsync();
            return sw.Elapsed.TotalMilliseconds;
        }

        /// <summary>Drives the handler directly. The rows are restored to the state they were in before the check.</summary>
        private bool KeyboardCheck()
        {
            TreeRow hkcr = _sample.Hkcr;
            int rowsBefore = _tree.RowCount;
            _tree.SelectRow(hkcr);
            bool ok = true;

            _tree.HandleKey(VirtualKey.Down);
            ok &= ReferenceEquals(_tree.SelectedRow, hkcr.Children![0]);
            _tree.HandleKey(VirtualKey.Up);
            ok &= ReferenceEquals(_tree.SelectedRow, hkcr);
            _tree.HandleKey(VirtualKey.Left);
            ok &= !hkcr.IsExpanded && _tree.RowCount == 2;
            _tree.HandleKey(VirtualKey.Right);
            ok &= hkcr.IsExpanded && _tree.RowCount == rowsBefore;
            _tree.HandleKey(VirtualKey.End);
            ok &= _tree.SelectedIndex == _tree.RowCount - 1;
            _tree.HandleKey(VirtualKey.Home);
            ok &= _tree.SelectedIndex == 0;
            _tree.HandleKey(VirtualKey.PageDown);
            ok &= _tree.SelectedIndex > 0;
            _tree.MoveTo(1);
            return ok;
        }

        private static double Percentile(double[] sorted, double p)
        {
            if (sorted.Length == 0) return 0;
            int idx = (int)Math.Ceiling(p * sorted.Length) - 1;
            return sorted[Math.Clamp(idx, 0, sorted.Length - 1)];
        }
    }
}
