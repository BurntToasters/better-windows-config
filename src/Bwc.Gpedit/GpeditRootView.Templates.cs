using System.Globalization;
using Bwc.Core;
using Bwc.Gpedit.Policy;
using Bwc.Gpedit.Templates;
using Bwc.Gpedit.Tree;
using Bwc.UI.Menu;
using Bwc.UI.Tree;
using Microsoft.UI;

namespace Bwc.Gpedit;

/// <summary>
/// Add/Remove Templates and the template load errors (piece G6, docs/failure-modes/gpedit-templates.md): the command at the
/// Administrative Templates node, the dialog, the one reload after a change, and the status bar notice with the number of
/// template problems (the dialog lists them). The dialog itself is <see cref="GpeditTemplatesDialog"/>; the engine is
/// <c>Bwc.Policy.Store.TemplateImport</c>.
/// </summary>
public sealed partial class GpeditRootView
{
    private const int AddRemoveTemplatesCommand = 2007;
    private const string CommandAddRemoveTemplates = "AddRemoveTemplates";

    private GpeditTemplatesDialog? _templatesDialog;
    private bool _templatesOpening;
    private bool _reloadFromTemplates;
    private int _templateReloads;

    /// <summary>True at the Administrative Templates node of either side: the only place the command exists.</summary>
    private static bool NodeHasTemplates(TreeNode? node) =>
        GpeditTreeSource.InfoOf(node) is { Kind: GpeditNodeKind.AdministrativeTemplates };

    private bool TemplatesCommandAllowed =>
        _model is not null && ModalCommandsAllowed && _policyDialog is null && _filterDialog is null && _templatesDialog is null && !_templatesOpening;

    /// <summary>The Add/Remove Templates entry, at the front of the Action popup and of the tasks submenu.</summary>
    private List<ClassicCommandItem> TemplateItems() =>
    [
        ClassicMenuBuilder.Command(GpeditStrings.Commands("GpeditCommandAddRemoveTemplates"), AddRemoveTemplatesCommand, _model is not null, invoke: RunAddRemoveTemplates),
    ];

    private void RunAddRemoveTemplates()
    {
        if (!TemplatesCommandAllowed || !NodeHasTemplates(Shell.TreeContent.SelectedNode) || !TryReserveModal())
        {
            return;
        }

        _templatesOpening = true;
        RecordCommand(CommandAddRemoveTemplates);
        _ = RunAddRemoveTemplatesAsync();
    }

    /// <summary>
    /// Reads the store, opens the dialog and, when it closes after a change, starts one reload. The list is read before the
    /// dialog shows, so the dialog opens with its rows in place. A failure to open the dialog is logged and changes nothing.
    /// </summary>
    private async Task RunAddRemoveTemplatesAsync()
    {
        GpeditTemplatesDialog? dialog = null;
        try
        {
            if (_closed || XamlRoot?.ContentIslandEnvironment is not { } environment || _model is not { } model)
            {
                return;
            }

            var store = new GpeditTemplateStore();
            IReadOnlyList<TemplateRowInfo> rows;
            try
            {
                rows = await Task.Run(store.ReadRows);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A store that cannot be listed (a folder that vanished) gives an empty list; the problem list tells the rest.
                Log.Warn("The import store could not be listed: " + ex.Message);
                rows = [];
            }

            if (_closed)
            {
                return;
            }

            dialog = new GpeditTemplatesDialog(store, rows, model.Problems);
            _templatesDialog = dialog;
            StateChanged();
            _ = await dialog.ShowAsync(Win32Interop.GetWindowFromWindowId(environment.AppWindowId));
            await dialog.PendingAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn("The Add/Remove Templates dialog failed: " + ex.Message);
        }
        finally
        {
            _templatesOpening = false;
            _templatesDialog = null;
            try
            {
                if (dialog is { Changed: true } && !_closed)
                {
                    ReloadAfterTemplates();
                }

                if (!_closed)
                {
                    StateChanged();
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Warn("The window could not be updated after the Add/Remove Templates dialog closed: " + ex.Message);
            }

            ReleaseModal();
        }
    }

    /// <summary>
    /// The reload that closing the dialog after a change starts: the same load as Refresh, counted apart from it
    /// (<c>TemplateReloads</c>). Refresh keeps the node, the expansion and the row.
    /// </summary>
    private void ReloadAfterTemplates()
    {
        _templateReloads++;
        _reloadFromTemplates = true;
        RequestReload();
        UpdateReadout();
    }

    /// <summary>
    /// The status bar notice: the number of template problems of the model (a template that did not load, an import store that
    /// cannot be used), present only while there is at least one. The pane is set when a load finishes and is not an event, so
    /// a node selection does not change it.
    /// </summary>
    private void UpdateTemplateNotice(GpeditModel model)
    {
        int count = model.Problems.Count(IsTemplateProblem);
        Shell.StatusBar.NoticeText = count == 0
            ? string.Empty
            : string.Format(CultureInfo.CurrentCulture, GpeditStrings.Templates("TemplatesNotice"), count);
    }

    private static bool IsTemplateProblem(GpeditProblem problem) =>
        problem.Kind is GpeditProblemKind.TemplateFailure or GpeditProblemKind.TemplateStore or GpeditProblemKind.LoadException;
}
