namespace Bwc.E2E.AppServices;

/// <summary>
/// The on-disk layout of the Registry Editor data root, as the app writes it under BWC_DATA_ROOT.
/// Kept apart from the process helpers so the layout is read in one place.
/// </summary>
internal static class AppServicesPaths
{
    /// <summary>The tool folder under the data root. Matches the tool id the app uses for its settings and logs.</summary>
    internal const string ToolFolder = "regedit";

    /// <summary>The settings file the Registry Editor writes when its window closes.</summary>
    internal static string SettingsFile(string dataRoot) => Path.Combine(dataRoot, ToolFolder, "settings.json");

    /// <summary>The directory that holds the daily log files.</summary>
    internal static string LogDirectory(string dataRoot) => Path.Combine(dataRoot, ToolFolder, "logs");
}
