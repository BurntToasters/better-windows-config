using System.Globalization;
using Bwc.Core;
using Bwc.Registry;
using Bwc.UI.Dialogs;
using Bwc.UI.Tree;
using Windows.ApplicationModel.DataTransfer;

namespace Bwc.Regedit.Controllers;

/// <summary>Key commands: New Key, Delete Key, Rename Key, and Copy Key Name.</summary>
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

    public void RenameKeyInteractive() => _context.StartCommand(RenameKeyInteractiveAsync);

    /// <summary>Applies an inline rename that the tree committed for one of its nodes.</summary>
    public void RenameFromTree(TreeNode node, string newName)
    {
        _context.StartCommand(async () =>
        {
            if (node.RegistryPath is not { Length: > 0 } displayPath ||
                !RegistryPath.TryParse(displayPath, out RegistryPath? parsed) ||
                parsed is null)
            {
                await _context.ShowMessageAsync(Get("DialogNoSelection"));
                return;
            }

            RegistryPath path = parsed;

            if (path.IsRoot)
            {
                await _context.ShowMessageAsync(Get("DialogRootKeyLocked"));
                return;
            }

            await RenameKeyCoreAsync(path, newName);
        });
    }

    public void CopySelectedKeyName()
    {
        if (!_context.TryGetSelectedKeyPath(out RegistryPath key))
        {
            return;
        }

        try
        {
            var package = new DataPackage();
            package.SetText(key.ToDisplayString());
            Clipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            // Clipboard access is environmental. The selection and menu stay usable.
            Log.Warn("Registry Editor could not copy the selected key name: " + ex.Message);
        }
    }

    private async Task NewKeyAsync()
    {
        if (!_context.TryGetSelectedKeyPath(out RegistryPath parent))
        {
            await _context.ShowMessageAsync(Get("DialogNoSelection"));
            return;
        }

        string suggested = NextUniqueKeyName(parent);
        var dialog = new TextPromptDialog(
            Get("DialogNewKeyTitle"),
            Get("DialogKeyNameLabel"),
            suggested,
            value => ValidateNewKeyName(parent, value));

        if (!await _context.ShowDialogAsync(dialog))
        {
            return;
        }

        RegistryPath created = parent.Append(dialog.Value);
        try
        {
            _context.Store.CreateKey(created);
        }
        catch (RegistryOperationException ex)
        {
            await _context.ShowRegistryErrorAsync(ex, RegistryAction.CreateKey, RegeditContext.DefaultSubject(created));
            return;
        }

        await _context.RefreshTreeAsync(created.ToDisplayString());
    }

    private async Task DeleteKeyAsync()
    {
        if (!_context.TryGetSelectedKeyPath(out RegistryPath path))
        {
            await _context.ShowMessageAsync(Get("DialogNoSelection"));
            return;
        }

        if (path.IsRoot)
        {
            await _context.ShowMessageAsync(Get("DialogRootKeyLocked"));
            return;
        }

        // The classic confirmation text is the same for every key, with or without subkeys. The
        // subkey count is still read so that a key which cannot be read is reported before the prompt.
        try
        {
            _ = _context.Store.Reader.GetKeyInfo(path);
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
            await _context.RunRegistryWriteAsync(() => _context.Store.DeleteKey(path, recursive: true));
        }
        catch (RegistryOperationException ex)
        {
            await _context.ShowRegistryErrorAsync(ex, RegistryAction.DeleteKey, RegeditContext.DefaultSubject(path));
            return;
        }

        string? parentPath = path.Parent?.ToDisplayString() ?? "Computer";
        await _context.RefreshTreeAsync(parentPath);
    }

    private async Task RenameKeyInteractiveAsync()
    {
        if (!_context.TryGetSelectedKeyPath(out RegistryPath path))
        {
            await _context.ShowMessageAsync(Get("DialogNoSelection"));
            return;
        }

        if (path.IsRoot)
        {
            await _context.ShowMessageAsync(Get("DialogRootKeyLocked"));
            return;
        }

        string currentName = path.Components[^1];
        var dialog = new TextPromptDialog(
            Get("DialogRenameKeyTitle"),
            Get("DialogKeyNameLabel"),
            currentName,
            value => ValidateRenameKey(path, value));

        if (!await _context.ShowDialogAsync(dialog))
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
            await _context.ShowMessageAsync(Get("MsgCaptionRenameKeyError"), error);
            return;
        }

        RegistryPath target = path.Parent!.Append(newName);
        try
        {
            await _context.RunRegistryWriteAsync(() => _context.Store.RenameKey(path, newName));
        }
        catch (RegistryOperationException ex)
        {
            await _context.ShowRegistryErrorAsync(ex, RegistryAction.RenameKey, RegeditContext.DefaultSubject(path));
            return;
        }

        await _context.RefreshTreeAsync(target.ToDisplayString());
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
            return _context.Store.Reader.KeyExists(child) ? Get("DialogNameExists") : null;
        }
        catch (RegistryOperationException ex)
        {
            return ValidationFailure(ex, RegistryAction.OpenKey, RegeditContext.DefaultSubject(parent));
        }
    }

    /// <summary>
    /// The text for a rename that cannot proceed. The classic rename texts name the old key, so the
    /// subject is the current leaf name. Null when the name is acceptable.
    /// </summary>
    private string? ValidateRenameKey(RegistryPath path, string newName)
    {
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
            return _context.Store.Reader.KeyExists(target) ? Format("MsgRenameKeyExists", subject) : null;
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

    private string NextUniqueKeyName(RegistryPath parent)
    {
        for (var index = 1; index < 10000; index += 1)
        {
            string candidate = MessageCatalog.NewKeyNamePrefix + index.ToString(CultureInfo.InvariantCulture);
            if (!_context.Store.Reader.KeyExists(parent.Append(candidate)))
            {
                return candidate;
            }
        }

        return MessageCatalog.NewKeyNamePrefix + Environment.TickCount64.ToString(CultureInfo.InvariantCulture);
    }
}
