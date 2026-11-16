using System.Globalization;
using Bwc.Core;
using Bwc.Registry;
using Bwc.UI.Dialogs;
using Bwc.UI.Tree;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;

namespace Bwc.Regedit.Controllers;

/// <summary>
/// Key commands: New Key, Delete Key, Rename Key, and Copy Key Name. Each acts on the key location of the
/// selection, so a remote key is written through its connection's store and never the local registry.
/// </summary>
internal sealed class KeyCommands
{
    /// <summary>Longest key name component the registry accepts, in characters.</summary>
    private const int MaxKeyNameLength = 255;

    private readonly RegeditContext _context;

    public KeyCommands(RegeditContext context)
    {
        _context = context;
    }

    private string Get(string key) => _context.Messages.Get(key);

    public void NewKey() => _context.StartCommand(NewKeyAsync);

    public void DeleteKey() => _context.StartCommand(DeleteKeyAsync);

    /// <summary>Applies an inline rename that the tree committed for one of its nodes.</summary>
    public void RenameFromTree(TreeNode node, string newName)
    {
        _context.StartCommand(async () =>
        {
            if (!_context.TryGetLocationForNode(node, out KeyLocation location))
            {
                await _context.ShowMessageAsync(Get("DialogNoSelection"));
                return;
            }

            if (location.Path.IsRoot)
            {
                await _context.ShowMessageAsync(Get("DialogRootKeyLocked"));
                return;
            }

            await RenameKeyCoreAsync(location, newName);
        });
    }

    /// <summary>
    /// Copies the display path of the selected key: <c>HKEY_...</c> locally and <c>\\COMPUTER\HKEY_...</c> for a
    /// remote key, which is the classic form.
    /// </summary>
    public void CopySelectedKeyName()
    {
        if (!_context.TryGetSelectedLocation(out KeyLocation location))
        {
            return;
        }

        try
        {
            var package = new DataPackage();
            package.SetText(location.DisplayPath);
            Clipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            // Clipboard access is environmental. The selection and menu stay usable.
            Log.Warn("Registry Editor could not copy the selected key name: " + ex.Message);
        }
    }

    /// <summary>
    /// Creates the child key under the selected key with the first unused classic name and opens the
    /// same inline editor as F2. Escape leaves the default name in place.
    /// </summary>
    private async Task NewKeyAsync()
    {
        if (!_context.TryGetSelectedLocation(out KeyLocation parent))
        {
            await _context.ShowMessageAsync(Get("DialogNoSelection"));
            return;
        }

        // The name search and the create read and write the registry, so they run off the UI thread. A remote key
        // makes a network call for each probe.
        string name = await _context.RunRegistryAsync(() => NextUniqueKeyName(parent));
        RegistryPath created = parent.Path.Append(name);
        try
        {
            await _context.RunRegistryWriteAsync(() => parent.Store.CreateKey(created));
        }
        catch (RegistryOperationException ex)
        {
            await _context.ShowRegistryErrorAsync(ex, RegistryAction.CreateKey, RegeditContext.DefaultSubject(created));
            return;
        }

        await _context.RefreshTreeAsync(parent.DisplayOf(created));
        BeginTreeRenameSoon();
    }

    /// <summary>
    /// Starts the inline editor on the next dispatcher turn. A command invoked from a menu flyout can
    /// otherwise lose the rename box to the focus restoration the closing flyout performs.
    /// </summary>
    private void BeginTreeRenameSoon()
    {
        if (!_context.Shell.DispatcherQueue.TryEnqueue(
                DispatcherQueuePriority.Low,
                _context.Shell.TreeContent.BeginRenameSelected))
        {
            _context.Shell.TreeContent.BeginRenameSelected();
        }
    }

    private async Task DeleteKeyAsync()
    {
        if (!_context.TryGetSelectedLocation(out KeyLocation location))
        {
            await _context.ShowMessageAsync(Get("DialogNoSelection"));
            return;
        }

        RegistryPath path = location.Path;
        if (path.IsRoot)
        {
            await _context.ShowMessageAsync(Get("DialogRootKeyLocked"));
            return;
        }

        // The classic confirmation text is the same for every key, with or without subkeys. The
        // subkey count is still read so that a key which cannot be read is reported before the prompt.
        try
        {
            _ = await _context.RunRegistryAsync(() => location.Reader.GetKeyInfo(path));
        }
        catch (RegistryOperationException ex)
        {
            await _context.ShowRegistryErrorAsync(ex, RegistryAction.DeleteKey, RegeditContext.DefaultSubject(path));
            return;
        }

        var dialog = new ConfirmDialog(
            Get("MsgCaptionConfirmKeyDelete"),
            Get("MsgDeleteKeyConfirm"),
            Get("DialogYes"),
            Get("DialogNo"));

        if (!await _context.ShowDialogAsync(dialog))
        {
            return;
        }

        try
        {
            await _context.RunRegistryWriteAsync(() => location.Store.DeleteKey(path, recursive: true));
        }
        catch (RegistryOperationException ex)
        {
            // RegDeleteTree can remove some subkeys and then fail on the key. Rebuild the tree from the registry first,
            // with the failed key still selected, so no removed subkey stays on screen behind the error box.
            await _context.RefreshTreeAsync(location.DisplayOf(path));
            await _context.ShowRegistryErrorAsync(ex, RegistryAction.DeleteKey, RegeditContext.DefaultSubject(path));
            return;
        }

        string parentPath = path.Parent is null ? "Computer" : location.DisplayOf(path.Parent);
        await _context.RefreshTreeAsync(parentPath);
    }

    private async Task RenameKeyCoreAsync(KeyLocation location, string newName)
    {
        RegistryPath path = location.Path;
        if (string.Equals(path.Components[^1], newName, StringComparison.Ordinal))
        {
            return;
        }

        string? error = await ValidateRenameKeyAsync(location, newName);
        if (error is not null)
        {
            await _context.ShowMessageAsync(Get("MsgCaptionRenameKeyError"), error);
            return;
        }

        RegistryPath target = path.Parent!.Append(newName);
        try
        {
            await _context.RunRegistryWriteAsync(() => location.Store.RenameKey(path, newName));
        }
        catch (RegistryOperationException ex)
        {
            await _context.ShowRegistryErrorAsync(ex, RegistryAction.RenameKey, RegeditContext.DefaultSubject(path));
            return;
        }

        await _context.RefreshTreeAsync(location.DisplayOf(target));
    }

    /// <summary>
    /// The text for a rename that cannot proceed. The classic rename texts name the old key, so the
    /// subject is the current leaf name. Null when the name is acceptable.
    /// </summary>
    private async Task<string?> ValidateRenameKeyAsync(KeyLocation location, string newName)
    {
        RegistryPath path = location.Path;
        if (string.Equals(path.Components[^1], newName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string subject = path.Components[^1];
        if (newName.Length == 0)
        {
            return Format("MsgRenameKeyEmpty", subject);
        }

        if (newName.Contains('\\', StringComparison.Ordinal))
        {
            return Format("MsgRenameKeyBackslash", subject);
        }

        if (newName.Length > MaxKeyNameLength)
        {
            return Format("MsgRenameKeyTooLong", subject);
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
            // The probe is a registry read, so it runs off the UI thread. A remote key makes a network call.
            bool exists = await _context.RunRegistryAsync(() => location.Reader.KeyExists(target));
            return exists ? Format("MsgRenameKeyExists", subject) : null;
        }
        catch (RegistryOperationException ex)
        {
            return ValidationFailure(ex, RegistryAction.RenameKey, subject);
        }
    }

    /// <summary>Logs the failure and returns the classic text for it, for a prompt to show.</summary>
    private string ValidationFailure(RegistryOperationException ex, RegistryAction action, string subject)
    {
        Log.Warn($"Registry Editor could not check a key name: {ex.Operation} for {ex.Path.ToDisplayString()}, Win32 {ex.Win32Error}: {ex.Message}");
        return _context.Messages.ForRegistryError(action, ex, subject).Text;
    }

    private string Format(string key, string subject) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), subject);

    private string NextUniqueKeyName(KeyLocation parent)
    {
        for (var index = 1; index < 10000; index += 1)
        {
            string candidate = MessageCatalog.NewKeyNamePrefix + index.ToString(CultureInfo.InvariantCulture);
            if (!parent.Reader.KeyExists(parent.Path.Append(candidate)))
            {
                return candidate;
            }
        }

        return MessageCatalog.NewKeyNamePrefix + Environment.TickCount64.ToString(CultureInfo.InvariantCulture);
    }
}
