namespace Bwc.Registry;

/// <summary>
/// A security walk changed the root and as many subkeys as it could, and some subkeys failed. Each failed subkey
/// is listed with its Win32 code. The keys that changed keep their new descriptor. See
/// docs/failure-modes/permissions.md sections 33 and 35.
/// </summary>
public sealed class RegistrySecurityWalkException : Exception
{
    public RegistrySecurityWalkException(IReadOnlyList<RegistryOperationException> failures)
        : base(BuildMessage(failures))
    {
        ArgumentNullException.ThrowIfNull(failures);
        Failures = failures;
        HResult = failures.Count > 0 ? failures[0].HResult : unchecked((int)0x80004005);
    }

    /// <summary>The subkeys that could not be changed, in walk order.</summary>
    public IReadOnlyList<RegistryOperationException> Failures { get; }

    private static string BuildMessage(IReadOnlyList<RegistryOperationException> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);
        return $"The security walk left {failures.Count} subkey(s) unchanged.";
    }
}
