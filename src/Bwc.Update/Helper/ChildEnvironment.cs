namespace Bwc.Update;

/// <summary>
/// The updater never runs under a compatibility layer. The Hub's old launch set __COMPAT_LAYER=RunAsInvoker,
/// and the helper, the installer, and the relaunched Hub would all inherit it. The helper removes it from
/// its own process environment before any child starts, so every child inherits a clean block (the
/// installer is started through the shell, which reads the same environment).
/// </summary>
public static class ChildEnvironment
{
    public const string CompatibilityLayer = "__COMPAT_LAYER";

    public static void RemoveCompatibilityLayer() =>
        Environment.SetEnvironmentVariable(CompatibilityLayer, null, EnvironmentVariableTarget.Process);
}
