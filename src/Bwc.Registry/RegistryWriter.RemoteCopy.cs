using Windows.Win32.System.Registry;

namespace Bwc.Registry;

/// <summary>
/// Managed tree copy for a remote root. RegCopyTree fails with ERROR_INVALID_HANDLE on a remote handle, so the copy
/// reads the source with the connection's reader and writes each key through the public write methods, so every
/// write still passes the policy. The copy merges into an existing destination, as RegCopyTree does.
/// </summary>
public sealed partial class RegistryWriter
{
    private void CopyTreeByHand(RegistryPath source, RegistryPath destination, string operation)
    {
        RegistryReader reader = _reader ?? throw new InvalidOperationException(
            "A remote writer needs its reader to copy a key tree.");

        if (_policy.RejectsSymbolicLinks)
        {
            // Refuse before the first key is written, as the native copy does.
            using (SafeRegistryKeyHandle sourceHandle = OpenKey(source, REG_SAM_FLAGS.KEY_READ, operation, source))
            {
                EnsureSubtreeHasNoSymbolicLinks(sourceHandle, source.ToAbbreviatedString(), operation, source, depth: 0);
            }

            using SafeRegistryKeyHandle destinationHandle = OpenKey(destination, REG_SAM_FLAGS.KEY_READ, operation, destination);
            EnsureSubtreeHasNoSymbolicLinks(destinationHandle, destination.ToAbbreviatedString(), operation, destination, depth: 0);
        }

        CopyKeyContents(reader, source, destination, depth: 0, operation);
    }

    private void CopyKeyContents(RegistryReader reader, RegistryPath source, RegistryPath destination, int depth, string operation)
    {
        if (depth >= MaxSubtreeDepth)
        {
            throw new RegistryOperationException(
                operation,
                destination,
                ErrorAccessDenied,
                $"The copy stops at '{destination.ToAbbreviatedString()}' because it is nested {MaxSubtreeDepth} levels deep.");
        }

        foreach (RegistryValueInfo value in reader.EnumerateValues(source))
        {
            SetValue(destination, value.Name, value.Type, value.Data);
        }

        foreach (string name in reader.EnumerateSubKeyNames(source))
        {
            RegistryPath destinationChild = destination.AppendEnumeratedName(name);
            CreateKey(destinationChild);
            CopyKeyContents(reader, source.AppendEnumeratedName(name), destinationChild, depth + 1, operation);
        }

        // The descriptor is written last, so a restrictive copied DACL cannot stop the children from being created.
        byte[] descriptor = reader.GetKeySecurity(source, RegistrySecurityInformation.Dacl);
        SetKeySecurity(destination, RegistrySecurityInformation.Dacl, descriptor);
    }
}
