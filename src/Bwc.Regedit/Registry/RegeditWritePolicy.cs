using Bwc.Core;
using Bwc.Registry;

namespace Bwc.Regedit.Registry;

/// <summary>
/// The single place that chooses the Registry Editor write policy. The UI and the command line
/// share it so an E2E build cannot accidentally write outside the configured sandbox.
/// </summary>
public static class RegeditWritePolicy
{
    private const string DenyAllSandboxRoot = @"HKCU\Software\BWC-E2E\unset";

    public static RegistryStore CreateStore()
    {
        IRegistryWritePolicy policy;
        string description;

#if BWC_E2E
        string? configured = Environment.GetEnvironmentVariable("BWC_E2E_SANDBOX_ROOT");
        if (!string.IsNullOrWhiteSpace(configured) &&
            RegistryPath.TryParse(configured.Trim(), out RegistryPath? sandboxRoot))
        {
            try
            {
                policy = new SandboxWritePolicy(sandboxRoot);
                description = "SandboxWritePolicy(" + sandboxRoot.ToDisplayString() + ")";
            }
            catch (ArgumentException ex)
            {
                policy = new SandboxWritePolicy(RegistryPath.Parse(DenyAllSandboxRoot));
                description = "SandboxWritePolicy(deny all, invalid BWC_E2E_SANDBOX_ROOT: " + ex.Message + ")";
            }
        }
        else
        {
            policy = new SandboxWritePolicy(RegistryPath.Parse(DenyAllSandboxRoot));
            description = "SandboxWritePolicy(deny all, BWC_E2E_SANDBOX_ROOT is not set)";
        }
#else
        policy = new ShippingWritePolicy();
        description = "ShippingWritePolicy";
#endif

        Log.Info("Registry Editor write policy: " + description);
        return new RegistryStore(policy);
    }
}
