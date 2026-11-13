using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media;
using Bwc.Spike.ListApp.Controls;

namespace Bwc.Spike.ListApp.Bench;

/// <summary>
/// Records composition frame intervals from CompositionTarget.Rendering while recording,
/// and exposes NextFrame() so the bench can await one rendered frame per step.
/// </summary>
internal sealed class FrameRecorder
{
    private readonly DetailsList _list;
    private readonly List<double> _deltas = new List<double>(4096);
    private readonly HashSet<double> _rowHeights = new HashSet<double>();
    private TimeSpan? _last;
    private bool _recording;

    public FrameRecorder(DetailsList list)
    {
        _list = list;
        CompositionTarget.Rendering += OnRendering;
    }

    public IReadOnlyList<double> Deltas => _deltas;

    public ISet<double> RowHeights => _rowHeights;

    public void Start()
    {
        _deltas.Clear();
        _rowHeights.Clear();
        _last = null;
        _recording = true;
    }

    public void Stop()
    {
        _recording = false;
    }

    private void OnRendering(object sender, object e)
    {
        if (!_recording)
        {
            _last = null;
            return;
        }

        TimeSpan now = ((RenderingEventArgs)e).RenderingTime;
        if (_last is TimeSpan previous)
        {
            _deltas.Add((now - previous).TotalMilliseconds);
        }

        _last = now;
        _list.SampleRowHeights(_rowHeights);
    }

    /// <summary>Completes on the next composition frame. Fails loudly if no frame arrives (for example a hidden window).</summary>
    public async Task NextFrame()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<object> handler = null;
        handler = (s, e) =>
        {
            CompositionTarget.Rendering -= handler;
            tcs.TrySetResult(true);
        };
        CompositionTarget.Rendering += handler;

        Task finished = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        if (finished != tcs.Task)
        {
            CompositionTarget.Rendering -= handler;
            throw new TimeoutException("No composition frame within 5 s");
        }
    }
}
