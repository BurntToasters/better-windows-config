namespace Bwc.Update;

/// <summary>
/// The environment the updater hands to the programs it starts. The updater never runs under a compatibility layer: the
/// Hub's old launch set __COMPAT_LAYER=RunAsInvoker, and the helper, the installer, and the relaunched Hub would all
/// inherit it. It also never passes on the .NET host and runtime switches (DOTNET_*, COREHOST_*, CORECLR_*, COMPlus_*):
/// a startup hook, a profiler, or a host trace set by a medium process would otherwise run inside the elevated helper,
/// or in the installer it starts. A switch that was already honored before Main (the profiler, the host trace) cannot be
/// taken back; update-elevation.md (HE5) records that as an open risk.
/// </summary>
public static class ChildEnvironment
{
    public const string CompatibilityLayer = "__COMPAT_LAYER";

    private static readonly string[] RuntimePrefixes = ["DOTNET_", "COREHOST_", "CORECLR_", "COMPlus_"];

    public static void RemoveCompatibilityLayer() =>
        Environment.SetEnvironmentVariable(CompatibilityLayer, null, EnvironmentVariableTarget.Process);

    /// <summary>True for a .NET host or runtime variable. Windows compares variable names without regard to case.</summary>
    public static bool IsRuntimeVariable(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (var prefix in RuntimePrefixes)
        {
            if (name.Length > prefix.Length && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Removes the runtime variables from this process's environment and returns what it removed, so a caller that only
    /// needs the clean environment for one start can put them back with <see cref="Restore"/>.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> RemoveRuntimeVariables()
    {
        var removed = new List<KeyValuePair<string, string>>();
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && IsRuntimeVariable(name))
            {
                removed.Add(new KeyValuePair<string, string>(name, entry.Value as string ?? string.Empty));
            }
        }

        foreach (var variable in removed)
        {
            Environment.SetEnvironmentVariable(variable.Key, null, EnvironmentVariableTarget.Process);
        }

        return removed;
    }

    /// <summary>Puts back what <see cref="RemoveRuntimeVariables"/> removed.</summary>
    public static void Restore(IReadOnlyList<KeyValuePair<string, string>> removed)
    {
        ArgumentNullException.ThrowIfNull(removed);
        foreach (var variable in removed)
        {
            Environment.SetEnvironmentVariable(variable.Key, variable.Value, EnvironmentVariableTarget.Process);
        }
    }

    /// <summary>
    /// The installer's environment block: no compatibility layer, no runtime variables, and the temp folder of the
    /// protected copy as TEMP and TMP, so the installer never unpacks anything into a user-writable folder.
    /// </summary>
    public static void ForInstaller(IDictionary<string, string?> environment, string tempFolder)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(tempFolder);
        foreach (var name in environment.Keys.ToList())
        {
            if (IsRuntimeVariable(name) || string.Equals(name, CompatibilityLayer, StringComparison.OrdinalIgnoreCase))
            {
                _ = environment.Remove(name);
            }
        }

        environment["TEMP"] = tempFolder;
        environment["TMP"] = tempFolder;
    }
}
