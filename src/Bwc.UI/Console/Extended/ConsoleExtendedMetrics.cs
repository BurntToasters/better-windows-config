using Bwc.UI.Theme;

namespace Bwc.UI.Console.Extended;

/// <summary>
/// Sizes of the Extended view in DIPs. The ratio, the tab height and the tab widths are measured from the classic
/// window (probe.json, steps panes and result-list: a left column of 212 in a body of 589, a tab strip 19 high with
/// tabs 69 and 68 wide); the minimums are BWC choices (docs/failure-modes/console-extended.md, declared choices).
/// </summary>
internal static class ConsoleExtendedMetrics
{
    /// <summary>The share of the view width that the description pane takes (212 of 589 in the probe).</summary>
    internal const double DescriptionRatio = 212.0 / 589.0;

    internal const double MinPaneWidth = 48;
    internal const double MinContentWidth = 48;
    internal const double OracleTabHeight = 19;
    internal const double ExtendedTabWidth = 69;
    internal const double StandardTabWidth = 68;

    /// <summary>The longest run of text one TextBlock holds; longer help is split over several blocks.</summary>
    internal const int MaxParagraphLength = 4000;

    internal static double TabHeight =>
        Math.Round(OracleTabHeight * DenseMetrics.FontSize / DenseMetrics.BaseFontSize, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The width of the description pane in a view of this width: the ratio of the width, at least the minimum pane
    /// width and, when the view has the room, leaving the content its minimum width. Never negative.
    /// </summary>
    internal static double PaneWidthFor(double viewWidth)
    {
        if (!double.IsFinite(viewWidth) || viewWidth <= 0)
        {
            return MinPaneWidth;
        }

        double ceiling = Math.Max(MinPaneWidth, viewWidth - MinContentWidth);
        return Math.Clamp(viewWidth * DescriptionRatio, MinPaneWidth, ceiling);
    }
}
