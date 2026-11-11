using System.Runtime.InteropServices.ComTypes;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.Registry;

namespace Bwc.Registry;

/// <summary>
/// Synchronous, read-only registry access. Every key is opened with KEY_READ and
/// KEY_WOW64_64KEY. The reader keeps no handles between calls and may be used from any thread.
/// </summary>
public sealed class RegistryReader
{
    private const string OpenKeyOperation = "OpenKey";
    private const string QueryInfoKeyOperation = "QueryInfoKey";
    private const string EnumKeyOperation = "EnumKey";
    private const string EnumValueOperation = "EnumValue";
    private const string QueryValueOperation = "QueryValue";
    private const string GetKeySecurityOperation = "GetKeySecurity";
    private const uint ReadControlStandardRight = 0x00020000;
    private const int ErrorNotEnoughMemory = 8;
    private const int ErrorInvalidSecurityDescriptor = 1338;
    private const int MaxSecurityDescriptorAttempts = 8;

    private readonly RemoteRegistryConnection? _remote;

    public RegistryReader()
    {
    }

    public RegistryReader(RemoteRegistryConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _remote = connection;
    }

    public RegistryKeyInfo GetKeyInfo(RegistryPath path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        cancellationToken.ThrowIfCancellationRequested();

        using SafeRegistryKeyHandle handle = OpenKey(path, cancellationToken);

        char[] classBuffer = new char[256];
        uint classLength = (uint)classBuffer.Length;
        uint subKeyCount;
        uint maxSubKeyNameLength;
        uint valueCount;
        uint maxValueNameLength;
        uint maxValueDataLength;
        FILETIME lastWriteTime;
        WIN32_ERROR error;

        while (true)
        {
            error = PInvoke.RegQueryInfoKey(
                handle,
                classBuffer,
                ref classLength,
                out subKeyCount,
                out maxSubKeyNameLength,
                out _,
                out valueCount,
                out maxValueNameLength,
                out maxValueDataLength,
                out _,
                out lastWriteTime);

            if (error != WIN32_ERROR.ERROR_MORE_DATA)
            {
                break;
            }

            int requiredLength = classLength > (uint)int.MaxValue
                ? int.MaxValue
                : (int)classLength;
            int nextLength = Math.Max(requiredLength, classBuffer.Length * 2);
            Array.Resize(ref classBuffer, Math.Max(nextLength, classBuffer.Length + 1));
            classLength = (uint)classBuffer.Length;
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (error != WIN32_ERROR.ERROR_SUCCESS)
        {
            throw new RegistryOperationException(QueryInfoKeyOperation, path, (int)error);
        }

        int classNameLength = classLength > (uint)classBuffer.Length
            ? classBuffer.Length
            : (int)classLength;
        string className = classNameLength == 0
            ? string.Empty
            : new string(classBuffer, 0, classNameLength);

        long fileTime = ((long)(uint)lastWriteTime.dwHighDateTime << 32)
            | (uint)lastWriteTime.dwLowDateTime;

        return new RegistryKeyInfo(
            path.IsRoot ? path.Root.DisplayName() : path.Components[^1],
            path,
            checked((int)subKeyCount),
            checked((int)valueCount),
            DateTime.FromFileTimeUtc(fileTime),
            checked((int)maxSubKeyNameLength),
            checked((int)maxValueNameLength),
            checked((int)maxValueDataLength),
            className);
    }

    public IReadOnlyList<string> EnumerateSubKeyNames(
        RegistryPath path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        cancellationToken.ThrowIfCancellationRequested();

        using SafeRegistryKeyHandle handle = OpenKey(path, cancellationToken);
        QueryMetadata(
            handle,
            path,
            out _,
            out uint maxSubKeyNameLength,
            out _,
            out _);

        int bufferLength = maxSubKeyNameLength >= (uint)int.MaxValue
            ? int.MaxValue
            : (int)maxSubKeyNameLength + 1;
        char[] buffer = new char[Math.Max(1, bufferLength)];
        var names = new List<string>();
        uint index = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            uint nameLength = (uint)buffer.Length;
            WIN32_ERROR error = PInvoke.RegEnumKeyEx(
                handle,
                index,
                buffer,
                ref nameLength,
                Span<char>.Empty);

            if (error == WIN32_ERROR.ERROR_SUCCESS)
            {
                names.Add(new string(buffer, 0, (int)nameLength));
                index += 1;
                continue;
            }

            if (error == WIN32_ERROR.ERROR_MORE_DATA)
            {
                Array.Resize(ref buffer, GrowBuffer(nameLength, buffer.Length));
                continue;
            }

            if (error == WIN32_ERROR.ERROR_NO_MORE_ITEMS)
            {
                break;
            }

            throw new RegistryOperationException(EnumKeyOperation, path, (int)error);
        }

        return names;
    }

    public IReadOnlyList<RegistryValueInfo> EnumerateValues(
        RegistryPath path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        cancellationToken.ThrowIfCancellationRequested();

        using SafeRegistryKeyHandle handle = OpenKey(path, cancellationToken);
        QueryMetadata(
            handle,
            path,
            out _,
            out _,
            out uint valueCount,
            out uint maxValueNameLength);

        int nameBufferLength = maxValueNameLength >= (uint)int.MaxValue
            ? int.MaxValue
            : (int)maxValueNameLength + 1;

        char[] nameBuffer = new char[Math.Max(1, nameBufferLength)];
        var values = new List<RegistryValueInfo>(
            valueCount > (uint)int.MaxValue ? 0 : (int)valueCount);
        uint index = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            uint nameLength = (uint)nameBuffer.Length;
            WIN32_ERROR error = PInvoke.RegEnumValue(
                handle,
                index,
                nameBuffer,
                ref nameLength,
                Span<byte>.Empty);

            if (error == WIN32_ERROR.ERROR_SUCCESS)
            {
                string name = new(nameBuffer, 0, (int)nameLength);
                RegistryValueInfo? value = QueryValueData(handle, path, name, cancellationToken);

                if (value is null)
                {
                    // The value disappeared between enumeration and the data query. Retry the
                    // same index: the next value has shifted into it, or the list is empty.
                    continue;
                }

                values.Add(value);
                index += 1;
                continue;
            }

            if (error == WIN32_ERROR.ERROR_MORE_DATA)
            {
                Array.Resize(ref nameBuffer, GrowBuffer(nameLength, nameBuffer.Length));
                continue;
            }

            if (error == WIN32_ERROR.ERROR_NO_MORE_ITEMS)
            {
                break;
            }

            throw new RegistryOperationException(EnumValueOperation, path, (int)error);
        }

        return values;
    }

    public RegistryValueInfo? ReadValue(
        RegistryPath key,
        string valueName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(valueName);
        cancellationToken.ThrowIfCancellationRequested();

        using SafeRegistryKeyHandle handle = OpenKey(key, cancellationToken);
        return QueryValueData(handle, key, valueName, cancellationToken);
    }

    public RegistryValueInfo? ReadDefaultValue(
        RegistryPath key,
        CancellationToken cancellationToken = default) =>
        ReadValue(key, string.Empty, cancellationToken);

    public bool KeyExists(RegistryPath path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        cancellationToken.ThrowIfCancellationRequested();

        SafeRegistryKeyHandle? rootOverride = _remote?.GetRoot(path.Root);
        WIN32_ERROR error = NativeRegistry.TryOpenKey(
            path,
            REG_SAM_FLAGS.KEY_READ,
            rootOverride,
            out SafeRegistryKeyHandle? handle);

        try
        {
            if (error == WIN32_ERROR.ERROR_SUCCESS)
            {
                return true;
            }

            if (error == WIN32_ERROR.ERROR_FILE_NOT_FOUND)
            {
                return false;
            }

            throw new RegistryOperationException(OpenKeyOperation, path, (int)error);
        }
        finally
        {
            handle?.Dispose();
        }
    }

    public byte[] GetKeySecurity(
        RegistryPath path,
        RegistrySecurityInformation information,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        cancellationToken.ThrowIfCancellationRequested();

        if (information == RegistrySecurityInformation.None)
        {
            throw new ArgumentException(
                "At least one security information flag is required.",
                nameof(information));
        }

        WIN32_ERROR error = NativeRegistry.TryOpenKey(
            path,
            (REG_SAM_FLAGS)ReadControlStandardRight,
            _remote?.GetRoot(path.Root),
            out SafeRegistryKeyHandle? handle);

        if (error != WIN32_ERROR.ERROR_SUCCESS)
        {
            handle?.Dispose();
            throw new RegistryOperationException(GetKeySecurityOperation, path, (int)error);
        }

        using SafeRegistryKeyHandle ownedHandle = handle!;
        var requested = (OBJECT_SECURITY_INFORMATION)(uint)information;
        uint size = 0;

        error = PInvoke.RegGetKeySecurity(ownedHandle, requested, default, ref size);

        if (error != WIN32_ERROR.ERROR_SUCCESS && error != WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER)
        {
            throw new RegistryOperationException(GetKeySecurityOperation, path, (int)error);
        }

        for (var attempt = 0; attempt < MaxSecurityDescriptorAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (size == 0)
            {
                throw InvalidSecurityDescriptor(path);
            }

            if (size > int.MaxValue)
            {
                throw new RegistryOperationException(
                    GetKeySecurityOperation,
                    path,
                    ErrorNotEnoughMemory,
                    "The security descriptor reported by the registry is too large.");
            }

            byte[] buffer = new byte[(int)size];
            uint actualLength = size;

            unsafe
            {
                fixed (byte* pointer = buffer)
                {
                    error = PInvoke.RegGetKeySecurity(
                        ownedHandle,
                        requested,
                        new PSECURITY_DESCRIPTOR(pointer),
                        ref actualLength);
                }
            }

            if (error == WIN32_ERROR.ERROR_SUCCESS)
            {
                if (actualLength == 0 || actualLength > buffer.Length)
                {
                    throw InvalidSecurityDescriptor(path);
                }

                byte[] descriptor = buffer.AsSpan(0, (int)actualLength).ToArray();
                if (!RegistrySecurityDescriptor.TryGetLength(descriptor, out int declaredLength) ||
                    declaredLength != descriptor.Length)
                {
                    throw InvalidSecurityDescriptor(path);
                }

                return descriptor;
            }

            if (error != WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER)
            {
                throw new RegistryOperationException(GetKeySecurityOperation, path, (int)error);
            }

            if (actualLength <= size)
            {
                throw new RegistryOperationException(
                    GetKeySecurityOperation,
                    path,
                    (int)WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER,
                    "The security descriptor did not grow between size queries.");
            }

            size = actualLength;
        }

        throw new RegistryOperationException(
            GetKeySecurityOperation,
            path,
            (int)WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER,
            "The security descriptor kept changing between size queries.");
    }

    public bool ValueExists(
        RegistryPath path,
        string valueName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(valueName);
        cancellationToken.ThrowIfCancellationRequested();

        WIN32_ERROR openError = NativeRegistry.TryOpenKey(
            path,
            REG_SAM_FLAGS.KEY_READ,
            _remote?.GetRoot(path.Root),
            out SafeRegistryKeyHandle? handle);

        if (openError != WIN32_ERROR.ERROR_SUCCESS)
        {
            handle?.Dispose();

            if (openError == WIN32_ERROR.ERROR_FILE_NOT_FOUND)
            {
                return false;
            }

            throw new RegistryOperationException(OpenKeyOperation, path, (int)openError);
        }

        using SafeRegistryKeyHandle ownedHandle = handle!;
        cancellationToken.ThrowIfCancellationRequested();

        uint size = 0;
        WIN32_ERROR error = PInvoke.RegQueryValueEx(
            ownedHandle,
            valueName,
            out _,
            Span<byte>.Empty,
            ref size);

        if (error == WIN32_ERROR.ERROR_SUCCESS || error == WIN32_ERROR.ERROR_MORE_DATA)
        {
            return true;
        }

        if (error == WIN32_ERROR.ERROR_FILE_NOT_FOUND)
        {
            return false;
        }

        throw new RegistryOperationException(QueryValueOperation, path, (int)error);
    }

    private static RegistryValueInfo? QueryValueData(
        SafeRegistryKeyHandle handle,
        RegistryPath path,
        string valueName,
        CancellationToken cancellationToken)
    {
        uint size = 0;
        WIN32_ERROR error = PInvoke.RegQueryValueEx(
            handle,
            valueName,
            out REG_VALUE_TYPE type,
            Span<byte>.Empty,
            ref size);

        if (error == WIN32_ERROR.ERROR_FILE_NOT_FOUND)
        {
            return null;
        }

        if (error != WIN32_ERROR.ERROR_SUCCESS && error != WIN32_ERROR.ERROR_MORE_DATA)
        {
            throw new RegistryOperationException(QueryValueOperation, path, (int)error);
        }

        int bufferLength = size == 0 ? 1 : checked((int)size);
        byte[] data = new byte[bufferLength];
        uint dataLength = (uint)data.Length;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            error = PInvoke.RegQueryValueEx(handle, valueName, out type, data, ref dataLength);

            if (error == WIN32_ERROR.ERROR_SUCCESS)
            {
                byte[] result = dataLength == 0
                    ? Array.Empty<byte>()
                    : data.AsSpan(0, (int)dataLength).ToArray();

                return new RegistryValueInfo(valueName, (uint)type, result);
            }

            if (error == WIN32_ERROR.ERROR_FILE_NOT_FOUND)
            {
                return null;
            }

            if (error == WIN32_ERROR.ERROR_MORE_DATA)
            {
                Array.Resize(ref data, GrowBuffer(dataLength, data.Length));
                dataLength = (uint)data.Length;
                continue;
            }

            throw new RegistryOperationException(QueryValueOperation, path, (int)error);
        }
    }

    private SafeRegistryKeyHandle OpenKey(
        RegistryPath path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        SafeRegistryKeyHandle? rootOverride = _remote?.GetRoot(path.Root);

        WIN32_ERROR error = NativeRegistry.TryOpenKey(
            path,
            REG_SAM_FLAGS.KEY_READ,
            rootOverride,
            out SafeRegistryKeyHandle? handle);

        if (error != WIN32_ERROR.ERROR_SUCCESS)
        {
            handle?.Dispose();
            throw new RegistryOperationException(OpenKeyOperation, path, (int)error);
        }

        return handle!;
    }

    private static void QueryMetadata(
        SafeRegistryKeyHandle handle,
        RegistryPath path,
        out uint subKeyCount,
        out uint maxSubKeyNameLength,
        out uint valueCount,
        out uint maxValueNameLength)
    {
        // Only GetKeyInfo needs the class name. A class longer than this buffer makes the call
        // return ERROR_MORE_DATA, but the count and length outputs are still usable here.
        Span<char> classBuffer = stackalloc char[256];
        uint classLength = 256;

        WIN32_ERROR error = PInvoke.RegQueryInfoKey(
            handle,
            classBuffer,
            ref classLength,
            out subKeyCount,
            out maxSubKeyNameLength,
            out _,
            out valueCount,
            out maxValueNameLength,
            out _,
            out _,
            out _);

        if (error != WIN32_ERROR.ERROR_SUCCESS && error != WIN32_ERROR.ERROR_MORE_DATA)
        {
            throw new RegistryOperationException(QueryInfoKeyOperation, path, (int)error);
        }
    }

    private static int GrowBuffer(uint required, int current)
    {
        int requiredLength = required > (uint)int.MaxValue
            ? int.MaxValue
            : (int)required;
        int doubled = current > int.MaxValue / 2 ? int.MaxValue : current * 2;
        int next = Math.Max(requiredLength, doubled);

        return current == int.MaxValue ? int.MaxValue : Math.Max(next, current + 1);
    }

    private static RegistryOperationException InvalidSecurityDescriptor(RegistryPath path) =>
        new(
            GetKeySecurityOperation,
            path,
            ErrorInvalidSecurityDescriptor,
            "The registry returned a security descriptor that is not valid self-relative data.");
}
