using System.Globalization;
using System.Text;
using Bwc.Core;
using Bwc.Regedit.Commands;
using Bwc.Regedit.Registry;
using Bwc.Registry;
using Bwc.UI.Dialogs;
using Bwc.UI.List;
using Bwc.UI.Shell;
using Bwc.UI.Tree;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.ApplicationModel.Resources;
using Microsoft.UI.Input;
using Windows.System;
using Windows.UI.Core;

namespace Bwc.Regedit;

/// <summary>The Registry Editor content: classic shell plus the full RT_MENU 103 command table.</summary>
public sealed partial class RegeditRootView : UserControl, IRegeditEditCommands
{
    private const int MaxValueNameLength = 16383;

    private readonly RegistryStore _store;
    private readonly RegistryValueSource _valueSource;
    private readonly RegeditSettings _settings = new();
    private readonly ResourceLoader _loader;
    private readonly RegistryFinder _finder;
    private readonly IReadOnlyList<RegistryPath>? _findRoots;
    private RegistryTreeSource _treeSource;
    private CancellationTokenSource? _valueLoad;
    private CancellationTokenSource? _silentFind;
    private int _valueGeneration;
    private int _navigationGeneration;
    private bool _applyingNavigation;
    private bool _settingsApplied;
    private bool _dialogOpen;
    private bool _fileDialogOpen;
    private FindDialog? _findDialog;
    private RegistryFindQuery? _lastFindQuery;
    private RegistryFindHit? _lastFindHit;
    private bool _findExhausted;
    private nint _windowHwnd;

    public RegeditRootView()
    {
        _store = CreateStore();
        _treeSource = new RegistryTreeSource(_store.Reader);
        _valueSource = new RegistryValueSource(_store.Reader);
        _finder = new RegistryFinder(_store.Reader);
        _findRoots = ResolveFindRoots();

        InitializeComponent();

        _loader = new ResourceLoader(ResourceLoader.GetDefaultResourceFilePath(), RegeditStrings.ResourceMap);
        var commands = RegeditMenu.Create(
            _loader,
            Shell,
            this,
            () => ExitRequested?.Invoke(this, EventArgs.Empty),
            Import,
            Export,
            displayBinaryData: DisplayBinaryData);
        RegeditMenu.Populate(Shell.MenuBar, commands);

        Shell.TreeContent.SelectionChanged += OnTreeSelectionChanged;
        Shell.TreeContent.RenameRequested += OnTreeRenameRequested;
        Shell.ListContent.SelectionChanged += OnListSelectionChanged;
        Shell.ListContent.RenameRequested += OnListRenameRequested;
        Shell.AddressBarSubmitted += OnAddressBarSubmitted;
        Shell.TreeContent.SetRoots(_treeSource.Roots);
        Shell.TreeContent.SelectNode(_treeSource.Computer);
        UpdateCommandStates();

        // handledEventsToo keeps Ctrl+F and F3 working when the tree or list already consumed a
        // different key. Dialogs are separate windows, so their keys never reach this handler.
        AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnRootKeyDown), handledEventsToo: true);
    }

    /// <summary>Raised when File > Exit is chosen. The window owns the actual close.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>The in-memory view state. Updated by selection changes, not written to disk.</summary>
    public RegeditSettings Settings => _settings;

    /// <summary>
    /// Applies persisted view state once. Call after <see cref="InitializeComponent"/> so the shell
    /// controls exist. Restoring LastKey walks the registry asynchronously and leaves the current
    /// Computer selection in place if the key no longer resolves.
    /// </summary>
    public void ApplySettings(RegeditSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_settingsApplied)
        {
            return;
        }

        _settingsApplied = true;
        _settings.LastKey = string.IsNullOrWhiteSpace(settings.LastKey) ? "Computer" : settings.LastKey;
        _settings.AddressBarVisible = settings.AddressBarVisible;
        _settings.ListColumnWidths = settings.ListColumnWidths is null
            ? new Dictionary<string, double>(StringComparer.Ordinal)
            : new Dictionary<string, double>(settings.ListColumnWidths, StringComparer.Ordinal);
        _settings.FindText = settings.FindText ?? string.Empty;
        _settings.FindFlags = settings.FindFlags;
        _settings.FindQueryText = settings.FindQueryText ?? string.Empty;
        _settings.FindQueryFlags = settings.FindQueryFlags;
        _settings.FindLastHitKey = settings.FindLastHitKey;
        _settings.FindLastHitValue = settings.FindLastHitValue;
        _settings.FindLastHitKind = settings.FindLastHitKind;
        _settings.FindLastHitText = settings.FindLastHitText;
        RestoreFindState();

        if (Shell.IsAddressBarVisible != _settings.AddressBarVisible)
        {
            Shell.ToggleAddressBar();
        }

        ApplyColumnWidths(_settings.ListColumnWidths);

        if (!string.Equals(_settings.LastKey, "Computer", StringComparison.OrdinalIgnoreCase))
        {
            _ = RestoreLastKeyAsync(_settings.LastKey);
        }
    }

    /// <summary>Copies the current key, address bar state, and column widths for saving.</summary>
    public RegeditSettings CaptureSettings()
    {
        var snapshot = new RegeditSettings
        {
            LastKey = _settings.LastKey,
            AddressBarVisible = Shell.IsAddressBarVisible,
            ListColumnWidths = new Dictionary<string, double>(StringComparer.Ordinal),
            FindText = _settings.FindText ?? string.Empty,
            FindFlags = _settings.FindFlags,
            FindQueryText = _settings.FindQueryText ?? string.Empty,
            FindQueryFlags = _settings.FindQueryFlags,
            FindLastHitKey = _settings.FindLastHitKey,
            FindLastHitValue = _settings.FindLastHitValue,
            FindLastHitKind = _settings.FindLastHitKind,
            FindLastHitText = _settings.FindLastHitText,
        };

        foreach (DetailsColumn column in Shell.ListContent.Columns)
        {
            snapshot.ListColumnWidths[column.Key] = column.Width;
        }

        return snapshot;
    }

    /// <summary>Creates the menu keyboard router after the window handle is known.</summary>
    public void AttachMenuKeyboard(nint hwnd)
    {
        Shell.AttachMenuKeyboard(hwnd);
    }

    /// <summary>Disposes the menu keyboard router.</summary>
    public void DetachMenuKeyboard()
    {
        Shell.DetachMenuKeyboard();
    }

    /// <summary>Gives modal dialogs an owner. Call after the window exists.</summary>
    public void AttachWindow(nint hwnd)
    {
        _windowHwnd = hwnd;
    }

    public void NewKey() => StartCommand(NewKeyAsync);

    public void NewStringValue() => StartCommand(() => NewValueAsync(NewValueKind.String));

    public void NewExpandableStringValue() => StartCommand(() => NewValueAsync(NewValueKind.ExpandableString));

    public void NewBinaryValue() => StartCommand(() => NewValueAsync(NewValueKind.Binary));

    public void NewDwordValue() => StartCommand(() => NewValueAsync(NewValueKind.Dword));

    public void NewQwordValue() => StartCommand(() => NewValueAsync(NewValueKind.Qword));

    public void NewMultiStringValue() => StartCommand(() => NewValueAsync(NewValueKind.MultiString));

    public void DeleteSelection()
    {
        if (Shell.ListContent.SelectedRows.Count > 0)
        {
            StartCommand(DeleteValuesAsync);
            return;
        }

        StartCommand(DeleteKeyAsync);
    }

    public void RenameSelection()
    {
        IReadOnlyList<DetailsRow> rows = Shell.ListContent.SelectedRows;
        if (rows.Count > 1)
        {
            StartCommand(() => ShowMessageAsync(Get("DialogNoSelection")));
            return;
        }

        if (rows.Count == 1)
        {
            StartCommand(() => RenameValueInteractiveAsync(rows[0]));
            return;
        }

        StartCommand(RenameKeyInteractiveAsync);
    }

    public void Import() => StartCommand(ImportAsync);

    public void Export() => StartCommand(ExportAsync);

    /// <summary>Opens the read-only binary viewer for the single selected value.</summary>
    public void DisplayBinaryData() => StartCommand(ShowBinaryDataAsync);

    private async Task ShowBinaryDataAsync()
    {
        if (_dialogOpen || _windowHwnd == 0)
        {
            Log.Warn(
                "Registry Editor ignored Display Binary Data because another modal is open or the window handle is not attached.");
            return;
        }

        if (!TryGetSelectedKeyPath(out RegistryPath key))
        {
            await ShowMessageAsync(Get("DialogNoSelection"));
            return;
        }

        IReadOnlyList<DetailsRow> rows = Shell.ListContent.SelectedRows;
        if (rows.Count != 1 || !TryGetRawValueName(rows[0], out string rawName))
        {
            await ShowMessageAsync(Get("DialogHexNoSelection"));
            return;
        }

        RegistryValueInfo? value;
        try
        {
            value = await Task.Run(() => _store.Reader.ReadValue(key, rawName));
        }
        catch (RegistryOperationException ex)
        {
            await ShowRegistryErrorAsync(ex);
            return;
        }

        if (value is null)
        {
            string valueLabel = rawName.Length == 0 ? "(Default)" : rawName;
            await ShowErrorAsync(ErrorDialog.ForOperation(
                Get("DialogErrorTitle"),
                "QueryValue",
                key.ToDisplayString(),
                2,
                "Value: " + valueLabel));
            return;
        }

        var dialog = new HexViewerDialog(Get("DialogHexTitle"), Get("DialogHexClose"));
        dialog.SetValue(
            rows[0].Name,
            key.ToDisplayString(),
            value.Data,
            RegistryValueTypeExtensions.ToDisplayName(value.Type));

        _ = await ShowDialogAsync(dialog);
        _ = Shell.ListContent.Focus(FocusState.Programmatic);
    }

    private async Task ImportAsync()
    {
        string? filePath = ShowFileDialog(save: false, defaultFileName: null);
        if (string.IsNullOrEmpty(filePath))
        {
            return;
        }

        string? selectedPath = Shell.TreeContent.SelectedNode?.RegistryPath;

        try
        {
            await Task.Run(() =>
            {
                RegistryStore store = RegeditWritePolicy.CreateStore();
                RegFileImporter.ApplyFile(store, filePath, CancellationToken.None);
            });
        }
        catch (Exception ex) when (IsFileOperationFailure(ex))
        {
            LogFileOperationFailure("import", filePath, ex);
            await ShowFileOperationErrorAsync(filePath, ex);
            return;
        }

        await RefreshTreeAsync(selectedPath);
        await RefreshValuesAsync(selectDisplayName: null);
    }

    private async Task ExportAsync()
    {
        if (!TryGetSelectedKeyPath(out RegistryPath selectedPath))
        {
            await ShowMessageAsync(Get("DialogExportNoSelection"));
            return;
        }

        string defaultFileName = BuildDefaultExportFileName(selectedPath);
        string? filePath = ShowFileDialog(save: true, defaultFileName);
        if (string.IsNullOrEmpty(filePath))
        {
            return;
        }

        try
        {
            await Task.Run(() =>
            {
                RegFileDocument document = RegFileExporter.Export(
                    _store.Reader,
                    selectedPath,
                    RegFileFormat.V5,
                    CancellationToken.None);

                using var buffer = new MemoryStream();
                RegFileWriter.WriteV5(document, buffer);
                File.WriteAllBytes(filePath, buffer.ToArray());
            });
        }
        catch (Exception ex) when (IsFileOperationFailure(ex))
        {
            LogFileOperationFailure("export", filePath, ex);
            await ShowFileOperationErrorAsync(filePath, ex);
        }
    }

    private string? ShowFileDialog(bool save, string? defaultFileName)
    {
        if (_dialogOpen || _fileDialogOpen)
        {
            Log.Warn("Registry Editor ignored a file dialog request because another modal is already open.");
            return null;
        }

        if (_windowHwnd == 0)
        {
            Log.Warn("Registry Editor ignored a file dialog request because the window handle is not attached.");
            return null;
        }

        _fileDialogOpen = true;
        try
        {
            return save
                ? CommonFileDialogs.ShowSave(_windowHwnd, defaultFileName)
                : CommonFileDialogs.ShowOpen(_windowHwnd, defaultFileName);
        }
        catch (Exception ex)
        {
            Log.Warn("Registry Editor could not show a file dialog: " + ex.Message);
            _ = ShowErrorAsync(ErrorDialog.ForMessage(Get("DialogErrorTitle"), ex.Message));
            return null;
        }
        finally
        {
            _fileDialogOpen = false;
        }
    }

    private static string BuildDefaultExportFileName(RegistryPath path)
    {
        string source = path.Components.Count == 0
            ? path.Root.DisplayName()
            : path.Components[^1];

        char[] invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(source.Length);
        foreach (char character in source)
        {
            builder.Append(Array.IndexOf(invalid, character) >= 0 ? '_' : character);
        }

        string name = builder.ToString().Trim().TrimEnd('.');
        return (name.Length == 0 ? "export" : name) + ".reg";
    }

    private static bool IsFileOperationFailure(Exception exception) =>
        exception is RegFileParseException
            or RegistryOperationException
            or IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException;

    private static void LogFileOperationFailure(string operation, string path, Exception exception) =>
        Log.Warn(
            $"Registry Editor {operation} of '{path}' failed: {exception.GetType().Name}: {exception.Message}");

    private async Task ShowFileOperationErrorAsync(string filePath, Exception exception)
    {
        if (exception is RegistryOperationException operation)
        {
            await ShowErrorAsync(ErrorDialog.ForOperation(
                Get("DialogErrorTitle"),
                operation.Operation,
                operation.Path.ToDisplayString(),
                operation.Win32Error,
                "File: " + filePath));
            return;
        }

        await ShowErrorAsync(ErrorDialog.ForMessage(
            Get("DialogErrorTitle"),
            "File: " + filePath + Environment.NewLine + Environment.NewLine + exception.Message));
    }

    public void Find() => StartCommand(ShowFindDialogAsync);

    public void FindNext()
    {
        if (_lastFindQuery is null)
        {
            Find();
            return;
        }

        if (_findExhausted)
        {
            StartCommand(() => ShowMessageAsync(Get("DialogFindFinished")));
            return;
        }

        _silentFind?.Cancel();
        _silentFind?.Dispose();
        var tokenSource = new CancellationTokenSource();
        _silentFind = tokenSource;
        RegistryFindQuery query = _lastFindQuery;
        CancellationToken token = tokenSource.Token;
        StartCommand(async () =>
        {
            try
            {
                await RunFindAsync(null, ToOptions(query), token);
            }
            finally
            {
                if (ReferenceEquals(_silentFind, tokenSource))
                {
                    _silentFind = null;
                }

                tokenSource.Dispose();
            }
        });
    }

    private enum NewValueKind
    {
        String,
        ExpandableString,
        Binary,
        Dword,
        Qword,
        MultiString,
    }

    private static RegistryStore CreateStore() => RegeditWritePolicy.CreateStore();

    private void StartCommand(Func<Task> command)
    {
        _ = RunCommandAsync(command);
    }

    private async Task RunCommandAsync(Func<Task> command)
    {
        try
        {
            await command();
        }
        catch (RegistryOperationException ex)
        {
            await ShowRegistryErrorAsync(ex);
        }
        catch (ArgumentException ex)
        {
            await ShowErrorAsync(ErrorDialog.ForMessage(Get("DialogErrorTitle"), ex.Message));
        }
        catch (Exception ex)
        {
            Log.Exception("Registry Editor command failed.", ex);
            await ShowErrorAsync(ErrorDialog.ForMessage(Get("DialogErrorTitle"), ex.Message));
        }
    }

    private async Task NewKeyAsync()
    {
        if (!TryGetSelectedKeyPath(out RegistryPath parent))
        {
            await ShowMessageAsync(Get("DialogNoSelection"));
            return;
        }

        string suggested = NextUniqueKeyName(parent);
        var dialog = new TextPromptDialog(
            Get("DialogNewKeyTitle"),
            Get("DialogKeyNameLabel"),
            suggested,
            value => ValidateNewKeyName(parent, value));

        if (!await ShowDialogAsync(dialog))
        {
            return;
        }

        RegistryPath created = parent.Append(dialog.Value);
        try
        {
            _store.CreateKey(created);
        }
        catch (RegistryOperationException ex)
        {
            await ShowRegistryErrorAsync(ex);
            return;
        }

        await RefreshTreeAsync(created.ToDisplayString());
    }

    private async Task NewValueAsync(NewValueKind kind)
    {
        if (!TryGetSelectedKeyPath(out RegistryPath key))
        {
            await ShowMessageAsync(Get("DialogNoSelection"));
            return;
        }

        string suggested = NextUniqueValueName(key);
        var nameDialog = new TextPromptDialog(
            Get("DialogNewValueTitle"),
            Get("DialogValueNameLabel"),
            suggested,
            value => ValidateNewValueName(key, value, oldName: null));

        if (!await ShowDialogAsync(nameDialog))
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
            await RefreshValuesAsync(RegistryValueDisplay.FormatName(valueName));
        }
    }

    private async Task<bool> WriteStringValueAsync(RegistryPath key, string valueName, bool expandable)
    {
        var dialog = new StringValueDialog(
            Get(expandable ? "DialogValueExpandableTitle" : "DialogValueStringTitle"),
            multiline: expandable);

        if (!await ShowDialogAsync(dialog))
        {
            return false;
        }

        try
        {
            if (expandable)
            {
                _store.SetExpandString(key, valueName, dialog.Value);
            }
            else
            {
                _store.SetString(key, valueName, dialog.Value);
            }

            return true;
        }
        catch (RegistryOperationException ex)
        {
            await ShowRegistryErrorAsync(ex);
            return false;
        }
    }

    private async Task<bool> WriteDwordValueAsync(RegistryPath key, string valueName)
    {
        var dialog = new DwordValueDialog(Get("DialogValueDwordTitle"));
        if (!await ShowDialogAsync(dialog))
        {
            return false;
        }

        try
        {
            _store.SetDword(key, valueName, dialog.Value);
            return true;
        }
        catch (RegistryOperationException ex)
        {
            await ShowRegistryErrorAsync(ex);
            return false;
        }
    }

    private async Task<bool> WriteQwordValueAsync(RegistryPath key, string valueName)
    {
        var dialog = new QwordValueDialog(Get("DialogValueQwordTitle"));
        if (!await ShowDialogAsync(dialog))
        {
            return false;
        }

        try
        {
            _store.SetQword(key, valueName, dialog.Value);
            return true;
        }
        catch (RegistryOperationException ex)
        {
            await ShowRegistryErrorAsync(ex);
            return false;
        }
    }

    private async Task<bool> WriteBinaryValueAsync(RegistryPath key, string valueName)
    {
        var dialog = new BinaryValueDialog(Get("DialogValueBinaryTitle"));
        if (!await ShowDialogAsync(dialog))
        {
            return false;
        }

        try
        {
            _store.SetBinary(key, valueName, dialog.Value);
            return true;
        }
        catch (RegistryOperationException ex)
        {
            await ShowRegistryErrorAsync(ex);
            return false;
        }
    }

    private async Task<bool> WriteMultiStringValueAsync(RegistryPath key, string valueName)
    {
        var dialog = new MultiStringValueDialog(Get("DialogValueMultiStringTitle"));
        if (!await ShowDialogAsync(dialog))
        {
            return false;
        }

        try
        {
            _store.SetMultiString(key, valueName, dialog.Value);
            return true;
        }
        catch (RegistryOperationException ex)
        {
            await ShowRegistryErrorAsync(ex);
            return false;
        }
    }

    private async Task DeleteValuesAsync()
    {
        if (!TryGetSelectedKeyPath(out RegistryPath key))
        {
            await ShowMessageAsync(Get("DialogNoSelection"));
            return;
        }

        IReadOnlyList<DetailsRow> rows = Shell.ListContent.SelectedRows;
        var rawNames = new List<string>(rows.Count);
        foreach (DetailsRow row in rows)
        {
            if (TryGetRawValueName(row, out string rawName))
            {
                rawNames.Add(rawName);
            }
        }

        if (rawNames.Count == 0)
        {
            await ShowMessageAsync(Get("DialogNoSelection"));
            return;
        }

        var dialog = new ConfirmDialog(
            Get("DialogDeleteValueTitle"),
            Get("DialogDeleteValueMessage"),
            Get("DialogYes"),
            Get("DialogNo"));

        if (!await ShowDialogAsync(dialog))
        {
            return;
        }

        try
        {
            foreach (string rawName in rawNames)
            {
                _store.DeleteValue(key, rawName);
            }
        }
        catch (RegistryOperationException ex)
        {
            await ShowRegistryErrorAsync(ex);
            return;
        }

        await RefreshValuesAsync(selectDisplayName: null);
    }

    private async Task DeleteKeyAsync()
    {
        if (!TryGetSelectedKeyPath(out RegistryPath path))
        {
            await ShowMessageAsync(Get("DialogNoSelection"));
            return;
        }

        if (path.IsRoot)
        {
            await ShowMessageAsync(Get("DialogRootKeyLocked"));
            return;
        }

        bool hasChildren;
        try
        {
            hasChildren = _store.Reader.GetKeyInfo(path).SubKeyCount > 0;
        }
        catch (RegistryOperationException ex)
        {
            await ShowRegistryErrorAsync(ex);
            return;
        }

        string message = Get("DialogDeleteKeyMessage");
        if (hasChildren)
        {
            message += Environment.NewLine + Environment.NewLine + Get("DialogDeleteKeyChildrenWarning");
        }

        var dialog = new ConfirmDialog(
            Get("DialogDeleteKeyTitle"),
            message,
            Get("DialogYes"),
            Get("DialogNo"));

        if (!await ShowDialogAsync(dialog))
        {
            return;
        }

        try
        {
            _store.DeleteKey(path, recursive: true);
        }
        catch (RegistryOperationException ex)
        {
            await ShowRegistryErrorAsync(ex);
            return;
        }

        string? parentPath = path.Parent?.ToDisplayString() ?? "Computer";
        await RefreshTreeAsync(parentPath);
    }

    private async Task RenameKeyInteractiveAsync()
    {
        if (!TryGetSelectedKeyPath(out RegistryPath path))
        {
            await ShowMessageAsync(Get("DialogNoSelection"));
            return;
        }

        if (path.IsRoot)
        {
            await ShowMessageAsync(Get("DialogRootKeyLocked"));
            return;
        }

        string currentName = path.Components[^1];
        var dialog = new TextPromptDialog(
            Get("DialogRenameKeyTitle"),
            Get("DialogKeyNameLabel"),
            currentName,
            value => ValidateRenameKey(path, value));

        if (!await ShowDialogAsync(dialog))
        {
            return;
        }

        await RenameKeyCoreAsync(path, dialog.Value);
    }

    private async Task RenameKeyCoreAsync(RegistryPath path, string newName)
    {
        if (string.Equals(path.Components[^1], newName, StringComparison.Ordinal))
        {
            return;
        }

        string? error = ValidateRenameKey(path, newName);
        if (error is not null)
        {
            await ShowMessageAsync(error);
            return;
        }

        RegistryPath target = path.Parent!.Append(newName);
        try
        {
            _store.RenameKey(path, newName);
        }
        catch (RegistryOperationException ex)
        {
            await ShowRegistryErrorAsync(ex);
            return;
        }

        await RefreshTreeAsync(target.ToDisplayString());
    }

    private async Task RenameValueInteractiveAsync(DetailsRow row)
    {
        if (!TryGetSelectedKeyPath(out RegistryPath key))
        {
            await ShowMessageAsync(Get("DialogNoSelection"));
            return;
        }

        if (!TryGetRawValueName(row, out string oldName) || oldName.Length == 0)
        {
            await ShowMessageAsync(Get("DialogDefaultValueLocked"));
            return;
        }

        var dialog = new TextPromptDialog(
            Get("DialogRenameValueTitle"),
            Get("DialogValueNameLabel"),
            oldName,
            value => ValidateRenameValue(key, oldName, value));

        if (!await ShowDialogAsync(dialog))
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
            await ShowMessageAsync(error);
            return;
        }

        try
        {
            _store.RenameValue(key, oldName, newName);
        }
        catch (RegistryOperationException ex)
        {
            await ShowRegistryErrorAsync(ex);
            return;
        }

        await RefreshValuesAsync(newName);
    }

    private string? ValidateNewKeyName(RegistryPath parent, string name)
    {
        RegistryPath child;
        try
        {
            child = parent.Append(name);
        }
        catch (ArgumentException)
        {
            return Get("DialogInvalidKeyName");
        }

        try
        {
            return _store.Reader.KeyExists(child) ? Get("DialogNameExists") : null;
        }
        catch (RegistryOperationException ex)
        {
            return ex.Message;
        }
    }

    private string? ValidateRenameKey(RegistryPath path, string newName)
    {
        if (string.Equals(path.Components[^1], newName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        RegistryPath target;
        try
        {
            target = path.Parent!.Append(newName);
        }
        catch (ArgumentException)
        {
            return Get("DialogInvalidKeyName");
        }

        try
        {
            return _store.Reader.KeyExists(target) ? Get("DialogNameExists") : null;
        }
        catch (RegistryOperationException ex)
        {
            return ex.Message;
        }
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
            return _store.Reader.ValueExists(key, name) ? Get("DialogNameExists") : null;
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

        if (name.Length > MaxValueNameLength || name.Contains('\0'))
        {
            return Get("DialogInvalidValueName");
        }

        return null;
    }

    private string NextUniqueKeyName(RegistryPath parent)
    {
        for (var index = 1; index < 10000; index += 1)
        {
            string candidate = "New Key #" + index.ToString(CultureInfo.InvariantCulture);
            if (!_store.Reader.KeyExists(parent.Append(candidate)))
            {
                return candidate;
            }
        }

        return "New Key #" + Environment.TickCount64.ToString(CultureInfo.InvariantCulture);
    }

    private string NextUniqueValueName(RegistryPath key)
    {
        for (var index = 1; index < 10000; index += 1)
        {
            string candidate = "New Value #" + index.ToString(CultureInfo.InvariantCulture);
            if (!_store.Reader.ValueExists(key, candidate))
            {
                return candidate;
            }
        }

        return "New Value #" + Environment.TickCount64.ToString(CultureInfo.InvariantCulture);
    }

    private bool TryGetSelectedKeyPath(out RegistryPath path)
    {
        path = null!;
        TreeNode? node = Shell.TreeContent.SelectedNode;
        if (node?.RegistryPath is not { Length: > 0 } displayPath ||
            !RegistryPath.TryParse(displayPath, out RegistryPath? parsed) ||
            parsed is null)
        {
            return false;
        }

        path = parsed;
        return true;
    }

    private bool TryGetRawValueName(DetailsRow row, out string rawName)
    {
        if (_valueSource.RawNamesByName.TryGetValue(row.Name, out string? mapped) && mapped is not null)
        {
            rawName = mapped;
            return true;
        }

        if (string.Equals(row.Name, "(Default)", StringComparison.Ordinal))
        {
            rawName = string.Empty;
            return true;
        }

        rawName = row.Name;
        return true;
    }

    private async Task<bool> ShowDialogAsync(ModalDialogWindow dialog)
    {
        if (_dialogOpen)
        {
            Log.Warn("Registry Editor ignored a dialog request because another modal is already open.");
            return false;
        }

        if (_windowHwnd == 0)
        {
            Log.Warn("Registry Editor ignored a dialog request because the window handle is not attached.");
            return false;
        }

        _dialogOpen = true;
        try
        {
            return await dialog.ShowAsync(_windowHwnd);
        }
        catch (Exception ex)
        {
            Log.Warn("Registry Editor could not show a dialog: " + ex.Message);
            return false;
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private Task ShowMessageAsync(string message) =>
        ShowDialogAsync(ErrorDialog.ForMessage(Get("DialogErrorTitle"), message));

    private Task ShowErrorAsync(ErrorDialog dialog) => ShowDialogAsync(dialog);

    private async Task ShowRegistryErrorAsync(RegistryOperationException ex)
    {
        Log.Warn(
            $"Registry Editor command failed: {ex.Operation} for {ex.Path.ToDisplayString()}, Win32 {ex.Win32Error}.");

        await ShowErrorAsync(ErrorDialog.ForOperation(
            Get("DialogErrorTitle"),
            ex.Operation,
            ex.Path.ToDisplayString(),
            ex.Win32Error,
            ex.Message));

        if (ex.Win32Error == 2)
        {
            // The selected key disappeared under the view. Rebuild the tree so a stale node is
            // not left behind; RefreshTreeAsync falls back to Computer when the path is gone.
            string? current = Shell.TreeContent.SelectedNode?.RegistryPath;
            await RefreshTreeAsync(current);
        }
    }

    private string Get(string key) => _loader.GetString(key);

    private void OnTreeSelectionChanged(object? sender, TreeNodeEventArgs e)
    {
        if (!_applyingNavigation)
        {
            _navigationGeneration++;
        }

        string displayPath = e.Node.RegistryPath ?? e.Node.Name;
        Shell.CurrentPath = displayPath;
        _settings.LastKey = displayPath;
        UpdateCommandStates();
        _ = LoadValuesAsync(e.Node);
    }

    private void OnAddressBarSubmitted(object? sender, AddressBarSubmittedEventArgs e)
    {
        int generation = ++_navigationGeneration;
        StartCommand(() => NavigateToAddressAsync(e.Text, generation));
    }

    /// <summary>
    /// Resolves the submitted text without blocking the dispatcher. The generation makes a slow
    /// result stale as soon as another submission or a tree selection happens.
    /// </summary>
    private async Task NavigateToAddressAsync(string text, int generation)
    {
        string attempted = (text ?? string.Empty).Trim();
        string candidate = attempted.TrimEnd('\\');

        if (candidate.Length == 0)
        {
            await FailAddressNavigationAsync(generation, attempted, Get("DialogAddressBarInvalidPath"));
            return;
        }

        if (string.Equals(candidate, "Computer", StringComparison.OrdinalIgnoreCase))
        {
            if (generation == _navigationGeneration)
            {
                ApplyNavigation(_treeSource.Computer);
            }

            return;
        }

        if (!RegistryPath.TryParse(candidate, out RegistryPath? parsed) || parsed is null)
        {
            await FailAddressNavigationAsync(generation, attempted, Get("DialogAddressBarInvalidPath"));
            return;
        }

        TreeNode? target;
        try
        {
            target = await _treeSource.FindPathAsync(parsed.ToDisplayString());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Registry Editor address bar could not resolve '{attempted}': {ex.Message}");
            target = null;
        }

        if (generation != _navigationGeneration)
        {
            return;
        }

        if (target is null)
        {
            await FailAddressNavigationAsync(generation, attempted, Get("DialogAddressBarKeyNotFound"));
            return;
        }

        ApplyNavigation(target);
    }

    private void ApplyNavigation(TreeNode node)
    {
        _applyingNavigation = true;
        try
        {
            Shell.TreeContent.SetRoots(_treeSource.Roots);
            Shell.TreeContent.SelectNode(node);
            Shell.CurrentPath = node.RegistryPath ?? "Computer";
        }
        finally
        {
            _applyingNavigation = false;
        }

        Shell.TreeContent.FocusSelected();
    }

    private async Task FailAddressNavigationAsync(int generation, string attempted, string message)
    {
        if (generation != _navigationGeneration)
        {
            return;
        }

        TreeNode? selected = Shell.TreeContent.SelectedNode;
        Shell.CurrentPath = selected?.RegistryPath ?? selected?.Name ?? "Computer";
        _ = Shell.AddressBar.Focus(FocusState.Programmatic);
        await ShowAddressBarErrorAsync(message, attempted);
    }

    private Task ShowAddressBarErrorAsync(string message, string attempted)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(attempted))
        {
            lines.Add("Path: " + attempted);
            lines.Add(string.Empty);
        }

        lines.Add(message);
        return ShowErrorAsync(
            ErrorDialog.ForMessage(Get("DialogErrorTitle"), string.Join(Environment.NewLine, lines)));
    }

    private void OnListSelectionChanged(object? sender, EventArgs e)
    {
        UpdateCommandStates();
    }

    private void OnTreeRenameRequested(object? sender, TreeRenameRequestedEventArgs e)
    {
        StartCommand(async () =>
        {
            if (e.Node.RegistryPath is not { Length: > 0 } displayPath ||
                !RegistryPath.TryParse(displayPath, out RegistryPath? parsed) ||
                parsed is null)
            {
                await ShowMessageAsync(Get("DialogNoSelection"));
                return;
            }

            RegistryPath path = parsed;

            if (path.IsRoot)
            {
                await ShowMessageAsync(Get("DialogRootKeyLocked"));
                return;
            }

            await RenameKeyCoreAsync(path, e.NewName);
        });
    }

    private void OnListRenameRequested(object? sender, ListRenameRequestedEventArgs e)
    {
        StartCommand(async () =>
        {
            if (!TryGetSelectedKeyPath(out RegistryPath key))
            {
                await ShowMessageAsync(Get("DialogNoSelection"));
                return;
            }

            if (string.Equals(e.Row.Name, "(Default)", StringComparison.Ordinal) ||
                !TryGetRawValueName(e.Row, out string oldName) ||
                oldName.Length == 0)
            {
                await ShowMessageAsync(Get("DialogDefaultValueLocked"));
                return;
            }

            await RenameValueCoreAsync(key, oldName, e.NewName);
        });
    }

    private void UpdateCommandStates()
    {
        bool hasKey = TryGetSelectedKeyPath(out RegistryPath key);
        bool mutableKey = hasKey && !key.IsRoot;
        IReadOnlyList<DetailsRow> rows = Shell.ListContent.SelectedRows;
        bool hasValue = rows.Count > 0;

        foreach (int id in new[] { 663, 664, 665, 674, 688, 676, 677 })
        {
            RegeditMenu.SetCommandEnabled(Shell.MenuBar, id, hasKey);
        }

        RegeditMenu.SetCommandEnabled(Shell.MenuBar, 913, hasValue || mutableKey);
        RegeditMenu.SetCommandEnabled(Shell.MenuBar, 659, hasKey);
        RegeditMenu.SetCommandEnabled(Shell.MenuBar, 679, hasValue && rows.Count == 1);

        bool canRename = mutableKey;
        if (hasValue)
        {
            canRename = rows.Count == 1 &&
                !string.Equals(rows[0].Name, "(Default)", StringComparison.Ordinal);
        }

        RegeditMenu.SetCommandEnabled(Shell.MenuBar, 914, canRename);
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.F3)
        {
            e.Handled = true;
            FindNext();
            return;
        }

        if (e.Key == VirtualKey.F && IsControlDown())
        {
            e.Handled = true;
            Find();
        }
    }

    private static bool IsControlDown() =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down)
        == CoreVirtualKeyStates.Down;

    private async Task ShowFindDialogAsync()
    {
        if (_dialogOpen || _windowHwnd == 0)
        {
            Log.Warn(
                "Registry Editor ignored Find because another modal is open or the window handle is not attached.");
            return;
        }

        var dialog = new FindDialog(CreateFindLabels(), OptionsFromSettings());
        dialog.FindRequested += OnFindRequested;
        _findDialog = dialog;

        try
        {
            _ = await ShowDialogAsync(dialog);
        }
        finally
        {
            dialog.FindRequested -= OnFindRequested;
            FindDialogOptions options = dialog.Options;
            _settings.FindText = options.SearchText;
            _settings.FindFlags = ToFlags(options);

            if (ReferenceEquals(_findDialog, dialog))
            {
                _findDialog = null;
            }
        }
    }

    private FindDialogLabels CreateFindLabels() => new()
    {
        Title = Get("DialogFindTitle"),
        FindWhat = Get("DialogFindWhat"),
        LookAt = Get("DialogFindLookAt"),
        Keys = Get("DialogFindKeys"),
        Values = Get("DialogFindValues"),
        Data = Get("DialogFindData"),
        MatchWholeString = Get("DialogFindMatchWhole"),
        SearchInSubtree = Get("DialogFindSubtree"),
        FindNext = Get("DialogFindNext"),
        Cancel = Get("DialogFindCancel"),
        Searching = Get("DialogFindSearching"),
        FoundFormat = Get("DialogFindFound"),
        Finished = Get("DialogFindFinished"),
        EmptyText = Get("DialogFindEmptyText"),
        NoScope = Get("DialogFindNoScope"),
    };

    private void OnFindRequested(object? sender, FindDialogRequestEventArgs e)
    {
        if (sender is FindDialog dialog)
        {
            _ = RunFindAsync(dialog, e.Options, e.CancellationToken);
        }
    }

    /// <summary>
    /// Runs one request. The engine call is on the thread pool. A result is applied only while its
    /// token is current, so Cancel or a newer request cannot be overwritten by a late hit.
    /// </summary>
    private async Task RunFindAsync(
        FindDialog? dialog,
        FindDialogOptions options,
        CancellationToken cancellationToken)
    {
        if (!TryBuildQuery(options, out RegistryFindQuery? query) || query is null)
        {
            dialog?.ShowSearchError(Get("DialogFindNoScope"));
            return;
        }

        _settings.FindText = options.SearchText;
        _settings.FindFlags = ToFlags(options);

        bool repeatsLastQuery = _lastFindQuery is not null && SameQuery(_lastFindQuery, query);
        RegistryFindCursor? cursor;

        if (repeatsLastQuery && _lastFindHit is not null)
        {
            cursor = RegistryFindCursor.FromHit(_lastFindHit);
        }
        else if (repeatsLastQuery && _findExhausted)
        {
            if (dialog is not null)
            {
                dialog.ShowFinished();
            }
            else
            {
                await ShowMessageAsync(Get("DialogFindFinished"));
            }

            return;
        }
        else
        {
            cursor = CreateSelectionCursor();
        }

        RegistryFindHit? hit;
        try
        {
            hit = await Task.Run(
                () => _finder.Find(query, cursor, cancellationToken),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Warn("Registry Editor find failed: " + ex.Message);
            if (dialog is not null)
            {
                dialog.ShowSearchError(Get("DialogFindSearchFailed"));
            }
            else
            {
                await ShowMessageAsync(Get("DialogFindSearchFailed"));
            }

            return;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        _lastFindQuery = query;
        _findExhausted = hit is null;

        if (hit is not null)
        {
            _lastFindHit = hit;
        }

        PersistFindHit(query, hit);

        if (hit is null)
        {
            if (dialog is not null)
            {
                dialog.ShowFinished();
            }
            else
            {
                await ShowMessageAsync(Get("DialogFindFinished"));
            }

            return;
        }

        bool applied = await ApplyFindHitAsync(hit, cancellationToken);
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (dialog is not null)
        {
            if (applied)
            {
                dialog.CompleteSearch(hit.DisplayText);
            }
            else
            {
                dialog.ShowFinished();
            }
        }
    }

    /// <summary>
    /// Resolves a hit key through the tree source, rebuilds the visible rows with the hit
    /// ancestors expanded, selects the key, and selects the matching value row when there is one.
    /// Focus is deliberately left alone so a modal Find dialog keeps keyboard focus.
    /// </summary>
    private async Task<bool> ApplyFindHitAsync(RegistryFindHit hit, CancellationToken cancellationToken)
    {
        string displayPath = hit.KeyPath.ToDisplayString();
        TreeNode? node;

        try
        {
            node = await _treeSource.FindPathAsync(displayPath);
        }
        catch (Exception ex)
        {
            Log.Warn($"Registry Editor could not resolve the find hit '{displayPath}': {ex.Message}");
            node = null;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        if (node is null)
        {
            Log.Warn(
                $"Registry Editor dropped the find hit '{displayPath}' because the key no longer resolves.");
            return false;
        }

        _applyingNavigation = true;
        try
        {
            Shell.TreeContent.SetRoots(_treeSource.Roots);
            Shell.TreeContent.SelectNode(node);
            Shell.CurrentPath = node.RegistryPath ?? displayPath;
        }
        finally
        {
            _applyingNavigation = false;
        }

        if (hit.ValueName is not null)
        {
            await LoadValuesAsync(node, RegistryValueDisplay.FormatName(hit.ValueName));
        }

        return true;
    }

    private RegistryFindCursor? CreateSelectionCursor()
    {
        if (!TryGetSelectedKeyPath(out RegistryPath key))
        {
            return null;
        }

        IReadOnlyList<DetailsRow> rows = Shell.ListContent.SelectedRows;
        if (rows.Count > 0 && TryGetRawValueName(rows[0], out string rawName))
        {
            return RegistryFindCursor.FromValueSelection(key, rawName);
        }

        return RegistryFindCursor.FromKeySelection(key);
    }

    private bool TryBuildQuery(FindDialogOptions options, out RegistryFindQuery? query)
    {
        query = null;
        if (string.IsNullOrEmpty(options.SearchText) ||
            (!options.LookAtKeys && !options.LookAtValues && !options.LookAtData))
        {
            return false;
        }

        try
        {
            query = new RegistryFindQuery(
                options.SearchText,
                options.LookAtKeys,
                options.LookAtValues,
                options.LookAtData,
                options.MatchWholeStringOnly,
                options.SearchInSubtree,
                _findRoots);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private FindDialogOptions OptionsFromSettings()
    {
        int flags = _settings.FindFlags;
        return new FindDialogOptions
        {
            SearchText = _settings.FindText ?? string.Empty,
            LookAtKeys = HasFindFlag(flags, RegeditFindFlags.Keys),
            LookAtValues = HasFindFlag(flags, RegeditFindFlags.Values),
            LookAtData = HasFindFlag(flags, RegeditFindFlags.Data),
            MatchWholeStringOnly = HasFindFlag(flags, RegeditFindFlags.MatchWholeString),
            SearchInSubtree = HasFindFlag(flags, RegeditFindFlags.SearchInSubtree),
        };
    }

    private static FindDialogOptions ToOptions(RegistryFindQuery query) => new()
    {
        SearchText = query.SearchText,
        LookAtKeys = query.LookAtKeys,
        LookAtValues = query.LookAtValues,
        LookAtData = query.LookAtData,
        MatchWholeStringOnly = query.MatchWholeStringOnly,
        SearchInSubtree = query.SearchInSubtree,
    };

    private static int ToFlags(FindDialogOptions options)
    {
        RegeditFindFlags flags = RegeditFindFlags.None;

        if (options.LookAtKeys)
        {
            flags |= RegeditFindFlags.Keys;
        }

        if (options.LookAtValues)
        {
            flags |= RegeditFindFlags.Values;
        }

        if (options.LookAtData)
        {
            flags |= RegeditFindFlags.Data;
        }

        if (options.MatchWholeStringOnly)
        {
            flags |= RegeditFindFlags.MatchWholeString;
        }

        if (options.SearchInSubtree)
        {
            flags |= RegeditFindFlags.SearchInSubtree;
        }

        return (int)flags;
    }

    private static int QueryFlags(RegistryFindQuery query)
    {
        RegeditFindFlags flags = RegeditFindFlags.None;

        if (query.LookAtKeys)
        {
            flags |= RegeditFindFlags.Keys;
        }

        if (query.LookAtValues)
        {
            flags |= RegeditFindFlags.Values;
        }

        if (query.LookAtData)
        {
            flags |= RegeditFindFlags.Data;
        }

        if (query.MatchWholeStringOnly)
        {
            flags |= RegeditFindFlags.MatchWholeString;
        }

        if (query.SearchInSubtree)
        {
            flags |= RegeditFindFlags.SearchInSubtree;
        }

        return (int)flags;
    }

    private static bool HasFindFlag(int flags, RegeditFindFlags flag) =>
        ((RegeditFindFlags)flags & flag) == flag;

    private static bool SameQuery(RegistryFindQuery left, RegistryFindQuery right) =>
        string.Equals(left.SearchText, right.SearchText, StringComparison.Ordinal) &&
        left.LookAtKeys == right.LookAtKeys &&
        left.LookAtValues == right.LookAtValues &&
        left.LookAtData == right.LookAtData &&
        left.MatchWholeStringOnly == right.MatchWholeStringOnly &&
        left.SearchInSubtree == right.SearchInSubtree;

    private void RestoreFindState()
    {
        _lastFindQuery = TryRestoreFindQuery();
        _lastFindHit = _lastFindQuery is null ? null : TryRestoreFindHit();
        _findExhausted = false;
    }

    private RegistryFindQuery? TryRestoreFindQuery()
    {
        if (string.IsNullOrEmpty(_settings.FindQueryText))
        {
            return null;
        }

        var options = new FindDialogOptions
        {
            SearchText = _settings.FindQueryText,
            LookAtKeys = HasFindFlag(_settings.FindQueryFlags, RegeditFindFlags.Keys),
            LookAtValues = HasFindFlag(_settings.FindQueryFlags, RegeditFindFlags.Values),
            LookAtData = HasFindFlag(_settings.FindQueryFlags, RegeditFindFlags.Data),
            MatchWholeStringOnly = HasFindFlag(
                _settings.FindQueryFlags,
                RegeditFindFlags.MatchWholeString),
            SearchInSubtree = HasFindFlag(
                _settings.FindQueryFlags,
                RegeditFindFlags.SearchInSubtree),
        };

        return TryBuildQuery(options, out RegistryFindQuery? query) ? query : null;
    }

    private RegistryFindHit? TryRestoreFindHit()
    {
        if (_settings.FindLastHitKind is < 0 or > 2 ||
            string.IsNullOrWhiteSpace(_settings.FindLastHitKey) ||
            !RegistryPath.TryParse(_settings.FindLastHitKey, out RegistryPath? path) ||
            path is null)
        {
            return null;
        }

        var kind = (RegistryFindHitKind)_settings.FindLastHitKind;
        string? valueName = kind == RegistryFindHitKind.KeyName
            ? null
            : _settings.FindLastHitValue ?? string.Empty;
        string matchedText = _settings.FindLastHitText ?? valueName ?? path.ToDisplayString();

        try
        {
            return new RegistryFindHit(path, valueName, kind, matchedText);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private void PersistFindHit(RegistryFindQuery query, RegistryFindHit? hit)
    {
        _settings.FindQueryText = query.SearchText;
        _settings.FindQueryFlags = QueryFlags(query);

        if (hit is null)
        {
            _settings.FindLastHitKey = null;
            _settings.FindLastHitValue = null;
            _settings.FindLastHitKind = -1;
            _settings.FindLastHitText = null;
            return;
        }

        _settings.FindLastHitKey = hit.KeyPath.ToDisplayString();
        _settings.FindLastHitValue = hit.ValueName;
        _settings.FindLastHitKind = (int)hit.Kind;
        _settings.FindLastHitText = hit.MatchedText;
    }

    private static IReadOnlyList<RegistryPath>? ResolveFindRoots()
    {
        string? configured = Environment.GetEnvironmentVariable("BWC_E2E_SANDBOX_ROOT");
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        return RegistryPath.TryParse(configured.Trim(), out RegistryPath? root) && root is not null
            ? new[] { root }
            : null;
    }

    private async Task LoadValuesAsync(TreeNode node, string? selectDisplayName = null)
    {
        _valueLoad?.Cancel();
        var tokenSource = new CancellationTokenSource();
        _valueLoad = tokenSource;
        int generation = ++_valueGeneration;
        IReadOnlyList<DetailsRow> rows;

        try
        {
            if (node.RegistryPath is not { Length: > 0 } displayPath ||
                !RegistryPath.TryParse(displayPath, out RegistryPath? parsed) ||
                parsed is null)
            {
                // Computer is virtual and has no registry values.
                rows = Array.Empty<DetailsRow>();
            }
            else
            {
                rows = await _valueSource.LoadAsync(parsed, tokenSource.Token);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Warn($"Registry values for '{node.Name}' could not be loaded: {ex.Message}");
            rows = Array.Empty<DetailsRow>();
        }
        finally
        {
            if (ReferenceEquals(_valueLoad, tokenSource))
            {
                _valueLoad = null;
            }

            tokenSource.Dispose();
        }

        if (generation != _valueGeneration)
        {
            return;
        }

        Shell.DispatcherQueue.TryEnqueue(() =>
        {
            if (generation == _valueGeneration)
            {
                Shell.ListContent.SetRows(rows);
                if (!string.IsNullOrEmpty(selectDisplayName))
                {
                    SelectListRow(rows, selectDisplayName);
                }
            }
        });
    }

    private async Task RefreshValuesAsync(string? selectDisplayName)
    {
        _valueLoad?.Cancel();
        _valueLoad = null;
        int generation = ++_valueGeneration;

        if (!TryGetSelectedKeyPath(out RegistryPath path))
        {
            Shell.ListContent.SetRows(Array.Empty<DetailsRow>());
            UpdateCommandStates();
            return;
        }

        IReadOnlyList<DetailsRow> rows;
        try
        {
            rows = await _valueSource.LoadAsync(path, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Warn($"Registry values for '{path.ToDisplayString()}' could not be refreshed: {ex.Message}");
            rows = Array.Empty<DetailsRow>();
        }

        if (generation != _valueGeneration)
        {
            return;
        }

        Shell.ListContent.SetRows(rows);
        UpdateCommandStates();

        if (!string.IsNullOrEmpty(selectDisplayName))
        {
            SelectListRow(rows, selectDisplayName);
        }
    }

    private async Task RefreshTreeAsync(string? selectDisplayPath)
    {
        var source = new RegistryTreeSource(_store.Reader);
        TreeNode? target = null;

        if (!string.IsNullOrWhiteSpace(selectDisplayPath))
        {
            target = await source.FindPathAsync(selectDisplayPath);
        }

        _treeSource = source;
        Shell.TreeContent.SetRoots(source.Roots);
        Shell.TreeContent.SelectNode(target ?? source.Computer);
        UpdateCommandStates();
    }

    /// <summary>
    /// Selects a row by display name through the row peer. The list control has no public select
    /// API, and the row peer's SelectionItem pattern is the supported path from another control.
    /// </summary>
    private void SelectListRow(IReadOnlyList<DetailsRow> rows, string displayName)
    {
        int index = -1;
        for (var i = 0; i < rows.Count; i += 1)
        {
            if (string.Equals(rows[i].Name, displayName, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return;
        }

        _ = Shell.DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                ItemsRepeater? repeater = FindDescendant<ItemsRepeater>(Shell.ListContent);
                if (repeater is null)
                {
                    return;
                }

                UIElement element = repeater.GetOrCreateElement(index);
                if (element is not FrameworkElement frameworkElement)
                {
                    return;
                }

                AutomationPeer? peer = FrameworkElementAutomationPeer.CreatePeerForElement(frameworkElement);
                if (peer is not null &&
                    string.Equals(peer.GetName(), displayName, StringComparison.Ordinal) &&
                    peer.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider provider)
                {
                    provider.Select();
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Registry Editor could not select the refreshed row '{displayName}': {ex.Message}");
            }
        });
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index += 1)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T typed)
            {
                return typed;
            }

            T? nested = FindDescendant<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private async Task RestoreLastKeyAsync(string displayPath)
    {
        int generation = _navigationGeneration;
        try
        {
            TreeNode? node = await _treeSource.FindPathAsync(displayPath);
            if (generation != _navigationGeneration)
            {
                return;
            }

            if (node is null)
            {
                Log.Warn($"Registry Editor kept Computer selected because '{displayPath}' did not resolve.");
                return;
            }

            // FindPathAsync marked every ancestor expanded. Rebuilding the flat rows makes the
            // restored key visible before SelectNode asks the tree to reveal it.
            Shell.TreeContent.SetRoots(_treeSource.Roots);
            Shell.TreeContent.SelectNode(node);
        }
        catch (Exception ex)
        {
            Log.Warn($"Registry Editor could not restore '{displayPath}': {ex.Message}");
        }
    }

    private void ApplyColumnWidths(IReadOnlyDictionary<string, double> widths)
    {
        foreach (KeyValuePair<string, double> pair in widths)
        {
            if (double.IsFinite(pair.Value) && pair.Value > 0)
            {
                Shell.ListContent.SetColumnWidth(pair.Key, pair.Value);
            }
        }
    }
}
