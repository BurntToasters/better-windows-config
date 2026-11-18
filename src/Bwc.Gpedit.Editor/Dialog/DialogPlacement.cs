using Bwc.UI;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using WinRT.Interop;

namespace Bwc.Gpedit.Editor.Dialog;

/// <summary>Where a dialog of the editor sits: centered on its owner and kept inside the work area.</summary>
internal static class DialogPlacement
{
    /// <summary>Centers <paramref name="window"/> on the owner window. A failure is logged and leaves the window
    /// where it is.</summary>
    internal static void CenterOnOwner(AppWindow window, nint ownerHwnd)
    {
        try
        {
            WindowId ownerId = Win32Interop.GetWindowIdFromWindow(ownerHwnd);
            AppWindow? owner = AppWindow.GetFromWindowId(ownerId);
            if (owner is null)
            {
                return;
            }

            RectInt32 work = DisplayArea.GetFromWindowId(ownerId, DisplayAreaFallback.Nearest).WorkArea;
            SizeInt32 size = window.Size;
            int left = owner.Position.X + (owner.Size.Width - size.Width) / 2;
            int top = owner.Position.Y + (owner.Size.Height - size.Height) / 2;
            left = Math.Clamp(left, work.X, Math.Max(work.X, work.X + work.Width - size.Width));
            top = Math.Clamp(top, work.Y, Math.Max(work.Y, work.Y + work.Height - size.Height));
            window.Move(new PointInt32(left, top));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            UiDiagnostics.Warn?.Invoke("A dialog could not be centered: " + ex.Message);
        }
    }
}
