using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;

namespace Bwc.Spike.UI;

/// <summary>
/// Shows a string that lives in this library's own .resw (Strings/en-US/Resources.resw).
/// </summary>
public sealed partial class ProbeBadge : UserControl
{
    /// <summary>
    /// Resource map of this library inside the PRI: "&lt;AssemblyName&gt;/Resources".
    /// The PRI that holds it is the head exe's own "&lt;AssemblyName&gt;.pri" (see ResourceLoader.GetDefaultResourceFilePath),
    /// because publish merges the library PRI into each app PRI.
    /// </summary>
    public const string ResourceMap = "Bwc.Spike.UI/Resources";
    public const string ResourceKey = "ProbeText";

    public ProbeBadge()
    {
        InitializeComponent();
        BadgeText.Text = Resolve();
    }

    /// <summary>The text currently shown by the badge.</summary>
    public string BadgeTextValue => BadgeText.Text;

    // A plain new ResourceLoader() only sees the app's own "Resources" map and cannot find ProbeText.
    // The two-argument form (PRI path + map id) resolves it. A failed lookup is shown as text
    // instead of being thrown: an exception escaping XAML construction takes the whole app down.
    private static string Resolve()
    {
        try
        {
            var loader = new ResourceLoader(ResourceLoader.GetDefaultResourceFilePath(), ResourceMap);
            var text = loader.GetString(ResourceKey);
            return string.IsNullOrEmpty(text) ? "UNRESOLVED: " + ResourceKey : text;
        }
        catch (Exception ex)
        {
            return $"UNRESOLVED: {ResourceKey} (0x{ex.HResult:X8})";
        }
    }
}
