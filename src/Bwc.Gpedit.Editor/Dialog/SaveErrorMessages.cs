using System.Globalization;
using Bwc.Gpo.Store;

namespace Bwc.Gpedit.Editor.Dialog;

/// <summary>A message for a failed or partly failed save: the kind (which names the automation id) and the text.</summary>
/// <param name="Kind">The kind, such as <c>ReadOnlyFile</c> or <c>LockTimeout</c>.</param>
/// <param name="Text">The text of the message box.</param>
internal sealed record SaveErrorMessage(string Kind, string Text)
{
    /// <summary>The automation id of the text of the message box.</summary>
    internal string AutomationId => "DialogSaveError." + Kind;
}

/// <summary>
/// Maps what the store says went wrong to one message for each kind, so a user can tell a read-only file from a held lock
/// (docs/failure-modes/gpedit-dialog.md, section 15). The engine formats no text; the words are in Dialog.resw.
/// </summary>
internal static class SaveErrorMessages
{
    /// <summary>The message for a failure of the store. A failure after some files were written is a partial save.</summary>
    /// <param name="failure">The failure.</param>
    internal static SaveErrorMessage For(GpoStoreException failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (failure.WrittenFiles.Count > 0)
        {
            string files = string.Join(", ", failure.WrittenFiles.Select(ShortName));
            return Make("Partial", "DialogSavePartial", files);
        }

        return failure.Reason switch
        {
            GpoStoreFailure.ReadOnlyTarget => Make("ReadOnlyFile", "DialogSaveReadOnlyFile", ShortName(failure.Path)),
            GpoStoreFailure.Malformed => Make("Malformed", "DialogSaveMalformed", ShortName(failure.Path)),
            GpoStoreFailure.LockTimeout or GpoStoreFailure.LockUnavailable => Make("LockTimeout", "DialogSaveLockTimeout"),
            GpoStoreFailure.InUse => Make("InUse", "DialogSaveInUse", ShortName(failure.Path)),
            GpoStoreFailure.ReadOnlyMode => Make("ReadOnlyMode", "DialogSaveReadOnlyMode"),
            GpoStoreFailure.RefreshFailed => ForRefresh(),
            _ => Make("Failed", "DialogSaveFailed", ShortName(failure.Path)),
        };
    }

    /// <summary>The notice for a save whose files are on disk but whose policy refresh failed.</summary>
    internal static SaveErrorMessage ForRefresh() => Make("RefreshFailed", "DialogSaveRefreshFailed");

    private static SaveErrorMessage Make(string kind, string key, string? detail = null)
    {
        string template = DialogStrings.Get(key);
        return new SaveErrorMessage(kind, detail is null ? template : string.Format(CultureInfo.CurrentCulture, template, detail));
    }

    /// <summary>The file name with the name of its folder (Machine\Registry.pol), enough to tell the files apart.</summary>
    private static string ShortName(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        string file = Path.GetFileName(path);
        string folder = Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty);
        return folder.Length == 0 || file.Length == 0 ? path : folder + "\\" + file;
    }
}
