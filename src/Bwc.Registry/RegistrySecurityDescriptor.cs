using System.Buffers.Binary;
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
    /// Checks the fixed header and the offsets of a self-relative descriptor without calling Win32. Returns false
    /// when the buffer is shorter than the 20-byte header, the revision is not 1, the SE_SELF_RELATIVE control bit
    /// is clear, or an owner, group, SACL or DACL offset points outside the buffer. An ACL must fit with its
    /// declared size. Run this before <see cref="TryGetLength"/>, so the Win32 check never reads past the buffer.
    /// </summary>
    internal static bool HasSelfRelativeLayout(ReadOnlySpan<byte> descriptor)
    {
        const int HeaderLength = 20;
        const ushort SelfRelative = 0x8000;
        const int SaclOffsetField = 12;
        const int DaclOffsetField = 16;

        if (descriptor.Length < HeaderLength || descriptor[0] != 1)
        {
            return false;
        }

        ushort control = BinaryPrimitives.ReadUInt16LittleEndian(descriptor[2..]);
        if ((control & SelfRelative) == 0)
        {
            return false;
        }

        for (int field = 4; field < HeaderLength; field += 4)
        {
            long offset = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[field..]);
            if (offset == 0)
            {
                continue;
            }

            // Every part starts with at least its eight-byte fixed header (a SID header or an ACL header).
            if (offset + 8 > descriptor.Length)
            {
                return false;
            }

            if (field is SaclOffsetField or DaclOffsetField)
            {
                long aclSize = BinaryPrimitives.ReadUInt16LittleEndian(descriptor[((int)offset + 2)..]);
                if (aclSize < 8 || offset + aclSize > descriptor.Length)
                {
                    return false;
                }
            }
        }

        return true;
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
    /// Copies and validates a descriptor owned by native code, such as the pointer aclui passes to
    /// <c>ISecurityInformation.SetSecurity</c>. An absolute descriptor is converted to self-relative form
    /// first. The source buffer is not freed.
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

        if (!IsSelfRelative(native))
        {
            return MakeSelfRelative(native);
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

    private const uint SelfRelativeControl = 0x8000;
    private const int ErrorInsufficientBuffer = 122;

    private static unsafe bool IsSelfRelative(PSECURITY_DESCRIPTOR native)
    {
        if (!PInvoke.GetSecurityDescriptorControl(native, out var control, out _))
        {
            throw new ArgumentException("The native security descriptor control flags cannot be read.");
        }

        return ((uint)control & SelfRelativeControl) != 0;
    }

    /// <summary>
    /// Converts an absolute descriptor with <c>MakeSelfRelativeSD</c>. The first call reports the size
    /// the self-relative copy needs, and the second call fills the buffer.
    /// </summary>
    private static unsafe byte[] MakeSelfRelative(PSECURITY_DESCRIPTOR absolute)
    {
        uint size = 0;
        if (PInvoke.MakeSelfRelativeSD(absolute, default, ref size) ||
            Marshal.GetLastPInvokeError() != ErrorInsufficientBuffer ||
            size == 0 ||
            size > int.MaxValue)
        {
            throw new ArgumentException("The absolute security descriptor cannot be converted to self-relative form.");
        }

        byte[] buffer = new byte[(int)size];
        fixed (byte* pointer = buffer)
        {
            if (!PInvoke.MakeSelfRelativeSD(absolute, new PSECURITY_DESCRIPTOR(pointer), ref size))
            {
                throw new ArgumentException(
                    $"The absolute security descriptor cannot be converted to self-relative form (error {Marshal.GetLastPInvokeError()}).");
            }
        }

        Validate(buffer);
        return buffer;
    }
}
