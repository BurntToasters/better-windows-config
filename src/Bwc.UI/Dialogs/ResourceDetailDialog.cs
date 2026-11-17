using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Bwc.UI.Dialogs;

/// <summary>
/// One requirement descriptor: Memory (RT_DIALOG 1601), Port (1700), Interrupt (1800), or DMA (1900).
/// The field set follows the
/// template for the kind. The Preferred and Alternative captions are template-disabled and stay disabled. Read-only.
/// </summary>
public sealed class ResourceDetailDialog : ClassicDialog
{
    public const string OkText = "OK";

    public ResourceDetailDialog(string caption, ResourceDetailKind kind, ResourceRequirementView row, string okText)
        : base(ClassicDialogTemplates.ResourceDetail(RequireText(caption), kind, RequireText(okText)))
    {
        ArgumentNullException.ThrowIfNull(row);

        switch (kind)
        {
            case ResourceDetailKind.Memory:
                ClassicControlFactory.SetStaticText(RequireControl<FrameworkElement>("AccessValue"), ResourceText.MemoryAccess(row.Flags));
                ClassicControlFactory.SetStaticText(RequireControl<FrameworkElement>("LengthValue"), ResourceText.Hex(row.Length));
                ClassicControlFactory.SetStaticText(RequireControl<FrameworkElement>("AlignmentValue"), ResourceText.Hex(row.Alignment));
                ClassicControlFactory.SetStaticText(RequireControl<FrameworkElement>("MinimumAddressValue"), ResourceText.Hex(row.Minimum));
                ClassicControlFactory.SetStaticText(RequireControl<FrameworkElement>("MaximumAddressValue"), ResourceText.Hex(row.Maximum));
                break;
            case ResourceDetailKind.Port:
                ClassicControlFactory.SetStaticText(RequireControl<FrameworkElement>("TypeValue"), ResourceText.PortType(row.Flags));
                ClassicControlFactory.SetStaticText(RequireControl<FrameworkElement>("LengthValue"), ResourceText.Hex(row.Length));
                ClassicControlFactory.SetStaticText(RequireControl<FrameworkElement>("AlignmentValue"), ResourceText.Hex(row.Alignment));
                ClassicControlFactory.SetStaticText(RequireControl<FrameworkElement>("MinimumAddressValue"), ResourceText.Hex(row.Minimum));
                ClassicControlFactory.SetStaticText(RequireControl<FrameworkElement>("MaximumAddressValue"), ResourceText.Hex(row.Maximum));
                break;
            case ResourceDetailKind.Interrupt:
                ClassicControlFactory.SetStaticText(RequireControl<FrameworkElement>("TypeValue"), ResourceText.InterruptType(row.Flags));
                ClassicControlFactory.SetStaticText(RequireControl<FrameworkElement>("MinimumVectorValue"), ResourceText.Decimal(row.MinimumVector));
                ClassicControlFactory.SetStaticText(RequireControl<FrameworkElement>("MaximumVectorValue"), ResourceText.Decimal(row.MaximumVector));
                break;
            default:
                ClassicControlFactory.SetStaticText(RequireControl<FrameworkElement>("MinimumChannelValue"), ResourceText.Decimal(row.MinimumChannel));
                ClassicControlFactory.SetStaticText(RequireControl<FrameworkElement>("MaximumChannelValue"), ResourceText.Decimal(row.MaximumChannel));
                break;
        }

        ClassicControlFactory.SetStaticText(RequireControl<FrameworkElement>("ShareDispositionValue"), ResourceText.ShareDisposition(row.ShareDisposition));
    }

    /// <summary>The caption of each detail viewer, as the template declares it.</summary>
    public static string CaptionFor(ResourceDetailKind kind) => kind switch
    {
        ResourceDetailKind.Memory => "Memory",
        ResourceDetailKind.Port => "Port",
        ResourceDetailKind.Interrupt => "Interrupt",
        _ => "DMA",
    };

    private static string RequireText(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        return value;
    }
}
