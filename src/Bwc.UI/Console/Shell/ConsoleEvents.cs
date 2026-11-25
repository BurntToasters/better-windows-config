using Bwc.UI.Tree;
using Windows.Foundation;

namespace Bwc.UI.Console.Shell;

/// <summary>A toolbar command: a click, or the key that stands for it (Alt+Left and Alt+Right).</summary>
public sealed class ConsoleToolbarEventArgs : EventArgs
{
    public ConsoleToolbarEventArgs(int id)
    {
        Id = id;
    }

    /// <summary>A <see cref="ConsoleToolbarIds"/> value, or the id of an extension button.</summary>
    public int Id { get; }
}

/// <summary>A context menu was asked for. The host shows its own menu.</summary>
public sealed class ConsoleContextMenuEventArgs : EventArgs
{
    public ConsoleContextMenuEventArgs(ConsolePane pane, TreeNode? node, Point? position, bool fromKeyboard)
    {
        Pane = pane;
        Node = node;
        Position = position;
        FromKeyboard = fromKeyboard;
    }

    /// <summary>The pane that held the focus (key) or was hit (pointer).</summary>
    public ConsolePane Pane { get; }

    /// <summary>The selected tree node when <see cref="Pane"/> is the tree, otherwise null.</summary>
    public TreeNode? Node { get; }

    /// <summary>
    /// The point in the shell's coordinates for a pointer request, null for a key request: the host then places
    /// the menu at the selection.
    /// </summary>
    public Point? Position { get; }

    /// <summary>True for Shift+F10 and the context-menu key.</summary>
    public bool FromKeyboard { get; }
}
