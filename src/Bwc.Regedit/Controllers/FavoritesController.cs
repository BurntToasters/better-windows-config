using Bwc.Core;
using Bwc.Regedit.Commands;
using Bwc.Regedit.Registry;
using Bwc.Registry;
using Bwc.UI.Dialogs;
using Bwc.UI.Menu;
using Bwc.UI.Tree;
using Microsoft.UI.Xaml.Controls;

namespace Bwc.Regedit.Controllers;

/// <summary>Add to Favorites, Remove Favorite, and the Favorites menu.</summary>
internal sealed class FavoritesController
{
    private readonly RegeditContext _context;

    public FavoritesController(RegeditContext context)
    {
        _context = context;
    }

    private string Get(string key) => _context.Messages.Get(key);

    public void AddFavorite() => _context.StartCommand(AddFavoriteAsync);

    public void RemoveFavorite() => _context.StartCommand(RemoveFavoriteAsync);

    /// <summary>Loads the saved favorites and builds the Favorites menu. Called once when the view is created.</summary>
    public void ReloadOnStartup()
    {
        _context.Favorites.Reload();
        if (_context.Favorites.LoadError is not null)
        {
            Log.Warn("Registry Editor could not load favorites at startup: " + _context.Favorites.LoadError);
        }

        ApplyFavoritesMenu();
    }

    private async Task AddFavoriteAsync()
    {
        if (_context.DialogOpen || _context.WindowHwnd == 0)
        {
            Log.Warn(
                "Registry Editor ignored Add to Favorites because another modal is open or the window handle is not attached.");
            return;
        }

        if (!_context.TryGetSelectedKeyPath(out RegistryPath key))
        {
            TreeNode? selected = _context.Shell.TreeContent.SelectedNode;
            string message = selected is not null &&
                string.Equals(selected.Name, "Computer", StringComparison.OrdinalIgnoreCase)
                ? Get("DialogFavoriteComputer")
                : Get("DialogNoSelection");
            await _context.ShowMessageAsync(message);
            return;
        }

        string suggested = key.Components.Count == 0
            ? key.Root.DisplayName()
            : key.Components[^1];

        var dialog = new TextPromptDialog(
            Get("DialogFavoriteAddTitle"),
            Get("DialogFavoriteNameLabel"),
            suggested,
            ValidateFavoriteName);

        if (!await _context.ShowDialogAsync(dialog))
        {
            _context.FocusTreeAfterDialog();
            return;
        }

        try
        {
            _context.Favorites.Add(dialog.Value, key);
        }
        catch (RegistryOperationException ex)
        {
            await _context.ShowRegistryErrorAsync(ex, RegistryAction.FavoriteAdd, dialog.Value);
            return;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Log.Warn($"Registry Editor could not add favorite '{dialog.Value}': {ex.Message}");
            await _context.ShowErrorAsync(
                new ClassicMessage(Get("DialogErrorAddingFavorite"), Get("MsgCreateValueWriteFailed")).ToErrorDialog());
            return;
        }

        await RefreshFavoritesAfterChangeAsync();
        _context.FocusTreeAfterDialog();
    }

    private string? ValidateFavoriteName(string name)
    {
        string candidate = name.Trim();
        if (candidate.Length == 0)
        {
            return Get("DialogFavoriteEmptyName");
        }

        if (candidate.Length > RegeditContext.MaxValueNameLength || candidate.Contains('\0'))
        {
            return Get("DialogInvalidValueName");
        }

        return _context.Favorites.ContainsName(candidate) ? Get("DialogFavoriteDuplicate") : null;
    }

    private async Task RemoveFavoriteAsync()
    {
        if (_context.DialogOpen || _context.WindowHwnd == 0)
        {
            Log.Warn(
                "Registry Editor ignored Remove Favorite because another modal is open or the window handle is not attached.");
            return;
        }

        _context.Favorites.Reload();
        if (_context.Favorites.LoadError is not null)
        {
            await ShowFavoriteLoadErrorAsync();
            return;
        }

        if (_context.Favorites.Favorites.Count == 0)
        {
            await _context.ShowErrorAsync(ErrorDialog.ForMessage(
                Get("DialogFavoriteCaption"),
                Get("DialogFavoriteNone")));
            return;
        }

        IReadOnlyList<string> names = _context.Favorites.Favorites.Select(favorite => favorite.Name).ToArray();
        var dialog = new RemoveFavoritesDialog(
            Get("DialogFavoriteRemoveTitle"),
            Get("DialogFavoriteSelectLabel"),
            names,
            Get("DialogOk"),
            Get("DialogCancel"),
            Get("DialogFavoriteNoSelection"));

        if (!await _context.ShowDialogAsync(dialog))
        {
            _context.FocusTreeAfterDialog();
            return;
        }

        try
        {
            _context.Favorites.Remove(dialog.SelectedNames);
        }
        catch (RegistryOperationException ex)
        {
            await _context.ShowRegistryErrorAsync(ex, RegistryAction.DeleteValues, string.Empty);
            return;
        }
        catch (InvalidOperationException ex)
        {
            Log.Warn($"Registry Editor could not remove favorites: {ex.Message}");
            await _context.ShowErrorAsync(
                new ClassicMessage(Get("DialogFavoriteCaption"), Get("MsgDeleteValuesFailed")).ToErrorDialog());
            return;
        }

        await RefreshFavoritesAfterChangeAsync();
        _context.FocusTreeAfterDialog();
    }

    private async Task RefreshFavoritesAfterChangeAsync()
    {
        _context.Favorites.Reload();
        if (_context.Favorites.LoadError is not null)
        {
            Log.Warn("Registry Editor could not refresh favorites: " + _context.Favorites.LoadError);
            await ShowFavoriteLoadErrorAsync();
            return;
        }

        ApplyFavoritesMenu();
    }

    private Task ShowFavoriteLoadErrorAsync()
    {
        // The load error text is developer detail. It goes to the log, and the dialog shows the plain text.
        Log.Warn("Registry Editor could not read the favorites list: " + (_context.Favorites.LoadError ?? "no detail"));
        return _context.ShowErrorAsync(ErrorDialog.ForMessage(
            Get("DialogFavoriteCaption"),
            Get("DialogFavoriteLoadFailed")));
    }

    private void ApplyFavoritesMenu()
    {
        MenuBarItem? favoritesMenu = RegeditMenu.FindTopLevelItem(_context.Shell.MenuBar, 517);
        if (favoritesMenu is null)
        {
            Log.Warn("Registry Editor could not find the Favorites menu to refresh.");
            return;
        }

        while (favoritesMenu.Items.Count > 2)
        {
            favoritesMenu.Items.RemoveAt(favoritesMenu.Items.Count - 1);
        }

        IReadOnlyList<Favorite> favorites = _context.Favorites.Favorites;
        if (favorites.Count > 0)
        {
            favoritesMenu.Items.Add(RegeditMenu.BuildFlyoutItem(ClassicCommandItem.Separator));
        }

        for (var index = 0; index < favorites.Count; index += 1)
        {
            Favorite favorite = favorites[index];
            favoritesMenu.Items.Add(RegeditMenu.BuildFavoriteItem(
                favorite.Name,
                index,
                () => _context.StartCommand(() => NavigateToFavoriteAsync(favorite))));
        }

        RegeditMenu.SetCommandEnabled(_context.Shell.MenuBar, 1280, favorites.Count > 0);
    }

    private async Task NavigateToFavoriteAsync(Favorite favorite)
    {
        TreeNode? target;
        try
        {
            target = await _context.TreeSource.FindPathAsync(favorite.DisplayPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Registry Editor could not resolve favorite '{favorite.Name}': {ex.Message}");
            target = null;
        }

        if (target is null)
        {
            Log.Warn(
                $"Registry Editor favorite '{favorite.Name}' points at '{favorite.DisplayPath}', which does not resolve.");
            await _context.ShowAddressBarErrorAsync(Get("DialogAddressBarKeyNotFound"), favorite.DisplayPath);
            _context.FocusTreeAfterDialog();
            return;
        }

        _context.ApplyNavigation(target);
    }
}
