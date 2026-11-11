using System.ComponentModel;
using System.Globalization;
using Bwc.Registry;
using Bwc.UI.Dialogs;
using Microsoft.Windows.ApplicationModel.Resources;

namespace Bwc.Regedit.Controllers;

/// <summary>
/// The registry operation a command was performing. The same engine operation can mean different
/// things, for example SetValue creates a value or edits one, so the controller names the action
/// and the catalog picks the classic text for it.
/// </summary>
internal enum RegistryAction
{
    /// <summary>A command with no registry operation of its own. Shows the plain command text.</summary>
    Command,
    RenameKey,
    RenameValue,
    DeleteKey,
    DeleteValues,
    CreateKey,
    CreateValue,
    EditValue,
    OpenKey,
    ReadValue,
    ImportFile,
    ExportFile,
    LoadHive,
    UnloadHive,
    Connect,
    Permissions,
    Print,
    FavoriteAdd,
}

/// <summary>A classic message box: the caption and the text, both already resolved.</summary>
internal readonly record struct ClassicMessage(string Caption, string Text)
{
    /// <summary>Builds the error box that shows this message.</summary>
    public ErrorDialog ToErrorDialog() => ErrorDialog.ForMessage(Caption, Text);
}

/// <summary>
/// The Registry Editor's user-facing text. Titles, labels, and messages come from the resource map by
/// key. The English text that used to be written inline in the view lives here as named constants.
/// The classic message and caption texts come from the oracle string table and are keyed Msg* in
/// Resources.resw, with the oracle id in a comment there.
/// </summary>
internal sealed class MessageCatalog
{
    /// <summary>Label shown for the unnamed default value.</summary>
    public const string DefaultValueLabel = "(Default)";

    /// <summary>Prefix of the attempted path line in an address bar error.</summary>
    public const string PathLinePrefix = "Path: ";

    /// <summary>Error text used when the Windows print dialog returns no printer device.</summary>
    public const string PrintNoDeviceMessage = "The Windows print dialog returned no printer device.";

    /// <summary>Base of the suggested name for a new key. An index follows it.</summary>
    public const string NewKeyNamePrefix = "New Key #";

    /// <summary>Base of the suggested name for a new value. An index follows it.</summary>
    public const string NewValueNamePrefix = "New Value #";

    /// <summary>File base name used when the exported key has no usable name.</summary>
    public const string FallbackExportName = "export";

    /// <summary>Win32 ERROR_ACCESS_DENIED.</summary>
    private const int ErrorAccessDenied = 5;

    /// <summary>Win32 ERROR_PRIVILEGE_NOT_HELD.</summary>
    private const int ErrorPrivilegeNotHeld = 1314;

    /// <summary>Win32 ERROR_FILE_NOT_FOUND and ERROR_PATH_NOT_FOUND, and ERROR_KEY_DELETED.</summary>
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorKeyDeleted = 1018;

    /// <summary>Win32 ERROR_SHARING_VIOLATION, the hive file is open in another process.</summary>
    private const int ErrorSharingViolation = 32;

    /// <summary>Win32 ERROR_FILE_EXISTS and ERROR_ALREADY_EXISTS.</summary>
    private const int ErrorFileExists = 80;
    private const int ErrorAlreadyExists = 183;

    private readonly ResourceLoader _loader;

    public MessageCatalog(ResourceLoader loader)
    {
        ArgumentNullException.ThrowIfNull(loader);
        _loader = loader;
    }

    /// <summary>Returns the resource string for a key.</summary>
    public string Get(string key) => _loader.GetString(key);

    /// <summary>
    /// Returns the classic message for a failed registry operation. <paramref name="subject"/> fills
    /// {0}: the leaf name of the key or value, or the full path where the classic text names a path.
    /// The developer detail stays in the log, which the caller writes. Unknown codes use the generic
    /// text of the action.
    /// </summary>
    public ClassicMessage ForRegistryError(RegistryAction action, RegistryOperationException error, string subject)
    {
        ArgumentNullException.ThrowIfNull(error);
        return Describe(action, error.Win32Error, subject, detail: null);
    }

    /// <summary>
    /// Returns the classic message for a failed read of a key that could not be opened. The system
    /// text of the Win32 code is shown as the Details line when the code has one.
    /// </summary>
    public ClassicMessage ForUnreadableKey(string keyPath, int win32Error)
    {
        string detail = win32Error < 0 ? string.Empty : new Win32Exception(win32Error).Message;
        return detail.Length == 0
            ? new ClassicMessage(Get("MsgCaptionOpenKeyError"), string.Format(CultureInfo.CurrentCulture, Get("MsgOpenKeyFailed"), keyPath))
            : new ClassicMessage(
                Get("MsgCaptionOpenKeyError"),
                string.Format(CultureInfo.CurrentCulture, Get("MsgOpenKeyFailedDetails"), keyPath, detail));
    }

    /// <summary>
    /// Returns the classic message for a value whose data cannot be read. The display and the edit
    /// texts differ in caption and text. <paramref name="valueLabel"/> fills {0}.
    /// </summary>
    public ClassicMessage ForValueRead(string valueLabel, bool forEdit) => forEdit
        ? new ClassicMessage(
            Get("MsgCaptionEditValueError"),
            string.Format(CultureInfo.CurrentCulture, Get("MsgEditValueReadFailed"), valueLabel))
        : new ClassicMessage(
            Get("MsgCaptionDisplayValueError"),
            string.Format(CultureInfo.CurrentCulture, Get("MsgDisplayValueFailed"), valueLabel));

    /// <summary>Returns the classic message for a failed connection to a remote registry.</summary>
    public ClassicMessage ForConnect(RemoteRegistryFailureReason reason, int win32Error, string computerName)
    {
        string caption = Get("DialogConnectFailedTitle");
        return reason switch
        {
            RemoteRegistryFailureReason.OwnComputer => new ClassicMessage(caption, Get("DialogConnectLocal")),
            RemoteRegistryFailureReason.PartialConnection => new ClassicMessage(caption, Get("MsgConnectRootsFailed")),
            _ => new ClassicMessage(
                caption,
                string.Format(
                    CultureInfo.CurrentCulture,
                    Get(win32Error == ErrorAccessDenied ? "MsgConnectPermission" : "MsgConnectFailed"),
                    computerName)),
        };
    }

    /// <summary>Returns the classic message for a permissions dialog that could not show the key's security.</summary>
    public ClassicMessage ForPermissions(int win32Error) =>
        Describe(RegistryAction.Permissions, win32Error, subject: string.Empty, detail: null);

    /// <summary>Returns the classic message for a command that failed with no registry operation.</summary>
    public ClassicMessage ForCommandFailure() =>
        new(Get("DialogErrorTitle"), Get("MsgCommandFailed"));

    /// <summary>
    /// The action an engine operation name stands for, used when a command did not name its action.
    /// Operations with no classic text map to <see cref="RegistryAction.Command"/>.
    /// </summary>
    public static RegistryAction ActionFor(string operation) => operation switch
    {
        "RenameKey" => RegistryAction.RenameKey,
        "RenameValue" => RegistryAction.RenameValue,
        "DeleteKey" => RegistryAction.DeleteKey,
        "DeleteValue" => RegistryAction.DeleteValues,
        "CreateKey" => RegistryAction.CreateKey,
        "SetValue" => RegistryAction.EditValue,
        "OpenKey" or "QueryInfoKey" or "EnumKey" or "EnumValue" or "GetKeySecurity" => RegistryAction.OpenKey,
        "QueryValue" => RegistryAction.ReadValue,
        "LoadHive" => RegistryAction.LoadHive,
        "UnloadHive" => RegistryAction.UnloadHive,
        "Connect" => RegistryAction.Connect,
        "ExportValueName" => RegistryAction.ExportFile,
        _ => RegistryAction.Command,
    };

    /// <summary>
    /// Returns the classic message for a failed import or export of a file. <paramref name="file"/>
    /// fills {0} and is the file name the user chose.
    /// </summary>
    public ClassicMessage ForFileError(RegistryAction action, Exception exception, string file)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is RegistryOperationException registry)
        {
            return ForRegistryError(action, registry, file);
        }

        string key = action == RegistryAction.ImportFile
            ? ImportFileKey(exception)
            : ExportFileKey(exception);
        return new ClassicMessage(Get("DialogErrorTitle"), string.Format(CultureInfo.CurrentCulture, Get(key), file));
    }

    private ClassicMessage Describe(RegistryAction action, int code, string subject, string? detail)
    {
        (string captionKey, string? messageKey) = Keys(action, code);
        string caption = Get(captionKey);
        string text = messageKey is null
            ? Get("MsgCommandFailed")
            : string.Format(CultureInfo.CurrentCulture, Get(messageKey), subject, detail ?? string.Empty);
        return new ClassicMessage(caption, text);
    }

    /// <summary>
    /// Picks the caption and the message key for an action and a Win32 code. The first matching code
    /// class wins, then the generic text of the action.
    /// </summary>
    private (string CaptionKey, string? MessageKey) Keys(RegistryAction action, int code)
    {
        bool denied = code is ErrorAccessDenied or ErrorPrivilegeNotHeld;
        bool missing = code is ErrorFileNotFound or ErrorPathNotFound or ErrorKeyDeleted;
        bool exists = code is ErrorFileExists or ErrorAlreadyExists;

        switch (action)
        {
            case RegistryAction.Command:
                return ("DialogErrorTitle", null);

            case RegistryAction.RenameKey:
                if (exists)
                {
                    return ("MsgCaptionRenameKeyError", "MsgRenameKeyExists");
                }

                return ("MsgCaptionRenameKeyError", "MsgRenameKeyFailed");

            case RegistryAction.RenameValue:
                if (exists)
                {
                    return ("MsgCaptionRenameValueError", "MsgRenameValueExists");
                }

                return ("MsgCaptionRenameValueError", "MsgRenameValueFailed");

            case RegistryAction.DeleteKey:
                if (missing)
                {
                    return ("MsgCaptionDeleteKeyError", "DialogAddressBarKeyNotFound");
                }

                return ("MsgCaptionDeleteKeyError", "MsgDeleteKeyFailed");

            case RegistryAction.DeleteValues:
                return ("MsgCaptionDeleteValuesError", "MsgDeleteValuesFailed");

            case RegistryAction.CreateKey:
                if (denied)
                {
                    return ("MsgCaptionCreateKeyError", "MsgCreateKeyPermission");
                }

                return ("MsgCaptionCreateKeyError", "MsgCreateKeyWriteFailed");

            case RegistryAction.CreateValue:
                return ("MsgCaptionCreateValueError", "MsgCreateValueWriteFailed");

            case RegistryAction.EditValue:
                return ("MsgCaptionEditValueError", "MsgEditValueWriteFailed");

            case RegistryAction.OpenKey:
                if (missing)
                {
                    return ("MsgCaptionOpenKeyError", "DialogAddressBarKeyNotFound");
                }

                return ("MsgCaptionOpenKeyError", "MsgOpenKeyFailed");

            case RegistryAction.ReadValue:
                return ("MsgCaptionDisplayValueError", "MsgDisplayValueFailed");

            case RegistryAction.ImportFile:
                if (denied)
                {
                    return ("DialogErrorTitle", "MsgImportPrivileges");
                }

                if (missing)
                {
                    return ("DialogErrorTitle", "MsgImportInvalidKey");
                }

                // The engine writes entries one at a time, so a failed write leaves the earlier ones in place.
                return ("DialogErrorTitle", "MsgImportPartial");

            case RegistryAction.ExportFile:
                if (denied)
                {
                    return ("DialogErrorTitle", "MsgExportPrivileges");
                }

                if (missing)
                {
                    return ("DialogErrorTitle", "MsgExportKeyInvalid");
                }

                return ("DialogErrorTitle", "MsgExportWriteFailed");

            case RegistryAction.LoadHive:
                if (code == ErrorPrivilegeNotHeld)
                {
                    return ("DialogLoadHiveTitle", "MsgLoadHivePrivileges");
                }

                if (code == ErrorAccessDenied)
                {
                    return ("DialogLoadHiveTitle", "MsgLoadHiveAccessDenied");
                }

                if (code == ErrorSharingViolation)
                {
                    return ("DialogLoadHiveTitle", "MsgLoadHiveInUse");
                }

                return ("DialogLoadHiveTitle", "MsgLoadHiveFailed");

            case RegistryAction.UnloadHive:
                if (code == ErrorPrivilegeNotHeld)
                {
                    return ("DialogUnloadHiveTitle", "MsgUnloadHivePrivileges");
                }

                if (code == ErrorAccessDenied)
                {
                    return ("DialogUnloadHiveTitle", "MsgUnloadHiveAccessDenied");
                }

                return ("DialogUnloadHiveTitle", "MsgUnloadHiveFailed");

            case RegistryAction.Connect:
                return ("DialogConnectFailedTitle", denied ? "MsgConnectPermission" : "MsgConnectFailed");

            case RegistryAction.Permissions:
                if (denied)
                {
                    return ("DialogErrorTitle", "MsgSecurityRetrieveDenied");
                }

                if (code == ErrorKeyDeleted)
                {
                    return ("DialogErrorTitle", "MsgSecurityRetrieveDeleted");
                }

                return ("DialogErrorTitle", "MsgSecurityRetrieveFailed");

            case RegistryAction.Print:
                return ("DialogErrorTitle", "MsgPrintFailed");

            case RegistryAction.FavoriteAdd:
                return ("DialogErrorAddingFavorite", "MsgCreateValueWriteFailed");

            default:
                return ("DialogErrorTitle", "MsgCommandFailed");
        }
    }

    private static string ImportFileKey(Exception exception) => exception switch
    {
        FileNotFoundException or DirectoryNotFoundException => "MsgImportFileMissing",
        UnauthorizedAccessException => "MsgImportPrivileges",
        RegFileParseException => "MsgImportNotRegistryFile",
        IOException => "MsgImportOpenFailed",
        _ => "MsgImportRegistryAccess",
    };

    private static string ExportFileKey(Exception exception) => exception switch
    {
        UnauthorizedAccessException => "MsgExportPrivileges",
        FileNotFoundException or DirectoryNotFoundException => "MsgExportOpenFailed",
        _ => "MsgExportWriteFailed",
    };
}
