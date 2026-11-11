using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Security;

namespace Bwc.Registry;

/// <summary>
/// Validation and copying helpers for self-relative security descriptors. The helpers never
/// reinterpret access masks or ACL contents; they only reject buffers that the Windows security
/// APIs consider invalid or whose declared length does not match the supplied buffer.
/// </summary>
public static class RegistrySecurityDescriptor
{
    /// <summary>
    /// Throws <see cref="ArgumentException"/> when the buffer is empty, invalid, has a zero
    /// declared length, or carries bytes beyond the declared descriptor length.
    /// </summary>
    public static void Validate(ReadOnlySpan<byte> descriptor)
    {
        if (!TryGetLength(descriptor, out int length) || length != descriptor.Length)
        {
            throw new ArgumentException(
                "The buffer is not a valid self-relative security descriptor with an exact declared length.",
                nameof(descriptor));
        }
    }

    /// <summary>
    /// Returns the descriptor length reported by Windows. Returns false for an empty buffer, an
    /// invalid descriptor, or a declared length that is larger than the supplied buffer.
    /// </summary>
    public static unsafe bool TryGetLength(ReadOnlySpan<byte> descriptor, out int length)
    {
        length = 0;

        if (descriptor.IsEmpty)
        {
            return false;
        }

        fixed (byte* pointer = descriptor)
        {
            var native = new PSECURITY_DESCRIPTOR(pointer);
            if (!PInvoke.IsValidSecurityDescriptor(native))
            {
                return false;
            }

            uint declaredLength = PInvoke.GetSecurityDescriptorLength(native);
            if (declaredLength == 0 ||
                declaredLength > int.MaxValue ||
                declaredLength > (uint)descriptor.Length)
            {
                return false;
            }

            length = (int)declaredLength;
            return true;
        }
    }

    /// <summary>
    /// Copies and validates a self-relative descriptor owned by native code, such as the pointer
    /// aclui passes to <c>ISecurityInformation.SetSecurity</c>. The source buffer is not freed.
    /// </summary>
    public static unsafe byte[] CopyFromNative(nint descriptor)
    {
        if (descriptor == 0)
        {
            throw new ArgumentException("A security descriptor pointer is required.", nameof(descriptor));
        }

        var native = new PSECURITY_DESCRIPTOR((void*)descriptor);
        if (!PInvoke.IsValidSecurityDescriptor(native))
        {
            throw new ArgumentException(
                "The native buffer is not a valid security descriptor.",
                nameof(descriptor));
        }

        uint declaredLength = PInvoke.GetSecurityDescriptorLength(native);
        if (declaredLength == 0 || declaredLength > int.MaxValue)
        {
            throw new ArgumentException(
                "The native security descriptor reports an invalid length.",
                nameof(descriptor));
        }

        byte[] buffer = new byte[(int)declaredLength];
        Marshal.Copy(descriptor, buffer, 0, buffer.Length);
        Validate(buffer);
        return buffer;
    }
}
