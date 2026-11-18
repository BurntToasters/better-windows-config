using Bwc.Gpedit.Editor.Dialog;
using Bwc.UI;
using Bwc.UI.Dialogs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinRT.Interop;

namespace Bwc.Gpedit.Editor.Options;

/// <summary>
/// The "Show Contents" dialog of a list element (the ListBoxDialog form of the classic editor): a label, an editable
/// grid of
/// one column (Value) or two (Value name, Value), OK and Cancel. OK checks the rows and keeps the dialog open with a
/// message
/// and the focus on the cell when a row is wrong; Cancel changes nothing. The rules and failure modes are in
/// docs/failure-modes/gpedit-options.md, section 9.
/// </summary>
internal sealed class ShowContentsDialog : ClassicDialog
{
    // The client size of the form in DIP.
    private const double ClientWidthDip = 494;
    private const double ClientHeightDip = 304;

    private readonly bool _explicit;
    private readonly StringComparer _comparer;
    private readonly ClassicEditableGrid _grid;
    private readonly FrameworkElement _label;
    private readonly Button _ok;
    private readonly Button _cancel;
    private bool _messageOpen;

    /// <param name="fieldName">The label of the list element, shown above the grid.</param>
    /// <param name="explicitValue">True for a list with a name column and a value column.</param>
    /// <param name="rows">The rows to show: one text per column.</param>
    /// <param name="valueComparer">How two values of a plain list are told to be equal.</param>
    internal ShowContentsDialog(string fieldName, bool explicitValue, IReadOnlyList<IReadOnlyList<string>> rows, StringComparer valueComparer)
        : base(BuildTemplate(fieldName, explicitValue))
    {
        _explicit = explicitValue;
        _comparer = valueComparer;
        _grid = RequireControl<ClassicEditableGrid>("dataGrid");
        _label = RequireControl<FrameworkElement>("label");
        _ok = RequireControl<Button>("buttonOK");
        _cancel = RequireControl<Button>("buttonCancel");
        _grid.SetRows(rows);
    }

    /// <summary>The rows after OK: one text per column. Null when the dialog was cancelled.</summary>
    internal IReadOnlyList<string[]>? Result { get; private set; }

    private static ClassicDialogDefinition BuildTemplate(string fieldName, bool explicitValue)
    {
        string[] columns = explicitValue
            ? [OptionsStrings.Get("ContentsColumnValueName"), OptionsStrings.Get("ContentsColumnValue")]
            : [OptionsStrings.Get("ContentsColumnValue")];

        // The rectangles are placeholders in dialog units (the form is laid out in DIPs). The order is the tab order of the
        // form: OK and Cancel (the button table), then the grid.
        ClassicControlDefinition[] controls =
        [
            new(ClassicControlKind.Button, 1, "buttonOK", OptionsStrings.Get("ContentsOk"), Dlu(315, 4.0 / 6.0), Dlu(275, 8.0 / 15.0), Dlu(79, 4.0 / 6.0), Dlu(23, 8.0 / 15.0))
            {
                TabStop = true,
                IsDefault = true,
            },
            new(ClassicControlKind.Button, 2, "buttonCancel", OptionsStrings.Get("ContentsCancel"), Dlu(400, 4.0 / 6.0), Dlu(275, 8.0 / 15.0), Dlu(79, 4.0 / 6.0), Dlu(23, 8.0 / 15.0))
            {
                TabStop = true,
            },
            new(ClassicControlKind.EditableGrid, 0, "dataGrid", fieldName, Dlu(12, 4.0 / 6.0), Dlu(47, 8.0 / 15.0), Dlu(470, 4.0 / 6.0), Dlu(219, 8.0 / 15.0))
            {
                TabStop = true,
                Columns = columns,
            },
            new(ClassicControlKind.Label, 65535, "label", fieldName, Dlu(12, 4.0 / 6.0), Dlu(20, 8.0 / 15.0), Dlu(470, 4.0 / 6.0), Dlu(18, 8.0 / 15.0))
            {
                LiteralText = true,
            },
        ];
        return new ClassicDialogDefinition(
            OptionsStrings.Get("ContentsCaption"),
            "WinForms ListBoxDialog form of Microsoft.GroupPolicy.AdmTmplEditor",
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

    /// <inheritdoc />
    protected override (int Width, int Height) MinimumClientSize(uint dpi)
    {
        double scale = (dpi == 0 ? 96u : dpi) / 96.0;
        return ((int)Math.Round(ClientWidthDip * scale), (int)Math.Round(ClientHeightDip * scale));
    }

    /// <inheritdoc />
    protected override void ApplySizeAndPosition(nint ownerHwnd)
    {
        base.ApplySizeAndPosition(ownerHwnd);

        // The base sizes the client from the dialog units; the form is 494 x 304 DIP exactly, at every DPI.
        double scale = Metrics.Scale;
        AppWindow.ResizeClient(new SizeInt32((int)Math.Round(ClientWidthDip * scale), (int)Math.Round(ClientHeightDip * scale)));
        DialogPlacement.CenterOnOwner(AppWindow, ownerHwnd);
    }

    /// <inheritdoc />
    protected override void OnDialogLoaded()
    {
        base.OnDialogLoaded();
        if (Content is FrameworkElement { ActualWidth: > 0, ActualHeight: > 0 } root)
        {
            Arrange(root.ActualWidth, root.ActualHeight);
        }
    }

    /// <inheritdoc />
    protected override void OnClientResized(double widthDip, double heightDip) => Arrange(widthDip, heightDip);

    /// <summary>The form anchors: the label and the grid stretch, the buttons keep their distance from the bottom
    /// right corner.</summary>
    private void Arrange(double width, double height)
    {
        width = Math.Max(width, 1);
        height = Math.Max(height, 1);
        Place(_label, 12, 20, width - 24, 18);
        Place(_grid, 12, 47, width - 24, height - 85);
        Place(_ok, width - 179, height - 29, 79, 23);
        Place(_cancel, width - 94, height - 29, 79, 23);
    }

    private static void Place(FrameworkElement element, double x, double y, double width, double height)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        element.Width = Math.Max(1, width);
        element.Height = Math.Max(1, height);
    }

    /// <summary>OK keeps the rows when they are right; otherwise it shows the message of the first wrong row.</summary>
    protected override bool TryAccept()
    {
        string[][] rows = _grid.Rows.Select(r => r.ToArray()).ToArray();
        ContentsProblem? problem = Check(rows, _explicit, _comparer);
        if (problem is null)
        {
            Result = rows;
            return true;
        }

        string text = problem.Value.Argument is { } argument
            ? OptionsStrings.Format(problem.Value.TextKey, argument)
            : OptionsStrings.Get(problem.Value.TextKey);
        ShowMessage(OptionsStrings.Get("ContentsErrorCaption"), text, problem.Value.Row, problem.Value.Column);
        return false;
    }

    /// <summary>
    /// The first wrong row, in row order: an empty name, an empty value, a name or a value that an earlier row has. Null when
    /// the rows are right.
    /// </summary>
    internal static ContentsProblem? Check(IReadOnlyList<string[]> rows, bool explicitValue, StringComparer valueComparer)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var values = new HashSet<string>(valueComparer);
        for (int i = 0; i < rows.Count; i++)
        {
            string[] row = rows[i];
            int valueColumn = explicitValue ? 1 : 0;
            if (explicitValue && string.IsNullOrEmpty(row[0]))
            {
                return new ContentsProblem("ContentsEmptyName", null, i, 0);
            }

            if (string.IsNullOrEmpty(row[valueColumn]))
            {
                return new ContentsProblem("ContentsEmptyValue", null, i, valueColumn);
            }

            if (explicitValue)
            {
                if (!names.Add(row[0]))
                {
                    return new ContentsProblem("ContentsDuplicateName", row[0], i, 0);
                }
            }
            else if (!values.Add(row[0]))
            {
                return new ContentsProblem("ContentsDuplicateValue", row[0], i, 0);
            }
        }

        return null;
    }

    private void ShowMessage(string caption, string text, int row, int column)
    {
        if (_messageOpen)
        {
            return;
        }

        _messageOpen = true;
        _ = ShowMessageAsync(caption, text, row, column);
    }

    private async Task ShowMessageAsync(string caption, string text, int row, int column)
    {
        try
        {
            var box = new ClassicMessageBox(caption, text, ClassicMessageIcon.Warning, ClassicMessageButtons.Ok);
            _ = await box.ShowAsync(WindowNative.GetWindowHandle(this));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            UiDiagnostics.Warn?.Invoke("A message of the Show Contents dialog could not be shown: " + ex.Message);
        }
        finally
        {
            _messageOpen = false;
            _ = _grid.CellAt(row, column).Focus(FocusState.Programmatic);
        }
    }

    /// <summary>A row that OK refuses: the resource key of the message, its argument, and the cell that gets the
    /// focus.</summary>
    internal readonly record struct ContentsProblem(string TextKey, string? Argument, int Row, int Column);
}
