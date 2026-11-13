using System;

namespace Bwc.Spike.ListApp.Model;

public readonly record struct ColumnSpec(string Title, double Width, double MinWidth);

/// <summary>
/// Shared column width model. Rows and header read widths from here in their ArrangeOverride;
/// changing a width raises WidthChanged so the view invalidates layout only, never re-templates rows.
/// </summary>
public sealed class ColumnModel
{
    private readonly string[] _titles;
    private readonly double[] _widths;
    private readonly double[] _minWidths;
    private readonly double[] _left;

    public ColumnModel(params ColumnSpec[] columns)
    {
        _titles = new string[columns.Length];
        _widths = new double[columns.Length];
        _minWidths = new double[columns.Length];
        _left = new double[columns.Length];
        for (int i = 0; i < columns.Length; i++)
        {
            _titles[i] = columns[i].Title;
            _widths[i] = columns[i].Width;
            _minWidths[i] = columns[i].MinWidth;
        }

        Recompute();
    }

    /// <summary>Raised after a width changes; argument is the column index.</summary>
    public event Action<int> WidthChanged;

    public int Count => _widths.Length;

    public double TotalWidth { get; private set; }

    public string Title(int column) => _titles[column];

    public double Width(int column) => _widths[column];

    public double MinWidth(int column) => _minWidths[column];

    public double Left(int column) => _left[column];

    public void SetWidth(int column, double width)
    {
        double w = Math.Max(_minWidths[column], Math.Round(width));
        if (w == _widths[column])
        {
            return;
        }

        _widths[column] = w;
        Recompute();
        WidthChanged?.Invoke(column);
    }

    private void Recompute()
    {
        double x = 0;
        for (int i = 0; i < _widths.Length; i++)
        {
            _left[i] = x;
            x += _widths[i];
        }

        TotalWidth = x;
    }
}
