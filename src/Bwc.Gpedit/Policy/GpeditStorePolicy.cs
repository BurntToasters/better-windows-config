using Bwc.Core;
using Bwc.Gpo.Refresh;
using Bwc.Gpo.Store;
using Bwc.Policy.Store;
using Bwc.Update;

namespace Bwc.Gpedit.Policy;

/// <summary>What <see cref="GpeditStorePolicy.Choose"/> picked, and why.</summary>
/// <param name="Policy">The policy the store opens through.</param>
/// <param name="Description">One line for the log.</param>
/// <param name="Problem">Why a requested sandbox was refused, or null.</param>
internal sealed record GpeditStoreChoice(IGpoStorePolicy Policy, string Description, string? Problem);

/// <summary>
/// The single place that chooses the policy object and the template sources of the Local Group Policy Editor, as
/// <c>RegeditWritePolicy</c> does for the registry. The ship flavor always uses the system policy and the system and
/// import store folders and reads no environment variable. Under <c>BWC_E2E</c> the sandbox root, the template root and
/// the import store come from variables that only the tests set (docs/failure-modes/gpedit-tree-list.md, sections 1
/// and 2). This piece only reads: no save path exists yet.
/// </summary>
internal static class GpeditStorePolicy
{
    /// <summary>Picks the policy object policy for this process.</summary>
    internal static GpeditStoreChoice Choose()
    {
#if BWC_E2E
        string? configured = Environment.GetEnvironmentVariable("BWC_E2E_GPO_ROOT");
        string runRoot = AppPaths.BaseDirectory;
        if (string.IsNullOrWhiteSpace(configured))
        {
            return new GpeditStoreChoice(
                new DenyAllGpoStorePolicy(runRoot),
                "DenyAllGpoStorePolicy (BWC_E2E_GPO_ROOT is not set)",
                null);
        }

        try
        {
            var sandbox = new SandboxGpoStorePolicy(configured.Trim(), runRoot);
            return new GpeditStoreChoice(sandbox, "SandboxGpoStorePolicy(" + sandbox.GpoRoot + ")", null);
        }
        catch (Exception ex) when (ex is ArgumentException or GpoStoreException)
        {
            return new GpeditStoreChoice(
                new DenyAllGpoStorePolicy(runRoot),
                "DenyAllGpoStorePolicy (invalid BWC_E2E_GPO_ROOT)",
                ex.Message);
        }
#else
        return new GpeditStoreChoice(new SystemGpoStorePolicy(), "SystemGpoStorePolicy", null);
#endif
    }

    /// <summary>The template sources: the system folder first, then the import store.</summary>
    internal static TemplateSources CreateTemplateSources()
    {
#if BWC_E2E
        string? admx = Environment.GetEnvironmentVariable("BWC_E2E_ADMX_ROOT");
        string system = string.IsNullOrWhiteSpace(admx) ? SystemTemplateFolder() : admx.Trim();
        string? store = Environment.GetEnvironmentVariable("BWC_E2E_IMPORT_STORE");
        string importStore = string.IsNullOrWhiteSpace(store)
            ? Path.Combine(AppPaths.BaseDirectory, "gpedit", "template-store")
            : store.Trim();

        // The store folder of a test is made by the current user, so the rules check the current user, not Administrators.
        return new TemplateSources(system, importStore, ProtectedAcl.ForCurrentUser());
#else
        return new TemplateSources(SystemTemplateFolder(), ImportStoreFolder(), ProtectedAcl.Ship());
#endif
    }

#if !BWC_E2E
    private static string ImportStoreFolder() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "BetterWindowsConfig",
            "PolicyDefinitions");
#endif

    private static string SystemTemplateFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "PolicyDefinitions");
}

#if BWC_E2E
/// <summary>
/// E2E flavor only. The policy of a run that named no sandbox: reads see an empty folder that does not exist (inside the
/// run data root, never the machine's real folder), and every write or removal is refused before any file operation.
/// </summary>
internal sealed class DenyAllGpoStorePolicy : IGpoStorePolicy
{
    internal DenyAllGpoStorePolicy(string runDataRoot)
    {
        GpoRoot = Path.Combine(runDataRoot, "gpedit", "no-policy-object");
        BackupRoot = Path.Combine(runDataRoot, "gpedit", "no-policy-object.backup");
        LockName = @"Local\BetterWindowsConfig.GpoWrite.Denied";
        Refresher = new DenyAllRefresher();
    }

    public string GpoRoot { get; }

    public string BackupRoot { get; }

    public string LockName { get; }

    public IPolicyRefresher Refresher { get; }

    public void EnsureWriteAllowed(string path) => throw Denied(path);

    public void EnsureRemoveAllowed(string path) => throw Denied(path);

    private static GpoStoreException Denied(string path) =>
        new(GpoStoreFailure.PolicyDenied, "Write", path, 0, "The E2E run has no sandbox policy object (BWC_E2E_GPO_ROOT is not set); nothing may be written.");

    private sealed class DenyAllRefresher : IPolicyRefresher
    {
        public void Refresh(GpoSide side) =>
            throw new GpoStoreException(GpoStoreFailure.RefreshFailed, "Refresh", side.ToString(), 0, "Refresh is denied in a run with no sandbox.");
    }
}
#endif
