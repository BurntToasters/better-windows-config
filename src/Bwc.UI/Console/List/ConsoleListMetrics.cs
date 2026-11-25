using Bwc.UI.Theme;

namespace Bwc.UI.Console.List;

/// <summary>
/// Sizes of the console list in DIPs. Row and header heights are measured from the classic list (probe.json, step
/// result-list: 38 and 48 px at 192 DPI) and scale with the dense font size as the registry list does. The icon cell
/// sizes and the label width limits are BWC choices (docs/failure-modes/console-list.md, declared choices).
/// </summary>
internal static class ConsoleListMetrics
{
    internal const double OracleRowHeight = 19;
    internal const double OracleHeaderHeight = 24;
    internal const double LargeCellWidth = 75;
    internal const double LargeCellHeight = 75;
    internal const double LargeIconSize = 32;
    internal const double SmallIconSize = 16;
    internal const double MinLabelItemWidth = 80;
    internal const double MaxLabelItemWidth = 250;
    internal const double IconLeft = 2;
    internal const double LabelLeft = 20;
    internal const double CellPad = 4;
    internal const double SortArrowWidth = 9;
    internal const double SortArrowHeight = 5;

    internal static double RowHeight => Scaled(OracleRowHeight);

    internal static double HeaderHeight => Scaled(OracleHeaderHeight);

    private static double Scaled(double oracle) =>
        Math.Round(oracle * DenseMetrics.FontSize / DenseMetrics.BaseFontSize, MidpointRounding.AwayFromZero);
}
