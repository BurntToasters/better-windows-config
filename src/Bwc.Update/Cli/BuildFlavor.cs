namespace Bwc.Update;

/// <summary> The flavor this exe was compiled for. A NativeAOT exe has no assembly metadata for
/// <c>scripts/flavor-metadata.js</c> to read, so the flavor is a string literal that the exe really uses (the
/// unexpected-error log line), which keeps the trimmer from dropping it. The publish audit searches the exe for <see
/// cref="MarkerPrefix"/> and requires exactly the marker of the folder's flavor (update-elevation.md HN14).
/// </summary>
internal static class BuildFlavor
{
    public const string MarkerPrefix = "BwcUpdateFlavor=";

#if BWC_E2E
    public const string Name = "E2E";
#else
    public const string Name = "ship";
#endif

    public const string Marker = MarkerPrefix + Name;
}
