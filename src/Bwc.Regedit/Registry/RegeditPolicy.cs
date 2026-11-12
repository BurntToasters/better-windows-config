using System.Runtime.InteropServices;
using Bwc.Core;
using Bwc.Registry;

namespace Bwc.Regedit.Registry;

/// <summary>
/// The administrator policy that disables registry editing:
/// <c>Policies\System\DisableRegistryTools</c> under HKCU and HKLM. The application only reads it. A
/// value of 1 or 2 disables the window; 2 also refuses a silent import. An E2E build can replace the
/// value with the <c>BWC_E2E_DISABLE_REGISTRY_TOOLS</c> environment variable, so tests never set the
/// real policy.
/// </summary>
public static partial class RegeditPolicy
{
    private const string PolicyKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string ValueName = "DisableRegistryTools";
    private const string E2EOverrideVariable = "BWC_E2E_DISABLE_REGISTRY_TOOLS";

    private const uint MbOk = 0x00000000;
    private const uint MbIconError = 0x00000010;

    /// <summary>
    /// The policy value that applies: the larger of the HKCU and HKLM values, or 0 when neither is set
    /// or neither can be read.
    /// </summary>
    public static int ReadDisableRegistryTools()
    {
#if BWC_E2E
        if (TryReadE2EOverride(out int overridden))
        {
            return overridden;
        }
#endif

        var reader = new RegistryReader();
        int user = ReadDword(reader, RegistryPath.Parse(@"HKEY_CURRENT_USER\" + PolicyKeyPath));
        int machine = ReadDword(reader, RegistryPath.Parse(@"HKEY_LOCAL_MACHINE\" + PolicyKeyPath));
        return Math.Max(user, machine);
    }

    /// <summary>True when the policy value disables the window: 1 and 2.</summary>
    public static bool DisablesRegistryEditing(int policy) => policy is 1 or 2;

    /// <summary>True when the policy value also refuses a silent import: 2.</summary>
    public static bool BlocksSilentImport(int policy) => policy == 2;

    /// <summary>
    /// Shows the classic policy message (RT_STRING 40) in a message box with the Registry Editor caption.
    /// The box has no owner window, because the window is never created.
    /// </summary>
    public static void ShowRegistryEditingDisabled() =>
        _ = MessageBoxW(
            nint.Zero,
            RegeditStrings.Get("MsgRegistryEditingDisabled"),
            RegeditStrings.Get("MsgCaptionRegistryEditor"),
            MbOk | MbIconError);

    private static int ReadDword(RegistryReader reader, RegistryPath key)
    {
        try
        {
            // A missing policy key is the normal case on a machine without the policy, so it is not logged.
            if (!reader.KeyExists(key))
            {
                return 0;
            }

            RegistryValueInfo? value = reader.ReadValue(key, ValueName);
            return value is not null && RegistryValueCodec.IsValidDword(value.Data)
                ? (int)RegistryValueCodec.DecodeDword(value.Data)
                : 0;
        }
        catch (Exception ex) when (ex is RegistryOperationException or FormatException)
        {
            Log.Warn($"Registry Editor could not read the policy '{key.ToDisplayString()}': {ex.Message}");
            return 0;
        }
    }

#if BWC_E2E
    private static bool TryReadE2EOverride(out int value)
    {
        string? configured = Environment.GetEnvironmentVariable(E2EOverrideVariable);
        return int.TryParse(configured, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out value)
            && value >= 0;
    }
#endif

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(nint hWnd, string text, string caption, uint type);
}
