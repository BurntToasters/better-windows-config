#if BWC_E2E
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace Bwc.Registry.Tests;

/// <summary>
/// Removes one test's run tree under HKCU\Software\BWC-E2E\&lt;run-id&gt;. A propagated child can hold no entry
/// for the current user, so the walk gives every key first a DACL that grants the current user full control
/// with container inherit, and lists the key's subkeys only after that. The owner can open such a key with
/// WRITE_DAC alone, so the walk needs no elevation. Link nodes are removed without being followed. The run
/// root is then deleted through the engine. See docs/failure-modes/permissions.md section 26.
/// </summary>
internal static class SandboxRunCleanup
{
    internal const string SandboxBase = @"Software\BWC-E2E";
    private const int ErrorFileNotFound = 2;

    /// <summary>Removes HKCU\Software\BWC-E2E\&lt;runId&gt;. Only that one key is touched.</summary>
    internal static void RemoveRunTree(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (runId.Contains('\\') || runId.Contains('/') || runId is "." or "..")
        {
            throw new ArgumentException("A run id is a single key name under " + SandboxBase + ".", nameof(runId));
        }

        string runRelative = SandboxBase + @"\" + runId;
        var writer = new RegistryWriter(new SandboxWritePolicy(RegistryPath.Parse(@"HKCU\" + SandboxBase)));

        try
        {
            // The run root goes through the engine, under the sandbox policy, like every other key the tests write.
            writer.SetKeySecurity(
                RegistryPath.Parse(@"HKCU\" + runRelative),
                RegistrySecurityInformation.Dacl | RegistrySecurityInformation.UnprotectedDacl,
                FullControlForCurrentUser());
        }
        catch (RegistryOperationException error) when (error.Win32Error == ErrorFileNotFound)
        {
            // The run tree was never created.
            return;
        }

        WalkChildren(runRelative);

        try
        {
            writer.DeleteKey(RegistryPath.Parse(@"HKCU\" + runRelative), recursive: true);
        }
        catch (RegistryOperationException error) when (error.Win32Error == ErrorFileNotFound)
        {
            // Nothing was left to delete.
        }
    }

    /// <summary>
    /// Walks the subkeys of <paramref name="relative"/>, whose DACL already grants the current user full control.
    /// Each child is given that DACL first, through a WRITE_DAC open that does not follow links. A link node is
    /// removed instead. Only then is the child's own subkey list read, so a child that denied the user access
    /// before the walk is still reached.
    /// </summary>
    private static void WalkChildren(string relative)
    {
        using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using RegistryKey? key = root.OpenSubKey(relative, writable: false);
        if (key is null)
        {
            return;
        }

        byte[] fullControl = FullControlForCurrentUser();
        foreach (string child in key.GetSubKeyNames())
        {
            if (RegistryLinkFixture.RestoreKeyOrRemoveLink(relative, child, fullControl))
            {
                continue;
            }

            WalkChildren(relative + @"\" + child);
        }
    }

    private static byte[] FullControlForCurrentUser()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        string sid = identity.User?.Value ?? throw new InvalidOperationException("The current Windows identity has no user SID.");
        var descriptor = new RawSecurityDescriptor($"D:(A;CI;KA;;;{sid})");
        byte[] binary = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(binary, 0);
        return binary;
    }
}
#endif
