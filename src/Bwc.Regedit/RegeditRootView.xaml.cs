using Bwc.Regedit.Commands;
using Bwc.Regedit.Controllers;
using Bwc.Regedit.Registry;
using Bwc.Registry;
using Bwc.UI.List;
using Bwc.UI.Shell;
using Bwc.UI.Tree;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.ApplicationModel.Resources;
using Windows.System;
using Windows.UI.Core;

namespace Bwc.Regedit;

/// <summary>The Registry Editor content: classic shell plus the full RT_MENU 103 command table.</summary>
public sealed partial class RegeditRootView : UserControl, IRegeditEditCommands
{
    private readonly RegeditContext _context;
    private readonly ViewCommands _viewCommands;
    private readonly KeyCommands _keyCommands;
    private readonly ValueCommands _valueCommands;
    private readonly ImportExportCommands _importExport;
    private readonly FindController _findController;
    private readonly FavoritesController _favoritesController;
    private readonly HiveRemoteCommands _hiveRemote;
    private readonly PrintController _printController;
    private readonly PersistenceController _persistence;
    private readonly ContextMenuController _contextMenus;

    public RegeditRootView()
    {
        RegistryStore store = CreateStore();
        var treeSource = new RegistryTreeSource(store.Reader);
        var valueSource = new RegistryValueSource(store.Reader);
        var favorites = new FavoritesStore();
        var finder = new RegistryFinder(store.Reader);
        IReadOnlyList<RegistryPath>? findRoots = FindController.ResolveFindRoots();

        InitializeComponent();

        var loader = new ResourceLoader(ResourceLoader.GetDefaultResourceFilePath(), RegeditStrings.ResourceMap);
        _context = new RegeditContext(
            Shell,
            new MessageCatalog(loader),
            store,
            treeSource,
            valueSource,
            favorites,
            finder,
            findRoots);
        _keyCommands = new KeyCommands(_context);
        _valueCommands = new ValueCommands(_context);
        _viewCommands = new ViewCommands(_context, _keyCommands, _valueCommands);
        _importExport = new ImportExportCommands(_context);
        _findController = new FindController(_context);
        _favoritesController = new FavoritesController(_context);
        _hiveRemote = new HiveRemoteCommands(_context);
        _printController = new PrintController(_context);
        _persistence = new PersistenceController(_context, _findController, _viewCommands);
        _contextMenus = new ContextMenuController(
            _context,
            this,
            _importExport,
            _hiveRemote,
            _keyCommands,
            _valueCommands,
            _viewCommands);

        var commands = RegeditMenu.Create(
            loader,
            Shell,
            this,
            () => ExitRequested?.Invoke(this, EventArgs.Empty),
            Import,
            Export,
            displayBinaryData: DisplayBinaryData,
            addFavorite: AddFavorite,
            removeFavorite: RemoveFavorite,
            permissions: Permissions,
            print: Print,
            loadHive: LoadHive,
            unloadHive: UnloadHive,
            connectNetworkRegistry: ConnectNetworkRegistry,
            disconnectNetworkRegistry: DisconnectNetworkRegistry);
        RegeditMenu.Populate(Shell.MenuBar, commands);
        _favoritesController.ReloadOnStartup();

        Shell.TreeContent.SelectionChanged += OnTreeSelectionChanged;
        Shell.TreeContent.RenameRequested += OnTreeRenameRequested;
        Shell.ListContent.SelectionChanged += OnListSelectionChanged;
        Shell.ListContent.RenameRequested += OnListRenameRequested;
        Shell.ListContent.RowActivated += OnListRowActivated;
        Shell.TreeContent.ContextRequested += OnTreeContextRequested;
        Shell.TreeContent.DeleteRequested += OnSelectionDeleteRequested;
        Shell.TreeContent.CopyKeyNameRequested += OnCopyKeyNameRequested;
        Shell.ListContent.ContextRequested += OnListContextRequested;
        Shell.ListContent.DeleteRequested += OnSelectionDeleteRequested;
        Shell.AddressBarSubmitted += OnAddressBarSubmitted;
        Shell.TreeContent.SetRoots(_context.TreeSource.Roots);
        Shell.TreeContent.SelectNode(_context.TreeSource.Computer);
        _context.UpdateCommandStates();

        // handledEventsToo keeps Ctrl+F and F3 working when the tree or list already consumed a
        // different key. Dialogs are separate windows, so their keys never reach this handler.
        AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnRootKeyDown), handledEventsToo: true);
        Unloaded += OnRegeditRootViewUnloaded;
    }

    /// <summary>Raised when File > Exit is chosen. The window owns the actual close.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>The in-memory view state. Updated by selection changes, not written to disk.</summary>
    public RegeditSettings Settings => _context.Settings;

    /// <summary>
    /// Applies persisted view state once. Call after <see cref="InitializeComponent"/> so the shell
    /// controls exist. Restoring LastKey walks the registry asynchronously and leaves the current
    /// Computer selection in place if the key no longer resolves.
    /// </summary>
    public void ApplySettings(RegeditSettings settings) => _persistence.ApplySettings(settings);

    /// <summary>Copies the current key, address bar state, and column widths for saving.</summary>
    public RegeditSettings CaptureSettings() => _persistence.CaptureSettings();

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
        _context.WindowHwnd = hwnd;
    }

    public void NewKey() => _keyCommands.NewKey();

    public void NewStringValue() => _valueCommands.NewValue(ValueCommands.NewValueKind.String);

    public void NewExpandableStringValue() => _valueCommands.NewValue(ValueCommands.NewValueKind.ExpandableString);

    public void NewBinaryValue() => _valueCommands.NewValue(ValueCommands.NewValueKind.Binary);

    public void NewDwordValue() => _valueCommands.NewValue(ValueCommands.NewValueKind.Dword);

    public void NewQwordValue() => _valueCommands.NewValue(ValueCommands.NewValueKind.Qword);

    public void NewMultiStringValue() => _valueCommands.NewValue(ValueCommands.NewValueKind.MultiString);

    public void DeleteSelection() => _viewCommands.DeleteSelection();

    public void RenameSelection() => _viewCommands.RenameSelection();

    public void Import() => _importExport.Import();

    public void Export() => _importExport.Export();

    public void LoadHive() => _hiveRemote.LoadHive();

    public void UnloadHive() => _hiveRemote.UnloadHive();

    public void ConnectNetworkRegistry() => _hiveRemote.ConnectNetworkRegistry();

    public void DisconnectNetworkRegistry() => _hiveRemote.DisconnectNetworkRegistry();

    /// <summary>Prints the selected key and its readable descendants after the owned print dialog.</summary>
    public void Print() => _printController.Print();

    /// <summary>Opens the read-only binary viewer for the single selected value.</summary>
    public void DisplayBinaryData() => _valueCommands.DisplayBinaryData();

    /// <summary>Opens the standard aclui security editor for the selected registry key.</summary>
    public void Permissions() => _hiveRemote.Permissions();

    public void Find() => _findController.Find();

    public void FindNext() => _findController.FindNext();

    private void AddFavorite() => _favoritesController.AddFavorite();

    private void RemoveFavorite() => _favoritesController.RemoveFavorite();

    private static RegistryStore CreateStore() => RegeditWritePolicy.CreateStore();

    private void OnTreeSelectionChanged(object? sender, TreeNodeEventArgs e) =>
        _viewCommands.HandleTreeSelectionChanged(e.Node);

    private void OnListSelectionChanged(object? sender, EventArgs e) => _context.UpdateCommandStates();

    private void OnTreeRenameRequested(object? sender, TreeRenameRequestedEventArgs e) =>
        _keyCommands.RenameFromTree(e.Node, e.NewName);

    private void OnListRenameRequested(object? sender, ListRenameRequestedEventArgs e) =>
        _valueCommands.RenameFromList(e.Row, e.NewName);

    private void OnListRowActivated(object? sender, DetailsRowActivatedEventArgs e) =>
        _valueCommands.ActivateValue(e.Row);

    private void OnSelectionDeleteRequested(object? sender, EventArgs e) => DeleteSelection();

    private void OnCopyKeyNameRequested(object? sender, EventArgs e) => _keyCommands.CopySelectedKeyName();

    private void OnTreeContextRequested(object? sender, TreeNodeContextRequestedEventArgs e) =>
        _contextMenus.ShowTreeContextMenu(e);

    private void OnListContextRequested(object? sender, DetailsRowContextRequestedEventArgs e) =>
        _contextMenus.ShowListContextMenu(e);

    private void OnAddressBarSubmitted(object? sender, AddressBarSubmittedEventArgs e) =>
        _viewCommands.SubmitAddress(e.Text);

    private void OnRegeditRootViewUnloaded(object sender, RoutedEventArgs e)
    {
        Unloaded -= OnRegeditRootViewUnloaded;
        _hiveRemote.DisposeRemoteConnections();
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.F3)
        {
            e.Handled = true;
            FindNext();
            return;
        }

        if (e.Key == VirtualKey.F5)
        {
            e.Handled = true;
            _viewCommands.Refresh();
            return;
        }

        if (e.Key == VirtualKey.F && IsControlDown())
        {
            e.Handled = true;
            Find();
            return;
        }

        if (e.Key == VirtualKey.P && IsControlDown() && IsTreeOrListFocused())
        {
            e.Handled = true;
            Print();
        }
    }

    private static bool IsControlDown() =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down)
        == CoreVirtualKeyStates.Down;

    private bool IsTreeOrListFocused() =>
        _context.IsFocusWithin(Shell.TreeContent) || _context.IsFocusWithin(Shell.ListContent);
}
