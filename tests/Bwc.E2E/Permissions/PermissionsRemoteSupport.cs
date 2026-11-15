using Bwc.E2E.Controls;
using Bwc.E2E.Editing;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace Bwc.E2E.Permissions;

/// <summary>
/// The loopback connect and the address-bar navigation that the Permissions remote test needs. They follow
/// tests/Bwc.E2E/Remote/RemoteCommandTests.cs, which keeps its copies private. This file only drives the UI and
/// writes no registry value. See docs/failure-modes/permissions.md section 34.
/// </summary>
internal static class PermissionsRemoteSupport
{
    internal const string LoopbackComputer = "127.0.0.1";
    internal const string LoopbackNode = @"\\127.0.0.1";
    internal const string ConnectTargetVariable = "BWC_E2E_CONNECT_TARGET";

    private const string AddressBarId = "AddressBar";
    private const string DialogRootId = "DialogRoot";
    private const string DialogErrorTextId = "DialogErrorText";
    private const int MenuFile = 512;
    private const int MenuFileConnect = 657;

    /// <summary>The result of File > Connect. <see cref="RemoteError"/> is set when the system refused it.</summary>
    internal sealed record ConnectResult(bool Connected, string? RemoteError, string Detail);

    internal static ConnectResult ConnectLoopback(AppSession session, EditingDriver driver)
    {
        try
        {
            driver.OpenMenu(MenuFile, MenuFileConnect);
        }
        catch (Exception)
        {
            return new ConnectResult(false, null, "File > Connect Network Registry did not open.");
        }

        // The session sets BWC_E2E_CONNECT_TARGET, so Connect skips the picker and connects to the loopback name.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (FindTreeRow(session, LoopbackNode) is not null)
            {
                return new ConnectResult(true, null, string.Empty);
            }

            string error = driver.TextOf(DialogErrorTextId);
            if (error.Length > 0)
            {
                _ = driver.DismissErrorBox();
                if (driver.WaitForElement(DialogRootId, TimeSpan.FromMilliseconds(300)) is not null)
                {
                    _ = driver.Invoke("DialogSecondary");
                }

                return new ConnectResult(false, error, error);
            }

            Thread.Sleep(100);
        }

        return new ConnectResult(false, null, $"Neither the '{LoopbackNode}' node nor an error appeared within 30 s.");
    }

    internal static AutomationElement? FindTreeRow(AppSession session, string name) =>
        ControlTestSupport.FindRowByName(session, ControlTestSupport.TreeControl, ControlType.TreeItem, name);

    internal static string SelectedRowName(AppSession session) =>
        ControlTestSupport.SafeName(
            ControlTestSupport.Rows(session, ControlTestSupport.TreeControl, ControlType.TreeItem)
                .FirstOrDefault(row => ControlTestSupport.IsSelected(row)));

    /// <summary>Types the path in the address bar and waits until the named row is the selected key.</summary>
    internal static bool NavigateAddress(AppSession session, string typedPath, string selectedRow)
    {
        _ = ControlTestSupport.ReleaseStuckModifiers();
        if (!ControlTestSupport.CloseOpenMenus(session) || !EditAddress(session, typedPath))
        {
            return false;
        }

        if (!TrySendKeys(session, "Enter"))
        {
            return false;
        }

        return ControlTestSupport.WaitUntil(
            () => string.Equals(SelectedRowName(session), selectedRow, StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(10));
    }

    internal static string AddressText(AppSession session)
    {
        AutomationElement? address = session.Find(AddressBarId);
        return address is null ? string.Empty : session.TextOf(address);
    }

    private static bool EditAddress(AppSession session, string text)
    {
        if (!FocusAddressBar(session))
        {
            return false;
        }

        AutomationElement? address = session.Find(AddressBarId);
        if (address is null)
        {
            return false;
        }

        try
        {
            address.AsTextBox().Text = string.Empty;
        }
        catch (Exception)
        {
            return false;
        }

        return TrySendKeys(session, text) && ControlTestSupport.WaitUntil(
            () => string.Equals(AddressText(session), text, StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(5));
    }

    private static bool FocusAddressBar(AppSession session)
    {
        if (!ControlTestSupport.EnsureForeground(session))
        {
            return false;
        }

        return ControlTestSupport.WaitUntil(
            () =>
            {
                AutomationElement? address = session.Find(AddressBarId);
                if (address is null)
                {
                    return false;
                }

                try
                {
                    address.Focus();
                    return session.Find(AddressBarId)?.Properties.HasKeyboardFocus.Value == true;
                }
                catch (Exception)
                {
                    // The next pass reacquires the element.
                    return false;
                }
            },
            TimeSpan.FromSeconds(3));
    }

    private static bool TrySendKeys(AppSession session, string key)
    {
        try
        {
            ControlTestSupport.SendKeys(session, key);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
