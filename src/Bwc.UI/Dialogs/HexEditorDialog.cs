using Bwc.UI.Hex;
using Microsoft.UI.Xaml.Controls;

namespace Bwc.UI.Dialogs;

/// <summary>
/// Edit Binary Value (RT_DIALOG 103). The host passes a snapshot, the user edits it in the classic
/// HEX control, and the host writes <see cref="Data"/> back only when
/// <see cref="ModalDialogWindow.Accepted"/> is true. The value name is read-only.
/// </summary>
public sealed class HexEditorDialog : ClassicDialog
{
    private readonly HexEditor _editor;
    private readonly TextBox _valueName;

    /// <summary>The classic caption of RT_DIALOG 103. The caller's title is not shown in the classic layout.</summary>
    internal const string ClassicCaption = "Edit Binary Value";

    public HexEditorDialog(string title)
        : base(ClassicDialogTemplates.EditBinary(ClassicCaption))
    {
        ArgumentException.ThrowIfNullOrEmpty(title);

        _editor = RequireControl<HexEditor>("HexEditor");
        _editor.IsReadOnly = false;
        _valueName = RequireControl<TextBox>("ValueNameInput");
    }

    /// <summary>The editor inside the dialog. Exposed for the host and tests.</summary>
    public HexEditor Editor => _editor;

    /// <summary>The edited bytes. Only meaningful after the dialog accepted.</summary>
    public byte[] Data => _editor.GetBytes();

    /// <summary>
    /// Shows one value snapshot for editing. The value name goes in the read-only name box. The key
    /// path and the type name are accepted for the callers; the classic layout shows neither.
    /// </summary>
    public void SetValue(string displayName, string keyPath, byte[] data, string? typeName = null)
    {
        ArgumentNullException.ThrowIfNull(displayName);
        ArgumentNullException.ThrowIfNull(keyPath);
        ArgumentNullException.ThrowIfNull(data);

        _valueName.Text = displayName;
        _editor.IsReadOnly = false;
        _editor.SetData(data);
    }

    protected override void OnDialogLoaded()
    {
        _editor.FocusEditor();
    }
}
