using Bwc.UI.Hex;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Bwc.UI.Dialogs;

/// <summary>
/// Editable binary value dialog. The host passes a snapshot, the user edits it in a virtualized
/// <see cref="HexEditor"/>, and the host writes <see cref="Data"/> back only when
/// <see cref="ModalDialogWindow.Accepted"/> is true.
/// </summary>
public sealed class HexEditorDialog : ModalDialogWindow
{
    private readonly string _title;
    private readonly HexEditor _editor;
    private readonly TextBlock _status;
    private string _displayName = string.Empty;
    private string _typeName = string.Empty;

    public HexEditorDialog(string title)
    {
        ArgumentException.ThrowIfNullOrEmpty(title);

        _title = title;
        Title = title;
        SetHeading(title);
        PrimaryText = "OK";
        SecondaryText = "Cancel";
        ShowSecondaryButton = true;
        SubmitsOnEnter = false;
        SetPreferredSize(760, 520);

        _editor = new HexEditor
        {
            Height = 340,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsReadOnly = false,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        _editor.DataChanged += OnEditorStateChanged;
        _editor.SelectionChanged += OnEditorStateChanged;
        _editor.ModeChanged += OnEditorStateChanged;

        _status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetAutomationId(_status, "HexStatus");

        var grid = new Grid
        {
            RowSpacing = 6,
        };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetRow(_editor, 0);
        grid.Children.Add(_editor);

        Grid.SetRow(_status, 1);
        grid.Children.Add(_status);

        var root = new DialogRootControl
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Content = grid,
        };
        AutomationProperties.SetAutomationId(root, "HexEditorDialog");
        AutomationProperties.SetName(root, title);
        SetBody(root);
    }

    /// <summary>The editor inside the dialog. Exposed for the host and tests.</summary>
    public HexEditor Editor => _editor;

    /// <summary>The edited bytes. Only meaningful after the dialog accepted.</summary>
    public byte[] Data => _editor.GetBytes();

    /// <summary>
    /// Shows one value snapshot for editing. The type name is optional; when present it appears in
    /// the status line next to the byte count and display name.
    /// </summary>
    public void SetValue(string displayName, string keyPath, byte[] data, string? typeName = null)
    {
        ArgumentNullException.ThrowIfNull(displayName);
        ArgumentNullException.ThrowIfNull(keyPath);
        ArgumentNullException.ThrowIfNull(data);

        _displayName = displayName;
        _typeName = typeName ?? string.Empty;
        _editor.IsReadOnly = false;

        Title = _title + ": " + displayName;
        SetHeading(_title + " - " + displayName);
        SetMessage(keyPath);
        _editor.SetData(data);
        UpdateStatus();
    }

    protected override void OnDialogLoaded()
    {
        _editor.FocusEditor();
    }

    private void OnEditorStateChanged(object? sender, EventArgs e) => UpdateStatus();

    private void UpdateStatus()
    {
        string type = string.IsNullOrWhiteSpace(_typeName) ? string.Empty : _typeName + "    ";
        string status =
            _editor.Length + " bytes    " + type + _displayName + "    " + _editor.Mode;
        _status.Text = status;
        AutomationProperties.SetName(_status, status);
    }
}
