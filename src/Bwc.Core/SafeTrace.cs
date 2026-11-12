using System.Diagnostics;

namespace Bwc.Core;

/// <summary>
/// Writes diagnostics without ever throwing. A registered <see cref="TraceListener"/> can
/// throw, including during shutdown, so every trace call in this assembly goes through here.
/// </summary>
internal static class SafeTrace
{
    public static void WriteLine(string message)
    {
        try
        {
            Trace.WriteLine(message);
        }
        catch
        {
            // Diagnostics must not become a failure path of their own.
        }
    }
}
