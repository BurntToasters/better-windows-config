using System;
using System.Globalization;
using System.IO;

namespace Bwc.Spike.ListApp;

/// <summary>Command-line options. Unknown arguments are rejected so a typo cannot silently change a measurement.</summary>
public sealed class SpikeOptions
{
    public int Rows { get; private set; } = 200000;
    public int RowHeight { get; private set; } = 20;
    public int HeaderHeight { get; private set; } = 22;
    public double FontSize { get; private set; } = 12;
    public double IconSize { get; private set; } = 16;
    public bool ExtendedSelection { get; private set; } = true;
    public string BenchOut { get; private set; }
    public string ShotPath { get; private set; }

    public static SpikeOptions Parse(string[] args)
    {
        var o = new SpikeOptions();
        for (int i = 1; i < args.Length; i++)
        {
            string arg = args[i];
            if (i + 1 >= args.Length)
            {
                throw new ArgumentException("Missing value for " + arg);
            }

            string value = args[++i];
            switch (arg)
            {
                case "--rows":
                    o.Rows = ParseInt(value, arg, 1, 2000000);
                    break;
                case "--row-height":
                    o.RowHeight = ParseInt(value, arg, 8, 60);
                    break;
                case "--header-height":
                    o.HeaderHeight = ParseInt(value, arg, 8, 60);
                    break;
                case "--font-size":
                    o.FontSize = double.Parse(value, CultureInfo.InvariantCulture);
                    if (o.FontSize < 4 || o.FontSize > 24)
                    {
                        throw new ArgumentException("--font-size out of range");
                    }
                    break;
                case "--icon-size":
                    o.IconSize = ParseInt(value, arg, 6, 24);
                    break;
                case "--selection":
                    if (value == "extended") { o.ExtendedSelection = true; }
                    else if (value == "single") { o.ExtendedSelection = false; }
                    else { throw new ArgumentException("--selection must be single or extended"); }
                    break;
                case "--bench":
                    o.BenchOut = Path.GetFullPath(value);
                    break;
                case "--shot":
                    o.ShotPath = Path.GetFullPath(value);
                    break;
                default:
                    throw new ArgumentException("Unknown argument " + arg);
            }
        }

        return o;
    }

    private static int ParseInt(string value, string name, int min, int max)
    {
        int v = int.Parse(value, CultureInfo.InvariantCulture);
        if (v < min || v > max)
        {
            throw new ArgumentException(name + " out of range");
        }

        return v;
    }
}
