using System.Runtime.InteropServices;
using Bwc.Gpo.Store;

namespace Bwc.Gpo.Refresh;

/// <summary>Applies the saved policy to the live registry after a save.</summary>
public interface IPolicyRefresher
{
    /// <summary>Requests a refresh of one side.</summary>
    /// <param name="side">The side that was saved.</param>
    /// <exception cref="GpoStoreException">The refresh failed (<see cref="GpoStoreFailure.RefreshFailed"/>).</exception>
    void Refresh(GpoSide side);
}

/// <summary>
/// The real refresher: <c>RefreshPolicyEx(machine, RP_FORCE)</c> from <c>userenv.dll</c>. The flag choice has no capture
/// (the editor does not show which refresh call it makes): gap GAP-02 in docs/parity/gpedit-audit.md. <c>RP_FORCE</c> (1)
/// refreshes even when no version changed; BWC bumps the version anyway, so the flag only guards against a missed bump.
/// The user call refreshes the calling user's policy: for an elevated split-token administrator that is the same user.
/// This is the only file that names the API (the static guard enforces it). Tests never call this class.
/// </summary>
public sealed partial class UserenvPolicyRefresher : IPolicyRefresher
{
    private const uint RpForce = 1;

    /// <inheritdoc />
    public void Refresh(GpoSide side)
    {
        if (!NativeRefresh(side == GpoSide.Machine, RpForce))
        {
            var code = Marshal.GetLastPInvokeError();
            throw new GpoStoreException(
                GpoStoreFailure.RefreshFailed, "Refresh", side.ToString(), code, $"Refreshing {side} policy failed (Win32 {code}).");
        }
    }

    [LibraryImport("userenv.dll", EntryPoint = "RefreshPolicyEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool NativeRefresh([MarshalAs(UnmanagedType.Bool)] bool machine, uint options);
}

/// <summary>
/// The sandbox replacement: appends one line (<c>Machine</c> or <c>User</c>) per refresh request to a file inside the
/// sandbox, through <see cref="GpoFileWriter"/> and so through the write policy, and calls nothing native.
/// </summary>
public sealed class RecordingPolicyRefresher : IPolicyRefresher
{
    private readonly string _recordFile;
    private readonly GpoFileWriter _writer;
    private readonly object _gate = new();

    /// <summary>Creates a recorder.</summary>
    /// <param name="recordFile">The file to append to; the writer's policy must allow it.</param>
    /// <param name="writer">The writer that appends.</param>
    public RecordingPolicyRefresher(string recordFile, GpoFileWriter writer)
    {
        _recordFile = recordFile ?? throw new ArgumentNullException(nameof(recordFile));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    /// <inheritdoc />
    public void Refresh(GpoSide side)
    {
        lock (_gate)
        {
            _writer.AppendLine(_recordFile, side.ToString());
        }
    }
}
