using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Registry;

namespace Bwc.Registry;

/// <summary>
/// Link checks for policies that set <see cref="IRegistryWritePolicy.RejectsSymbolicLinks"/>. Every
/// key on the path is opened without following links, so a link at any component is seen before
/// the write runs. Probes never create keys. Shipping writes never reach this code.
/// </summary>
public sealed partial class RegistryWriter
{
    private const uint RegOptionOpenLink = 0x00000008;
    private const uint KeyWow64_64Key = 0x0100;
    private const int ErrorBadFormat = 11;
    private const int MaxSubtreeDepth = 512;

    /// <summary>
    /// Opens <paramref name="path"/> from its predefined root, one component at a time. Every
    /// component, including the last, is opened as a link node and refused when it is a link.
    /// A missing component fails with the native not-found error. Ancestors are opened with read
    /// rights; the last component gets the rights the caller asked for plus query and enumerate
    /// rights, so the link check and the child-name check can run on the same handle the write uses.
    /// </summary>
    private SafeRegistryKeyHandle OpenGuardedKey(
        RegistryPath path,
        REG_SAM_FLAGS access,
        string operation,
        RegistryPath errorPath)
    {
        SafeRegistryKeyHandle? current = null;

        try
        {
            WIN32_ERROR error = NativeLinkGuard.TryOpenRoot(path.Root, out current);
            ThrowIfFailed(error, operation, errorPath);

            IReadOnlyList<string> components = path.Components;
            for (int index = 0; index < components.Count; index += 1)
            {
                bool last = index == components.Count - 1;
                // The final handle also gets enumerate rights: the guard lists its children to confirm
                // a name before it opens the link node, and the caller's write rights do not include them.
                REG_SAM_FLAGS childAccess = last
                    ? access | REG_SAM_FLAGS.KEY_QUERY_VALUE | REG_SAM_FLAGS.KEY_ENUMERATE_SUB_KEYS
                    : REG_SAM_FLAGS.KEY_READ;

                SafeRegistryKeyHandle parent = current!;
                current = null;

                error = OpenLinkNode(parent, components[index], childAccess, out SafeRegistryKeyHandle? child);
                parent.Dispose();
                ThrowIfFailed(error, operation, errorPath);

                current = child;
                RefuseIfSymbolicLink(current!, PrefixText(path, index + 1), operation, errorPath);
            }

            SafeRegistryKeyHandle result = current ?? throw new InvalidOperationException("The guarded key walk ended without a handle.");
            current = null;
            return result;
        }
        catch
        {
            current?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens the child <paramref name="name"/> of <paramref name="parent"/> as the link node itself.
    /// The ordinary Win32 open and the NT relative open both follow a link, and the NT open ignores
    /// the open-link option in this build, so the child is opened with RegCreateKeyEx and
    /// REG_OPTION_OPEN_LINK. That call creates a key when none exists, so the name is confirmed first
    /// by enumerating the parent. A missing child returns ERROR_FILE_NOT_FOUND. If the child is removed
    /// between the check and the open, the call creates an empty normal key; that is the one residual
    /// write of the probe (failure mode D2).
    /// </summary>
    private static WIN32_ERROR OpenLinkNode(
        SafeRegistryKeyHandle parent,
        string name,
        REG_SAM_FLAGS access,
        out SafeRegistryKeyHandle? child)
    {
        child = null;

        WIN32_ERROR error = NativeLinkGuard.TryFindChildName(parent, name, out bool exists);
        if (error != WIN32_ERROR.ERROR_SUCCESS)
        {
            return error;
        }

        if (!exists)
        {
            return WIN32_ERROR.ERROR_FILE_NOT_FOUND;
        }

        REG_OPEN_CREATE_OPTIONS options = (REG_OPEN_CREATE_OPTIONS)RegOptionOpenLink;
        error = PInvoke.RegCreateKeyEx(
            parent,
            name,
            default,
            options,
            access | (REG_SAM_FLAGS)KeyWow64_64Key,
            null,
            out SafeRegistryHandle opened);

        if (error != WIN32_ERROR.ERROR_SUCCESS)
        {
            opened.Dispose();
            return error;
        }

        // The wrapper takes ownership of the raw handle. The CsWin32 handle is detached so it is not
        // closed twice.
        child = SafeRegistryKeyHandle.FromOwned(opened.DangerousGetHandle());
        opened.SetHandleAsInvalid();
        return WIN32_ERROR.ERROR_SUCCESS;
    }

    /// <summary>Opens and closes <paramref name="path"/> through the guard so a link or a missing key fails first.</summary>
    private void EnsureKeyIsReachable(RegistryPath path, string operation)
    {
        OpenKey(path, REG_SAM_FLAGS.KEY_READ, operation, path).Dispose();
    }

    /// <summary>
    /// Refuses when <paramref name="name"/> under <paramref name="parent"/> is a link, and when
    /// <paramref name="recursive"/> is true refuses when any key below it is a link. A missing child
    /// returns without error so the native call reports not-found.
    /// </summary>
    private void EnsureChildIsNotLink(
        SafeRegistryKeyHandle parent,
        string parentText,
        string name,
        bool recursive,
        string operation,
        RegistryPath errorPath)
    {
        string childText = parentText + @"\" + name;

        WIN32_ERROR error = OpenLinkNode(parent, name, REG_SAM_FLAGS.KEY_READ, out SafeRegistryKeyHandle? child);
        if (error is WIN32_ERROR.ERROR_FILE_NOT_FOUND or WIN32_ERROR.ERROR_PATH_NOT_FOUND)
        {
            return;
        }

        ThrowIfFailed(error, operation, errorPath);

        using SafeRegistryKeyHandle handle = child!;
        RefuseIfSymbolicLink(handle, childText, operation, errorPath);

        if (recursive)
        {
            EnsureSubtreeHasNoSymbolicLinks(handle, childText, operation, errorPath, depth: 0);
        }
    }

    /// <summary>
    /// Opens every key below <paramref name="node"/> without following links and refuses when any of
    /// them is a link. A recursive native delete or copy could otherwise enter a link target.
    /// </summary>
    private void EnsureSubtreeHasNoSymbolicLinks(
        SafeRegistryKeyHandle node,
        string nodeText,
        string operation,
        RegistryPath errorPath,
        int depth)
    {
        if (depth >= MaxSubtreeDepth)
        {
            throw new RegistryOperationException(
                operation,
                errorPath,
                ErrorAccessDenied,
                $"The write policy refuses to inspect '{nodeText}' because it is nested {MaxSubtreeDepth} levels deep.");
        }

        for (uint index = 0; ; index += 1)
        {
            WIN32_ERROR error = NativeLinkGuard.TryEnumerateSubKey(node, index, out string? name);
            if (error == WIN32_ERROR.ERROR_NO_MORE_ITEMS)
            {
                return;
            }

            ThrowIfFailed(error, operation, errorPath);

            string childText = nodeText + @"\" + name;
            error = OpenLinkNode(node, name!, REG_SAM_FLAGS.KEY_READ, out SafeRegistryKeyHandle? child);
            ThrowIfFailed(error, operation, errorPath);

            using SafeRegistryKeyHandle handle = child!;
            RefuseIfSymbolicLink(handle, childText, operation, errorPath);
            EnsureSubtreeHasNoSymbolicLinks(handle, childText, operation, errorPath, depth + 1);
        }
    }

    private static void RefuseIfSymbolicLink(
        SafeHandle key,
        string keyText,
        string operation,
        RegistryPath errorPath)
    {
        WIN32_ERROR error = NativeLinkGuard.TryQueryIsSymbolicLink(key, out bool isLink);
        if (error != WIN32_ERROR.ERROR_SUCCESS)
        {
            throw new RegistryOperationException(
                operation,
                errorPath,
                (int)error,
                $"The write policy could not check '{keyText}' for a symbolic link, so the write is refused.");
        }

        if (isLink)
        {
            throw new RegistryOperationException(
                operation,
                errorPath,
                ErrorAccessDenied,
                $"The write policy refuses writes through a symbolic link at '{keyText}'.");
        }
    }

    /// <summary>
    /// Refuses a hive file that the restore would load with a symbolic link key, and a file the
    /// scanner cannot verify. Runs before the native restore, so the target key is not touched. For a
    /// clean file the returned object keeps the file open with a sharing mode that refuses writers, so
    /// the caller must dispose it only after the native call returns. Otherwise the file could change
    /// between the scan and the load.
    /// </summary>
    private static HeldHiveScan HoldVerifiedHiveFile(string fullPath, string operation, RegistryPath errorPath)
    {
        HeldHiveScan held = HiveFileLinkScanner.OpenAndScan(fullPath);
        HiveScanResult result = held.Result;

        switch (result.Outcome)
        {
            case HiveScanOutcome.Clean:
                return held;

            case HiveScanOutcome.Linked:
                held.Dispose();
                throw new RegistryOperationException(
                    operation,
                    errorPath,
                    ErrorAccessDenied,
                    $"The write policy refuses hive file '{fullPath}' because its key '{result.KeyPath}' is a symbolic link.");

            default:
                held.Dispose();
                throw new RegistryOperationException(
                    operation,
                    errorPath,
                    ErrorBadFormat,
                    $"The sandbox cannot verify that hive file '{fullPath}' contains no symbolic links: {result.Reason}.");
        }
    }

    private static string PrefixText(RegistryPath path, int componentCount)
    {
        RegistryPath prefix = path;
        while (prefix.Components.Count > componentCount)
        {
            prefix = prefix.Parent!;
        }

        return prefix.ToAbbreviatedString();
    }
}
