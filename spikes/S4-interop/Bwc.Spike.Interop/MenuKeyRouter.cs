using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Bwc.Spike.Interop;

/// <summary>
/// Win32-style menu-bar keyboard model for a WinUI MenuBar:
///   Alt press+release alone, or F10  -> activate the bar (first item focused and highlighted)
///   Alt+mnemonic (or a bare mnemonic while the bar is active) -> open that top-level item's flyout
///   Esc (bar active)   -> restore the focus that was active before activation
///   Left/Right (bar active) -> previous/next top-level item (XAML XYFocus ring, wraps; see HandleKeyFromXaml)
///   Down (bar active)  -> open that item's flyout (HandleKeyFromXaml); Enter/Space -> open it (window hook)
///   Esc (flyout open)  -> passed through so XAML closes the flyout; the bar stays active
///
/// Hook approach: SetWindowSubclass on the top-level HWND and on every descendant HWND. The WinUI
/// keyboard focus lives in a child HWND of the window, so WM_SYSKEYDOWN / WM_KEYDOWN are delivered there.
/// A top-level-only subclass does not see them. Consumed messages are not passed to DefSubclassProc.
/// Arrow keys are the exception: XAML's island input pipeline handles them before the window hook runs.
/// </summary>
internal sealed unsafe class MenuKeyRouter : IDisposable
{
    private const nuint HookId = 0x42533434; // 'BS44'

    private enum Mode { Idle, BarFocused, FlyoutOpen }

    private readonly nint _top;
    private readonly IReadOnlyList<MenuBarItem> _items;
    private readonly Action<string> _onState;
    private readonly List<nint> _hooked = new();
    private readonly GCHandle _self;
    private bool _altDown;
    private bool _altChord;
    private bool _suppressSysChar;
    private Mode _mode;
    private int _index;
    private object? _savedFocus;
    private bool _disposed;

    public MenuKeyRouter(nint topHwnd, IReadOnlyList<MenuBarItem> items, Action<string> onState)
    {
        _top = topHwnd;
        _items = items;
        _onState = onState;
        _self = GCHandle.Alloc(this);
        for (var i = 0; i < items.Count; i++)
        {
            var index = i;
            // Focus can also move between top-level items inside XAML; keep the reported state in step with focus.
            items[i].GotFocus += (_, _) => OnItemGotFocus(index);
        }
        HookTree(topHwnd);
        Publish();
    }

    /// <summary>
    /// Arrow keys are routed by XAML's island input pipeline before window messages are dispatched, so the
    /// window-procedure hook never sees them. Left/Right are moved by the XYFocus ring that MainWindow
    /// configures on the MenuBarItems (XAML focus navigation, which runs regardless of Handled), so this
    /// only opens the flyout for Down. The MenuBar's PreviewKeyDown calls this.
    /// Returns true when the key was consumed.
    /// </summary>
    public bool HandleKeyFromXaml(VirtualKey key)
    {
        if (_mode != Mode.BarFocused || key != VirtualKey.Down) return false;
        OpenFlyout(_index);
        return true;
    }

    private void OnItemGotFocus(int index)
    {
        if (_mode != Mode.BarFocused || index == _index) return;
        _index = index;
        Publish();
    }

    /// <summary>Hooks a window and all of its descendants. Safe to call repeatedly.</summary>
    public void HookTree(nint hwnd)
    {
        Hook(hwnd);
        for (var child = Win32.GetWindow(hwnd, Win32.GW_CHILD); child != 0; child = Win32.GetWindow(child, Win32.GW_HWNDNEXT))
        {
            HookTree(child);
        }
    }

    private void Hook(nint hwnd)
    {
        if (_hooked.Contains(hwnd)) return;
        if (Win32.SetWindowSubclass(hwnd, &SubclassProc, HookId, (nuint)GCHandle.ToIntPtr(_self)) != 0)
        {
            _hooked.Add(hwnd);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
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
        var self = (MenuKeyRouter?)GCHandle.FromIntPtr((nint)refData).Target;
        if (self is not null && !self._disposed)
        {
            try
            {
                if (self.Handle(hWnd, msg, wParam, lParam, out var consumed))
                {
                    return consumed;
                }
            }
            catch (Exception ex)
            {
                // Exceptions must never unwind into native code.
                Trace.WriteLine("MenuKeyRouter: " + ex);
            }
        }
        return Win32.DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    /// <summary>Returns true when the message is consumed; the LRESULT to return is written to result.</summary>
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
                    // Alt itself still goes to DefWindowProc so OS Alt+Space / Alt+Tab behaviour is preserved.
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
                        if (vk == Win32.VK_ESCAPE) { Deactivate(); return true; }
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
                // wParam == WA_INACTIVE (0): losing activation (for example a modal dialog opening) ends menu mode.
                if (wParam == 0 && _mode != Mode.Idle && hWnd == _top)
                {
                    Deactivate();
                }
                return false;
        }
        return false;
    }

    private static bool IsCtrlOrShiftDown()
    {
        return (Win32.GetKeyState(Win32.VK_CONTROL) & 0x8000) != 0 || (Win32.GetKeyState(Win32.VK_SHIFT) & 0x8000) != 0;
    }

    private void Toggle()
    {
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
        _savedFocus = FocusManager.GetFocusedElement(_items[0].XamlRoot);
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

    private bool TryMnemonic(int vk)
    {
        if (vk < 'A' || vk > 'Z') return false;
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

    /// <summary>The top-level item's UIA ExpandCollapse peer (public Expand/Collapse on MenuBarItemAutomationPeer).</summary>
    private MenuBarItemAutomationPeer? PeerFor(int index)
    {
        var item = _items[index];
        var peer = FrameworkElementAutomationPeer.FromElement(item) ?? FrameworkElementAutomationPeer.CreatePeerForElement(item);
        return peer as MenuBarItemAutomationPeer;
    }

    private void Publish()
    {
        var name = _items[_index].Title;
        var text = _mode switch
        {
            Mode.BarFocused => "Focused:" + name,
            Mode.FlyoutOpen => "Open:" + name,
            _ => "Idle",
        };
        _onState(text);
    }
}
