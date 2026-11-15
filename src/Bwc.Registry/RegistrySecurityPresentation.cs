namespace Bwc.Registry;

/// <summary>
/// The security descriptor in the form the permissions editor presents. The registry stores no inherited marks, so a
/// key created under a parent holds plain copies of the entries it inherits. The editor needs them marked
/// <c>INHERITED_ACE</c> to show where an entry comes from and to send back only the explicit ones.
/// </summary>
public static class RegistrySecurityPresentation
{
    /// <summary>
    /// Returns <paramref name="descriptor"/> with the entries that equal an entry the parent passes down marked
    /// inherited, and the auto-inherited control bit set. A protected DACL is returned unchanged.
    /// </summary>
    /// <exception cref="ArgumentException">A descriptor is not a valid self-relative descriptor.</exception>
    public static byte[] MarkInheritedEntries(byte[] descriptor, byte[] parentDescriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(parentDescriptor);
        return RegistrySecurityInheritance.MarkInheritedEntries(descriptor, parentDescriptor);
    }
}
