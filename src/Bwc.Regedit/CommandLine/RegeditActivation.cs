namespace Bwc.Regedit.CommandLine;

/// <summary>
/// The message a second launch sends to the running instance. <c>activate</c> brings the window to
/// the front. <c>import:&lt;path&gt;</c> also asks the window to import a .reg file.
/// </summary>
public static class RegeditActivation
{
    private const string ActivateMessage = "activate";
    private const string ImportPrefix = "import:";

    /// <summary>Builds the message for a launch with an optional .reg path.</summary>
    public static string Encode(string? regFile) =>
        string.IsNullOrWhiteSpace(regFile) ? ActivateMessage : ImportPrefix + regFile;

    /// <summary>
    /// Reads a message from the pipe. Unknown text is treated as a plain activation, so a message from
    /// another build never starts an import.
    /// </summary>
    public static bool TryDecode(string? message, out string? regFile)
    {
        regFile = null;
        if (message is null || !message.StartsWith(ImportPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        string path = message[ImportPrefix.Length..];
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        regFile = path;
        return true;
    }
}
