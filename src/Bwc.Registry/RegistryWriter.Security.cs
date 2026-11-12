using Windows.Win32.Foundation;
using Windows.Win32.System.Registry;

namespace Bwc.Registry;

/// <summary>
/// Progress for a security change. <see cref="Path"/> is the key that was just changed, and
/// <see cref="KeysChanged"/> is the running count for the whole call.
/// </summary>
public sealed record KeySecurityProgress(RegistryPath Path, int KeysChanged);

/// <summary>
/// Permission propagation for the Permissions dialog. The walk applies one descriptor to a key and to
/// every key below it. See docs/failure-modes/registry-writer.md section I for the rules.
/// </summary>
public sealed partial class RegistryWriter
{
    /// <summary>
    /// Applies <paramref name="securityDescriptor"/> to <paramref name="path"/>. When
    /// <paramref name="propagateToSubkeys"/> is true the same descriptor is applied to every key below it,
    /// which is the "Replace all child object permission entries" action. Each key goes through the write
    /// policy. The sandbox refuses the whole call, before any key changes, when the subtree contains a
    /// symbolic link. The walk checks <paramref name="cancellationToken"/> before each key and reports
    /// <paramref name="progress"/> after each one. A failure on one key stops the walk. Keys already changed
    /// keep the new descriptor, and no rollback is attempted.
    /// </summary>
    /// <remarks>
    /// Each descendant receives the exact descriptor, including its inheritance flags. The call does not
    /// recompute inherited entries the way <c>TreeSetNamedSecurityInfo</c> does. This difference is recorded
    /// in the failure-mode document.
    /// </remarks>
    public void SetKeySecurity(
        RegistryPath path,
        ReadOnlySpan<byte> securityDescriptor,
        RegistrySecurityInformation information,
        bool propagateToSubkeys,
        IProgress<KeySecurityProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        cancellationToken.ThrowIfCancellationRequested();

        const string Operation = nameof(RegistryWriteOperation.SetKeySecurity);
        _policy.EnsureWriteAllowed(path, RegistryWriteOperation.SetKeySecurity);
        ValidateSecurityArguments(information, securityDescriptor);

        if (!propagateToSubkeys)
        {
            SetKeySecurity(path, information, securityDescriptor);
            progress?.Report(new KeySecurityProgress(path, 1));
            return;
        }

        byte[] descriptor = securityDescriptor.ToArray();

        if (_policy.RejectsSymbolicLinks)
        {
            // Refuse before the first key changes, so a refused propagation leaves every key as it was.
            using SafeRegistryKeyHandle root = OpenKey(path, REG_SAM_FLAGS.KEY_READ, Operation, path);
            EnsureSubtreeHasNoSymbolicLinks(root, path.ToAbbreviatedString(), Operation, path, depth: 0);
        }

        int changed = 0;
        PropagateSecurity(path, descriptor, information, progress, cancellationToken, ref changed, depth: 0);
    }

    private void PropagateSecurity(
        RegistryPath path,
        byte[] descriptor,
        RegistrySecurityInformation information,
        IProgress<KeySecurityProgress>? progress,
        CancellationToken cancellationToken,
        ref int changed,
        int depth)
    {
        const string Operation = nameof(RegistryWriteOperation.SetKeySecurity);

        cancellationToken.ThrowIfCancellationRequested();

        if (depth >= MaxSubtreeDepth)
        {
            throw new RegistryOperationException(
                Operation,
                path,
                ErrorAccessDenied,
                $"The permission propagation stops at '{path.ToAbbreviatedString()}' because it is nested {MaxSubtreeDepth} levels deep.");
        }

        _policy.EnsureWriteAllowed(path, RegistryWriteOperation.SetKeySecurity);
        SetKeySecurity(path, information, descriptor);

        changed += 1;
        progress?.Report(new KeySecurityProgress(path, changed));

        // Names are listed before the walk descends, so a child that is changed does not alter the list.
        var names = new List<string>();
        using (SafeRegistryKeyHandle handle = OpenKey(path, REG_SAM_FLAGS.KEY_READ, Operation, path))
        {
            for (uint index = 0; ; index += 1)
            {
                WIN32_ERROR error = NativeLinkGuard.TryEnumerateSubKey(handle, index, out string? name);
                if (error == WIN32_ERROR.ERROR_NO_MORE_ITEMS)
                {
                    break;
                }

                ThrowIfFailed(error, Operation, path);
                names.Add(name!);
            }
        }

        foreach (string name in names)
        {
            PropagateSecurity(
                path.Append(name),
                descriptor,
                information,
                progress,
                cancellationToken,
                ref changed,
                depth + 1);
        }
    }

    private static void ValidateSecurityArguments(
        RegistrySecurityInformation information,
        ReadOnlySpan<byte> securityDescriptor)
    {
        if (information == RegistrySecurityInformation.None)
        {
            throw new ArgumentException(
                "At least one security information flag is required.",
                nameof(information));
        }

        if (!RegistrySecurityDescriptor.TryGetLength(securityDescriptor, out int declaredLength) ||
            declaredLength != securityDescriptor.Length)
        {
            throw new ArgumentException(
                "The buffer is not a valid self-relative security descriptor with an exact declared length.",
                nameof(securityDescriptor));
        }
    }
}
