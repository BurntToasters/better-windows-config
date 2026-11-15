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
    private readonly List<LUID> _enabledByThisScope;
    private int _disposed;

    private OptionalPrivilegeScope(SafeFileHandle? token, List<LUID> enabledByThisScope)
    {
        _token = token;
        _enabledByThisScope = enabledByThisScope;
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
        var enabled = new List<LUID>();

        if (names.Length == 0 ||
            !PrivilegeScope.TryOpenToken(
                TOKEN_ACCESS_MASK.TOKEN_QUERY | TOKEN_ACCESS_MASK.TOKEN_ADJUST_PRIVILEGES,
                out SafeFileHandle? token,
                out _))
        {
            return new OptionalPrivilegeScope(null, enabled);
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
                        enabled.Add(luid);
                    }
                }
            }

            var scope = new OptionalPrivilegeScope(token, enabled);
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
                foreach (LUID luid in _enabledByThisScope)
                {
                    PrivilegeScope.DisablePrivilege(_token, luid);
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
