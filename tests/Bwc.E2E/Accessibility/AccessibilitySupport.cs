using Bwc.E2E.Controls;
using Bwc.E2E.Dialogs;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace Bwc.E2E.Accessibility;

/// <summary>
/// Helpers for the UI Automation checks. Each read returns a neutral value (empty text, null, false) when the
/// element or the pattern is missing, so a missing pattern becomes a recorded failure and never a thrown exception
/// that would hide the rest of the checks.
/// </summary>
internal static class AccessibilitySupport
{
    internal static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Starts the app against a seeded sandbox with a fresh data root for this scope.</summary>
    internal static AppSession Launch(E2EConfig config, SandboxRegistry sandbox, string scope, IReadOnlyDictionary<string, string>? extra = null)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [E2EConfig.DataRootEnvironmentVariable] = ClassicDialogTests.FreshDataRoot(config, scope),
            ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
        };

        if (extra is not null)
        {
            foreach (KeyValuePair<string, string> pair in extra)
            {
                environment[pair.Key] = pair.Value;
            }
        }

        return new AppSession(config, config.RegeditExePath, environment: environment);
    }

    internal static string ControlTypeOf(AutomationElement? element)
    {
        if (element is null)
        {
            return "missing";
        }

        try
        {
            return element.ControlType.ToString();
        }
        catch (Exception)
        {
            return "stale";
        }
    }

    internal static bool HasPattern(AutomationElement? element, Func<AutomationElement, bool> supported)
    {
        if (element is null)
        {
            return false;
        }

        try
        {
            return supported(element);
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static string TextOf(Func<string> read)
    {
        try
        {
            return read() ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>The Name of an element, or an empty string when it cannot be read.</summary>
    internal static string NameOf(AutomationElement? element) => ControlTestSupport.SafeName(element);

    /// <summary>The automation id of an element, or an empty string when it cannot be read.</summary>
    internal static string IdOf(AutomationElement? element) => ControlTestSupport.SafeId(element);

    /// <summary>Reads the Expand/Collapse state name, or a marker such as "missing" or "unsupported".</summary>
    internal static string StateOf(AutomationElement? element) => ControlTestSupport.StateOf(element);

    /// <summary>
    /// Waits until the element with this automation id exists, then returns it. Null when it does not appear in time.
    /// </summary>
    internal static AutomationElement? WaitForId(AppSession session, string automationId, TimeSpan timeout)
    {
        AutomationElement? found = null;
        _ = ControlTestSupport.WaitUntil(() => (found = session.Find(automationId)) is not null, timeout);
        return found;
    }

    /// <summary>The window handle of the element's top-level window, or zero.</summary>
    internal static IntPtr WindowHandleOf(AutomationElement element)
    {
        try
        {
            return (IntPtr)element.Properties.NativeWindowHandle.Value;
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }
}
