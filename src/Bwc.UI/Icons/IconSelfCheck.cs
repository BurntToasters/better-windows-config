#if BWC_E2E
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Bwc.UI.Icons;

/// <summary>
/// E2E only. When <c>BWC_E2E_ICON_REPORT</c> names a file, the first <see cref="IconProvider.Get(IconKind, double)"/>
/// call writes a JSON report proving the module icon path: a regedit icon loaded by id and by index is byte for byte
/// the icon of the <see cref="IconKind"/> path, absent modules, ids, indexes and path-like names give the fallback
/// glyph, the cache is per size, and repeated loads leak no GDI or USER objects. The test reads the report. It is
/// not compiled into the ship flavor.
/// </summary>
internal static class IconSelfCheck
{
    internal const string ReportVariable = "BWC_E2E_ICON_REPORT";

    private const int LeakLoads = 100;
    private static readonly IconKind[] Kinds = [IconKind.Computer, IconKind.ClosedFolder, IconKind.StringValue, IconKind.BinaryValue];
    private static readonly int[] RegeditGroupIds = [201, 203, 205, 206];

    private static bool _done;

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(nint process, uint flags);

    public static void RunOnce()
    {
        if (_done)
        {
            return;
        }

        _done = true;
        string? path = Environment.GetEnvironmentVariable(ReportVariable);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var report = new Dictionary<string, object?>(StringComparer.Ordinal);
        try
        {
            Build(report);
        }
        catch (Exception ex)
        {
            report["error"] = ex.ToString();
        }

        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, overwrite: true);
    }

    private static void Build(Dictionary<string, object?> report)
    {
        report["sizes"] = new[] { 16, 32 }.Select(CompareSize).ToArray();

        ModuleIcon regedit = ModuleIcon.ById("regedit.exe", 201, IconModuleDirectory.Windows);
        IReadOnlyList<int>? ids = IconExtraction.TryListGroupIconIds(regedit);
        report["regeditGroupIds"] = ids;
        int index = ids is null ? -1 : ids.ToList().IndexOf(201);
        report["regeditComputerIndex"] = index;
        IconExtraction.Pixels? byId = IconExtraction.TryLoadModuleIcon(regedit, 16);
        IconExtraction.Pixels? byIndex = index < 0
            ? null
            : IconExtraction.TryLoadModuleIcon(ModuleIcon.ByIndex("regedit.exe", index, IconModuleDirectory.Windows), 16);
        report["indexMatchesId"] = byId is not null && byIndex is not null && Same(byId.Value, byIndex.Value);

        // Absent sources give exactly the generic fallback glyph (the neutral page), and it is not a regedit icon.
        int size = 16;
        IconExtraction.Pixels generic = FallbackGlyph.RenderGeneric(size);
        report["missingModuleIsFallback"] = IsGeneric(ModuleIcon.ById("bwc-no-such-module.dll", 1), size, generic);
        report["missingIdIsFallback"] = IsGeneric(ModuleIcon.ById("regedit.exe", 60000, IconModuleDirectory.Windows), size, generic);
        report["indexPastEndIsFallback"] = IsGeneric(
            ModuleIcon.ByIndex("regedit.exe", (ids?.Count ?? 0) + 5, IconModuleDirectory.Windows),
            size,
            generic);
        report["negativeIndexIsFallback"] = IsGeneric(ModuleIcon.ByIndex("regedit.exe", -1, IconModuleDirectory.Windows), size, generic);
        report["zeroIdIsFallback"] = IsGeneric(ModuleIcon.ById("regedit.exe", 0, IconModuleDirectory.Windows), size, generic);
        report["pathNameIsFallback"] = IsGeneric(ModuleIcon.ById(@"..\regedit.exe", 201, IconModuleDirectory.System32), size, generic) &&
            IsGeneric(ModuleIcon.ById(@"C:\Windows\regedit.exe", 201, IconModuleDirectory.Windows), size, generic);
        report["defaultIsFallback"] = IsGeneric(default, size, generic);
        report["genericDiffersFromRegedit"] = byId is not null && !Same(byId.Value, generic);

        // A classic fallback kind is honored for an absent module.
        IconExtraction.Pixels folderFallback = IconProvider.LoadModulePixels(ModuleIcon.ById("bwc-no-such-module.dll", 1), size, IconKind.ClosedFolder);
        report["kindFallbackIsFolderGlyph"] = Same(folderFallback, FallbackGlyph.Render(IconKind.ClosedFolder, size));

        // Cache: one instance per icon and pixel size, a different instance and width at another scale.
        var first = IconProvider.Get(regedit, 1.0);
        var again = IconProvider.Get(regedit, 1.0);
        var doubled = IconProvider.Get(regedit, 2.0);
        var missing = ModuleIcon.ById("bwc-no-such-module.dll", 1);
        var missingFirst = IconProvider.Get(missing, 1.0);
        var missingAgain = IconProvider.Get(missing, 1.0);
        report["cache"] = new Dictionary<string, object?>
        {
            ["sameInstance"] = ReferenceEquals(first, again),
            ["otherSizeDiffers"] = !ReferenceEquals(first, doubled),
            ["width16"] = (first as Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap)?.PixelWidth,
            ["width32"] = (doubled as Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap)?.PixelWidth,
            ["missingCachedAsFallback"] = ReferenceEquals(missingFirst, missingAgain),
        };

        // Handle leaks: many loads, success and failure, must not grow the process GDI or USER object counts.
        nint process = Process.GetCurrentProcess().Handle;
        uint gdiBefore = GetGuiResources(process, 0);
        uint userBefore = GetGuiResources(process, 1);
        for (int i = 0; i < LeakLoads; i++)
        {
            _ = IconExtraction.TryLoadModuleIcon(regedit, 16);
            _ = IconExtraction.TryLoadModuleIcon(ModuleIcon.ByIndex("regedit.exe", i % 8, IconModuleDirectory.Windows), 32);
            _ = IconExtraction.TryLoadModuleIcon(ModuleIcon.ById("regedit.exe", 60000, IconModuleDirectory.Windows), 16);
        }

        report["handles"] = new Dictionary<string, object?>
        {
            ["loads"] = LeakLoads * 3,
            ["gdiBefore"] = gdiBefore,
            ["gdiAfter"] = GetGuiResources(process, 0),
            ["userBefore"] = userBefore,
            ["userAfter"] = GetGuiResources(process, 1),
        };
    }

    /// <summary>Hashes the four regedit icons through the kind path and through the module path at one size.</summary>
    private static Dictionary<string, object?> CompareSize(int size)
    {
        var rows = new List<Dictionary<string, object?>>();
        foreach (IconKind kind in Kinds)
        {
            IconExtraction.Pixels? old = IconExtraction.TryLoadRegeditIcon(kind, size);
            int groupId = RegeditGroupIds[(int)kind];
            IconExtraction.Pixels? viaModule = IconExtraction.TryLoadModuleIcon(
                ModuleIcon.ById("regedit.exe", groupId, IconModuleDirectory.Windows),
                size);
            rows.Add(new Dictionary<string, object?>
            {
                ["kind"] = kind.ToString(),
                ["groupId"] = groupId,
                ["oldHash"] = Hash(old),
                ["moduleHash"] = Hash(viaModule),
                ["loaded"] = old is not null && viaModule is not null,
                ["identical"] = old is not null && viaModule is not null && Same(old.Value, viaModule.Value),
            });
        }

        return new Dictionary<string, object?> { ["size"] = size, ["icons"] = rows };
    }

    private static bool IsGeneric(ModuleIcon icon, int size, IconExtraction.Pixels generic) =>
        Same(IconProvider.LoadModulePixels(icon, size, null), generic);

    private static bool Same(IconExtraction.Pixels a, IconExtraction.Pixels b) =>
        a.Width == b.Width && a.Height == b.Height && a.Bgra.AsSpan().SequenceEqual(b.Bgra);

    private static string? Hash(IconExtraction.Pixels? pixels) =>
        pixels is null ? null : $"{pixels.Value.Width}x{pixels.Value.Height}:{Convert.ToHexString(SHA256.HashData(pixels.Value.Bgra))}";
}
#endif
