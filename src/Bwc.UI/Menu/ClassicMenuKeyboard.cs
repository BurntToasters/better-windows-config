using System.Diagnostics;
using System.Runtime.InteropServices;
using Bwc.UI.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
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
                        }

                        return false;
                    default:
                        return false;
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
                // Pointer input leaves keyboard menu mode.
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
        if (_savedFocus is Control control)
        {
            control.Focus(FocusState.Programmatic);
        }

        _savedFocus = null;
        Publish();
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
