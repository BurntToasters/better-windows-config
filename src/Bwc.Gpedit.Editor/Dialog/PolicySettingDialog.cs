using System.Globalization;
using Bwc.Core;
using Bwc.Gpo.Comments;
using Bwc.Gpedit.Editor.Options;
using Bwc.Gpo.Pol;
using Bwc.Gpo.Store;
using Bwc.Policy.Admx;
using Bwc.Policy.Model;
using Bwc.UI;
using Bwc.UI.Dialogs;
using Bwc.UI.Icons;
using Bwc.UI.Theme;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;
using Windows.Graphics;
using WinRT.Interop;

namespace Bwc.Gpedit.Editor.Dialog;

/// <summary>
/// The policy-setting dialog of the Local Group Policy Editor (piece G3a): the frame of the classic WinForms Editor form. A
/// header row with the policy name and Previous Setting and Next Setting, the Not Configured, Enabled and Disabled radio
/// buttons, the Comment and Supported on boxes, an Options pane that piece G3b fills, the Help pane
/// and OK, Cancel and Apply.
/// It is resizable, laid out in DIPs from the form (client 684 x 604, minimum 684 x 601). Every rule,
/// and the ways it can fail,
/// is in docs/failure-modes/gpedit-dialog.md.
/// </summary>
/// <remarks>
/// The dialog reads the state and the comment through <see cref="IPolicyEditStore"/> and applies through the same seam,
/// which saves the edit and hands out the files as read back (piece G4). The save runs off the UI thread, because it can
/// wait for the write lock; the dialog is busy meanwhile and ignores every command. The dialog names no file.
/// </remarks>
public sealed class PolicySettingDialog : ClassicDialog
{
    // Control ids of the template. 1 and 2 are IDOK and IDCANCEL, which the dialog base maps to Accept and Cancel.
    private const int PreviousId = 100;
    private const int NextId = 101;
    private const int ApplyId = 102;
    private const int NotConfiguredId = 110;
    private const int EnabledId = 111;
    private const int DisabledId = 112;
    private const int CommentId = 120;
    private const int SupportedOnId = 121;
    private const int HelpId = 122;
    private const int StaticId = 65535;

    // The client size of the form in DIPs, and the smallest height (the minimum window height of the form less its frame).
    private const double ClientWidthDip = 684;
    private const double ClientHeightDip = 604;
    private const double MinimumClientHeightDip = 601;
    private const int CommentMaxLength = 4095;

    // The split container of the form: 301 | 5 | 372 DIP at the default size.
    private const double SplitterWidthDip = 5;
    private const double SplitPanel1Dip = 301;
    private const double SplitAvailableDip = 673;

    private readonly IPolicyEditStore _store;
    private readonly IReadOnlyList<PolicyRef> _order;
    private readonly TextBlock _title;
    private readonly Button _previous;
    private readonly Button _next;
    private readonly Button _apply;
    private readonly Button _ok;
    private readonly Button _cancel;
    private readonly RadioButton _notConfigured;
    private readonly RadioButton _enabled;
    private readonly RadioButton _disabled;
    private readonly TextBox _comment;
    private readonly TextBox _supportedOn;
    private readonly TextBox _help;
    private readonly FrameworkElement _commentLabel;
    private readonly FrameworkElement _supportedOnLabel;
    private readonly FrameworkElement _optionsLabel;
    private readonly FrameworkElement _helpLabel;
    private readonly TextBlock _readOnlyNotice;
    private readonly AutomationHost _icon;
    private readonly AutomationHost _optionsHost;
    private readonly Canvas _optionsPanel;
    private readonly ScrollViewer _optionsScroll;

    private int _index;
    private bool _loading;
    private bool _messageOpen;
    private bool _busy;
    private bool _closed;
    private bool _optionsDirty;
    private bool _externalDirty;
    private PolicyOptionsView? _options;
    private string _optionsBaseline = string.Empty;
    private double _optionsWidth;
    private double _optionsHeight;
    private int _applyCount;
    private PolicyStateSnapshot? _snapshot;
    private PolicyState _baseState;
    private string _baseComment = string.Empty;

    /// <summary>Creates the dialog for the policy at <paramref name="index"/> of <paramref name="order"/>.</summary>
    /// <param name="order">The policies of the shown list in display order,
    /// with their sides. Previous and Next move through it.</param>
    /// <param name="index">The position of the policy to show.</param>
    /// <param name="store">Where the state and comment are read and applied.</param>
    public PolicySettingDialog(IReadOnlyList<PolicyRef> order, int index, IPolicyEditStore store)
        : base(BuildTemplate(order, index))
    {
        _order = order;
        _index = index;
        _store = store ?? throw new ArgumentNullException(nameof(store));

        _title = RequireControl<TextBlock>("labelTitle");
        _previous = RequireControl<Button>("buttonPrevious");
        _next = RequireControl<Button>("buttonNext");
        _apply = RequireControl<Button>("buttonApply");
        _ok = RequireControl<Button>("buttonOK");
        _cancel = RequireControl<Button>("buttonCancel");
        _notConfigured = RequireControl<RadioButton>("radioButtonNotConfigured");
        _enabled = RequireControl<RadioButton>("radioButtonEnabled");
        _disabled = RequireControl<RadioButton>("radioButtonDisabled");
        _comment = RequireControl<TextBox>("textBoxComment");
        _supportedOn = RequireControl<TextBox>("labelSupportedOnText");
        _help = RequireControl<TextBox>("textBoxHelp");
        _commentLabel = RequireControl<FrameworkElement>("labelComment");
        _supportedOnLabel = RequireControl<FrameworkElement>("labelSupportedOn");
        _optionsLabel = RequireControl<FrameworkElement>("label2");
        _helpLabel = RequireControl<FrameworkElement>("label1");
        _readOnlyNotice = RequireControl<TextBlock>("DialogReadOnlyNotice");
        _readOnlyNotice.Visibility = Visibility.Collapsed;
        Closed += (_, _) => _closed = true;

        _title.TextTrimming = TextTrimming.CharacterEllipsis;
        _comment.MaxLength = CommentMaxLength;
        // The Supported on box is the form's read-only field: painted in the dialog face, not in the window color.
        _supportedOn.Background = BwcPaint.Face;

        // The two parts that are not template controls: the icon and the Options pane. They sit on the canvas of the dialog.
        _icon = new AutomationHost { ControlType = AutomationControlType.Image };
        AutomationProperties.SetAutomationId(_icon, "pictureBox");
        // Top left: a canvas shorter than the viewport would otherwise be centered in it.
        _optionsPanel = new Canvas { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        _optionsScroll = new ScrollViewer
        {
            Content = _optionsPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            IsTabStop = false,
        };
        _optionsHost = new AutomationHost
        {
            Content = _optionsScroll,
            // The pane is painted like the edit boxes (the window color, shared with the comment box) inside a thin border.
            Background = _comment.Background,
            BorderBrush = BwcPaint.Shadow,
            BorderThickness = new Thickness(1),
        };
        AutomationProperties.SetAutomationId(_optionsHost, "flowLayoutPanelDynamic");
        AutomationProperties.SetName(_optionsHost, DialogStrings.Get("DialogOptionsName"));
        Canvas? canvas = (Content as ContentControl)?.Content as Canvas ?? _ok.Parent as Canvas;
        if (canvas is null)
        {
            throw new InvalidOperationException("The dialog surface is not the canvas that the dialog base builds.");
        }

        canvas.Children.Add(_icon);
        canvas.Children.Add(_optionsHost);

        _notConfigured.Checked += OnStateChecked;
        _enabled.Checked += OnStateChecked;
        _disabled.Checked += OnStateChecked;
        _comment.TextChanged += OnCommentChanged;

        // The dialog handles Tab itself: the option controls come between Supported on and Help, and the base follows
        // the template.
        if (Content is UIElement surface)
        {
            surface.PreviewKeyDown += OnPreviewKeyDown;
        }

        LoadCurrent();
    }

    /// <summary>
    /// Raised after an Apply (or an OK, or a move that applied a
    /// pending change) succeeded, with the policy applied.
    /// </summary>
    public event EventHandler<PolicyRef>? Applied;

    /// <summary>The policy the dialog shows now (the last one shown, once the dialog has closed).</summary>
    public PolicyRef Shown => _order[_index];

    /// <summary>
    /// The pane that holds the option controls (a canvas: the options view places each control). The dialog fills it from the
    /// presentation of the policy and handles Tab for it, because the dialog base follows the template only (see
    /// docs/failure-modes/gpedit-options.md, section 14).
    /// </summary>
    public Panel OptionsPane => _optionsPanel;

    /// <summary>
    /// Supplies the element values of an Enabled edit. When null, the values of the Options pane are used (the stored values
    /// and the defaults, changed by the user).
    /// </summary>
    public Func<IReadOnlyDictionary<string, ElementValue>>? ElementValues { get; set; }

    /// <summary>Tells the dialog that an option control changed, so Apply is enabled (piece G3b).</summary>
    public void MarkOptionsDirty()
    {
        _externalDirty = true;
        _optionsDirty = true;
        UpdateButtons();
    }

    /// <summary>
    /// True when the dialog cannot change the policy: a read-only
    /// store, or a side whose file could not be read.
    /// </summary>
    public bool IsReadOnly => _store.IsReadOnly || _snapshot is null;

    /// <summary>True when the state, the comment or an option differs from what was last read or applied.</summary>
    public bool IsDirty => SelectedState != _baseState || NormalizeLineBreaks(_comment.Text) != _baseComment || _optionsDirty;

    private PolicyState SelectedState =>
        _enabled.IsChecked == true ? PolicyState.Enabled : _disabled.IsChecked == true ? PolicyState.Disabled : PolicyState.NotConfigured;

    // ---- the template --------------------------------------------------------------------------

    private static ClassicDialogDefinition BuildTemplate(IReadOnlyList<PolicyRef> order, int index)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (index < 0 || index >= order.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), "The policy index is outside the list.");
        }

        PolicyDefinition policy = order[index].Policy;
        string caption = string.IsNullOrEmpty(policy.DisplayName) ? policy.Name : policy.DisplayName;

        // The rectangles are placeholders in dialog units (the form is laid out in DIPs in OnClientResized). The order is the
        // tab order: Previous, Next, the radio buttons, Comment, Supported on, Help, OK, Cancel, Apply.
        ClassicControlDefinition[] controls =
        [
            PushButton(PreviousId, "buttonPrevious", DialogStrings.Get("DialogPrevious"), 374, 14, 102, 25),
            PushButton(NextId, "buttonNext", DialogStrings.Get("DialogNext"), 482, 14, 102, 25),
            RadioDef(NotConfiguredId, "radioButtonNotConfigured", DialogStrings.Get("DialogNotConfigured"), 9, 54, 108, 19),
            RadioDef(EnabledId, "radioButtonEnabled", DialogStrings.Get("DialogEnabled"), 9, 79, 67, 19),
            RadioDef(DisabledId, "radioButtonDisabled", DialogStrings.Get("DialogDisabled"), 9, 104, 70, 19),
            EditDef(CommentId, "textBoxComment", DialogStrings.Get("DialogCommentName"), 217, 54, 458, 72, readOnly: false),
            EditDef(SupportedOnId, "labelSupportedOnText", DialogStrings.Get("DialogSupportedOnName"), 217, 132, 458, 46, readOnly: true),
            EditDef(HelpId, "textBoxHelp", DialogStrings.Get("DialogHelpName"), 309, 227, 370, 329, readOnly: true),
            PushButton(1, "buttonOK", DialogStrings.Get("DialogOk"), 438, 567, 75, 25, isDefault: true),
            PushButton(2, "buttonCancel", DialogStrings.Get("DialogCancel"), 519, 567, 75, 25),
            PushButton(ApplyId, "buttonApply", DialogStrings.Get("DialogApply"), 600, 567, 75, 25, disabled: true),
            LabelDef("labelTitle", string.Empty, 28, 11, 340, 23, literal: true),
            LabelDef("labelComment", DialogStrings.Get("DialogCommentLabel"), 129, 51, 82, 78),
            LabelDef("labelSupportedOn", DialogStrings.Get("DialogSupportedOnLabel"), 129, 129, 82, 52),
            LabelDef("label2", DialogStrings.Get("DialogOptionsLabel"), 3, 190, 52, 15),
            LabelDef("label1", DialogStrings.Get("DialogHelpLabel"), 309, 190, 35, 15),
            LabelDef("DialogReadOnlyNotice", string.Empty, 9, 567, 420, 25, literal: true),
        ];
        return new ClassicDialogDefinition(
            caption,
            "WinForms Editor form of Microsoft.GroupPolicy.AdmTmplEditor",
            Dlu(ClientWidthDip, 4.0 / 6.0),
            Dlu(ClientHeightDip, 8.0 / 15.0),
            controls)
        {
            FontFace = "Segoe UI",
            FontPointSize = 9,
            Resizable = true,
        };
    }

    private static int Dlu(double dip, double factor) => (int)Math.Round(dip * factor);

    private static ClassicControlDefinition Def(ClassicControlKind kind, int id, string automationId, string text, double x, double y, double w, double h) =>
        new(kind, id, automationId, text, Dlu(x, 4.0 / 6.0), Dlu(y, 8.0 / 15.0), Dlu(w, 4.0 / 6.0), Dlu(h, 8.0 / 15.0));

    private static ClassicControlDefinition PushButton(int id, string automationId, string text, double x, double y, double w, double h, bool isDefault = false, bool disabled = false) =>
        Def(ClassicControlKind.Button, id, automationId, text, x, y, w, h) with { TabStop = true, IsDefault = isDefault, Disabled = disabled };

    private static ClassicControlDefinition RadioDef(int id, string automationId, string text, double x, double y, double w, double h) =>
        Def(ClassicControlKind.RadioButton, id, automationId, text, x, y, w, h) with { TabStop = true };

    private static ClassicControlDefinition EditDef(int id, string automationId, string name, double x, double y, double w, double h, bool readOnly) =>
        Def(ClassicControlKind.Edit, id, automationId, name, x, y, w, h) with { TabStop = true, Multiline = true, ReadOnly = readOnly };

    private static ClassicControlDefinition LabelDef(string automationId, string text, double x, double y, double w, double h, bool literal = false) =>
        Def(ClassicControlKind.Label, StaticId, automationId, text, x, y, w, h) with { LiteralText = literal };

    // ---- reading a policy ----------------------------------------------------------------------

    private void LoadCurrent()
    {
        PolicyRef current = _order[_index];
        PolicyDefinition policy = current.Policy;
        string name = string.IsNullOrEmpty(policy.DisplayName) ? policy.Name : policy.DisplayName;

        _loading = true;
        try
        {
            Title = name;
            _title.Text = name;
            AutomationProperties.SetName(_title, name);
            _supportedOn.Text = policy.SupportedOnText;
            _help.Text = policy.ExplainText;
            _help.Select(0, 0);

            _snapshot = ReadSnapshot(current);
            _baseState = _snapshot?.State ?? PolicyState.NotConfigured;
            _baseComment = ReadComment(current);
            _optionsDirty = false;
            _externalDirty = false;
            _notConfigured.IsChecked = _baseState == PolicyState.NotConfigured;
            _enabled.IsChecked = _baseState == PolicyState.Enabled;
            _disabled.IsChecked = _baseState == PolicyState.Disabled;
            _comment.Text = _baseComment;
        }
        finally
        {
            _loading = false;
        }

        bool locked = IsReadOnly;
        string notice = _store.IsReadOnly ? DialogStrings.Get("DialogReadOnlyNotice") : DialogStrings.Get("DialogUnreadableNotice");
        _readOnlyNotice.Text = locked ? notice : string.Empty;
        AutomationProperties.SetName(_readOnlyNotice, _readOnlyNotice.Text);
        _readOnlyNotice.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
        _notConfigured.IsEnabled = !locked;
        _enabled.IsEnabled = !locked;
        _disabled.IsEnabled = !locked;
        _comment.IsReadOnly = locked;
        RebuildOptions(policy);
        UpdateButtons();
    }

    /// <summary>Builds the Options pane of the policy that is shown now and loads the stored values or the defaults.</summary>
    private void RebuildOptions(PolicyDefinition policy)
    {
        var context = new OptionsContext
        {
            Create = CreateControl,
            Changed = OnOptionsChanged,
            OwnerHandle = () => WindowNative.GetWindowHandle(this),
        };
        _options = new PolicyOptionsView(policy, _optionsPanel, context);
        _options.Load(_snapshot);
        _options.SetEnabled(SelectedState == PolicyState.Enabled && !IsReadOnly);
        _optionsBaseline = _options.Fingerprint();

        // A check box or a drop-down reports its own change at once while it loads: that is not an edit.
        _optionsDirty = false;
        _ = _optionsScroll.ChangeView(0, 0, null, disableAnimation: true);
        ArrangeOptions();
    }

    /// <summary>
    /// An option control changed. The control may have changed its own text while the pane loaded (the text box reports that
    /// late), so Apply is enabled only when the values differ from what was loaded or last applied.
    /// </summary>
    private void OnOptionsChanged()
    {
        if (_options is null)
        {
            return;
        }

        _optionsDirty = _externalDirty || _options.Fingerprint() != _optionsBaseline;
        UpdateButtons();
    }

    private PolicyStateSnapshot? ReadSnapshot(PolicyRef target)
    {
        try
        {
            PolDocument? document = _store.PolFor(target.Side);
            return document is null ? null : PolicyStateReader.Read(target.Policy, document);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            UiDiagnostics.Warn?.Invoke($"The state of '{target.Policy.FullName}' could not be read: {ex.Message}");
            return null;
        }
    }

    private string ReadComment(PolicyRef target)
    {
        try
        {
            CmtxDocument? comments = _store.CommentsFor(target.Side);
            return NormalizeLineBreaks(comments?.GetComment(target.Policy.Namespace, target.Policy.Name) ?? string.Empty);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            UiDiagnostics.Warn?.Invoke($"The comment of '{target.Policy.FullName}' could not be read: {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>The box returns a lone carriage return for a line break, the comment file holds the Windows form.</summary>
    private static string NormalizeLineBreaks(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\n", "\r\n", StringComparison.Ordinal);

    // ---- state and buttons ---------------------------------------------------------------------

    private void OnStateChecked(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            _options?.SetEnabled(SelectedState == PolicyState.Enabled && !IsReadOnly);
            UpdateButtons();
        }
    }

    private void OnCommentChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading)
        {
            UpdateButtons();
        }
    }

    private void UpdateButtons()
    {
        if (_closed)
        {
            return;
        }

        _apply.IsEnabled = !IsReadOnly && IsDirty && !_busy;
        _previous.IsEnabled = _index > 0 && !_busy;
        _next.IsEnabled = _index < _order.Count - 1 && !_busy;
        _ok.IsEnabled = !_busy;
        _cancel.IsEnabled = !_busy;
        try
        {
            EnsureFocusIsUsable();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // A selection or text event of a control can arrive while the window closes; the closed window refuses its content.
            _closed = true;
            return;
        }

        UpdateReadout();
    }

    /// <summary>
    /// A button that has the focus and becomes disabled (Next on the
    /// last policy) hands the focus on, as a form does.
    /// </summary>
    private void EnsureFocusIsUsable()
    {
        if (Content is not FrameworkElement { XamlRoot: { } xamlRoot })
        {
            return;
        }

        if (FocusManager.GetFocusedElement(xamlRoot) is not Button { IsEnabled: false } disabled)
        {
            return;
        }

        Button[] candidates = ReferenceEquals(disabled, _next) ? [_previous, _ok] : ReferenceEquals(disabled, _previous) ? [_next, _ok] : [_ok];
        foreach (Button candidate in candidates)
        {
            if (candidate.IsEnabled && candidate.Focus(FocusState.Programmatic))
            {
                return;
            }
        }
    }

    // ---- commands ------------------------------------------------------------------------------

    /// <inheritdoc />
    protected override void OnCommand(int id, string automationId)
    {
        switch (id)
        {
            case PreviousId:
                MoveAsync(-1).Forget("policy setting previous");
                break;
            case NextId:
                MoveAsync(1).Forget("policy setting next");
                break;
            case ApplyId:
                ApplyAsync().Forget("policy setting apply");
                break;
            default:
                if (_busy)
                {
                    break;
                }

                if (_options?.TryRun(id) != true)
                {
                    base.OnCommand(id, automationId);
                }

                break;
        }
    }

    /// <summary>
    /// OK with nothing changed (or read-only) only closes. OK with a pending change starts the save and keeps the dialog
    /// open; the dialog closes when the save succeeded, and stays open with a message when it did not.
    /// </summary>
    protected override bool TryAccept()
    {
        if (_busy)
        {
            return false;
        }

        if (!IsDirty || IsReadOnly)
        {
            return true;
        }

        ApplyThenAcceptAsync().Forget("policy setting apply then accept");
        return false;
    }

    /// <summary>A close from the caption or Alt+F4 is refused while a save is running.</summary>
    protected override bool IsCloseAllowed() => !_busy;

    private async Task ApplyThenAcceptAsync()
    {
        if (await ApplyAsync())
        {
            // Nothing is pending now, so the base closes the dialog.
            Accept();
        }
    }

    private async Task MoveAsync(int delta)
    {
        int target = _index + delta;
        if (_busy || target < 0 || target >= _order.Count || !await CommitPendingAsync())
        {
            return;
        }

        _index = target;
        LoadCurrent();
    }

    /// <summary>Applies a pending change before the dialog moves to another policy (docs gpedit-dialog.md, OQ-D1).</summary>
    private async Task<bool> CommitPendingAsync() => IsReadOnly || !IsDirty || await ApplyAsync();

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UpdateButtons();
    }

    /// <summary>
    /// Saves the pending change. True when it is saved (also when the policy refresh failed after the write, which the dialog
    /// reports); false when nothing was written because of a problem or a failure, with the message shown and the change kept.
    /// </summary>
    private async Task<bool> ApplyAsync()
    {
        if (IsReadOnly || _busy)
        {
            return false;
        }

        PolicyRef target = _order[_index];
        PolicyState state = SelectedState;
        string comment = NormalizeLineBreaks(_comment.Text);

        // Only a comment changed: the state is not applied again, so its values and its order in the file stay as they are.
        PolicyEdit? edit = state == _baseState && !_optionsDirty ? null : BuildEdit(state);
        if (edit is { State: PolicyState.Enabled })
        {
            IReadOnlyList<PolicyEditProblem> problems = PolicyEditValidator.Validate(target.Policy, edit);
            if (problems.Count > 0)
            {
                ShowProblem(target.Policy, problems[0]);
                return false;
            }
        }

        PolicyApplyOutcome outcome;
        SetBusy(true);
        try
        {
            // The save can wait for the write lock for seconds, so it never runs on the thread of the window.
            outcome = await Task.Run(() => _store.Apply(target, edit, comment));
        }
        catch (GpoStoreException ex)
        {
            SetBusy(false);
            UiDiagnostics.Warn?.Invoke($"The setting '{target.Policy.FullName}' was not saved ({ex.Reason}): {ex.Message}");
            SaveErrorMessage message = SaveErrorMessages.For(ex);
            ShowMessage(DialogStrings.Get("DialogMessageCaption"), message.Text, textId: message.AutomationId);
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SetBusy(false);
            UiDiagnostics.Warn?.Invoke($"The setting '{target.Policy.FullName}' could not be applied: {ex.Message}");
            ShowMessage(DialogStrings.Get("DialogMessageCaption"), DialogStrings.Get("DialogFailedText"));
            return false;
        }

        SetBusy(false);
        if (!outcome.IsApplied)
        {
            ShowProblem(target.Policy, outcome.Problems[0]);
            return false;
        }

        _snapshot = ReadSnapshot(target) ?? _snapshot;
        _baseState = state;
        _baseComment = comment;
        _options?.Rebase(_snapshot);
        _optionsBaseline = _options?.Fingerprint() ?? string.Empty;
        _optionsDirty = false;
        _externalDirty = false;
        _applyCount++;
        UpdateButtons();
        Applied?.Invoke(this, target);
        if (outcome.RefreshFailure is not null)
        {
            // The files are on disk and the change is applied; only the policy refresh failed. The message is a notice.
            UiDiagnostics.Warn?.Invoke($"The policy refresh after saving '{target.Policy.FullName}' failed: {outcome.RefreshFailure.Message}");
            SaveErrorMessage notice = SaveErrorMessages.ForRefresh();
            await ShowMessageAndWaitAsync(DialogStrings.Get("DialogMessageCaption"), notice.Text, null, notice.AutomationId);
        }

        return true;
    }

    private PolicyEdit BuildEdit(PolicyState state)
    {
        switch (state)
        {
            case PolicyState.Disabled:
                return PolicyEdit.Disabled;
            case PolicyState.NotConfigured:
                return PolicyEdit.NotConfigured;
        }

        return PolicyEdit.EnabledWith(ElementValues is { } provider ? provider() : ReadOptionValues());
    }

    /// <summary>
    /// The values of the Options pane: what Apply writes. An element the pane has no control for keeps the value that was read
    /// for it.
    /// </summary>
    private IReadOnlyDictionary<string, ElementValue> ReadOptionValues() =>
        _options?.ReadValues() ?? new Dictionary<string, ElementValue>();

    // ---- messages ------------------------------------------------------------------------------

    private void ShowProblem(PolicyDefinition policy, PolicyEditProblem problem)
    {
        if (_options?.Describe(problem) is { } message)
        {
            ShowMessage(message.Caption, message.Text, message.AfterClose);
            return;
        }

        if (problem.Kind == PolicyProblemKind.Required)
        {
            string caption = string.Format(CultureInfo.CurrentCulture, DialogStrings.Get("DialogRequiredCaption"), FieldLabel(policy, problem.ElementId));
            ShowMessage(caption, DialogStrings.Get("DialogRequiredText"));
        }
        else
        {
            ShowMessage(DialogStrings.Get("DialogMessageCaption"), DialogStrings.Get("DialogProblemText"));
        }
    }

    /// <summary>The label that the presentation gives an element, without its colon; the element id when it has none.</summary>
    private static string FieldLabel(PolicyDefinition policy, string elementId)
    {
        string? label = policy.Presentation?.Controls
            .FirstOrDefault(c => string.Equals(c.RefId, elementId, StringComparison.OrdinalIgnoreCase))?.Label;
        return string.IsNullOrWhiteSpace(label) ? elementId : label.Trim().TrimEnd(':').TrimEnd();
    }

    private void ShowMessage(string caption, string text, Action? afterClose = null, string? textId = null)
    {
        ShowMessageAndWaitAsync(caption, text, afterClose, textId).Forget("policy setting message");
    }

    /// <summary>Shows a message box and completes when it has been closed. A message that is already open wins.</summary>
    private Task ShowMessageAndWaitAsync(string caption, string text, Action? afterClose, string? textId)
    {
        if (_messageOpen)
        {
            return Task.CompletedTask;
        }

        _messageOpen = true;
        return ShowMessageAsync(caption, text, afterClose, textId);
    }

    private async Task ShowMessageAsync(string caption, string text, Action? afterClose, string? textId)
    {
        try
        {
            var box = new ClassicMessageBox(
                caption,
                text,
                ClassicMessageIcon.Warning,
                ClassicMessageButtons.Ok,
                textAutomationId: textId ?? "DialogMessage");
            _ = await box.ShowAsync(WindowNative.GetWindowHandle(this));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            UiDiagnostics.Warn?.Invoke("A message of the setting dialog could not be shown: " + ex.Message);
        }
        finally
        {
            _messageOpen = false;
            if (afterClose is not null)
            {
                // After the message box has gone and the dialog is active again, so the focus stays where it is put.
                _ = DispatcherQueue.TryEnqueue(() => afterClose());
            }
        }
    }

    // ---- size and layout -----------------------------------------------------------------------

    /// <inheritdoc />
    protected override (int Width, int Height) MinimumClientSize(uint dpi)
    {
        double scale = (dpi == 0 ? 96u : dpi) / 96.0;
        return ((int)Math.Round(ClientWidthDip * scale), (int)Math.Round(MinimumClientHeightDip * scale));
    }

    /// <inheritdoc />
    protected override void ApplySizeAndPosition(nint ownerHwnd)
    {
        base.ApplySizeAndPosition(ownerHwnd);

        // The base sizes the client from the dialog units; the form is 684 x 604 DIP exactly, at every DPI.
        double scale = Metrics.Scale;
        AppWindow.ResizeClient(new SizeInt32((int)Math.Round(ClientWidthDip * scale), (int)Math.Round(ClientHeightDip * scale)));
        DialogPlacement.CenterOnOwner(AppWindow, ownerHwnd);
    }

    /// <inheritdoc />
    protected override void OnDialogLoaded()
    {
        base.OnDialogLoaded();
        try
        {
            _icon.Content = new Image
            {
                Source = IconProvider.Get(IconReference.FromModule(ModuleIcon.ById("admtmpl.dll", 4), IconKind.StringValue), Metrics.Scale),
                Width = 16,
                Height = 16,
                Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            UiDiagnostics.Warn?.Invoke("The icon of the setting dialog could not be loaded: " + ex.Message);
        }

        if (Content is FrameworkElement { ActualWidth: > 0, ActualHeight: > 0 } root)
        {
            Arrange(root.ActualWidth, root.ActualHeight);
        }
    }

    /// <inheritdoc />
    protected override void OnClientResized(double widthDip, double heightDip) => Arrange(widthDip, heightDip);

    /// <summary>
    /// Places every control from the form (docs/failure-modes/gpedit-dialog.md, section 4): the header and the radio column are
    /// fixed, the Comment and Supported on boxes stretch to the right edge, the split container takes the
    /// rest of the height with
    /// its 301 : 5 : 372 proportion, and the three buttons keep their distance from the bottom right corner.
    /// </summary>
    private void Arrange(double width, double height)
    {
        width = Math.Max(width, 1);
        height = Math.Max(height, 1);

        Place(_icon, 6, 14, 16, 16);
        Place(_title, 28, 11, 340, 23);
        Place(_previous, 374, 14, 102, 25);
        Place(_next, 482, 14, 102, 25);
        Place(_notConfigured, 9, 54, 108, 19);
        Place(_enabled, 9, 79, 67, 19);
        Place(_disabled, 9, 104, 70, 19);
        Place(_commentLabel, 129, 51, 82, 78);
        Place(_supportedOnLabel, 129, 129, 82, 52);

        double boxWidth = width - 226;
        Place(_comment, 217, 54, boxWidth, 72);
        Place(_supportedOn, 217, 132, boxWidth, 46);

        double splitTop = 190;
        double splitHeight = height - 236;
        double available = width - 6 - SplitterWidthDip;
        double panel1 = Math.Round(available * SplitPanel1Dip / SplitAvailableDip);
        double panel2 = available - panel1;
        double paneHeight = splitHeight - 39;
        Place(_optionsLabel, 3, splitTop, 52, 15);
        Place(_optionsHost, 3, splitTop + 37, panel1 - 3, paneHeight);
        _optionsWidth = Math.Max(1, panel1 - 3 - 2);
        _optionsHeight = Math.Max(1, paneHeight - 2);
        ArrangeOptions();
        double helpLeft = 3 + panel1 + SplitterWidthDip;
        Place(_helpLabel, helpLeft, splitTop, 35, 15);
        Place(_help, helpLeft, splitTop + 37, panel2 - 2, paneHeight);

        double buttonTop = height - 37;
        Place(_readOnlyNotice, 9, buttonTop, Math.Max(40, width - 246 - 9 - 8), 25);
        Place(_ok, width - 246, buttonTop, 75, 25);
        Place(_cancel, width - 165, buttonTop, 75, 25);
        Place(_apply, width - 84, buttonTop, 75, 25);
    }

    /// <summary>Lays the option controls out in the viewport of the pane (inside its 1 DIP border).</summary>
    private void ArrangeOptions()
    {
        if (_options is not null && _optionsWidth > 0)
        {
            _options.Layout(_optionsWidth, _optionsHeight);
        }
    }

    private static void Place(FrameworkElement element, double x, double y, double width, double height)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        element.Width = Math.Max(1, width);
        element.Height = Math.Max(1, height);
    }

    // ---- Tab order -----------------------------------------------------------------------------

    /// <summary>
    /// Tab and Shift+Tab. The order is the one of the form: Previous, Next, the radio buttons, Comment, Supported on,
    /// the option
    /// controls of the pane, Help, OK, Cancel, Apply. A disabled control is skipped, so the option controls are passed over
    /// while the policy is not Enabled. The base handles every other key.
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (Content is not FrameworkElement { XamlRoot: { } xamlRoot })
        {
            return;
        }

        // While a save runs the dialog takes no key: Escape would close it under the save.
        if (_busy)
        {
            e.Handled = true;
            return;
        }

        // Enter and Escape in a combo box whose list is closed are the keys of the dialog (OK and Cancel, as in classic). The
        // combo box would handle them itself and the dialog base never sees them.
        if (e.Key is VirtualKey.Enter or VirtualKey.Escape && !IsDown(VirtualKey.Menu) && InsideClosedCombo(FocusManager.GetFocusedElement(xamlRoot)))
        {
            e.Handled = true;
            OnCommand(e.Key == VirtualKey.Enter ? 1 : 2, e.Key == VirtualKey.Enter ? "buttonOK" : "buttonCancel");
            return;
        }

        if (e.Key != VirtualKey.Tab || IsDown(VirtualKey.Control) || IsDown(VirtualKey.Menu))
        {
            return;
        }

        List<(FrameworkElement Owner, FrameworkElement Target)> stops = [];
        void Add(FrameworkElement element)
        {
            if (element is Control { IsEnabled: true } && element.Visibility == Visibility.Visible)
            {
                stops.Add((element, element));
            }
        }

        foreach (FrameworkElement element in new FrameworkElement[] { _previous, _next, _notConfigured, _enabled, _disabled, _comment, _supportedOn })
        {
            Add(element);
        }

        if (_options is not null)
        {
            foreach ((FrameworkElement owner, FrameworkElement target) in _options.TabStops())
            {
                if (owner is Control { IsEnabled: true })
                {
                    stops.Add((owner, target));
                }
            }
        }

        foreach (FrameworkElement element in new FrameworkElement[] { _help, _ok, _cancel, _apply })
        {
            Add(element);
        }

        if (stops.Count == 0)
        {
            return;
        }

        bool backward = IsDown(VirtualKey.Shift);
        object? focused = FocusManager.GetFocusedElement(xamlRoot);
        int current = stops.FindIndex(stop => ReferenceEquals(focused, stop.Owner) || ReferenceEquals(focused, stop.Target) || IsInside(focused, stop.Owner));
        int next = current < 0
            ? (backward ? stops.Count - 1 : 0)
            : (current + (backward ? -1 : 1) + stops.Count) % stops.Count;
        e.Handled = true;
        _ = stops[next].Target.Focus(FocusState.Keyboard);
    }

    private static bool InsideClosedCombo(object? focused)
    {
        for (DependencyObject? node = focused as DependencyObject; node is not null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is ComboBox combo)
            {
                return !combo.IsDropDownOpen;
            }
        }

        return false;
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private static bool IsInside(object? focused, FrameworkElement owner)
    {
        for (DependencyObject? node = focused as DependencyObject; node is not null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, owner))
            {
                return true;
            }
        }

        return false;
    }

    // ---- E2E readout ---------------------------------------------------------------------------

    /// <summary>
    /// E2E flavor only: publishes the state of the dialog as the UIA help text of DialogRoot (docs/failure-modes/
    /// gpedit-dialog.md). A screen reader would announce it, so the ship flavor never sets it.
    /// </summary>
    private void UpdateReadout()
    {
#if BWC_E2E
        if (Content is not FrameworkElement root)
        {
            return;
        }

        PolicyRef current = _order[_index];
        string state = SelectedState.ToString();
        string text =
            $"Policy={current.Policy.FullName};Side={current.Side};State={state};Dirty={(IsDirty ? 1 : 0)};" +
            $"Index={_index};Count={_order.Count};ReadOnly={(IsReadOnly ? 1 : 0)};Applies={_applyCount};";
        AutomationProperties.SetHelpText(root, text.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal));
        AutomationProperties.SetHelpText(_optionsHost, _options?.Readout() ?? "Enabled=0;");
#endif
    }

    /// <summary>
    /// A panel with one child that UI Automation exposes (with its
    /// rectangle), as the icon and the Options pane need.
    /// </summary>
    private sealed class AutomationHost : Grid
    {
        /// <summary>The one child of the host.</summary>
        internal UIElement? Content
        {
            set
            {
                Children.Clear();
                if (value is not null)
                {
                    Children.Add(value);
                }
            }
        }

        /// <summary>The UI Automation control type of the host.</summary>
        internal AutomationControlType ControlType { get; set; } = AutomationControlType.Pane;

        protected override AutomationPeer OnCreateAutomationPeer() => new HostPeer(this);

        /// <summary>
        /// A panel is not in the control view of UI Automation by default; the host says it is, so the pane and the icon can be
        /// found and measured.
        /// </summary>
        private sealed class HostPeer : FrameworkElementAutomationPeer
        {
            private readonly AutomationHost _host;

            internal HostPeer(AutomationHost host)
                : base(host)
            {
                _host = host;
            }

            protected override bool IsControlElementCore() => true;

            protected override bool IsContentElementCore() => true;

            protected override AutomationControlType GetAutomationControlTypeCore() => _host.ControlType;
        }
    }
}
