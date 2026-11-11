using System.Diagnostics;
using System.Runtime.InteropServices;
using Bwc.UI.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Bwc.UI.Menu;

/// <summary>
/// Win32-style menu-bar keyboard model for a WinUI MenuBar:
///   Alt press and release alone, or F10  -> activate the bar (first item focused)
///   Alt+mnemonic, or a bare mnemonic while the bar is active -> open that top-level flyout
///   Esc (bar active)   -> restore the focus that was active before activation
///   Left/Right (bar active) -> previous/next top-level item through the XAML XYFocus ring
///   Down (bar active)  -> open that item's flyout (HandleKeyFromXaml)
///   Esc (flyout open)  -> passed through so XAML closes the flyout; the bar stays active
///   Letter (flyout open) -> the innermost open popup is searched for that access key. An enabled
///     leaf is invoked and the menu closes; a submenu is expanded. Separators and disabled items
///     are skipped, and a letter with no match does nothing.
///
/// Mnemonic underlines follow the input method: keyboard activation (Alt, F10, or a letter on the
/// bar) sets ClassicMenuState.ShowMnemonics, and a pointer press or the end of menu mode clears it.
///
/// Hook approach: SetWindowSubclass on the top-level HWND and on every descendant HWND. The WinUI
/// keyboard focus lives in a child HWND of the window, so WM_SYSKEYDOWN and WM_KEYDOWN are
/// delivered there. A top-level-only subclass does not see them. Consumed messages are not passed
/// to DefSubclassProc. Arrow keys are the exception: XAML's island input pipeline handles them
/// before the window hook runs, so Down is delivered by the MenuBar PreviewKeyDown handler and
/// Left/Right move through XYFocus.
/// </summary>
public sealed unsafe class ClassicMenuKeyboard : IDisposable
{
    private const nuint HookId = 0x42555755; // 'BWUU'

    private enum Mode
    {
        Idle,
        BarFocused,
        FlyoutOpen,
    }

    private readonly nint _top;
    private readonly IReadOnlyList<MenuBarItem> _items;
    private readonly Action<string>? _onState;
    private readonly List<nint> _hooked = new();
    private readonly List<(MenuBarItem Item, RoutedEventHandler Handler)> _focusHandlers = new();
    private readonly GCHandle _self;
    private bool _altDown;
    private bool _altChord;
    private bool _suppressSysChar;
    private Mode _mode;
    private int _index;
    private object? _savedFocus;
    private bool _disposed;

    /// <summary>
    /// Creates the router, saves a self handle for the native callback, and hooks the window tree.
    /// </summary>
    /// <param name="topHwnd">Top-level window handle.</param>
    /// <param name="items">Top-level menu bar items, in display order.</param>
    /// <param name="onState">Optional debug callback. It receives Idle, Focused:name, or Open:name.</param>
    public ClassicMenuKeyboard(nint topHwnd, IReadOnlyList<MenuBarItem> items, Action<string>? onState = null)
    {
        _top = topHwnd;
        _items = items;
        _onState = onState;
        _self = GCHandle.Alloc(this);
        for (var i = 0; i < items.Count; i++)
        {
            var index = i;
            // Focus can also move between top-level items inside XAML. Keep the state in step.
            RoutedEventHandler handler = (_, _) => OnItemGotFocus(index);
            items[i].GotFocus += handler;
            _focusHandlers.Add((items[i], handler));
        }

        HookTree(topHwnd);
        Publish();
    }

    /// <summary>
    /// Arrow keys are routed by XAML's island input pipeline before window messages are dispatched,
    /// so the window-procedure hook never sees them. This opens the flyout for Down. Returns true
    /// when the key was consumed.
    /// </summary>
    public bool HandleKeyFromXaml(VirtualKey key)
    {
        if (_mode != Mode.BarFocused || key != VirtualKey.Down || _items.Count == 0)
        {
            return false;
        }

        OpenFlyout(_index);
        return true;
    }

    /// <summary>Hooks a window and all of its descendants. Safe to call repeatedly.</summary>
    public void HookTree(nint hwnd)
    {
        if (hwnd == 0 || _disposed)
        {
            return;
        }

        Hook(hwnd);
        for (var child = Win32.GetWindow(hwnd, Win32.GW_CHILD); child != 0; child = Win32.GetWindow(child, Win32.GW_HWNDNEXT))
        {
            HookTree(child);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var (item, handler) in _focusHandlers)
        {
            item.GotFocus -= handler;
        }

        _focusHandlers.Clear();
        foreach (var hwnd in _hooked)
        {
            Win32.RemoveWindowSubclass(hwnd, &SubclassProc, HookId);
        }

        _hooked.Clear();
        _self.Free();
    }

    [UnmanagedCallersOnly]
    private static nint SubclassProc(nint hWnd, uint msg, nuint wParam, nint lParam, nuint id, nuint refData)
    {
        try
        {
            var target = GCHandle.FromIntPtr((nint)refData).Target;
            if (target is ClassicMenuKeyboard self && !self._disposed)
            {
                if (self.Handle(hWnd, msg, wParam, lParam, out var result))
                {
                    return result;
                }
            }
        }
        catch (Exception ex)
        {
            // Exceptions must never unwind into native code.
            Trace.WriteLine("ClassicMenuKeyboard: " + ex);
        }

        return Win32.DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    /// <summary>Returns true when the message is consumed; the LRESULT is written to result.</summary>
    private bool Handle(nint hWnd, uint msg, nuint wParam, nint lParam, out nint result)
    {
        result = 0;
        var vk = (int)wParam;
        switch (msg)
        {
            case Win32.WM_SYSKEYDOWN:
            case Win32.WM_KEYDOWN:
            {
                var autoRepeat = (lParam & (1 << 30)) != 0;
                if (vk == Win32.VK_MENU)
                {
                    // Alt itself still goes to DefWindowProc so Alt+Space and Alt+Tab keep working.
                    if (!autoRepeat)
                    {
                        _altDown = true;
                        _altChord = false;
                    }

                    return false;
                }

                if (vk == Win32.VK_F10 && !IsCtrlOrShiftDown())
                {
                    if (!autoRepeat)
                    {
                        Toggle();
                    }

                    return true;
                }

                if (_altDown)
                {
                    _altChord = true;
                    if (TryMnemonic(vk))
                    {
                        _suppressSysChar = true;
                        return true;
                    }

                    return false;
                }

                switch (_mode)
                {
                    case Mode.BarFocused:
                        if (vk == Win32.VK_ESCAPE)
                        {
                            Deactivate();
                            return true;
                        }

                        if (vk == Win32.VK_DOWN || vk == Win32.VK_RETURN || vk == Win32.VK_SPACE)
                        {
                            OpenFlyout(_index);
                            return true;
                        }

                        return TryMnemonic(vk);
                    case Mode.FlyoutOpen:
                        // XAML closes the flyout on Esc; the bar stays active.
                        if (vk == Win32.VK_ESCAPE)
                        {
                            _mode = Mode.BarFocused;
                            Publish();
                            return false;
                        }

                        return TryPopupMnemonic(vk);
                    default:
                        // A popup can be open without menu mode (a pointer opened it). A letter then
                        // works in that popup, as it does in a Win32 menu. With no popup open this
                        // returns false, so ordinary typing in the tree is not taken.
                        return TryPopupMnemonic(vk);
                }
            }

            case Win32.WM_KEYUP:
            case Win32.WM_SYSKEYUP:
                if (vk == Win32.VK_MENU)
                {
                    var alone = _altDown && !_altChord;
                    _altDown = false;
                    if (alone)
                    {
                        Toggle();
                        return true;
                    }
                }

                return false;

            case Win32.WM_SYSCHAR:
                if (_suppressSysChar)
                {
                    _suppressSysChar = false;
                    return true;
                }

                return false;

            case Win32.WM_LBUTTONDOWN:
            case Win32.WM_RBUTTONDOWN:
                // Pointer input leaves keyboard menu mode and hides the mnemonic underlines.
                ClassicMenuState.ShowMnemonics = false;
                if (_mode != Mode.Idle)
                {
                    _mode = Mode.Idle;
                    _savedFocus = null;
                    Publish();
                }

                return false;

            case Win32.WM_ACTIVATE:
                // wParam 0 is WA_INACTIVE: losing activation ends menu mode. Only the top-level
                // window reports it, so a descendant WM_ACTIVATE does not clear the state.
                if (wParam == 0 && _mode != Mode.Idle && hWnd == _top)
                {
                    Deactivate();
                }

                return false;

            default:
                return false;
        }
    }

    private void Hook(nint hwnd)
    {
        if (_hooked.Contains(hwnd))
        {
            return;
        }

        if (Win32.SetWindowSubclass(hwnd, &SubclassProc, HookId, (nuint)GCHandle.ToIntPtr(_self)) != 0)
        {
            _hooked.Add(hwnd);
        }
    }

    private static bool IsCtrlOrShiftDown()
    {
        return (Win32.GetKeyState(Win32.VK_CONTROL) & 0x8000) != 0
            || (Win32.GetKeyState(Win32.VK_SHIFT) & 0x8000) != 0;
    }

    private void Toggle()
    {
        if (_items.Count == 0)
        {
            return;
        }

        if (_mode == Mode.Idle)
        {
            ActivateBar(0);
        }
        else
        {
            Deactivate();
        }
    }

    private void ActivateBar(int index)
    {
        if (_items.Count == 0)
        {
            return;
        }

        var root = _items[0].XamlRoot;
        if (root is null)
        {
            return;
        }

        _savedFocus = FocusManager.GetFocusedElement(root);
        _mode = Mode.BarFocused;
        ClassicMenuState.ShowMnemonics = true;
        FocusItem(index);
        Publish();
    }

    private void Deactivate()
    {
        if (_mode == Mode.FlyoutOpen)
        {
            PeerFor(_index)?.Collapse();
        }

        _mode = Mode.Idle;
        ClassicMenuState.ShowMnemonics = false;
        if (_savedFocus is Control control)
        {
            control.Focus(FocusState.Programmatic);
        }

        _savedFocus = null;
        Publish();
    }

    /// <summary>
    /// Handles a letter typed while a popup is open. The innermost open popup is the only one searched,
    /// as in a Win32 menu. Returns true when the key was consumed.
    /// </summary>
    private bool TryPopupMnemonic(int vk)
    {
        if (vk < 'A' || vk > 'Z')
        {
            return false;
        }

        MenuBarItem? top = OpenTopLevelItem();
        if (top is null)
        {
            return false;
        }

        var letter = (char)vk;
        MenuFlyoutSubItem? open = FindExpandedSubItem(top.Items);
        IList<MenuFlyoutItemBase> scope = open is not null ? open.Items : top.Items;

        // Only enabled items in the open popup with this access key. Separators have no key and are skipped.
        var matches = new List<Control>();
        foreach (MenuFlyoutItemBase entry in scope)
        {
            if (entry is MenuFlyoutItem or MenuFlyoutSubItem)
            {
                var control = (Control)entry;
                if (control.IsEnabled && MatchesAccessKey(control.AccessKey, letter))
                {
                    matches.Add(control);
                }
            }
        }

        if (matches.Count == 0)
        {
            return false;
        }

        // A single match runs at once, as in a Win32 menu. With several, the first press focuses the first
        // match and each further press focuses the next one; Enter then runs the focused item.
        if (matches.Count == 1)
        {
            Activate(matches[0]);
            return true;
        }

        int current = matches.FindIndex(IsFocused);
        matches[(current + 1) % matches.Count].Focus(FocusState.Keyboard);
        return true;
    }

    private void Activate(Control control)
    {
        if (control is MenuFlyoutSubItem sub)
        {
            ExpandSubItem(sub);
            return;
        }

        InvokeLeaf((MenuFlyoutItem)control);
    }

    private static bool IsFocused(Control control)
    {
        return control.XamlRoot is { } root && ReferenceEquals(FocusManager.GetFocusedElement(root), control);
    }

    /// <summary>
    /// The top-level item whose popup is open. Menu mode knows it directly. Otherwise the UI Automation
    /// expand state of each top-level item is read, so a popup opened by a pointer or by automation
    /// still takes letters.
    /// </summary>
    private MenuBarItem? OpenTopLevelItem()
    {
        if (_mode == Mode.FlyoutOpen && _index >= 0 && _index < _items.Count)
        {
            return _items[_index];
        }

        foreach (MenuBarItem item in _items)
        {
            if (PeerOf(item) is IExpandCollapseProvider provider
                && provider.ExpandCollapseState == ExpandCollapseState.Expanded)
            {
                return item;
            }
        }

        return null;
    }

    /// <summary>
    /// Entry point for a letter that reaches the shell through XAML instead of the window hook. Returns
    /// true when the key was consumed by an open popup.
    /// </summary>
    public bool HandlePopupKey(VirtualKey key)
    {
        return TryPopupMnemonic((int)key);
    }

    private static bool MatchesAccessKey(string? accessKey, char letter)
    {
        return !string.IsNullOrEmpty(accessKey)
            && char.ToUpperInvariant(accessKey[0]) == letter;
    }

    /// <summary>The submenu of this popup that UIA reports as expanded, or null when none is open.</summary>
    private static MenuFlyoutSubItem? FindExpandedSubItem(IList<MenuFlyoutItemBase> items)
    {
        foreach (MenuFlyoutItemBase entry in items)
        {
            if (entry is not MenuFlyoutSubItem sub)
            {
                continue;
            }

            if (PeerOf(sub) is IExpandCollapseProvider provider
                && provider.ExpandCollapseState == ExpandCollapseState.Expanded)
            {
                return sub;
            }
        }

        return null;
    }

    private void ExpandSubItem(MenuFlyoutSubItem sub)
    {
        if (PeerOf(sub) is IExpandCollapseProvider provider)
        {
            provider.Expand();
        }
    }

    /// <summary>
    /// Invokes a leaf through its automation peer, which runs the same path as a click and closes the
    /// popup. Menu mode ends after the command, so the mnemonic underlines are hidden.
    /// </summary>
    private void InvokeLeaf(MenuFlyoutItem item)
    {
        // The menu closes first, as it does after a click. The command then runs with the same action a click
        // would run, queued so it starts after this key message has returned rather than inside the window hook.
        // The open top-level item is found before the state changes; _index is stale when a pointer or
        // automation opened the popup, so it cannot name the popup that must close.
        MenuBarItem? open = OpenTopLevelItem();
        _mode = Mode.Idle;
        _savedFocus = null;
        ClassicMenuState.ShowMnemonics = false;
        if (open is not null && PeerOf(open) is IExpandCollapseProvider provider)
        {
            provider.Collapse();
        }

        Publish();
        if (MenuItemActions.TryGet(item, out Action action))
        {
            _ = item.DispatcherQueue.TryEnqueue(() => action());
        }
    }

    private static AutomationPeer? PeerOf(FrameworkElement element)
    {
        return FrameworkElementAutomationPeer.FromElement(element)
            ?? FrameworkElementAutomationPeer.CreatePeerForElement(element);
    }

    private void FocusItem(int index)
    {
        _index = index;
        _items[index].Focus(FocusState.Keyboard);
    }

    private void OpenFlyout(int index)
    {
        FocusItem(index);
        PeerFor(index)?.Expand();
        _mode = Mode.FlyoutOpen;
        Publish();
    }

    private void OnItemGotFocus(int index)
    {
        if (_mode != Mode.BarFocused || index == _index)
        {
            return;
        }

        _index = index;
        Publish();
    }

    private bool TryMnemonic(int vk)
    {
        if (vk < 'A' || vk > 'Z')
        {
            return false;
        }

        var letter = ((char)vk).ToString();
        for (var i = 0; i < _items.Count; i++)
        {
            if (string.Equals(_items[i].AccessKey, letter, StringComparison.OrdinalIgnoreCase))
            {
                if (_mode == Mode.Idle)
                {
                    ActivateBar(i);
                }

                OpenFlyout(i);
                return true;
            }
        }

        return false;
    }

    /// <summary>The top-level item's UIA ExpandCollapse peer.</summary>
    private MenuBarItemAutomationPeer? PeerFor(int index)
    {
        if (index < 0 || index >= _items.Count)
        {
            return null;
        }

        var item = _items[index];
        var peer = FrameworkElementAutomationPeer.FromElement(item) ?? FrameworkElementAutomationPeer.CreatePeerForElement(item);
        return peer as MenuBarItemAutomationPeer;
    }

    private void Publish()
    {
        if (_onState is null)
        {
            return;
        }

        if (_items.Count == 0 || _index < 0 || _index >= _items.Count)
        {
            _onState("Idle");
            return;
        }

        var text = _mode switch
        {
            Mode.BarFocused => "Focused:" + _items[_index].Title,
            Mode.FlyoutOpen => "Open:" + _items[_index].Title,
            _ => "Idle",
        };
        _onState(text);
    }
}
