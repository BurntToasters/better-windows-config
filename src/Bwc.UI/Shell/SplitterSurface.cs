using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace Bwc.UI.Shell;

/// <summary>
/// The face of the splitter thumb. The Thumb itself is sealed and cannot set ProtectedCursor, so its template
/// uses this grid. A grid with a background takes the pointer, and the classic east-west resize cursor shows
/// over it.
/// </summary>
public sealed class SplitterSurface : Grid
{
    public SplitterSurface()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    }
}
