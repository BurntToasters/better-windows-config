using Bwc.Core;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Bwc.Hub.Tools;

/// <summary>
/// The tool list the Hub shows. The registry is <see cref="ToolCatalog"/>; this class adds only what
/// the Hub displays: the description text from the resource file, the install status, and the icon
/// read from the tool executable.
/// </summary>
internal static class HubTools
{
    public const string RowAutomationPrefix = "ToolRow_";

    public static IReadOnlyList<ToolCatalog.Tool> All => ToolCatalog.Tools;

    public static string ExePath(ToolCatalog.Tool tool) => Path.Combine(AppContext.BaseDirectory, tool.ExeName);

    public static bool IsInstalled(ToolCatalog.Tool tool) => File.Exists(ExePath(tool));

    public static string StatusText(ToolCatalog.Tool tool) =>
        IsInstalled(tool) ? HubStrings.Get("StatusInstalled") : HubStrings.Get("StatusNotInstalled");

    /// <summary>The description from Strings/en-US/Resources.resw ("Tool" + id + "Description"), or the catalog text.</summary>
    public static string Description(ToolCatalog.Tool tool)
    {
        string key = "Tool" + PascalCase(tool.Id) + "Description";
        return HubStrings.TryGet(key) ?? tool.Description;
    }

    public static string RowAutomationId(ToolCatalog.Tool tool) => RowAutomationPrefix + tool.Id;

    public static string LaunchAutomationId(ToolCatalog.Tool tool) => "Launch_" + tool.Id;

    /// <summary>
    /// The icon of the tool executable, read through the shell at <paramref name="size"/> pixels.
    /// Returns null when the file is missing or the shell has no image for it.
    /// </summary>
    public static async Task<ImageSource?> LoadIconAsync(ToolCatalog.Tool tool, int size)
    {
        string path = ExePath(tool);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(path);
            using StorageItemThumbnail thumbnail = await file.GetThumbnailAsync(
                ThumbnailMode.SingleItem,
                (uint)size,
                ThumbnailOptions.ResizeThumbnail);
            var bitmap = new BitmapImage { DecodePixelWidth = size, DecodePixelHeight = size };
            await bitmap.SetSourceAsync(thumbnail);
            return bitmap;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Warn("Icon for " + tool.DisplayName + " could not be read: " + ex.Message);
            return null;
        }
    }

    private static string PascalCase(string id)
    {
        var parts = id.Split('-', StringSplitOptions.RemoveEmptyEntries);
        var builder = new System.Text.StringBuilder();
        foreach (string part in parts)
        {
            builder.Append(char.ToUpperInvariant(part[0]));
            builder.Append(part, 1, part.Length - 1);
        }

        return builder.ToString();
    }
}
