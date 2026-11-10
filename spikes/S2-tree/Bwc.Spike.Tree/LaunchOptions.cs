using System;
using System.Globalization;

namespace Bwc.Spike.Tree
{
    /// <summary>
    /// Command-line options. Parsing also writes the row metrics that every TreeRow reads.
    /// </summary>
    public sealed class LaunchOptions
    {
        public int Rows { get; private set; } = 1000;
        public double RowHeight { get; private set; } = 20;
        public double FontSize { get; private set; } = 12;
        public double Indent { get; private set; } = 19;
        public int Steps { get; private set; } = 300;

        /// <summary>Notification style for expand and collapse (see NotifyStyle).</summary>
        public NotifyStyle Notify { get; private set; } = NotifyStyle.Range;

        /// <summary>When set, runs the scripted benchmark and writes JSON here, then exits.</summary>
        public string? BenchOut { get; private set; }

        /// <summary>When set, saves a PNG of the tree control (after the bench, if any), then exits.</summary>
        public string? Screenshot { get; private set; }

        public static LaunchOptions Parse(string[] argv)
        {
            var o = new LaunchOptions();
            for (int i = 1; i < argv.Length; i++)
            {
                string key = argv[i];
                string Next()
                {
                    if (i + 1 >= argv.Length) throw new ArgumentException("Missing value for " + key);
                    return argv[++i];
                }

                switch (key)
                {
                    case "--rows": o.Rows = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--row-height": o.RowHeight = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--font-size": o.FontSize = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--indent": o.Indent = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--steps": o.Steps = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--notify":
                        o.Notify = Next() switch
                        {
                            "range" => NotifyStyle.Range,
                            "reset" => NotifyStyle.Reset,
                            "range-count-only" => NotifyStyle.RangeCountOnly,
                            _ => throw new ArgumentException("--notify must be range, reset or range-count-only"),
                        };
                        break;
                    case "--bench": o.BenchOut = Next(); break;
                    case "--screenshot": o.Screenshot = Next(); break;
                    default: throw new ArgumentException("Unknown argument: " + key);
                }
            }

            if (o.Rows < 1) throw new ArgumentException("--rows must be >= 1");
            if (o.RowHeight < 8 || o.RowHeight > 64) throw new ArgumentException("--row-height out of range");
            if (o.Steps < 2) throw new ArgumentException("--steps must be >= 2");

            TreeMetrics.RowHeight = o.RowHeight;
            TreeMetrics.FontSize = o.FontSize;
            TreeMetrics.IndentPx = o.Indent;
            FlatRowList.Style = o.Notify;
            return o;
        }
    }
}
