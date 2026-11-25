using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;

namespace Bwc.Registry.Privileges;

/// <summary>
/// Enables the named privileges that the process token already holds, and puts each back to its previous state when
/// disposed. A privilege the token does not hold is skipped: nothing is added and nothing is elevated. Used for the
/// one security call that needs SeSecurityPrivilege (audit entries) or SeTakeOwnershipPrivilege (owner change).
/// </summary>
public sealed class OptionalPrivilegeScope : IDisposable
{
    public const string SecurityPrivilege = "SeSecurityPrivilege";
    public const string TakeOwnershipPrivilege = "SeTakeOwnershipPrivilege";

    private readonly SafeFileHandle? _token;
    private readonly List<(string Name, LUID Luid)> _enabledByThisScope;
    private readonly Func<string, int>? _restoreOverride;
    private int _disposed;

    private OptionalPrivilegeScope(
        SafeFileHandle? token,
        List<(string Name, LUID Luid)> enabledByThisScope,
        Func<string, int>? restoreOverride)
    {
        _token = token;
        _enabledByThisScope = enabledByThisScope;
        _restoreOverride = restoreOverride;
    }

    /// <summary>
    /// Test seam: a scope that claims <paramref name="names"/> as enabled and whose restore call is
    /// <paramref name="restore"/> (privilege name to Win32 error, 0 for success). It never touches the process token.
    /// </summary>
    internal static OptionalPrivilegeScope CreateForRestoreTest(string[] names, Func<string, int> restore)
    {
        var enabled = new List<(string Name, LUID Luid)>();
        foreach (string name in names)
        {
            enabled.Add((name, default));
        }

        return new OptionalPrivilegeScope(new SafeFileHandle(IntPtr.Zero, ownsHandle: false), enabled, restore);
    }

    /// <summary>The privileges this scope turned on. Zero when the token has none of them or they were already on.</summary>
    public int EnabledCount => _enabledByThisScope.Count;

    /// <summary>
    /// Enables each privilege in <paramref name="names"/> that the token holds in a disabled state. A failure to read or
    /// adjust the token leaves the scope empty; the caller's own access check then reports the problem.
    /// </summary>
    public static OptionalPrivilegeScope Enable(params string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var enabled = new List<(string Name, LUID Luid)>();

        if (names.Length == 0 ||
            !PrivilegeScope.TryOpenToken(
                TOKEN_ACCESS_MASK.TOKEN_QUERY | TOKEN_ACCESS_MASK.TOKEN_ADJUST_PRIVILEGES,
                out SafeFileHandle? token,
                out _))
        {
            return new OptionalPrivilegeScope(null, enabled, null);
        }

        try
        {
            if (PrivilegeScope.TryReadTokenPrivileges(token!, out byte[] privileges, out _))
            {
                foreach (string name in names)
                {
                    if (!PrivilegeScope.TryLookupPrivilege(name, out LUID luid, out _))
                    {
                        continue;
                    }

                    PrivilegeScope.FindPrivilege(privileges, luid, out bool present, out bool alreadyEnabled);
                    if (present && !alreadyEnabled && TryEnable(token!, luid))
                    {
                        enabled.Add((name, luid));
                    }
                }
            }

            var scope = new OptionalPrivilegeScope(token, enabled, null);
            token = null;
            return scope;
        }
        finally
        {
            token?.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (_token is not null)
            {
                foreach ((string name, LUID luid) in _enabledByThisScope)
                {
                    int win32Error = _restoreOverride is null
                        ? PrivilegeScope.DisablePrivilege(_token, luid)
                        : _restoreOverride(name);

                    if (win32Error != 0)
                    {
                        PrivilegeDiagnostics.ReportRestoreFailed(name, win32Error);
                    }
                }
            }
        }
        catch
        {
            // Dispose must never throw. A failed restore leaves the process token as it was.
        }

        _token?.Dispose();
    }

    private static unsafe bool TryEnable(SafeFileHandle token, LUID luid)
    {
        byte[] stateBuffer = new byte[TOKEN_PRIVILEGES.SizeOf(1)];
        fixed (byte* statePointer = stateBuffer)
        {
            TOKEN_PRIVILEGES* state = (TOKEN_PRIVILEGES*)statePointer;
            state->PrivilegeCount = 1;
            state->Privileges[0].Luid = luid;
            state->Privileges[0].Attributes = TOKEN_PRIVILEGES_ATTRIBUTES.SE_PRIVILEGE_ENABLED;

            BOOL adjusted = PInvoke.AdjustTokenPrivileges(token, false, state, default);
            return adjusted && System.Runtime.InteropServices.Marshal.GetLastWin32Error() == 0;
        }
    }
}
