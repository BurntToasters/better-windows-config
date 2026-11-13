using System.Text.Json;

namespace Bwc.E2E.Harness;

/// <summary>
/// Reads the classic Registry Editor geometry from docs/parity/oracle/regedit-metrics.json.
/// The file was captured with the window at <c>dpi.window</c> (192, which is 200 percent scale), so
/// every pixel value in it is a device pixel at that DPI. One DIP is <c>capture DPI / 96</c> of
/// them. A capture value converts to device pixels at window DPI <c>d</c> as
/// <c>value * d / capture DPI</c>, which gives the same numbers at 200 percent and half of them at
/// 100 percent.
/// </summary>
public sealed class OracleMetrics
{
    /// <summary>Path of the oracle metrics file, relative to the repository root.</summary>
    public const string RelativePath = @"docs\parity\oracle\regedit-metrics.json";

    private static readonly object Sync = new();
    private static OracleMetrics? _cached;

    private readonly JsonDocument _document;
    private readonly string _path;

    private OracleMetrics(JsonDocument document, string path)
    {
        _document = document;
        _path = path;
        CaptureDpi = Int(Get("dpi", "window"));
        if (CaptureDpi <= 0)
        {
            throw new InvalidOperationException($"'{path}' has no positive dpi.window value.");
        }
    }

    /// <summary>DPI the oracle was captured at (dpi.window).</summary>
    public int CaptureDpi { get; }

    /// <summary>Loads the file once per repository root.</summary>
    public static OracleMetrics Load(E2EConfig config)
    {
        lock (Sync)
        {
            if (_cached is not null && string.Equals(_cached._path, PathFor(config), StringComparison.OrdinalIgnoreCase))
            {
                return _cached;
            }

            string path = PathFor(config);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"The oracle metrics file '{path}' is missing. Density checks cannot run without it.",
                    path);
            }

            _cached = new OracleMetrics(JsonDocument.Parse(File.ReadAllText(path)), path);
            return _cached;
        }
    }

    /// <summary>Converts an oracle pixel value to device pixels at the given window DPI.</summary>
    public double DevicePixels(double capturePixels, uint windowDpi) =>
        capturePixels * windowDpi / CaptureDpi;

    /// <summary>
    /// Tolerance for a density check: one DIP in device pixels, rounded to the nearest whole pixel and
    /// never less than one pixel. At 200 percent it is 2 px, at 100 percent it is 1 px.
    /// </summary>
    public static int ToleranceDevicePixels(uint windowDpi) =>
        Math.Max(1, (int)Math.Round(windowDpi / 96.0, MidpointRounding.AwayFromZero));

    /// <summary>Menu bar height in oracle pixels (menuBar.height).</summary>
    public int MenuBarHeight => Int(Get("menuBar", "height"));

    /// <summary>Height of each top level menu item in oracle pixels (menuBar.items[].rect.height).</summary>
    public int[] MenuItemHeights => MenuItemRects("height");

    /// <summary>Width of each top level menu item in oracle pixels (menuBar.items[].rect.width).</summary>
    public int[] MenuItemWidths => MenuItemRects("width");

    /// <summary>Address bar height in oracle pixels (addressBar.rect.height).</summary>
    public int AddressBarHeight => Int(Get("addressBar", "rect", "height"));

    /// <summary>Tree pane width in oracle pixels (tree.rect.width).</summary>
    public int TreeWidth => Int(Get("tree", "rect", "width"));

    /// <summary>
    /// Splitter width in oracle pixels: the gap between the tree's right edge and the list's left
    /// edge (splitter.listLeft minus splitter.treeRight).
    /// </summary>
    public int SplitterWidth => Int(Get("splitter", "listLeft")) - Int(Get("splitter", "treeRight"));

    /// <summary>List header row height in oracle pixels (list.header.height).</summary>
    public int ListHeaderHeight => Int(Get("list", "header", "height"));

    /// <summary>Tree item height in oracle pixels (tree.itemHeight).</summary>
    public int TreeItemHeight => Int(Get("tree", "itemHeight"));

    /// <summary>List row height in oracle pixels (list.rowHeight).</summary>
    public int ListRowHeight => Int(Get("list", "rowHeight"));

    /// <summary>Human readable origin of the numbers, for notes.</summary>
    public string Describe() => $"{RelativePath} (captured at {CaptureDpi} DPI)";

    private static string PathFor(E2EConfig config) =>
        Path.Combine(config.RepoRoot, RelativePath);

    private int[] MenuItemRects(string property)
    {
        JsonElement items = Get("menuBar", "items");
        var values = new List<int>();
        foreach (JsonElement item in items.EnumerateArray())
        {
            values.Add(Int(item.GetProperty("rect").GetProperty(property)));
        }

        return values.ToArray();
    }

    private JsonElement Get(params string[] path)
    {
        JsonElement current = _document.RootElement;
        foreach (string segment in path)
        {
            if (!current.TryGetProperty(segment, out current))
            {
                throw new InvalidOperationException($"'{_path}' has no '{string.Join('.', path)}' value.");
            }
        }

        return current;
    }

    private static int Int(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out int value))
        {
            throw new InvalidOperationException($"Expected an integer in the oracle metrics, found {element.ValueKind}.");
        }

        return value;
    }
}
