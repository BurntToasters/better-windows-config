using Microsoft.Windows.ApplicationModel.Resources;

namespace Bwc.Regedit.Controllers;

/// <summary>
/// The Registry Editor's user-facing text. Titles, labels, and messages come from the resource map by
/// key. The English text that used to be written inline in the view lives here as named constants.
/// </summary>
internal sealed class MessageCatalog
{
    /// <summary>Label shown for the unnamed default value.</summary>
    public const string DefaultValueLabel = "(Default)";

    /// <summary>Prefix of the value line in the query-value error detail.</summary>
    public const string ValueLinePrefix = "Value: ";

    /// <summary>Prefix of the attempted path line in an address bar error.</summary>
    public const string PathLinePrefix = "Path: ";

    /// <summary>Prefix of the file line in a file operation error.</summary>
    public const string FileLinePrefix = "File: ";

    /// <summary>Operation name shown in the error dialog when a value cannot be queried.</summary>
    public const string QueryValueOperation = "QueryValue";

    /// <summary>Error text used when the Windows print dialog returns no printer device.</summary>
    public const string PrintNoDeviceMessage = "The Windows print dialog returned no printer device.";

    /// <summary>Base of the suggested name for a new key. An index follows it.</summary>
    public const string NewKeyNamePrefix = "New Key #";

    /// <summary>Base of the suggested name for a new value. An index follows it.</summary>
    public const string NewValueNamePrefix = "New Value #";

    /// <summary>File base name used when the exported key has no usable name.</summary>
    public const string FallbackExportName = "export";

    private readonly ResourceLoader _loader;

    public MessageCatalog(ResourceLoader loader)
    {
        ArgumentNullException.ThrowIfNull(loader);
        _loader = loader;
    }

    /// <summary>Returns the resource string for a key.</summary>
    public string Get(string key) => _loader.GetString(key);
}
