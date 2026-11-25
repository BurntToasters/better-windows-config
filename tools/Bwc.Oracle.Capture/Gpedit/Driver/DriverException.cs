using System.Text.Json.Nodes;

namespace Bwc.Oracle.Capture.Gpedit.Driver;

// A driven step that cannot be trusted. Fatal means the runner must stop (mmc gone, a window that cannot be closed). Evidence is
// whatever was read from classic when the step failed (names found, a window tree), kept in the result file.
internal sealed class DriverException : Exception
{
    internal DriverException(string message, bool fatal = false, JsonNode? evidence = null)
        : base(message)
    {
        Fatal = fatal;
        Evidence = evidence;
    }

    internal bool Fatal { get; }

    internal JsonNode? Evidence { get; }

    // Set by the tree selection when a segment of the path was missing under a parent that was found (failure mode 42).
    internal TreeGap? Gap { get; init; }
}
