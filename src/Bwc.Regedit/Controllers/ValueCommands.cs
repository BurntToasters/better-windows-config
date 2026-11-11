using System.Globalization;
using Bwc.Core;
using Bwc.Regedit.Registry;
using Bwc.Registry;
using Bwc.UI.Dialogs;
using Bwc.UI.List;
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

    public void RenameValueInteractive(DetailsRow row) => _context.StartCommand(() => RenameValueInteractiveAsync(row));

    /// <summary>Opens the hex editor for a double-clicked value.</summary>
    public void ActivateValue(DetailsRow row) => _context.StartCommand(() => EditValueHexAsync(row));

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

    public void ModifySelectedValue() => _context.StartCommand(ModifySelectedValueAsync);

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
            await _context.ShowRegistryErrorAsync(ex);
            return;
        }

        if (value is null)
        {
            string valueLabel = rawName.Length == 0 ? MessageCatalog.DefaultValueLabel : rawName;
            await _context.ShowErrorAsync(ErrorDialog.ForOperation(
                Get("DialogErrorTitle"),
                MessageCatalog.QueryValueOperation,
                key.ToDisplayString(),
                2,
                MessageCatalog.ValueLinePrefix + valueLabel));
            return;
        }

        var dialog = new HexViewerDialog(Get("DialogHexTitle"), Get("DialogHexClose"));
        dialog.SetValue(
            rows[0].Name,
            key.ToDisplayString(),
            value.Data,
            RegistryValueTypeExtensions.ToDisplayName(value.Type));

        _ = await _context.ShowDialogAsync(dialog);
        _ = _context.Shell.ListContent.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// Opens the hex editor for a double-clicked value when the raw type has no dedicated typed
    /// editor. The value is read on the thread pool and written back with its original type when
    /// the dialog accepts.
    /// </summary>
    private Task EditValueHexAsync(DetailsRow activatedRow) =>
        EditValueHexAsync(activatedRow, requireHexEditableType: true);

    private async Task EditValueHexAsync(DetailsRow activatedRow, bool requireHexEditableType)
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
            await _context.ShowRegistryErrorAsync(ex);
            return;
        }

        if (value is null)
        {
            await ShowQueryValueErrorAsync(key, rawName);
            return;
        }

        if (requireHexEditableType && !IsHexEditableType(value.Type))
        {
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
            await _context.ShowRegistryErrorAsync(ex);
            return;
        }

        await _context.RefreshValuesAsync(activatedRow.Name);
    }

    private static bool IsHexEditableType(uint type) =>
        type != (uint)RegistryValueType.String &&
        type != (uint)RegistryValueType.ExpandString &&
        type != (uint)RegistryValueType.Dword &&
        type != (uint)RegistryValueType.Qword &&
        type != (uint)RegistryValueType.MultiString;

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

        // The context menu enables this command for any single value. Unlike the double-click
        // path, a typed value is intentionally editable here and is written back with its type.
        await EditValueHexAsync(rows[0], requireHexEditableType: false);
    }

    private async Task ModifySelectedValueAsync()
    {
        if (_context.DialogOpen || _context.WindowHwnd == 0)
        {
            Log.Warn(
                "Registry Editor ignored Modify because another modal is open or the window handle is not attached.");
            return;
        }

        IReadOnlyList<DetailsRow> rows = _context.Shell.ListContent.SelectedRows;
        if (rows.Count != 1 ||
            !_context.TryGetSelectedKeyPath(out RegistryPath key) ||
            !_context.TryGetRawValueName(rows[0], out string rawName))
        {
            await _context.ShowMessageAsync(Get("DialogNoSelection"));
            return;
        }

        RegistryValueInfo? value;
        try
        {
            value = await _context.RunRegistryAsync(() => _context.Store.Reader.ReadValue(key, rawName));
        }
        catch (RegistryOperationException ex)
        {
            await _context.ShowRegistryErrorAsync(ex);
            return;
        }

        if (value is null)
        {
            await ShowQueryValueErrorAsync(key, rawName);
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
                // The menu state keeps Modify disabled for these types. If a stale row still
                // reaches this path, the editable hex dialog is the lossless fallback.
                await EditValueHexAsync(rows[0], requireHexEditableType: false);
                return;
        }

        if (saved)
        {
            await _context.RefreshValuesAsync(rows[0].Name);
        }
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
            await _context.ShowRegistryErrorAsync(ex);
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
            await _context.ShowRegistryErrorAsync(ex);
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
            await _context.ShowRegistryErrorAsync(ex);
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
        var dialog = new MultiStringValueDialog(Get("DialogEditMultiStringTitle"), initialText);
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
            await _context.ShowRegistryErrorAsync(ex);
            return false;
        }
    }

    private Task ShowQueryValueErrorAsync(RegistryPath key, string rawName)
    {
        string valueLabel = rawName.Length == 0 ? MessageCatalog.DefaultValueLabel : rawName;
        return _context.ShowErrorAsync(ErrorDialog.ForOperation(
            Get("DialogErrorTitle"),
            MessageCatalog.QueryValueOperation,
            key.ToDisplayString(),
            2,
            MessageCatalog.ValueLinePrefix + valueLabel));
    }

    private async Task NewValueAsync(NewValueKind kind)
    {
        if (!_context.TryGetSelectedKeyPath(out RegistryPath key))
        {
            await _context.ShowMessageAsync(Get("DialogNoSelection"));
            return;
        }

        string suggested = NextUniqueValueName(key);
        var nameDialog = new TextPromptDialog(
            Get("DialogNewValueTitle"),
            Get("DialogValueNameLabel"),
            suggested,
            value => ValidateNewValueName(key, value, oldName: null));

        if (!await _context.ShowDialogAsync(nameDialog))
        {
            return;
        }

        string valueName = nameDialog.Value;
        bool saved = kind switch
        {
            NewValueKind.String => await WriteStringValueAsync(key, valueName, expandable: false),
            NewValueKind.ExpandableString => await WriteStringValueAsync(key, valueName, expandable: true),
            NewValueKind.Binary => await WriteBinaryValueAsync(key, valueName),
            NewValueKind.Dword => await WriteDwordValueAsync(key, valueName),
            NewValueKind.Qword => await WriteQwordValueAsync(key, valueName),
            NewValueKind.MultiString => await WriteMultiStringValueAsync(key, valueName),
            _ => false,
        };

        if (saved)
        {
            await _context.RefreshValuesAsync(RegistryValueDisplay.FormatName(valueName));
        }
    }

    private async Task<bool> WriteStringValueAsync(RegistryPath key, string valueName, bool expandable)
    {
        var dialog = new StringValueDialog(
            Get(expandable ? "DialogValueExpandableTitle" : "DialogValueStringTitle"),
            multiline: expandable,
            valueName: RegistryValueDisplay.FormatName(valueName));

        if (!await _context.ShowDialogAsync(dialog))
        {
            return false;
        }

        try
        {
            if (expandable)
            {
                _context.Store.SetExpandString(key, valueName, dialog.Value);
            }
            else
            {
                _context.Store.SetString(key, valueName, dialog.Value);
            }

            return true;
        }
        catch (RegistryOperationException ex)
        {
            await _context.ShowRegistryErrorAsync(ex);
            return false;
        }
    }

    private async Task<bool> WriteDwordValueAsync(RegistryPath key, string valueName)
    {
        var dialog = new DwordValueDialog(
            Get("DialogValueDwordTitle"),
            valueName: RegistryValueDisplay.FormatName(valueName));
        if (!await _context.ShowDialogAsync(dialog))
        {
            return false;
        }

        try
        {
            _context.Store.SetDword(key, valueName, dialog.Value);
            return true;
        }
        catch (RegistryOperationException ex)
        {
            await _context.ShowRegistryErrorAsync(ex);
            return false;
        }
    }

    private async Task<bool> WriteQwordValueAsync(RegistryPath key, string valueName)
    {
        var dialog = new QwordValueDialog(
            Get("DialogValueQwordTitle"),
            valueName: RegistryValueDisplay.FormatName(valueName));
        if (!await _context.ShowDialogAsync(dialog))
        {
            return false;
        }

        try
        {
            _context.Store.SetQword(key, valueName, dialog.Value);
            return true;
        }
        catch (RegistryOperationException ex)
        {
            await _context.ShowRegistryErrorAsync(ex);
            return false;
        }
    }

    private async Task<bool> WriteBinaryValueAsync(RegistryPath key, string valueName)
    {
        var dialog = new BinaryValueDialog(Get("DialogValueBinaryTitle"));
        if (!await _context.ShowDialogAsync(dialog))
        {
            return false;
        }

        try
        {
            _context.Store.SetBinary(key, valueName, dialog.Value);
            return true;
        }
        catch (RegistryOperationException ex)
        {
            await _context.ShowRegistryErrorAsync(ex);
            return false;
        }
    }

    private async Task<bool> WriteMultiStringValueAsync(RegistryPath key, string valueName)
    {
        var dialog = new MultiStringValueDialog(Get("DialogValueMultiStringTitle"));
        if (!await _context.ShowDialogAsync(dialog))
        {
            return false;
        }

        try
        {
            _context.Store.SetMultiString(key, valueName, dialog.Value);
            return true;
        }
        catch (RegistryOperationException ex)
        {
            await _context.ShowRegistryErrorAsync(ex);
            return false;
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

        var dialog = new ConfirmDialog(
            Get("DialogDeleteValueTitle"),
            Get("DialogDeleteValueMessage"),
            Get("DialogYes"),
            Get("DialogNo"));

        if (!await _context.ShowDialogAsync(dialog))
        {
            return;
        }

        try
        {
            foreach (string rawName in rawNames)
            {
                _context.Store.DeleteValue(key, rawName);
            }
        }
        catch (RegistryOperationException ex)
        {
            await _context.ShowRegistryErrorAsync(ex);
            return;
        }

        await _context.RefreshValuesAsync(selectDisplayName: null);
    }

    private async Task RenameValueInteractiveAsync(DetailsRow row)
    {
        if (!_context.TryGetSelectedKeyPath(out RegistryPath key))
        {
            await _context.ShowMessageAsync(Get("DialogNoSelection"));
            return;
        }

        if (!_context.TryGetRawValueName(row, out string oldName) || oldName.Length == 0)
        {
            await _context.ShowMessageAsync(Get("DialogDefaultValueLocked"));
            return;
        }

        var dialog = new TextPromptDialog(
            Get("DialogRenameValueTitle"),
            Get("DialogValueNameLabel"),
            oldName,
            value => ValidateRenameValue(key, oldName, value));

        if (!await _context.ShowDialogAsync(dialog))
        {
            return;
        }

        await RenameValueCoreAsync(key, oldName, dialog.Value);
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
            await _context.ShowMessageAsync(error);
            return;
        }

        try
        {
            _context.Store.RenameValue(key, oldName, newName);
        }
        catch (RegistryOperationException ex)
        {
            await _context.ShowRegistryErrorAsync(ex);
            return;
        }

        await _context.RefreshValuesAsync(newName);
    }

    private string? ValidateNewValueName(RegistryPath key, string name, string? oldName)
    {
        string? invalid = ValidateValueName(name, allowEmpty: true);
        if (invalid is not null)
        {
            return invalid;
        }

        if (oldName is not null &&
            string.Equals(oldName, name, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            return _context.Store.Reader.ValueExists(key, name) ? Get("DialogNameExists") : null;
        }
        catch (RegistryOperationException ex)
        {
            return ex.Message;
        }
    }

    private string? ValidateRenameValue(RegistryPath key, string oldName, string newName)
    {
        string? invalid = ValidateValueName(newName, allowEmpty: false);
        if (invalid is not null)
        {
            return invalid;
        }

        if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return ValidateNewValueName(key, newName, oldName);
    }

    private string? ValidateValueName(string name, bool allowEmpty)
    {
        if (!allowEmpty && name.Length == 0)
        {
            return Get("DialogInvalidValueName");
        }

        if (name.Length > RegeditContext.MaxValueNameLength || name.Contains('\0'))
        {
            return Get("DialogInvalidValueName");
        }

        return null;
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
