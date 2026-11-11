using Bwc.Core;
using Bwc.Registry;
using Bwc.UI.List;
using Bwc.UI.Tree;
using Microsoft.UI.Xaml;

namespace Bwc.Regedit.Controllers;

/// <summary>
/// Navigation and selection routing: tree selection, the address bar, Go To HKLM, Refresh, the
/// restored last key, list column widths, and the Delete and Rename dispatch for the selection.
/// </summary>
internal sealed class ViewCommands
{
    private readonly RegeditContext _context;
    private readonly KeyCommands _keys;
    private readonly ValueCommands _values;

    public ViewCommands(RegeditContext context, KeyCommands keys, ValueCommands values)
    {
        _context = context;
        _keys = keys;
        _values = values;
    }

    private string Get(string key) => _context.Messages.Get(key);

    /// <summary>
    /// Deletes the selection of the pane that has focus. The tree deletes the selected key, and the list
    /// deletes the selected values. The menu uses the pane that had focus last.
    /// </summary>
    public void DeleteSelection() => DeleteFrom(_context.FocusedPane());

    /// <summary>Deletes from one pane. The tree and list Delete keys name their own pane.</summary>
    public void DeleteFrom(RegeditContext.Pane pane)
    {
        if (pane == RegeditContext.Pane.List)
        {
            _values.DeleteValues();
            return;
        }

        _keys.DeleteKey();
    }

    /// <summary>
    /// Renames the item of the pane that has focus. The tree renames the selected key. The list renames
    /// the single selected value, or the selected key when the list has no selection.
    /// </summary>
    public void RenameSelection()
    {
        IReadOnlyList<DetailsRow> rows = _context.Shell.ListContent.SelectedRows;
        if (_context.FocusedPane() == RegeditContext.Pane.List && rows.Count > 1)
        {
            _context.StartCommand(() => _context.ShowMessageAsync(Get("DialogNoSelection")));
            return;
        }

        if (_context.FocusedPane() == RegeditContext.Pane.List && rows.Count == 1)
        {
            _values.RenameValueInteractive(rows[0]);
            return;
        }

        _keys.RenameKeyInteractive();
    }

    public void HandleTreeSelectionChanged(TreeNode node)
    {
        if (!_context.ApplyingNavigation)
        {
            _context.NavigationGeneration++;
        }

        string displayPath = node.RegistryPath ?? node.Name;
        _context.Shell.CurrentPath = displayPath;
        _context.Settings.LastKey = displayPath;
        _context.UpdateCommandStates();
        _ = _context.LoadValuesAsync(node);
    }

    public void SubmitAddress(string text)
    {
        int generation = ++_context.NavigationGeneration;
        _context.StartCommand(() => NavigateToAddressAsync(text, generation));
    }

    /// <summary>
    /// Re-reads the tree and the value list. Expansion, the selected key and value rows, and the scroll
    /// positions are kept. Bound to F5 and View &gt; Refresh.
    /// </summary>
    public void Refresh() => _context.StartCommand(_context.RefreshViewAsync);

    public void GoToLocalMachine()
    {
        _context.StartCommand(async () =>
        {
            TreeNode? target;
            try
            {
                target = await _context.TreeSource.FindPathAsync("Computer\\HKEY_LOCAL_MACHINE");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn("Registry Editor could not resolve HKEY_LOCAL_MACHINE: " + ex.Message);
                target = null;
            }

            if (target is null)
            {
                await _context.ShowMessageAsync(Get("DialogAddressBarKeyNotFound"));
                return;
            }

            _context.ApplyNavigation(target);
        });
    }

    /// <summary>Walks to the saved last key. A key that no longer resolves leaves Computer selected.</summary>
    public async Task RestoreLastKeyAsync(string displayPath)
    {
        int generation = _context.NavigationGeneration;
        try
        {
            TreeNode? node = await _context.TreeSource.FindPathAsync(displayPath);
            if (generation != _context.NavigationGeneration)
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
            _context.Shell.TreeContent.SetRoots(_context.TreeSource.Roots);
            _context.Shell.TreeContent.SelectNode(node);
        }
        catch (Exception ex)
        {
            Log.Warn($"Registry Editor could not restore '{displayPath}': {ex.Message}");
        }
    }

    public void ApplyColumnWidths(IReadOnlyDictionary<string, double> widths)
    {
        foreach (KeyValuePair<string, double> pair in widths)
        {
            if (double.IsFinite(pair.Value) && pair.Value > 0)
            {
                _context.Shell.ListContent.SetColumnWidth(pair.Key, pair.Value);
            }
        }
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
            if (generation == _context.NavigationGeneration)
            {
                _context.ApplyNavigation(_context.TreeSource.Computer);
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
            target = await _context.TreeSource.FindPathAsync(parsed.ToDisplayString());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Registry Editor address bar could not resolve '{attempted}': {ex.Message}");
            target = null;
        }

        if (generation != _context.NavigationGeneration)
        {
            return;
        }

        if (target is null)
        {
            await FailAddressNavigationAsync(generation, attempted, Get("DialogAddressBarKeyNotFound"));
            return;
        }

        _context.ApplyNavigation(target);
    }

    private async Task FailAddressNavigationAsync(int generation, string attempted, string message)
    {
        if (generation != _context.NavigationGeneration)
        {
            return;
        }

        TreeNode? selected = _context.Shell.TreeContent.SelectedNode;
        _context.Shell.CurrentPath = selected?.RegistryPath ?? selected?.Name ?? "Computer";
        _ = _context.Shell.AddressBar.Focus(FocusState.Programmatic);
        await _context.ShowAddressBarErrorAsync(message, attempted);
    }
}
