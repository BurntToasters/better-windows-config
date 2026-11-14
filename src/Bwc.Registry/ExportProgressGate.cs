using System.Diagnostics;

namespace Bwc.Registry;

/// <summary>
/// Passes export progress to the caller. Starting, skip, and completion events always go through.
/// Key and value events are throttled to one report per <see cref="ThrottleInterval"/> for each kind,
/// so a large export does not flood the UI thread. The first event of each kind goes through at once.
/// Value events have their own interval, so a value report cannot use up the slot of the key report
/// that follows it. Completion carries the final running totals.
/// </summary>
internal sealed class ExportProgressGate
{
    internal static readonly TimeSpan ThrottleInterval = TimeSpan.FromMilliseconds(100);

    private readonly IProgress<ExportProgress>? _progress;
    private long _lastKeyReport;
    private long _lastValueReport;

    internal ExportProgressGate(IProgress<ExportProgress>? progress)
    {
        _progress = progress;
    }

    internal void Always(ExportProgress report)
    {
        _progress?.Report(report);
    }

    internal void Throttled(ExportProgress report)
    {
        if (_progress is null)
        {
            return;
        }

        ref long last = ref report.Phase == ExportPhase.ValueExported ? ref _lastValueReport : ref _lastKeyReport;
        long now = Stopwatch.GetTimestamp();
        if (last != 0 && Stopwatch.GetElapsedTime(last, now) < ThrottleInterval)
        {
            return;
        }

        last = now;
        _progress.Report(report);
    }
}
