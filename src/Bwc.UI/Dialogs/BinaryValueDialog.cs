using Bwc.UI.Hex;

namespace Bwc.UI.Dialogs;

/// <summary>
/// Edit Binary Value for a new REG_BINARY value. The classic HEX control starts with the bytes
/// parsed from <paramref name="initialText"/> (hex pairs, separated by spaces or commas), or empty
/// when the text does not parse. The value name is read-only and empty for a new value.
/// </summary>
public sealed class BinaryValueDialog : ClassicDialog
{
    private readonly HexEditor _editor;

    public BinaryValueDialog(string title, string initialText = "")
        : base(ClassicDialogTemplates.EditBinary(HexEditorDialog.ClassicCaption))
    {
        ArgumentException.ThrowIfNullOrEmpty(title);

        _editor = RequireControl<HexEditor>("HexEditor");
        _editor.IsReadOnly = false;

        byte[] initial = ValueTextParser.TryParseBinary(initialText ?? string.Empty, out byte[] parsed, out _)
            ? parsed
            : Array.Empty<byte>();
        _editor.SetData(initial);
    }

    /// <summary>The bytes after a successful primary action.</summary>
    public byte[] Value { get; private set; } = Array.Empty<byte>();

    protected override bool TryAccept()
    {
        Value = _editor.GetBytes();
        return true;
    }

    protected override void OnDialogLoaded()
    {
        _editor.FocusEditor();
    }
}
