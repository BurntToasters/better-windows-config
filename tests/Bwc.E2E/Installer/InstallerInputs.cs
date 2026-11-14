namespace Bwc.E2E.Installer;

/// <summary>
/// What scripts/test-installer-e2e.js passes to the installer tests through the environment. Without
/// <see cref="GateVariable"/> set to "1" the tests are Inconclusive, so the normal E2E run never installs.
/// </summary>
internal sealed record InstallerInputs(
    string InstallerA,
    string InstallerB,
    string VersionA,
    string VersionB,
    string ResultPath)
{
    public const string GateVariable = "BWC_INSTALLER_TEST";
    public const string InstallerAVariable = "BWC_INSTALLER_A";
    public const string InstallerBVariable = "BWC_INSTALLER_B";
    public const string VersionAVariable = "BWC_INSTALLER_VERSION_A";
    public const string VersionBVariable = "BWC_INSTALLER_VERSION_B";
    public const string ResultPathVariable = "BWC_INSTALLER_RESULT";

    /// <summary>The inputs, or null when the driver did not start this run.</summary>
    public static InstallerInputs? TryRead()
    {
        if (Environment.GetEnvironmentVariable(GateVariable) != "1")
        {
            return null;
        }

        return new InstallerInputs(
            Required(InstallerAVariable),
            Required(InstallerBVariable),
            Required(VersionAVariable),
            Required(VersionBVariable),
            Required(ResultPathVariable));
    }

    private static string Required(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{name} is not set. Run scripts/test-installer-e2e.js.");
        }

        return value;
    }
}
