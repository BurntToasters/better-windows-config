using System.Globalization;
using Bwc.Core;
using Bwc.Regedit.Registry;
using Bwc.Registry;
using Bwc.UI.Dialogs;
using Bwc.UI.List;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Bwc.Regedit.Controllers;

/// <summary>Value commands: new, modify, rename, delete, the hex viewer and editor, and binary data.</summary>
internal sealed class ValueCommands
{
    private readonly RegeditContext _context;

    public ValueCommands(RegeditContext context)
    {
        _context = context;
    }

    public enum NewValueKind
    {
        String,
        ExpandableString,
        Binary,
        Dword,
        Qword,
        MultiString,
    }

    private string Get(string key) => _context.Messages.Get(key);

    public void NewValue(NewValueKind kind) => _context.StartCommand(() => NewValueAsync(kind));

    public void DeleteValues() => _context.StartCommand(DeleteValuesAsync);

    /// <summary>Opens the editor for an Entered or double-clicked value. It is the same dispatch as Modify.</summary>
    public void ActivateValue(DetailsRow row) => _context.StartCommand(() => ModifyValueAsync(row));

    /// <summary>Applies an inline rename that the list committed for one of its rows.</summary>
    public void RenameFromList(DetailsRow row, string newName)
    {
        _context.StartCommand(async () =>
        {
            if (!_context.TryGetSelectedKeyPath(out RegistryPath key))
            {
                await _context.ShowMessageAsync(Get("DialogNoSelection"));
                return;
            }

            if (string.Equals(row.Name, MessageCatalog.DefaultValueLabel, StringComparison.Ordinal) ||
                !_context.TryGetRawValueName(row, out string oldName) ||
                oldName.Length == 0)
            {
                await _context.ShowMessageAsync(Get("DialogDefaultValueLocked"));
                return;
            }

            await RenameValueCoreAsync(key, oldName, newName);
        });
    }

    /// <summary>Opens the read-only binary viewer for the single selected value.</summary>
    public void DisplayBinaryData() => _context.StartCommand(ShowBinaryDataAsync);

    public void ModifyBinaryData() => _context.StartCommand(ModifyBinaryDataAsync);

    public void ModifySelectedValue() => _context.StartCommand(() => ModifyValueAsync(null));

    private async Task ShowBinaryDataAsync()
    {
        if (_context.DialogOpen || _context.WindowHwnd == 0)
        {
            Log.Warn(
                "Registry Editor ignored Display Binary Data because another modal is open or the window handle is not attached.");
            return;
        }

        if (!_context.TryGetSelectedKeyPath(out RegistryPath key))
        {
            await _context.ShowMessageAsync(Get("DialogNoSelection"));
            return;
        }

        IReadOnlyList<DetailsRow> rows = _context.Shell.ListContent.SelectedRows;
        if (rows.Count != 1 || !_context.TryGetRawValueName(rows[0], out string rawName))
        {
            await _context.ShowMessageAsync(Get("DialogHexNoSelection"));
            return;
        }

        RegistryValueInfo? value;
        try
        {
            value = await _context.RunRegistryAsync(() => _context.Store.Reader.ReadValue(key, rawName));
        }
        catch (RegistryOperationException ex)
        {
            await _context.ShowRegistryErrorAsync(ex, RegistryAction.ReadValue, ValueLabel(rawName));
            return;
        }

        if (value is null)
        {
            await ShowValueReadErrorAsync(rawName, forEdit: false);
            return;
        }

        await ShowReadOnlyViewerAsync(rows[0].Name, key, value);
        _ = _context.Shell.ListContent.Focus(FocusState.Programmatic);
    }

    /// <summary>Shows the read-only binary viewer for a value. The bytes are not written back.</summary>
    private async Task ShowReadOnlyViewerAsync(string displayName, RegistryPath key, RegistryValueInfo value)
    {
        var dialog = new HexViewerDialog(Get("DialogHexTitle"), Get("DialogHexClose"));
        dialog.SetValue(
            displayName,
            key.ToDisplayString(),
            value.Data,
            RegistryValueTypeExtensions.ToDisplayName(value.Type));

        _ = await _context.ShowDialogAsync(dialog);
    }

    /// <summary>
    /// The resource list types hold hardware resource data. Modify and Modify Binary Data open them
    /// read-only.
    /// </summary>
    private static bool IsReadOnlyResourceType(uint type) =>
        type is (uint)RegistryValueType.ResourceList
            or (uint)RegistryValueType.FullResourceDescriptor
            or (uint)RegistryValueType.ResourceRequirementsList;

    /// <summary>
    /// Opens the hex editor for a value whose type has no typed editor, or for Modify Binary Data. The
    /// value is read on the thread pool and written back with its original type when the dialog accepts.
    /// </summary>
    private async Task EditValueHexAsync(DetailsRow activatedRow)
    {
        if (_context.DialogOpen || _context.WindowHwnd == 0)
        {
            Log.Warn(
                "Registry Editor ignored value editing because another modal is open or the window handle is not attached.");
            return;
        }

        IReadOnlyList<DetailsRow> rows = _context.Shell.ListContent.SelectedRows;
        if (rows.Count != 1 || !ReferenceEquals(rows[0], activatedRow))
        {
            return;
        }

        if (!_context.TryGetSelectedKeyPath(out RegistryPath key) ||
            !_context.TryGetRawValueName(activatedRow, out string rawName))
        {
            return;
        }

        RegistryValueInfo? value;
        try
        {
            value = await _context.RunRegistryAsync(() => _context.Store.Reader.ReadValue(key, rawName));
        }
        catch (RegistryOperationException ex)
        {
            await _context.ShowRegistryErrorAsync(ex, RegistryAction.ReadValue, ValueLabel(rawName));
            return;
        }

        if (value is null)
        {
            await ShowValueReadErrorAsync(rawName, forEdit: true);
            return;
        }

        if (IsReadOnlyResourceType(value.Type))
        {
            // Hardware resource data is shown read-only, so a typed edit cannot change device resources.
            await ShowReadOnlyViewerAsync(activatedRow.Name, key, value);
            _ = _context.Shell.ListContent.Focus(FocusState.Programmatic);
            return;
        }

        var dialog = new HexEditorDialog(Get("DialogHexEditTitle"));
        dialog.SetValue(
            activatedRow.Name,
            key.ToDisplayString(),
            value.Data,
            RegistryValueTypeExtensions.ToDisplayName(value.Type));

        bool accepted = await _context.ShowDialogAsync(dialog);
        _ = _context.Shell.ListContent.Focus(FocusState.Programmatic);
        if (!accepted)
        {
            return;
        }

        try
        {
            _context.Store.SetValue(key, rawName, (RegistryValueType)value.Type, dialog.Data);
        }
        catch (RegistryOperationException ex)
        {
            await _context.ShowRegistryErrorAsync(ex, RegistryAction.EditValue, ValueLabel(rawName));
            return;
        }

        await _context.RefreshValuesAsync(activatedRow.Name);
    }

    private async Task ModifyBinaryDataAsync()
    {
        if (_context.DialogOpen || _context.WindowHwnd == 0)
        {
            Log.Warn(
                "Registry Editor ignored Modify Binary Data because another modal is open or the window handle is not attached.");
            return;
        }

        IReadOnlyList<DetailsRow> rows = _context.Shell.ListContent.SelectedRows;
        if (rows.Count != 1)
        {
            await _context.ShowMessageAsync(Get("DialogHexNoSelection"));
            return;
        }

        // The menus enable this command for any single value. A typed value is intentionally editable
        // here and is written back with its type.
        await EditValueHexAsync(rows[0]);
    }

    /// <summary>
    /// The one dispatch for Modify, Enter, and double-click. REG_SZ and REG_EXPAND_SZ open Edit String,
    /// REG_DWORD opens Edit DWORD, REG_QWORD opens Edit QWORD, and REG_MULTI_SZ opens Edit Multi-String.
    /// Every other type opens Edit Binary, as classic regedit does. The activated row is given for Enter
    /// and double-click, and must be the only selected row. Modify passes null and uses the selection.
    /// </summary>
    private async Task ModifyValueAsync(DetailsRow? activated)
    {
        if (_context.DialogOpen || _context.WindowHwnd == 0)
        {
            Log.Warn(
                "Registry Editor ignored Modify because another modal is open or the window handle is not attached.");
            return;
        }

        IReadOnlyList<DetailsRow> rows = _context.Shell.ListContent.SelectedRows;
        if (activated is not null)
        {
            // A stale activation, for a row that is no longer the single selection, does nothing.
            if (rows.Count != 1 || !ReferenceEquals(rows[0], activated))
            {
                return;
            }
        }
        else if (rows.Count != 1)
        {
            await _context.ShowMessageAsync(Get("DialogNoSelection"));
            return;
        }

        DetailsRow row = rows[0];
        if (!_context.TryGetSelectedKeyPath(out RegistryPath key))
        {
            if (activated is null)
            {
                await _context.ShowMessageAsync(Get("DialogNoSelection"));
            }

            return;
        }

        if (_context.IsSyntheticDefault(row))
        {
            await EditSyntheticDefaultAsync(key);
            return;
        }

        if (!_context.TryGetRawValueName(row, out string rawName))
        {
            return;
        }

        RegistryValueInfo? value;
        try
        {
            value = await _context.RunRegistryAsync(() => _context.Store.Reader.ReadValue(key, rawName));
        }
        catch (RegistryOperationException ex)
        {
            await _context.ShowRegistryErrorAsync(ex, RegistryAction.ReadValue, ValueLabel(rawName));
            return;
        }

        if (value is null)
        {
            await ShowValueReadErrorAsync(rawName, forEdit: true);
            return;
        }

        bool saved;
        switch ((RegistryValueType)value.Type)
        {
            case RegistryValueType.String:
                saved = await EditStringValueAsync(key, rawName, value, expandable: false);
                break;
            case RegistryValueType.ExpandString:
                saved = await EditStringValueAsync(key, rawName, value, expandable: true);
                break;
            case RegistryValueType.Dword when !RegistryValueCodec.IsValidDword(value.Data):
            case RegistryValueType.Qword when !RegistryValueCodec.IsValidQword(value.Data):
                // A number with the wrong data length has no typed editor. Classic regedit opens the
                // binary editor, which keeps the bytes and the type.
                await EditValueHexAsync(row);
                return;
            case RegistryValueType.Dword:
                saved = await EditDwordValueAsync(key, rawName, value);
                break;
            case RegistryValueType.Qword:
                saved = await EditQwordValueAsync(key, rawName, value);
                break;
            case RegistryValueType.MultiString:
                saved = await EditMultiStringValueAsync(key, rawName, value);
                break;
            default:
                // Every other type has no typed editor. The hex editor keeps the bytes and the type.
                await EditValueHexAsync(row);
                return;
        }

        if (saved)
        {
            await _context.RefreshValuesAsync(row.Name);
        }
    }

    /// <summary>
    /// Edit String for the synthetic (Default) row, which has no value behind it. Accepting the dialog
    /// writes the default value, as classic regedit does.
    /// </summary>
    private async Task EditSyntheticDefaultAsync(RegistryPath key)
    {
        var dialog = new StringValueDialog(
            Get("DialogEditStringTitle"),
            multiline: false,
            string.Empty,
            RegistryValueDisplay.FormatName(string.Empty));

        if (!await _context.ShowDialogAsync(dialog))
        {
            return;
        }

        try
        {
            _context.Store.SetString(key, string.Empty, dialog.Value);
        }
        catch (RegistryOperationException ex)
        {
            await _context.ShowRegistryErrorAsync(ex, RegistryAction.EditValue, ValueLabel(string.Empty));
            return;
        }

        await _context.RefreshValuesAsync(MessageCatalog.DefaultValueLabel);
    }

    private async Task<bool> EditStringValueAsync(
        RegistryPath key,
        string rawName,
        RegistryValueInfo value,
        bool expandable)
    {
        var dialog = new StringValueDialog(
            Get(expandable ? "DialogEditExpandableTitle" : "DialogEditStringTitle"),
            multiline: expandable,
            RegistryValueCodec.DecodeString(value.Data),
            RegistryValueDisplay.FormatName(rawName));

        if (!await _context.ShowDialogAsync(dialog))
        {
            return false;
        }

        try
        {
            if (expandable)
            {
                _context.Store.SetExpandString(key, rawName, dialog.Value);
            }
            else
            {
                _context.Store.SetString(key, rawName, dialog.Value);
            }

            return true;
        }
        catch (RegistryOperationException ex)
        {
            await _context.ShowRegistryErrorAsync(ex, RegistryAction.EditValue, ValueLabel(rawName));
            return false;
        }
    }

    private async Task<bool> EditDwordValueAsync(
        RegistryPath key,
        string rawName,
        RegistryValueInfo value)
    {
        if (!RegistryValueCodec.IsValidDword(value.Data))
        {
            return false;
        }

        var dialog = new DwordValueDialog(
            Get("DialogEditDwordTitle"),
            RegistryValueCodec.DecodeDword(value.Data).ToString(CultureInfo.InvariantCulture),
            RegistryValueDisplay.FormatName(rawName));
        if (!await _context.ShowDialogAsync(dialog))
        {
            return false;
        }

        try
        {
            _context.Store.SetDword(key, rawName, dialog.Value);
            return true;
        }
        catch (RegistryOperationException ex)
        {
            await _context.ShowRegistryErrorAsync(ex, RegistryAction.EditValue, ValueLabel(rawName));
            return false;
        }
    }

    private async Task<bool> EditQwordValueAsync(
        RegistryPath key,
        string rawName,
        RegistryValueInfo value)
    {
        if (!RegistryValueCodec.IsValidQword(value.Data))
        {
            return false;
        }

        var dialog = new QwordValueDialog(
            Get("DialogEditQwordTitle"),
            RegistryValueCodec.DecodeQword(value.Data).ToString(CultureInfo.InvariantCulture),
            RegistryValueDisplay.FormatName(rawName));
        if (!await _context.ShowDialogAsync(dialog))
        {
            return false;
        }

        try
        {
            _context.Store.SetQword(key, rawName, dialog.Value);
            return true;
        }
        catch (RegistryOperationException ex)
        {
            await _context.ShowRegistryErrorAsync(ex, RegistryAction.EditValue, ValueLabel(rawName));
            return false;
        }
    }

    private async Task<bool> EditMultiStringValueAsync(
        RegistryPath key,
        string rawName,
        RegistryValueInfo value)
    {
        string initialText = string.Join(
            Environment.NewLine,
            RegistryValueCodec.DecodeMultiString(value.Data).Strings);
        var dialog = new MultiStringValueDialog(Get("DialogEditMultiStringTitle"), initialText, ValueLabel(rawName));
        if (!await _context.ShowDialogAsync(dialog))
        {
            return false;
        }

        try
        {
            _context.Store.SetMultiString(key, rawName, dialog.Value);
            return true;
        }
        catch (RegistryOperationException ex)
        {
            await _context.ShowRegistryErrorAsync(ex, RegistryAction.EditValue, ValueLabel(rawName));
            return false;
        }
    }

    /// <summary>Shows the classic read error for a value whose data cannot be read.</summary>
    private Task ShowValueReadErrorAsync(string rawName, bool forEdit)
    {
        Log.Warn($"Registry Editor could not read the data of value '{ValueLabel(rawName)}'.");
        return _context.ShowErrorAsync(_context.Messages.ForValueRead(ValueLabel(rawName), forEdit).ToErrorDialog());
    }

    /// <summary>The name a value message names: the display name, or the default value label.</summary>
    private static string ValueLabel(string rawName) =>
        rawName.Length == 0 ? MessageCatalog.DefaultValueLabel : RegistryValueDisplay.FormatName(rawName);

    /// <summary>
    /// Creates the value with the first unused classic name, its classic default data, and opens the
    /// same inline editor as F2 with the default name selected. Escape leaves the default name.
    /// </summary>
    private async Task NewValueAsync(NewValueKind kind)
    {
        if (!_context.TryGetSelectedKeyPath(out RegistryPath key))
        {
            await _context.ShowMessageAsync(Get("DialogNoSelection"));
            return;
        }

        string valueName = NextUniqueValueName(key);
        try
        {
            WriteDefaultValue(key, valueName, kind);
        }
        catch (RegistryOperationException ex)
        {
            await _context.ShowRegistryErrorAsync(ex, RegistryAction.CreateValue, ValueLabel(valueName));
            return;
        }

        await _context.RefreshValuesAsync(RegistryValueDisplay.FormatName(valueName));
        BeginListRenameSoon();
    }

    /// <summary>
    /// The classic default data of a new value: empty strings for the text types, zero-length binary,
    /// zero for the numbers, and the empty multi-string list (the double NUL terminator).
    /// </summary>
    private void WriteDefaultValue(RegistryPath key, string valueName, NewValueKind kind)
    {
        switch (kind)
        {
            case NewValueKind.String:
                _context.Store.SetString(key, valueName, string.Empty);
                break;
            case NewValueKind.ExpandableString:
                _context.Store.SetExpandString(key, valueName, string.Empty);
                break;
            case NewValueKind.Binary:
                _context.Store.SetBinary(key, valueName, ReadOnlySpan<byte>.Empty);
                break;
            case NewValueKind.Dword:
                _context.Store.SetDword(key, valueName, 0);
                break;
            case NewValueKind.Qword:
                _context.Store.SetQword(key, valueName, 0);
                break;
            case NewValueKind.MultiString:
                _context.Store.SetMultiString(key, valueName, Array.Empty<string>());
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    /// <summary>
    /// Starts the inline editor on the next dispatcher turn, after the row selection that the refresh
    /// queued. A menu flyout that closes later would otherwise take the rename box focus away.
    /// </summary>
    private void BeginListRenameSoon()
    {
        if (!_context.Shell.DispatcherQueue.TryEnqueue(
                DispatcherQueuePriority.Low,
                _context.Shell.ListContent.BeginRenameSelected))
        {
            _context.Shell.ListContent.BeginRenameSelected();
        }
    }

    private async Task DeleteValuesAsync()
    {
        if (!_context.TryGetSelectedKeyPath(out RegistryPath key))
        {
            await _context.ShowMessageAsync(Get("DialogNoSelection"));
            return;
        }

        IReadOnlyList<DetailsRow> rows = _context.Shell.ListContent.SelectedRows;
        var rawNames = new List<string>(rows.Count);
        foreach (DetailsRow row in rows)
        {
            if (_context.TryGetRawValueName(row, out string rawName))
            {
                rawNames.Add(rawName);
            }
        }

        if (rawNames.Count == 0)
        {
            await _context.ShowMessageAsync(Get("DialogNoSelection"));
            return;
        }

        // Several values use the plural text, one value the singular text. Both use the same caption.
        var dialog = new ConfirmDialog(
            Get("MsgCaptionConfirmValueDelete"),
            Get(rawNames.Count > 1 ? "MsgDeleteValuesConfirm" : "MsgDeleteValueConfirm"),
            Get("DialogYes"),
            Get("DialogNo"));

        if (!await _context.ShowDialogAsync(dialog))
        {
            return;
        }

        // Every value is attempted. A failure does not stop the rest, and the first failure is shown once,
        // after the work, with the classic text. The list is refreshed in every case.
        RegistryOperationException? first = null;
        string? firstName = null;
        await _context.RunRegistryWriteAsync(() =>
        {
            foreach (string rawName in rawNames)
            {
                try
                {
                    _context.Store.DeleteValue(key, rawName);
                }
                catch (RegistryOperationException ex)
                {
                    Log.Warn($"Registry Editor could not delete value '{ValueLabel(rawName)}': Win32 {ex.Win32Error} ({ex.Message}).");
                    first ??= ex;
                    firstName ??= ValueLabel(rawName);
                }
            }
        });

        if (first is not null)
        {
            await _context.ShowRegistryErrorAsync(first, RegistryAction.DeleteValues, firstName);
        }

        await _context.RefreshValuesAsync(selectDisplayName: null);
    }

    private async Task RenameValueCoreAsync(RegistryPath key, string oldName, string newName)
    {
        if (string.Equals(oldName, newName, StringComparison.Ordinal))
        {
            return;
        }

        string? error = ValidateRenameValue(key, oldName, newName);
        if (error is not null)
        {
            await _context.ShowMessageAsync(Get("MsgCaptionRenameValueError"), error);
            return;
        }

        try
        {
            _context.Store.RenameValue(key, oldName, newName);
        }
        catch (RegistryOperationException ex)
        {
            await _context.ShowRegistryErrorAsync(ex, RegistryAction.RenameValue, ValueLabel(oldName));
            return;
        }

        await _context.RefreshValuesAsync(newName);
    }

    /// <summary>
    /// The text for a rename that cannot proceed. The classic rename texts name the old value, so the
    /// subject is the current name. Null when the new name is acceptable.
    /// </summary>
    private string? ValidateRenameValue(RegistryPath key, string oldName, string newName)
    {
        string subject = ValueLabel(oldName);
        if (newName.Length == 0)
        {
            return string.Format(CultureInfo.CurrentCulture, Get("MsgRenameValueEmpty"), subject);
        }

        string? invalid = ValidateValueName(newName);
        if (invalid is not null)
        {
            return invalid;
        }

        if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            return _context.Store.Reader.ValueExists(key, newName)
                ? string.Format(CultureInfo.CurrentCulture, Get("MsgRenameValueExists"), subject)
                : null;
        }
        catch (RegistryOperationException ex)
        {
            return ValidationFailure(ex, RegistryAction.RenameValue, subject);
        }
    }

    /// <summary>Checks the value name limits. Empty names are checked by the callers.</summary>
    private string? ValidateValueName(string name)
    {
        if (name.Length > RegeditContext.MaxValueNameLength || name.Contains('\0'))
        {
            return Get("DialogInvalidValueName");
        }

        return null;
    }

    /// <summary>Logs a failed check and returns the classic text for it, for a prompt to show.</summary>
    private string ValidationFailure(RegistryOperationException ex, RegistryAction action, string subject)
    {
        Log.Warn($"Registry Editor could not check a value name: {ex.Operation} for {ex.Path.ToDisplayString()}, Win32 {ex.Win32Error}: {ex.Message}");
        return _context.Messages.ForRegistryError(action, ex, subject).Text;
    }

    private string NextUniqueValueName(RegistryPath key)
    {
        for (var index = 1; index < 10000; index += 1)
        {
            string candidate = MessageCatalog.NewValueNamePrefix + index.ToString(CultureInfo.InvariantCulture);
            if (!_context.Store.Reader.ValueExists(key, candidate))
            {
                return candidate;
            }
        }

        return MessageCatalog.NewValueNamePrefix + Environment.TickCount64.ToString(CultureInfo.InvariantCulture);
    }
}
