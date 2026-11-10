using Bwc.UI.Hex;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Bwc.UI.Dialogs;

/// <summary>
/// Read-only binary value viewer. The dialog owns a snapshot of the bytes passed to
/// <see cref="SetValue"/> and never writes to the registry. Escape or Close dismisses it.
/// </summary>
public sealed class HexViewerDialog : ModalDialogWindow
{
    private readonly string _title;
    private readonly HexEditor _editor;
    private readonly TextBlock _status;

    public HexViewerDialog(string title, string closeText = "Close")
    {
        ArgumentException.ThrowIfNullOrEmpty(title);
        ArgumentException.ThrowIfNullOrEmpty(closeText);

        _title = title;
        Title = title;
        SetHeading(title);
        PrimaryText = closeText;
        SecondaryText = string.Empty;
        ShowSecondaryButton = false;
        SubmitsOnEnter = false;
        SetPreferredSize(720, 480);

        _editor = new HexEditor
        {
            Height = 260,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        _status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetAutomationId(_status, "HexViewerStatus");

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
        AutomationProperties.SetAutomationId(root, "HexViewerDialog");
        AutomationProperties.SetName(root, title);
        SetBody(root);
    }

    /// <summary>The editor inside the dialog. Exposed for the host and tests.</summary>
    public HexEditor Editor => _editor;

    /// <summary>
    /// Shows one value snapshot. The type name is optional; when present it appears in the status
    /// line next to the byte count and display name.
    /// </summary>
    public void SetValue(string displayName, string keyPath, byte[] data, string? typeName = null)
    {
        ArgumentNullException.ThrowIfNull(displayName);
        ArgumentNullException.ThrowIfNull(keyPath);
        ArgumentNullException.ThrowIfNull(data);

        Title = _title + ": " + displayName;
        SetHeading(_title + " - " + displayName);
        SetMessage(keyPath);
        _editor.SetData(data);

        string type = string.IsNullOrWhiteSpace(typeName) ? string.Empty : typeName + "    ";
        string status = data.Length + " bytes    " + type + displayName;
        _status.Text = status;
        AutomationProperties.SetName(_status, status);
    }

    protected override void OnDialogLoaded()
    {
        _editor.FocusEditor();
    }
}
