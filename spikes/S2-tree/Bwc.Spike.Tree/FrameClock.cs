using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media;

namespace Bwc.Spike.Tree
{
    /// <summary>
    /// Frame timing from CompositionTarget.Rendering. Intervals are differences between consecutive
    /// RenderingTime stamps, so they include all UI-thread work done between two frames.
    /// </summary>
    internal static class FrameClock
    {
        private static TimeSpan? _last;

        public static bool Recording { get; set; }
        public static List<double> Intervals { get; } = new List<double>();
        public static int Timeouts { get; set; }

        public static void Start()
        {
            CompositionTarget.Rendering += OnRendering;
        }

        private static void OnRendering(object? sender, object e)
        {
            TimeSpan t = ((RenderingEventArgs)e).RenderingTime;
            if (Recording && _last is TimeSpan prev)
            {
                Intervals.Add((t - prev).TotalMilliseconds);
            }
            _last = t;
        }

        /// <summary>Completes on the next rendered frame. A 2 s fallback keeps a hidden window from hanging the bench.</summary>
        public static async Task NextFrameAsync()
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<object> handler = null!;
            handler = (_, _) =>
            {
                CompositionTarget.Rendering -= handler;
                tcs.TrySetResult();
            };
            CompositionTarget.Rendering += handler;

            Task winner = await Task.WhenAny(tcs.Task, Task.Delay(2000));
            if (!ReferenceEquals(winner, tcs.Task))
            {
                CompositionTarget.Rendering -= handler;
                Timeouts++;
            }
        }

        public static async Task FramesAsync(int count)
        {
            for (int i = 0; i < count; i++)
            {
                await NextFrameAsync();
            }
        }
    }
}
