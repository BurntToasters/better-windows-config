using System.Diagnostics;

namespace Bwc.Core.Tests;

/// <summary>A trace listener whose only behavior is to fail, for quiet-failure tests.</summary>
internal sealed class ThrowingTraceListener : TraceListener
{
    public override void Write(string? message)
    {
        throw new InvalidOperationException("trace write failed");
    }

    public override void WriteLine(string? message)
    {
        throw new InvalidOperationException("trace line failed");
    }
}
