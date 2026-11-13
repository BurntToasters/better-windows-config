using System;
using System.Collections.Generic;
using System.Text;

namespace Bwc.Spike.ListApp.Model;

/// <summary>Deterministic synthetic registry values: names cycle REG_SZ / REG_DWORD / REG_BINARY / REG_MULTI_SZ.</summary>
public static class SampleData
{
    public static List<RegValue> Generate(int count)
    {
        var rng = new Random(20260501);
        var items = new List<RegValue>(count);
        var sb = new StringBuilder(256);
        for (int i = 0; i < count; i++)
        {
            string name = "Value" + (i + 1).ToString("D6");
            switch (i & 3)
            {
                case 0:
                    items.Add(new RegValue(name, "REG_SZ",
                        "C:\\Program Files\\Vendor" + (i % 977).ToString() + "\\bin\\component" + i.ToString("D6") + ".dll"));
                    break;
                case 1:
                    uint dword = (uint)rng.Next();
                    items.Add(new RegValue(name, "REG_DWORD", "0x" + dword.ToString("x8") + " (" + dword.ToString() + ")"));
                    break;
                case 2:
                    sb.Clear();
                    int bytes = rng.Next(1, 67); // 1..66 bytes -> at most 197 chars of "xx " text
                    for (int b = 0; b < bytes; b++)
                    {
                        if (b > 0)
                        {
                            sb.Append(' ');
                        }

                        sb.Append(rng.Next(256).ToString("x2"));
                    }

                    items.Add(new RegValue(name, "REG_BINARY", sb.ToString()));
                    break;
                default:
                    items.Add(new RegValue(name, "REG_MULTI_SZ",
                        "first" + i.ToString("D6") + " \\0 second" + (i % 100).ToString("D2") + " \\0 third"));
                    break;
            }
        }

        return items;
    }
}
