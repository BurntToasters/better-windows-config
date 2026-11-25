using System.Globalization;
using Bwc.UI.Theme;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Bwc.Gpedit.Tree;

/// <summary>
/// The page of a placeholder node (Software Settings and the children of Windows Settings): a BWC authored "not available
/// yet" notice in the result pane in place of the list. A declared difference from classic
/// (docs/failure-modes/gpedit-tree-list.md, section 5).
/// </summary>
internal sealed partial class GpeditPlaceholderPage : UserControl
{
    public GpeditPlaceholderPage()
    {
        InitializeComponent();
        AutomationProperties.SetName(this, GpeditStrings.Tree("GpeditPlaceholderPageName"));
        TitleText.Text = GpeditStrings.Tree("GpeditPlaceholderTitle");
        ApplyFont();
        Loaded += (_, _) => ApplyFont();
    }

    /// <summary>Names the node the page stands for.</summary>
    internal void Show(string nodeName)
    {
        BodyText.Text = string.Format(CultureInfo.CurrentCulture, GpeditStrings.Tree("GpeditPlaceholderText"), nodeName);
    }

    private void ApplyFont()
    {
        TitleText.FontFamily = DenseMetrics.FontFamily;
        TitleText.FontSize = DenseMetrics.FontSize;
        BodyText.FontFamily = DenseMetrics.FontFamily;
        BodyText.FontSize = DenseMetrics.FontSize;
    }
}
