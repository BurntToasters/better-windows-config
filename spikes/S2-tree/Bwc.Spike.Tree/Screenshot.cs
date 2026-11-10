using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Bwc.Spike.Tree
{
    /// <summary>Renders a XAML element with RenderTargetBitmap and writes it as a PNG.</summary>
    internal static class Screenshot
    {
        /// <summary>Writes the element at device resolution (DIP size times rasterization scale).</summary>
        public static async Task SaveAsync(FrameworkElement element, string path, double scale)
        {
            int w = (int)Math.Round(element.ActualWidth * scale);
            int h = (int)Math.Round(element.ActualHeight * scale);
            var rtb = new RenderTargetBitmap();
            await rtb.RenderAsync(element, w, h);
            IBuffer pixels = await rtb.GetPixelsAsync();

            using var ras = new InMemoryRandomAccessStream();
            BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, ras);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                (uint)rtb.PixelWidth,
                (uint)rtb.PixelHeight,
                96,
                96,
                pixels.ToArray());
            await encoder.FlushAsync();

            ras.Seek(0);
            string full = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            using Stream src = ras.AsStreamForRead();
            using FileStream dst = File.Create(full);
            await src.CopyToAsync(dst);
        }
    }
}
